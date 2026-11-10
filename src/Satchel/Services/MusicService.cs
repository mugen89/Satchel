using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Controls;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Security;

namespace Satchel.Services;

/// <summary>
/// The background music player: a queue of YouTube / YouTube Music tracks played by a hidden player that lives in the main
/// window, so music keeps going while you switch pages — and even while Satchel is locked.
/// </summary>
public sealed partial class MusicService : ObservableObject
{
    private readonly DataStore _store;
    private readonly SettingsService _settings;
    private readonly ToastService _toasts;
    private readonly WebService _web;
    private YouTubePlayer? _player;
    private List<MusicTrack> _order = [];
    private int _index = -1;
    private DateTime _lastSave;
    private int _failuresInRow;

    public MusicService(DataStore store, SettingsService settings, ToastService toasts, WebService web)
    {
        _store = store;
        _settings = settings;
        _toasts = toasts;
        _web = web;
    }

    /// <summary>The tracks in playing order (shuffled when shuffle is on).</summary>
    public ObservableCollection<MusicTrack> Queue { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrack), nameof(Title), nameof(Artist), nameof(Thumbnail))]
    private MusicTrack? _current;

    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isBuffering;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText), nameof(Progress))]
    private double _position;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText), nameof(Progress))]
    private double _duration;

    [ObservableProperty] private string _sourceName = "";

    public bool HasTrack => Current != null;
    public string Title => Current?.DisplayTitle ?? "";
    public string Artist => Current?.Artist ?? "";
    public byte[]? Thumbnail => Current?.Thumbnail;
    public string PositionText => YouTube.FormatTime(Position);
    public string DurationText => YouTube.FormatTime(Duration);
    public double Progress => Duration > 0 ? Math.Clamp(Position / Duration, 0, 1) : 0;
    public bool HasNext => _order.Count > 1 || RepeatMode == "one";

    public double Volume
    {
        get => _settings.Current.MusicVolume;
        set
        {
            value = Math.Clamp(value, 0, 100);
            if (Math.Abs(_settings.Current.MusicVolume - value) < 0.01) return;
            _settings.Current.MusicVolume = value;
            _player?.SetVolume(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(VolumeGlyph));
        }
    }

    public string VolumeGlyph => Volume <= 0 ? "" : Volume < 34 ? "" : Volume < 67 ? "" : "";

    public bool Shuffle
    {
        get => _settings.Current.MusicShuffle;
        set
        {
            if (_settings.Current.MusicShuffle == value) return;
            _settings.Current.MusicShuffle = value;
            OnPropertyChanged();
            Reorder();
        }
    }

    /// <summary>"off", "all" or "one".</summary>
    public string RepeatMode
    {
        get => _settings.Current.MusicRepeat;
        set
        {
            _settings.Current.MusicRepeat = value is "off" or "one" ? value : "all";
            OnPropertyChanged();
            OnPropertyChanged(nameof(RepeatGlyph));
            OnPropertyChanged(nameof(RepeatOn));
            OnPropertyChanged(nameof(HasNext));
        }
    }

    public bool RepeatOn => RepeatMode != "off";
    public string RepeatGlyph => RepeatMode == "one" ? "" : "";

    /// <summary>The main window hands over the hidden player once it's loaded.</summary>
    public void Attach(YouTubePlayer player)
    {
        _player = player;
        player.StateChanged += OnState;
        player.TimeChanged += OnTime;
        player.ErrorOccurred += OnError;
        player.VideoInfo += OnInfo;
    }

    /// <summary>The visible "Show video" surface (Music page) — the same hidden player, shown over a slot on the page.</summary>
    public YouTubePlayer? Player => _player;

    // ----- playing -----

    public void PlayList(IEnumerable<MusicTrack> tracks, MusicTrack? start, string sourceName)
    {
        var list = tracks.Where(t => !string.IsNullOrEmpty(t.YoutubeId)).ToList();
        if (list.Count == 0) return;
        SourceName = sourceName;
        _order = list;
        if (Shuffle)
        {
            var rest = list.Where(t => !ReferenceEquals(t, start)).OrderBy(_ => Random.Shared.Next()).ToList();
            _order = start != null ? [start, .. rest] : rest;
        }
        _index = start != null ? Math.Max(0, _order.IndexOf(start)) : 0;
        RebuildQueue();
        PlayCurrent();
    }

    public void PlayOne(MusicTrack track) => PlayList([track], track, track.DisplayTitle);

    /// <summary>Adds tracks after the current one (or starts playing when nothing is).</summary>
    public void PlayNext(IEnumerable<MusicTrack> tracks)
    {
        var list = tracks.ToList();
        if (Current == null) { PlayList(list, list.FirstOrDefault(), "Queue"); return; }
        _order.InsertRange(_index + 1, list);
        RebuildQueue();
        _toasts.Success(list.Count == 1 ? "Plays next" : $"{list.Count} tracks play next");
    }

    private void PlayCurrent()
    {
        if (_index < 0 || _index >= _order.Count) return;
        var track = _order[_index];
        foreach (var t in _order) t.IsCurrent = ReferenceEquals(t, track);
        Current = track;
        Position = 0;
        Duration = track.Duration;
        IsBuffering = true;
        if (_player == null) { _toasts.Warning("The music player isn't ready yet"); return; }
        _player.SetVolume(Volume);
        _player.Load(track.YoutubeId, 0, autoplay: true);
        IsPlaying = true;
        OnPropertyChanged(nameof(HasNext));
    }

    [RelayCommand]
    public void PlayPause()
    {
        if (_player == null) return;
        if (Current == null)
        {
            if (_order.Count > 0) { _index = Math.Max(0, _index); PlayCurrent(); }
            return;
        }
        if (IsPlaying) _player.Pause();
        else _player.Play();
        IsPlaying = !IsPlaying;
    }

    [RelayCommand]
    public void Next() => Step(+1, userAction: true);

    [RelayCommand]
    public void Previous()
    {
        if (Position > 4 && Current != null) { _player?.Seek(0); return; }
        Step(-1, userAction: true);
    }

    private void Step(int delta, bool userAction)
    {
        if (_order.Count == 0) return;
        if (!userAction && RepeatMode == "one") { _player?.Seek(0); return; }
        int next = _index + delta;
        if (next >= _order.Count)
        {
            if (RepeatMode == "off" && !userAction) { Stopped(); return; }
            if (Shuffle) Reshuffle();
            next = 0;
        }
        if (next < 0) next = _order.Count - 1;
        _index = next;
        PlayCurrent();
    }

    private void Stopped()
    {
        IsPlaying = false;
        IsBuffering = false;
        Position = 0;
    }

    [RelayCommand]
    public void Stop()
    {
        _player?.Halt();
        foreach (var t in _order) t.IsCurrent = false;
        Current = null;
        _order.Clear();
        _index = -1;
        Queue.Clear();
        IsPlaying = false;
        IsBuffering = false;
        Position = Duration = 0;
        SourceName = "";
    }

    public void Seek(double seconds)
    {
        if (_player == null || Current == null) return;
        Position = seconds;
        _player.Seek(seconds);
        IsPlaying = true;
    }

    public void SeekFraction(double fraction)
    {
        if (Duration > 0) Seek(Math.Clamp(fraction, 0, 1) * Duration);
    }

    [RelayCommand]
    private void JumpTo(MusicTrack? track)
    {
        if (track == null) return;
        int i = _order.IndexOf(track);
        if (i < 0) return;
        _index = i;
        PlayCurrent();
    }

    [RelayCommand]
    private void RemoveFromQueue(MusicTrack? track)
    {
        if (track == null) return;
        int i = _order.IndexOf(track);
        if (i < 0) return;
        _order.RemoveAt(i);
        if (i < _index) _index--;
        else if (i == _index)
        {
            if (_order.Count == 0) { Stop(); return; }
            _index = Math.Min(_index, _order.Count - 1);
            PlayCurrent();
        }
        RebuildQueue();
    }

    [RelayCommand]
    private void ToggleShuffle() => Shuffle = !Shuffle;

    [RelayCommand]
    private void CycleRepeat() => RepeatMode = RepeatMode switch { "all" => "one", "one" => "off", _ => "all" };

    public void ChangeVolume(double delta) => Volume += delta;

    private void Reorder()
    {
        if (_order.Count < 2 || Current == null) return;
        if (Shuffle) Reshuffle();
        else
        {
            // Back to list order: the original playlist order is the tracks' SortOrder.
            _order = _order.OrderBy(t => t.PlaylistId).ThenBy(t => t.SortOrder).ToList();
            _index = _order.IndexOf(Current);
        }
        RebuildQueue();
    }

    private void Reshuffle()
    {
        var current = Current;
        var rest = _order.Where(t => !ReferenceEquals(t, current)).OrderBy(_ => Random.Shared.Next()).ToList();
        _order = current != null ? [current, .. rest] : rest;
        _index = current != null ? 0 : -1;
        RebuildQueue();
    }

    private void RebuildQueue()
    {
        Queue.Clear();
        foreach (var t in _order) Queue.Add(t);
        OnPropertyChanged(nameof(HasNext));
    }

    // ----- player events -----

    private void OnState(int state, double time, double duration)
    {
        switch (state)
        {
            case 0: // ended
                MarkPlayed();
                Step(+1, userAction: false);
                break;
            case 1:
                IsPlaying = true;
                IsBuffering = false;
                _failuresInRow = 0;
                break;
            case 2:
                IsPlaying = false;
                IsBuffering = false;
                break;
            case 3:
                IsBuffering = true;
                break;
        }
        if (duration > 0) Duration = duration;
        if (time >= 0 && state != 0) Position = time;
    }

    private void OnTime(double time, double duration)
    {
        Position = time;
        if (duration > 0 && Math.Abs(Duration - duration) > 0.5)
        {
            Duration = duration;
            if (Current is { } t && Math.Abs(t.Duration - duration) > 1)
            {
                t.Duration = Math.Round(duration);
                _ = SaveTrackAsync(t);
            }
        }
    }

    private void OnInfo(string id, string title, string author)
    {
        if (Current is not { } t || t.YoutubeId != id) return;
        bool changed = false;
        if ((t.Title.Length == 0 || t.Title == "YouTube track") && title.Length > 0) { t.Title = title; changed = true; }
        if (t.Artist.Length == 0 && author.Length > 0) { t.Artist = author; changed = true; }
        if (changed)
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Artist));
            _ = SaveTrackAsync(t);
        }
    }

    private void OnError(int code)
    {
        if (Current is not { } t) return;
        _failuresInRow++;
        if (code is 101 or 150 or 152 or 153 && !t.CannotEmbed)
        {
            t.CannotEmbed = true;
            _ = SaveTrackAsync(t);
        }
        _toasts.Warning($"Skipped “{t.DisplayTitle}” — {YouTubePlayer.ErrorText(code).TrimEnd('.').ToLowerInvariant()}.");
        if (_failuresInRow >= Math.Min(_order.Count, 5)) { Stopped(); return; }
        Step(+1, userAction: false);
    }

    private void MarkPlayed()
    {
        if (Current is not { } t) return;
        t.PlayCount++;
        t.LastPlayedAt = DateTime.Now;
        _ = SaveTrackAsync(t);
    }

    private async Task SaveTrackAsync(MusicTrack t)
    {
        // Tracks are encrypted: only save while unlocked (music keeps playing on the lock screen).
        if (t.Id == 0 || !Crypto.IsUnlocked) return;
        if ((DateTime.Now - _lastSave).TotalMilliseconds < 300) await Task.Delay(300);
        _lastSave = DateTime.Now;
        try { await _store.UpdateAsync(t); } catch { /* best effort */ }
    }

    // ----- adding tracks from links -----

    [GeneratedRegex(@"[?&]list=([A-Za-z0-9_-]{10,})")]
    private static partial Regex ListRegex();

    public static string? PlaylistId(string? url) => url == null ? null : ListRegex().Match(url) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>Title, channel and thumbnail of a video (oEmbed — no API key).</summary>
    public async Task<MusicTrack> FetchTrackAsync(string videoId)
    {
        var track = new MusicTrack { YoutubeId = videoId, Title = "YouTube track" };
        if (!_web.IsAvailable) return track;
        var json = await _web.GetStringAsync($"https://www.youtube.com/oembed?url={Uri.EscapeDataString(YouTube.WatchUrl(videoId))}&format=json");
        if (json != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("title", out var t)) track.Title = t.GetString() ?? track.Title;
                if (doc.RootElement.TryGetProperty("author_name", out var a)) track.Artist = CleanArtist(a.GetString() ?? "");
            }
            catch (JsonException) { }
        }
        track.Thumbnail = await _web.GetImageAsync(YouTube.ThumbnailUrl(videoId), 320);
        return track;
    }

    /// <summary>The videos of a YouTube (or YouTube Music) playlist, read from its public page — the first 100 or so.</summary>
    public async Task<(string Title, List<MusicTrack> Tracks)?> FetchPlaylistAsync(string listId)
    {
        if (!_web.IsAvailable) return null;
        var html = await _web.GetStringAsync($"https://www.youtube.com/playlist?list={listId}&hl=en");
        if (html == null) return null;
        var start = html.IndexOf("ytInitialData", StringComparison.Ordinal);
        if (start < 0) return null;
        start = html.IndexOf('{', start);
        var end = html.IndexOf(";</script>", start, StringComparison.Ordinal);
        if (start < 0 || end < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(html[start..end]);
            var tracks = new List<MusicTrack>();
            var seen = new HashSet<string>();
            string title = "";
            Walk(doc.RootElement, el =>
            {
                if (el.TryGetProperty("playlistVideoRenderer", out var r) && r.TryGetProperty("videoId", out var id) && id.GetString() is { } vid && seen.Add(vid))
                {
                    var t = new MusicTrack { YoutubeId = vid, Title = Runs(r, "title"), Artist = CleanArtist(Runs(r, "shortBylineText")) };
                    if (r.TryGetProperty("lengthSeconds", out var len) && double.TryParse(len.GetString(), out var secs)) t.Duration = secs;
                    tracks.Add(t);
                }
                if (title.Length == 0 && el.TryGetProperty("playlistMetadataRenderer", out var meta) && meta.TryGetProperty("title", out var mt))
                    title = mt.GetString() ?? "";
            });
            return tracks.Count == 0 ? null : (title, tracks);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task FillThumbnailsAsync(IEnumerable<MusicTrack> tracks, CancellationToken ct = default)
    {
        foreach (var t in tracks.Where(t => t.Thumbnail == null).ToList())
        {
            if (ct.IsCancellationRequested) return;
            t.Thumbnail = await _web.GetImageAsync(YouTube.ThumbnailUrl(t.YoutubeId), 320, ct);
        }
    }

    private static string Runs(JsonElement r, string name)
    {
        if (!r.TryGetProperty(name, out var node)) return "";
        if (node.TryGetProperty("simpleText", out var s)) return s.GetString() ?? "";
        if (node.TryGetProperty("runs", out var runs)) return string.Concat(runs.EnumerateArray().Select(x => x.TryGetProperty("text", out var tx) ? tx.GetString() : ""));
        return "";
    }

    private static void Walk(JsonElement el, Action<JsonElement> visit)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                visit(el);
                foreach (var p in el.EnumerateObject()) Walk(p.Value, visit);
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) Walk(item, visit);
                break;
        }
    }

    /// <summary>"Artist - Topic" (YouTube Music auto channels) → "Artist".</summary>
    public static string CleanArtist(string author) =>
        author.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase) ? author[..^8] : author.Replace("VEVO", "", StringComparison.Ordinal).Trim();
}
