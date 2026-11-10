using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Behaviors;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Services;

namespace Satchel.ViewModels;

/// <summary>YouTube companion: follow channels (RSS), save videos, watch in the app with timestamped notes and a notes board.</summary>
public sealed partial class VideosViewModel : PageViewModel, IDisposable
{
    private readonly INavigator _nav;
    private readonly DataStore _store;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly SettingsService _settings;
    private readonly WebService _web;
    private readonly VideoFeedService _feed;
    private List<Video> _all = [];
    private bool _loaded;
    private DateTime _lastProgressSave;

    public VideosViewModel(INavigator nav, DataStore store, DialogService dialogs, ToastService toasts, SettingsService settings, WebService web, VideoFeedService feed)
    {
        _nav = nav;
        _store = store;
        _dialogs = dialogs;
        _toasts = toasts;
        _settings = settings;
        _web = web;
        _feed = feed;
        _feed.Updated += OnFeedUpdated;
    }

    public override string Title => "Videos";
    public ObservableCollection<VideoChannel> Channels { get; } = [];
    public ObservableCollection<Video> Visible { get; } = [];
    public ObservableCollection<VideoNote> Notes { get; } = [];
    public ObservableCollection<Video> UpNext { get; } = [];

    [ObservableProperty] private string _section = "New";
    [ObservableProperty] private VideoChannel? _selectedChannel;
    [ObservableProperty] private Video? _playing;
    [ObservableProperty] private double _currentTime;
    [ObservableProperty] private double _duration;
    [ObservableProperty] private string _newNote = "";
    [ObservableProperty] private bool _isRefreshing;

    public int NewCount => _all.Count(v => v.State == VideoState.New && v.ChannelId != null && (!HideShorts || !v.IsShort));
    public int LaterCount => _all.Count(v => v.IsWatchLater);
    public int SavedCount => _all.Count(v => v.IsSaved);
    public bool IsPlaying => Playing != null;
    public bool IsEmpty => Visible.Count == 0;
    public bool HasChannels => Channels.Count > 0;
    public bool IsOnline => _web.IsAvailable;
    public bool PlayInApp => _settings.Current.VideoPlayInApp;
    public string CurrentTimeText => YouTube.FormatTime(CurrentTime);

    public bool HideShorts
    {
        get => _settings.Current.VideoHideShorts;
        set
        {
            _settings.Current.VideoHideShorts = value;
            OnPropertyChanged();
            ApplyFilter();
            _ = _feed.PublishUnseenAsync();
        }
    }

    public string SectionTitle => Section switch
    {
        "New" => "New uploads",
        "Later" => "Watch later",
        "Saved" => "Saved videos",
        "History" => "Watched",
        "Channel" => SelectedChannel?.Title ?? "Channel",
        _ => "All videos"
    };

    public string StatusText
    {
        get
        {
            if (!_web.Enabled) return "Online features are off (Settings → Sidebar & online)";
            if (!_web.IsAvailable) return "Offline — showing saved videos";
            if (IsRefreshing) return "Checking channels…";
            var last = Channels.Where(c => c.LastCheckedAt.HasValue).Select(c => c.LastCheckedAt!.Value).DefaultIfEmpty().Max();
            return last == default ? "Not checked yet" : $"Checked {Format.Relative(last)}";
        }
    }

    /// <summary>The view seeks the player when a timestamp is clicked.</summary>
    public event Action<double>? SeekRequested;
    /// <summary>The view loads a video into the player (id, start seconds).</summary>
    public event Action<string, double>? PlayRequested;

    partial void OnSectionChanged(string value)
    {
        if (value != "Channel") SelectedChannel = null;
        OnPropertyChanged(nameof(SectionTitle));
        ApplyFilter();
    }

    partial void OnPlayingChanged(Video? value)
    {
        OnPropertyChanged(nameof(IsPlaying));
        RefreshUpNext();
        _ = LoadNotesAsync(value);
    }

    private void RefreshUpNext()
    {
        UpNext.Clear();
        if (Playing == null) return;
        var pool = Visible.Where(v => !ReferenceEquals(v, Playing)).ToList();
        if (pool.Count == 0 && Playing.ChannelId != null)
            pool = _all.Where(v => v.ChannelId == Playing.ChannelId && !ReferenceEquals(v, Playing)).OrderByDescending(v => v.PublishedAt).ToList();
        foreach (var v in pool.Take(12)) UpNext.Add(v);
    }

    partial void OnCurrentTimeChanged(double value) => OnPropertyChanged(nameof(CurrentTimeText));

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_loaded) await LoadAsync();
        switch (parameter)
        {
            case VideoRequest request: await OpenRequestAsync(request); break;
            case int id when _all.FirstOrDefault(v => v.Id == id) is { } video: await Play(video); break;
            case "New": Section = "New"; break;
            case "add": await SaveVideo(); break;
            case "follow": await FollowChannel(); break;
        }
        OnPropertyChanged(nameof(StatusText));
    }

    public override async Task OnNavigatedFromAsync()
    {
        await SaveProgressAsync();
        await base.OnNavigatedFromAsync();
    }

    private async Task LoadAsync()
    {
        var (channels, videos) = await _store.RunAsync(db => (db.VideoChannels.OrderBy(c => c.SortOrder).ToList(), db.Videos.ToList()));
        Channels.Clear();
        foreach (var c in channels) Channels.Add(c);
        _all = videos;
        _loaded = true;
        RefreshCounts();
        if (NewCount == 0 && Section == "New") Section = SavedCount + LaterCount > 0 ? (LaterCount > 0 ? "Later" : "Saved") : "All";
        ApplyFilter();
    }

    private async void OnFeedUpdated()
    {
        if (!_loaded) return;
        var current = Playing?.Id;
        await LoadAsync();
        OnPropertyChanged(nameof(StatusText));
        if (current != null && _all.FirstOrDefault(v => v.Id == current) is { } same) Playing = same;
    }

    private void RefreshCounts()
    {
        foreach (var c in Channels) c.NewCount = _all.Count(v => v.ChannelId == c.Id && v.State == VideoState.New && (!HideShorts || !v.IsShort));
        OnPropertyChanged(nameof(NewCount));
        OnPropertyChanged(nameof(LaterCount));
        OnPropertyChanged(nameof(SavedCount));
        OnPropertyChanged(nameof(HasChannels));
    }

    private void ApplyFilter()
    {
        IEnumerable<Video> items = Section switch
        {
            "New" => _all.Where(v => v.State == VideoState.New && v.ChannelId != null),
            "Later" => _all.Where(v => v.IsWatchLater),
            "Saved" => _all.Where(v => v.IsSaved),
            "History" => _all.Where(v => v.State == VideoState.Watched).OrderByDescending(v => v.WatchedAt),
            "Channel" when SelectedChannel != null => _all.Where(v => v.ChannelId == SelectedChannel.Id),
            _ => _all
        };
        if (HideShorts) items = items.Where(v => !v.IsShort);
        if (Section != "History") items = items.OrderByDescending(v => v.PublishedAt ?? v.CreatedAt);
        Visible.Clear();
        foreach (var v in items.Take(300)) Visible.Add(v);
        OnPropertyChanged(nameof(IsEmpty));
    }

    // ----- navigation -----

    [RelayCommand]
    private void SelectSection(string? section)
    {
        if (section != null) Section = section;
    }

    [RelayCommand]
    private void SelectChannel(VideoChannel? channel)
    {
        if (channel == null) return;
        SelectedChannel = channel;
        if (Section == "Channel") { OnPropertyChanged(nameof(SectionTitle)); ApplyFilter(); }
        else Section = "Channel";
    }

    // ----- channels -----

    [RelayCommand]
    private async Task FollowChannel()
    {
        var input = await _dialogs.PromptAsync("Follow a YouTube channel",
            "Paste the channel's link (youtube.com/@name), its @handle, or a link to any of its videos. New uploads will show up here.", "", "@channel or link", "Follow");
        if (string.IsNullOrWhiteSpace(input)) return;
        IsRefreshing = true;
        OnPropertyChanged(nameof(StatusText));
        try
        {
            var (channel, error) = await _feed.ResolveAsync(input);
            if (channel == null)
            {
                _toasts.Warning(error ?? "Couldn't add that channel");
                return;
            }
            if (Channels.FirstOrDefault(c => c.YoutubeId == channel.YoutubeId) is { } existing)
            {
                _toasts.Info($"You already follow {existing.Title}");
                SelectChannel(existing);
                return;
            }
            channel.SortOrder = Channels.Count;
            await _store.AddAsync(channel);
            Channels.Add(channel);
            var known = _all.Select(v => v.YoutubeId).ToHashSet();
            await _feed.RefreshChannelAsync(channel, known, firstImport: true);
            await LoadAsync();
            if (Channels.FirstOrDefault(c => c.Id == channel.Id) is { } added) SelectChannel(added);
            _toasts.Success($"Following {channel.Title}");
            await _feed.PublishUnseenAsync();
        }
        finally
        {
            IsRefreshing = false;
            OnPropertyChanged(nameof(StatusText));
        }
    }

    [RelayCommand]
    private async Task Unfollow(VideoChannel? channel)
    {
        channel ??= SelectedChannel;
        if (channel == null) return;
        if (!await _dialogs.ConfirmAsync("Unfollow channel?", $"Stop following {channel.Title}? Videos you saved or put on “Watch later” are kept.", "Unfollow", danger: true)) return;
        var keep = _all.Where(v => v.ChannelId == channel.Id && (v.IsSaved || v.IsWatchLater || v.BoardId != null)).ToList();
        var drop = _all.Where(v => v.ChannelId == channel.Id && !keep.Contains(v)).ToList();
        await _store.RunAsync(db =>
        {
            db.RemoveRange(drop);
            db.Remove(channel);
            db.SaveChanges();
        });
        Channels.Remove(channel);
        if (Section == "Channel") Section = "All";
        await LoadAsync();
        await _feed.PublishUnseenAsync();
    }

    [RelayCommand]
    private async Task ToggleNotify(VideoChannel? channel)
    {
        if (channel == null) return;
        channel.Notify = !channel.Notify;
        await _store.UpdateAsync(channel);
        _toasts.Info(channel.Notify ? $"You'll be told about new {channel.Title} videos" : $"No more notifications for {channel.Title}");
    }

    [RelayCommand]
    private async Task RefreshNow()
    {
        if (!_web.IsAvailable) { _toasts.Info(_web.Enabled ? "You're offline" : "Online features are turned off in Settings"); return; }
        if (Channels.Count == 0) { await FollowChannel(); return; }
        IsRefreshing = true;
        OnPropertyChanged(nameof(StatusText));
        var added = await _feed.RefreshAllAsync(notify: false);
        IsRefreshing = false;
        OnPropertyChanged(nameof(StatusText));
        _toasts.Info(added == 0 ? "No new videos" : added == 1 ? "1 new video" : $"{added} new videos");
    }

    [RelayCommand]
    private async Task MarkAllSeen()
    {
        var targets = Visible.Where(v => v.State == VideoState.New).ToList();
        if (targets.Count == 0) return;
        foreach (var v in targets) v.State = VideoState.Seen;
        await _store.UpdateRangeAsync(targets);
        RefreshCounts();
        ApplyFilter();
        await _feed.PublishUnseenAsync();
    }

    // ----- videos -----

    [RelayCommand]
    private async Task SaveVideo()
    {
        var url = await _dialogs.PromptAsync("Save a video", "Paste a YouTube link. You can watch it here and take timestamped notes.", "", "https://youtu.be/…", "Save");
        if (string.IsNullOrWhiteSpace(url)) return;
        var id = YouTube.VideoId(url);
        if (id == null) { _toasts.Warning("That isn't a YouTube video link"); return; }
        var video = await EnsureVideoAsync(id, "", "");
        video.IsSaved = true;
        await _store.UpdateAsync(video);
        Section = "Saved";
        RefreshCounts();
        ApplyFilter();
        _toasts.Success("Video saved");
    }

    private async Task<Video> EnsureVideoAsync(string youtubeId, string title, string author)
    {
        if (_all.FirstOrDefault(v => v.YoutubeId == youtubeId) is { } existing) return existing;
        var video = await _feed.FetchVideoAsync(youtubeId) ?? new Video { YoutubeId = youtubeId };
        if (title.Length > 0 && video.Title == "YouTube video") video.Title = title;
        if (author.Length > 0 && video.Author.Length == 0) video.Author = author;
        await _store.AddAsync(video);
        _all.Add(video);
        return video;
    }

    private async Task OpenRequestAsync(VideoRequest request)
    {
        var video = await EnsureVideoAsync(request.VideoId, request.Title, request.Author);
        if (!video.IsSaved && video.ChannelId == null)
        {
            video.IsSaved = true;
            await _store.UpdateAsync(video);
            RefreshCounts();
        }
        await Play(video, request.Start > 0 ? request.Start : null);
    }

    [RelayCommand]
    private Task Play(Video? video) => Play(video, null);

    private async Task Play(Video? video, double? start)
    {
        if (video == null) return;
        await SaveProgressAsync();
        if (video.State == VideoState.New)
        {
            video.State = VideoState.Seen;
            await _store.UpdateAsync(video);
            RefreshCounts();
            _ = _feed.PublishUnseenAsync();
        }
        Playing = video;
        CurrentTime = start ?? (video.WatchProgress is > 0.02 and < 0.95 ? video.Position : 0);
        Duration = video.Duration;
        if (PlayInApp) PlayRequested?.Invoke(video.YoutubeId, CurrentTime);
        else OpenInBrowser();
    }

    [RelayCommand]
    private async Task ClosePlayer()
    {
        await SaveProgressAsync();
        Playing = null;
        ApplyFilter();
    }

    /// <summary>Playback position from the embedded player (about once a second).</summary>
    public void ReportPlayback(double time, double duration)
    {
        if (Playing is not { } v) return;
        CurrentTime = time;
        if (duration > 0) Duration = duration;
        v.Position = time;
        if (duration > 0) v.Duration = duration;
        if (duration > 30 && time / duration > 0.9 && v.State != VideoState.Watched)
        {
            v.State = VideoState.Watched;
            v.WatchedAt = DateTime.Now;
            if (v.IsWatchLater) v.IsWatchLater = false;
            _ = _store.UpdateAsync(v);
            RefreshCounts();
        }
        else if ((DateTime.Now - _lastProgressSave).TotalSeconds > 15) _ = SaveProgressAsync();
    }

    private async Task SaveProgressAsync()
    {
        if (Playing is not { Id: > 0 } v) return;
        _lastProgressSave = DateTime.Now;
        await _store.UpdateAsync(v);
    }

    [RelayCommand]
    private async Task ToggleWatchLater(Video? video)
    {
        video ??= Playing;
        if (video == null) return;
        video.IsWatchLater = !video.IsWatchLater;
        await _store.UpdateAsync(video);
        RefreshCounts();
        if (Section == "Later") ApplyFilter();
    }

    [RelayCommand]
    private async Task ToggleSaved(Video? video)
    {
        video ??= Playing;
        if (video == null) return;
        video.IsSaved = !video.IsSaved;
        await _store.UpdateAsync(video);
        RefreshCounts();
        if (Section == "Saved") ApplyFilter();
    }

    [RelayCommand]
    private async Task ToggleWatched(Video? video)
    {
        video ??= Playing;
        if (video == null) return;
        bool watched = video.State != VideoState.Watched;
        video.State = watched ? VideoState.Watched : VideoState.Seen;
        video.WatchedAt = watched ? DateTime.Now : null;
        await _store.UpdateAsync(video);
        RefreshCounts();
        if (Section is "New" or "History") ApplyFilter();
        _ = _feed.PublishUnseenAsync();
    }

    [RelayCommand]
    private async Task DeleteVideo(Video? video)
    {
        video ??= Playing;
        if (video == null) return;
        if (video.ChannelId != null && !video.IsSaved && !video.IsWatchLater)
        {
            await ToggleWatched(video);
            return;
        }
        if (ReferenceEquals(Playing, video)) Playing = null;
        _all.Remove(video);
        await _store.DeleteAsync(video);
        RefreshCounts();
        ApplyFilter();
    }

    [RelayCommand]
    private void OpenInBrowser()
    {
        if (Playing is { } v) UiCommands.OpenUrl.Execute(YouTube.WatchUrl(v.YoutubeId, CurrentTime));
    }

    [RelayCommand]
    private void OpenVideoInBrowser(Video? video)
    {
        if (video != null) UiCommands.OpenUrl.Execute(YouTube.WatchUrl(video.YoutubeId));
    }

    // ----- notes -----

    private async Task LoadNotesAsync(Video? video)
    {
        Notes.Clear();
        if (video is not { Id: > 0 }) return;
        var id = video.Id;
        var notes = await _store.ListAsync<VideoNote>(q => q.Where(n => n.VideoId == id).OrderBy(n => n.Seconds));
        if (!ReferenceEquals(Playing, video)) return;
        foreach (var n in notes) Notes.Add(n);
    }

    [RelayCommand]
    private async Task AddNote()
    {
        if (Playing is not { } v || string.IsNullOrWhiteSpace(NewNote)) return;
        var text = NewNote.Trim();
        double seconds = CurrentTime;
        // "1:23 text" sets an explicit timestamp.
        var parts = text.Split(' ', 2);
        if (parts.Length == 2 && parts[0].Contains(':') && YouTube.ParseTime(parts[0]) is { } explicitTime)
        {
            seconds = explicitTime;
            text = parts[1];
        }
        var note = new VideoNote { VideoId = v.Id, Seconds = Math.Floor(seconds), Text = text };
        await _store.AddAsync(note);
        int index = Notes.TakeWhile(n => n.Seconds <= note.Seconds).Count();
        Notes.Insert(index, note);
        NewNote = "";
        await AddToBoardAsync(v, note);
    }

    [RelayCommand]
    private void SeekTo(VideoNote? note)
    {
        if (note == null) return;
        CurrentTime = note.Seconds;
        if (PlayInApp) SeekRequested?.Invoke(note.Seconds);
        else if (Playing != null) UiCommands.OpenUrl.Execute(YouTube.WatchUrl(Playing.YoutubeId, note.Seconds));
    }

    [RelayCommand]
    private async Task DeleteNote(VideoNote? note)
    {
        if (note == null) return;
        Notes.Remove(note);
        await _store.DeleteAsync(note);
    }

    [RelayCommand]
    private void CopyNotes()
    {
        if (Playing is not { } v || Notes.Count == 0) return;
        var lines = Notes.Select(n => $"[{n.TimeText}] {n.Text}  {YouTube.WatchUrl(v.YoutubeId, n.Seconds)}");
        Security.SecureClipboard.Copy($"{v.Title}\n\n{string.Join("\n", lines)}", sensitive: false, "Notes copied");
    }

    /// <summary>Opens (creating if needed) a board for this video, with the video card and its notes as stickies.</summary>
    [RelayCommand]
    private async Task OpenBoard()
    {
        if (Playing is not { } v) return;
        if (v.BoardId is not { } boardId || await _store.FindAsync<Board>(boardId) == null)
        {
            var board = new Board { Name = v.Title.Length > 40 ? v.Title[..40] + "…" : v.Title, Style = "cork", SortOrder = 500 };
            await _store.AddAsync(board);
            var card = new StickyNote
            {
                BoardId = board.Id, Kind = StickyKind.Link, Url = YouTube.WatchUrl(v.YoutubeId), Caption = v.Title, Subtitle = v.Author, Image = v.Thumbnail,
                Color = "white", X = 60, Y = 60, Width = 320, Height = 266, Rotation = -1.5, ZIndex = 1
            };
            await _store.AddAsync(card);
            var colors = new[] { "yellow", "pink", "green", "blue", "orange", "teal" };
            int i = 0;
            // Each note keeps a link with its time: the ▶ chip on the board jumps the video there.
            var stickies = Notes.Select(n => new StickyNote
            {
                BoardId = board.Id, Color = colors[i % colors.Length], Text = $"{n.TimeText} — {n.Text}",
                Content = BoardViewModel.PlainToRich(n.Text), Url = YouTube.WatchUrl(v.YoutubeId, n.Seconds),
                X = 440 + (i % 4) * 250, Y = 70 + (i++ / 4) * 230, Rotation = Math.Round(Random.Shared.NextDouble() * 5 - 2.5, 1), ZIndex = i + 1
            }).ToList();
            if (stickies.Count > 0)
            {
                await _store.AddRangeAsync(stickies);
                await _store.AddRangeAsync(stickies.Select(s => new BoardLink { BoardId = board.Id, FromId = card.Id, ToId = s.Id }));
            }
            v.BoardId = board.Id;
            await _store.UpdateAsync(v);
            boardId = board.Id;
        }
        if ((_nav as ShellViewModel)?.GetPage<BoardViewModel>("Board") is { } boards) await boards.ReloadBoardsAsync(boardId);
        await SaveProgressAsync();
        // The board opens with the video in its study panel, at the current moment.
        _nav.NavigateTo("Board", new BoardStudyRequest(boardId, v.YoutubeId, v.Title, CurrentTime));
    }

    /// <summary>A note taken here also lands on the video's board (if it has one), strung to the video card.</summary>
    private async Task AddToBoardAsync(Video v, VideoNote note)
    {
        if (v.BoardId is not int boardId) return;
        var placed = await _store.RunAsync(db =>
        {
            var stickies = db.Stickies.Where(s => s.BoardId == boardId).ToList();
            if (stickies.Count == 0 && !db.Boards.Any(b => b.Id == boardId)) return false;
            var card = stickies.FirstOrDefault(s => s.Kind == StickyKind.Link && YouTube.VideoId(s.Url) == v.YoutubeId);
            int index = stickies.Count(s => s.Kind == StickyKind.Text && YouTube.VideoId(s.Url) == v.YoutubeId);
            var colors = new[] { "yellow", "pink", "green", "blue", "orange", "teal" };
            var sticky = new StickyNote
            {
                BoardId = boardId, Color = colors[index % colors.Length], Text = $"{note.TimeText} — {note.Text}", Content = BoardViewModel.PlainToRich(note.Text),
                Url = YouTube.WatchUrl(v.YoutubeId, note.Seconds), Width = 220, Height = 170,
                X = card != null ? card.X + card.Width + 60 + (index / 5) * 250 : 440 + (index % 4) * 250,
                Y = card != null ? card.Y + (index % 5) * 215 : 70 + (index / 4) * 230,
                Rotation = Math.Round(Random.Shared.NextDouble() * 5 - 2.5, 1),
                ZIndex = stickies.Count == 0 ? 1 : stickies.Max(s => s.ZIndex) + 1
            };
            db.Stickies.Add(sticky);
            db.SaveChanges();
            if (card != null)
            {
                db.BoardLinks.Add(new BoardLink { BoardId = boardId, FromId = card.Id, ToId = sticky.Id });
                db.SaveChanges();
            }
            return true;
        });
        if (placed) (_nav as ShellViewModel)?.GetPage<BoardViewModel>("Board")?.MarkChanged(boardId);
    }

    public void Dispose() => _feed.Updated -= OnFeedUpdated;
}
