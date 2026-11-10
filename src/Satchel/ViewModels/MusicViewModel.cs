using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Behaviors;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Services;
using Satchel.ViewModels.Dialogs;

namespace Satchel.ViewModels;

/// <summary>Playlists of YouTube / YouTube Music links, played in the background by <see cref="MusicService"/>.</summary>
public sealed partial class MusicViewModel : PageViewModel, IDisposable
{
    private readonly DataStore _store;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly SettingsService _settings;
    private readonly WebService _web;
    private bool _loaded;
    private int? _playAfterLoad;
    private CancellationTokenSource? _thumbs;

    public MusicViewModel(INavigator nav, DataStore store, DialogService dialogs, ToastService toasts, SettingsService settings, MusicService music, WebService web)
    {
        _store = store;
        _dialogs = dialogs;
        _toasts = toasts;
        _settings = settings;
        _web = web;
        Player = music;
        Player.PropertyChanged += OnPlayerChanged;
    }

    public override string Title => "Music";
    public MusicService Player { get; }
    public ObservableCollection<MusicPlaylist> Playlists { get; } = [];
    public ObservableCollection<MusicTrack> Tracks { get; } = [];

    [ObservableProperty] private MusicPlaylist? _selected;
    [ObservableProperty] private bool _showQueue;
    [ObservableProperty] private bool _isAdding;
    [ObservableProperty] private string _addText = "";
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private string _workText = "";
    [ObservableProperty] private string _search = "";

    public bool HasPlaylists => Playlists.Count > 0;
    public bool IsEmpty => !ShowQueue && Tracks.Count == 0;
    public bool IsOnline => _web.IsAvailable;
    public string HeaderTitle => ShowQueue ? "Playing queue" : Selected?.Name ?? "Music";

    public string HeaderInfo
    {
        get
        {
            var list = ShowQueue ? Player.Queue.ToList() : Tracks.ToList();
            var seconds = list.Sum(t => t.Duration);
            var length = seconds <= 0 ? "" : seconds >= 3600 ? $" · {seconds / 3600:0} h {seconds % 3600 / 60:0} min" : $" · {Math.Round(seconds / 60)} min";
            return $"{list.Count} track{(list.Count == 1 ? "" : "s")}{length}";
        }
    }

    public bool ShowVideo
    {
        get => _settings.Current.MusicShowVideo;
        set
        {
            _settings.Current.MusicShowVideo = value;
            OnPropertyChanged();
            VideoModeChanged?.Invoke();
        }
    }

    /// <summary>The view moves the hidden player over its video slot (or hides it again).</summary>
    public event Action? VideoModeChanged;

    partial void OnSelectedChanged(MusicPlaylist? value)
    {
        if (value != null) ShowQueue = false;
        _ = LoadTracksAsync();
        OnPropertyChanged(nameof(HeaderTitle));
    }

    partial void OnShowQueueChanged(bool value)
    {
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HeaderInfo));
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnSearchChanged(string value) => _ = LoadTracksAsync();

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MusicService.Current))
        {
            var current = Player.Current;
            foreach (var t in Tracks) t.IsCurrent = current != null && (ReferenceEquals(t, current) || (t.Id != 0 && t.Id == current.Id));
            if (ShowQueue) OnPropertyChanged(nameof(HeaderInfo));
        }
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_loaded) await LoadAsync();
        if (parameter is "add") OpenAdd();
        else if (parameter is "queue") ShowQueue = true;
        else if (parameter is ItemRef r && Playlists.FirstOrDefault(p => p.Id == r.ParentId) is { } playlist)
        {
            // A song from search: open its playlist and play it once the tracks are in.
            _playAfterLoad = r.Id == 0 ? null : r.Id;
            if (ReferenceEquals(Selected, playlist)) await LoadTracksAsync();
            else Selected = playlist;
        }
    }

    private async Task LoadAsync()
    {
        var (lists, counts) = await _store.RunAsync(db => (db.MusicPlaylists.OrderBy(p => p.SortOrder).ToList(),
            db.MusicTracks.GroupBy(t => t.PlaylistId).Select(g => new { g.Key, Count = g.Count() }).ToList()));
        Playlists.Clear();
        foreach (var p in lists)
        {
            p.TrackCount = counts.FirstOrDefault(c => c.Key == p.Id)?.Count ?? 0;
            Playlists.Add(p);
        }
        _loaded = true;
        OnPropertyChanged(nameof(HasPlaylists));
        if (Player.HasTrack) ShowQueue = true;
        Selected ??= Playlists.FirstOrDefault();
        if (Selected == null) OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task LoadTracksAsync()
    {
        var playlist = Selected;
        Tracks.Clear();
        if (playlist == null)
        {
            RaiseList();
            return;
        }
        var id = playlist.Id;
        var tracks = await _store.ListAsync<MusicTrack>(q => q.Where(t => t.PlaylistId == id).OrderBy(t => t.SortOrder));
        if (!ReferenceEquals(Selected, playlist)) return;
        var q = Search.Trim();
        if (q.Length > 0) tracks = tracks.Where(t => t.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || t.Artist.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        var current = Player.Current;
        foreach (var t in tracks)
        {
            t.IsCurrent = current != null && t.Id == current.Id;
            Tracks.Add(t);
        }
        playlist.TrackCount = tracks.Count;
        RaiseList();
        if (_playAfterLoad is int pending)
        {
            _playAfterLoad = null;
            if (Tracks.FirstOrDefault(t => t.Id == pending) is { } track && Player.Current?.Id != track.Id) PlayTrack(track);
        }
    }

    private void RaiseList()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HeaderInfo));
    }

    public override void OnNewShortcut() => OpenAdd();

    // ----- playlists -----

    [RelayCommand]
    private void SelectPlaylist(MusicPlaylist? playlist)
    {
        if (playlist == null) return;
        if (ReferenceEquals(Selected, playlist)) ShowQueue = false;
        else Selected = playlist;
    }

    [RelayCommand]
    private void ShowQueueView() => ShowQueue = true;

    [RelayCommand]
    private async Task NewPlaylist()
    {
        var name = await _dialogs.PromptAsync("New playlist", "Collect songs, mixes or ambient videos — anything with a YouTube link.", "", "e.g. Focus", "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        var playlist = await CreatePlaylistAsync(name.Trim());
        Selected = playlist;
    }

    private async Task<MusicPlaylist> CreatePlaylistAsync(string name, string source = "")
    {
        var playlist = new MusicPlaylist { Name = name, SourceUrl = source, SortOrder = Playlists.Count, Color = Palettes.Tags[Playlists.Count % 10].Key };
        await _store.AddAsync(playlist);
        Playlists.Add(playlist);
        OnPropertyChanged(nameof(HasPlaylists));
        return playlist;
    }

    [RelayCommand]
    private async Task RenamePlaylist()
    {
        if (Selected is not { } p) return;
        var name = await _dialogs.PromptAsync("Rename playlist", null, p.Name, "Name", "Rename");
        if (string.IsNullOrWhiteSpace(name)) return;
        p.Name = name.Trim();
        await _store.UpdateAsync(p);
        OnPropertyChanged(nameof(HeaderTitle));
    }

    [RelayCommand]
    private async Task DeletePlaylist()
    {
        if (Selected is not { } p) return;
        if (!await _dialogs.ConfirmAsync("Delete playlist?", $"“{p.Name}” and its {p.TrackCount} track(s) will be removed from Satchel.", "Delete", danger: true)) return;
        await _store.DeleteAsync(p);
        Playlists.Remove(p);
        Selected = Playlists.FirstOrDefault();
        OnPropertyChanged(nameof(HasPlaylists));
    }

    [RelayCommand]
    private async Task ReorderPlaylists(ReorderEventArgs? args)
    {
        int i = 0;
        foreach (var p in Playlists) p.SortOrder = i++;
        await _store.SaveOrderAsync(Playlists, p => p.SortOrder);
    }

    // ----- playing -----

    [RelayCommand]
    private void PlayAll()
    {
        if (Tracks.Count == 0) return;
        Player.Shuffle = false;
        Player.PlayList(Tracks, Tracks[0], Selected?.Name ?? "Music");
    }

    [RelayCommand]
    private void ShuffleAll()
    {
        if (Tracks.Count == 0) return;
        Player.Shuffle = true;
        Player.PlayList(Tracks, Tracks[Random.Shared.Next(Tracks.Count)], Selected?.Name ?? "Music");
    }

    [RelayCommand]
    private void PlayTrack(MusicTrack? track)
    {
        if (track == null) return;
        if (ShowQueue)
        {
            Player.JumpToCommand.Execute(track);
            return;
        }
        if (ReferenceEquals(Player.Current, track) || (Player.Current?.Id == track.Id && track.Id != 0))
        {
            Player.PlayPause();
            return;
        }
        Player.PlayList(Tracks, track, Selected?.Name ?? "Music");
    }

    [RelayCommand]
    private void PlayNextTrack(MusicTrack? track)
    {
        if (track != null) Player.PlayNext([track]);
    }

    [RelayCommand]
    private void OpenOnYouTube(MusicTrack? track)
    {
        if (track != null) UiCommands.OpenUrl.Execute(YouTube.WatchUrl(track.YoutubeId, ReferenceEquals(track, Player.Current) ? Player.Position : 0));
    }

    [RelayCommand]
    private void SeekTo(double fraction) => Player.SeekFraction(fraction);

    // ----- editing tracks -----

    [RelayCommand]
    private async Task RemoveTrack(MusicTrack? track)
    {
        if (track == null) return;
        if (ShowQueue)
        {
            Player.RemoveFromQueueCommand.Execute(track);
            OnPropertyChanged(nameof(HeaderInfo));
            return;
        }
        Tracks.Remove(track);
        await _store.DeleteAsync(track);
        if (Selected != null) Selected.TrackCount = Tracks.Count;
        RaiseList();
        _toasts.Show($"Removed “{track.DisplayTitle}”", ToastKind.Info, "Undo", async () =>
        {
            track.Id = 0;
            await _store.AddAsync(track);
            if (Selected?.Id == track.PlaylistId) await LoadTracksAsync();
        });
    }

    [RelayCommand]
    private async Task MoveTrack(MusicTrack? track)
    {
        if (track == null) return;
        var options = Playlists.Where(p => p.Id != track.PlaylistId).Select(p => new PickOption(p.Name, $"{p.TrackCount} tracks", Glyphs.Music, p, p.Color)).ToList();
        if (options.Count == 0) { _toasts.Info("Create another playlist first"); return; }
        if (await _dialogs.ShowAsync(new PickDialogViewModel("Move to playlist", track.DisplayTitle, options)) is not PickOption { Value: MusicPlaylist target }) return;
        var oldList = Playlists.FirstOrDefault(p => p.Id == track.PlaylistId);
        track.PlaylistId = target.Id;
        track.SortOrder = target.TrackCount;
        await _store.UpdateAsync(track);
        target.TrackCount++;
        if (oldList != null) oldList.TrackCount = Math.Max(0, oldList.TrackCount - 1);
        Tracks.Remove(track);
        RaiseList();
        _toasts.Success($"Moved to {target.Name}");
    }

    [RelayCommand]
    private async Task ReorderTracks(ReorderEventArgs? args)
    {
        if (ShowQueue || Search.Length > 0) return;
        int i = 0;
        foreach (var t in Tracks) t.SortOrder = i++;
        await _store.SaveOrderAsync(Tracks, t => t.SortOrder);
    }

    // ----- adding -----

    [RelayCommand]
    private void OpenAdd()
    {
        AddText = "";
        IsAdding = true;
    }

    [RelayCommand]
    private void CloseAdd()
    {
        if (IsWorking) return;
        IsAdding = false;
    }

    /// <summary>One link per line: videos are added to the selected playlist; a playlist link imports the whole list.</summary>
    [RelayCommand]
    private async Task AddLinks()
    {
        var lines = AddText.Split(['\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Contains("youtu", StringComparison.OrdinalIgnoreCase) || YouTube.VideoId(l) != null).ToList();
        if (lines.Count == 0) { _toasts.Info("Paste YouTube or YouTube Music links (one per line)"); return; }
        if (!_web.IsAvailable) _toasts.Info("You're offline — tracks are added without titles; they fill in when played.");
        IsWorking = true;
        int added = 0;
        try
        {
            var singles = new List<string>();
            foreach (var line in lines)
            {
                var videoId = YouTube.VideoId(line);
                var listId = MusicService.PlaylistId(line);
                if (videoId == null && listId != null)
                {
                    WorkText = "Reading the playlist…";
                    var result = await Player.FetchPlaylistAsync(listId);
                    if (result is not { } r || r.Tracks.Count == 0) { _toasts.Warning("Couldn't read that playlist (is it public?)"); continue; }
                    var target = await CreatePlaylistAsync(string.IsNullOrWhiteSpace(r.Title) ? "Imported playlist" : r.Title, line);
                    WorkText = $"Adding {r.Tracks.Count} tracks…";
                    int i = 0;
                    foreach (var t in r.Tracks) { t.PlaylistId = target.Id; t.SortOrder = i++; }
                    await _store.AddRangeAsync(r.Tracks);
                    target.TrackCount = r.Tracks.Count;
                    added += r.Tracks.Count;
                    Selected = target;
                    _ = FillThumbnailsLaterAsync(r.Tracks);
                }
                else if (videoId != null) singles.Add(videoId);
            }
            if (singles.Count > 0)
            {
                var playlist = Selected ?? await CreatePlaylistAsync("My music");
                var existing = (await _store.ListAsync<MusicTrack>(q => q.Where(t => t.PlaylistId == playlist.Id))).Select(t => t.YoutubeId).ToHashSet();
                int order = playlist.TrackCount;
                foreach (var id in singles.Distinct().Where(id => !existing.Contains(id)))
                {
                    WorkText = $"Adding track {added + 1}…";
                    var track = await Player.FetchTrackAsync(id);
                    track.PlaylistId = playlist.Id;
                    track.SortOrder = order++;
                    await _store.AddAsync(track);
                    added++;
                }
                if (ReferenceEquals(Selected, playlist)) await LoadTracksAsync();
                else Selected = playlist;
            }
        }
        finally
        {
            IsWorking = false;
            WorkText = "";
        }
        if (added > 0)
        {
            IsAdding = false;
            _toasts.Success(added == 1 ? "1 track added" : $"{added} tracks added");
        }
    }

    private async Task FillThumbnailsLaterAsync(List<MusicTrack> tracks)
    {
        _thumbs?.Cancel();
        var cts = _thumbs = new CancellationTokenSource();
        await Player.FillThumbnailsAsync(tracks, cts.Token);
        if (cts.IsCancellationRequested) return;
        await _store.UpdateRangeAsync(tracks.Where(t => t.Thumbnail != null));
        if (Selected?.Id == tracks.FirstOrDefault()?.PlaylistId) await LoadTracksAsync();
    }

    [RelayCommand]
    private void ClearSearch() => Search = "";

    public void Dispose()
    {
        Player.PropertyChanged -= OnPlayerChanged;
        _thumbs?.Cancel();
    }
}
