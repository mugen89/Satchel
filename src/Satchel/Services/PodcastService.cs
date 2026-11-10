using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Controls;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;

namespace Satchel.Services;

/// <summary>
/// Podcast episodes from Feeds. Plays through the main window's <see cref="AudioHost"/>, so an episode keeps going on every
/// page; the position is saved as it plays, so the next time it resumes where you stopped. Music and podcasts pause
/// each other. Locking saves the position and stops the episode.
/// </summary>
public sealed partial class PodcastService : ObservableObject
{
    private static readonly double[] Speeds = [1, 1.25, 1.5, 1.75, 2, 0.8];
    private readonly DataStore _store;
    private readonly ToastService _toasts;
    private readonly MusicService _music;
    private AudioHost? _host;
    private DateTime _lastSave;

    public PodcastService(DataStore store, ToastService toasts, MusicService music, SessionService session)
    {
        _store = store;
        _toasts = toasts;
        _music = music;
        _music.PropertyChanged += OnMusicChanged;
        session.Locking += async () =>
        {
            if (NowPlaying == null) return;
            await SaveAsync();
            Stop();
        };
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEpisode), nameof(Title), nameof(FeedTitle))]
    private FeedItem? _nowPlaying;

    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText), nameof(Progress))]
    private double _position;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText), nameof(Progress))]
    private double _duration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    private double _speed = 1;

    [ObservableProperty] private double _volume = 1;

    /// <summary>Why the last episode couldn't play (empty when fine).</summary>
    [ObservableProperty] private string _lastError = "";

    public bool HasEpisode => NowPlaying != null;
    public string Title => NowPlaying?.Title ?? "";
    public string FeedTitle => NowPlaying?.FeedTitle ?? "";
    public string PositionText => YouTube.FormatTime(Position);
    public string DurationText => YouTube.FormatTime(Duration);
    public double Progress => Duration > 0 ? Math.Clamp(Position / Duration, 0, 1) : 0;
    public string SpeedText => $"{Speed:0.##}×";

    /// <summary>An episode played to the end (it is marked played).</summary>
    public event Action<FeedItem>? Finished;

    public void Attach(AudioHost host)
    {
        _host = host;
        host.TimeChanged += OnTime;
        host.PlayingChanged += playing =>
        {
            IsPlaying = playing;
            if (playing) IsLoading = false;
            else _ = SaveAsync();
        };
        host.Ended += () => _ = EndedAsync();
        host.Failed += message =>
        {
            LastError = message;
            IsPlaying = false;
            IsLoading = false;
            App.Log("Podcast", new InvalidOperationException($"{message} ({NowPlaying?.EnclosureUrl})"));
            _toasts.Warning("Couldn't play this episode — " + message);
        };
    }

    private void OnTime(double time, double duration)
    {
        Position = time;
        if (duration > 0) Duration = duration;
        if (NowPlaying is not { } item) return;
        item.Position = time;
        if (duration > 0) item.Duration = duration;
        if ((DateTime.Now - _lastSave).TotalSeconds > 15) _ = SaveAsync();
    }

    /// <summary>Starts an episode (or pauses / resumes it if it's the one already loaded).</summary>
    public void Play(FeedItem item)
    {
        if (!item.IsAudio) return;
        if (ReferenceEquals(NowPlaying, item) || (item.Id != 0 && NowPlaying?.Id == item.Id))
        {
            TogglePlay();
            return;
        }
        _ = SaveAsync();
        if (_music.IsPlaying) _music.PlayPause();
        NowPlaying = item;
        LastError = "";
        Duration = item.Duration;
        double start = item.Position > 5 && item.ListenProgress < 0.97 ? item.Position : 0;
        Position = start;
        IsLoading = true;
        IsPlaying = true;
        if (_host == null)
        {
            LastError = "The player isn't ready yet";
            IsPlaying = IsLoading = false;
            return;
        }
        _ = _host.PlayAsync(item.EnclosureUrl, start, Speed, Volume);
    }

    [RelayCommand]
    public void TogglePlay()
    {
        if (NowPlaying == null || _host == null) return;
        if (IsPlaying)
        {
            _ = _host.PauseAsync();
            IsPlaying = false;
            _ = SaveAsync();
        }
        else
        {
            if (_music.IsPlaying) _music.PlayPause();
            _ = _host.ResumeAsync();
            IsPlaying = true;
        }
    }

    [RelayCommand]
    private void Skip(string? seconds)
    {
        if (NowPlaying == null || _host == null || !double.TryParse(seconds, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s)) return;
        var target = Math.Clamp(Position + s, 0, Duration > 1 ? Duration - 1 : double.MaxValue);
        _ = _host.SeekAsync(target);
        Position = target;
    }

    public void SeekFraction(double fraction)
    {
        if (NowPlaying == null || _host == null || Duration <= 0) return;
        var target = Math.Clamp(fraction, 0, 1) * Duration;
        _ = _host.SeekAsync(target);
        Position = target;
    }

    [RelayCommand]
    private void CycleSpeed()
    {
        int i = Array.IndexOf(Speeds, Speed);
        Speed = Speeds[(i + 1) % Speeds.Length];
        _ = _host?.SetRateAsync(Speed);
    }

    partial void OnVolumeChanged(double value) => _ = _host?.SetVolumeAsync(Math.Clamp(value, 0, 1));

    [RelayCommand]
    public void Stop()
    {
        _ = SaveAsync();
        _ = _host?.StopAsync();
        NowPlaying = null;
        IsPlaying = IsLoading = false;
        Position = Duration = 0;
    }

    private void OnMusicChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Music started: pause the episode (and the other way round in Play / TogglePlay).
        if (e.PropertyName != nameof(MusicService.IsPlaying) || !_music.IsPlaying || !IsPlaying) return;
        _ = _host?.PauseAsync();
        IsPlaying = false;
        _ = SaveAsync();
    }

    private async Task SaveAsync()
    {
        if (NowPlaying is not { Id: > 0 } item || !Security.Crypto.IsUnlocked) return;
        _lastSave = DateTime.Now;
        try { await _store.UpdateAsync(item); }
        catch (Exception ex) { App.Log("Podcast position", ex); }
    }

    private async Task EndedAsync()
    {
        if (NowPlaying is not { } item) return;
        item.IsPlayed = true;
        item.Position = 0;
        if (item.Id > 0 && Security.Crypto.IsUnlocked) await _store.UpdateAsync(item);
        IsPlaying = false;
        NowPlaying = null;
        Position = Duration = 0;
        Finished?.Invoke(item);
        _toasts.Info($"Finished “{item.Title}”");
    }
}
