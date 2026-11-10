using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Behaviors;
using Satchel.Core;
using Satchel.Models;
using Satchel.Services;
using Satchel.ViewModels.Dialogs;

namespace Satchel.ViewModels;

/// <summary>Board extras: live cards (tasks, notes, projects, countdowns, clocks, weather, music) and the video study panel.</summary>
public sealed partial class BoardViewModel
{
    private DispatcherTimer? _liveTimer;
    private int _liveTicks;

    // ===================== live cards =====================

    private void EnsureLiveTimer()
    {
        bool any = Stickies.Any(s => s.IsLive);
        if (!any)
        {
            _liveTimer?.Stop();
            return;
        }
        if (_liveTimer == null)
        {
            _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _liveTimer.Tick += async (_, _) =>
            {
                foreach (var s in Stickies) if (s.Live is LiveClockData clock) clock.Tick();
                if (++_liveTicks % 60 == 0) await RefreshLiveAsync();
            };
        }
        _liveTimer.Start();
    }

    /// <summary>Loads fresh data into every live card on the current board.</summary>
    public async Task RefreshLiveAsync()
    {
        var lives = Stickies.Where(s => s.IsLive).ToList();
        EnsureLiveTimer();
        if (lives.Count == 0) return;
        var kinds = lives.Select(s => s.LiveKind).ToHashSet();
        int Id(StickyNote s) => int.TryParse(s.LiveRef.Split(':').Last(), out var v) ? v : 0;
        List<int> IdsOf(string kind) => lives.Where(s => s.LiveKind == kind).Select(Id).ToList();
        var noteIds = IdsOf("note");
        var gameIds = IdsOf("game");
        var bookIds = IdsOf("book");
        var showIds = IdsOf("show");
        var data = await _store.RunAsync(db => (
            Tasks: kinds.Contains("tasks") || kinds.Contains("project") ? db.Tasks.ToList() : [],
            Lists: kinds.Contains("tasks") ? db.TaskLists.ToList() : [],
            Notes: noteIds.Count > 0 ? db.Notes.Where(n => noteIds.Contains(n.Id)).ToList() : [],
            Projects: kinds.Contains("project") ? db.Projects.ToList() : [],
            Milestones: kinds.Contains("project") ? db.Milestones.ToList() : [],
            Countdowns: kinds.Contains("countdown") ? db.Countdowns.ToList() : [],
            Games: gameIds.Count > 0 ? db.Games.Where(g => gameIds.Contains(g.Id)).ToList() : [],
            Books: bookIds.Count > 0 ? db.Books.Where(b => bookIds.Contains(b.Id)).ToList() : [],
            Shows: showIds.Count > 0 ? db.WatchItems.Where(w => showIds.Contains(w.Id)).ToList() : []));
        WeatherReport? weather = kinds.Contains("weather") ? await _weather.GetAsync() : null;

        foreach (var s in lives)
        {
            switch (s.LiveKind)
            {
                case "tasks":
                {
                    var d = s.Live as LiveTasksData ?? new LiveTasksData();
                    var open = data.Tasks.Where(t => !t.IsDone).ToList();
                    if (s.LiveRef == "today")
                    {
                        d.Title = "Today";
                        open = open.Where(t => t.DueDate.HasValue && t.DueDate.Value.Date <= DateTime.Today).OrderBy(t => t.DueDate).ToList();
                    }
                    else
                    {
                        var listId = Id(s);
                        d.Title = data.Lists.FirstOrDefault(l => l.Id == listId)?.Name ?? "Tasks";
                        open = open.Where(t => t.ListId == listId).OrderBy(t => t.SortOrder).ToList();
                    }
                    int max = Math.Max(2, (int)((s.Height - 70) / 30));
                    d.Items.Clear();
                    foreach (var t in open.Take(max)) d.Items.Add(t);
                    d.More = Math.Max(0, open.Count - max);
                    d.Subtitle = open.Count == 0 ? "All done 🎉" : $"{open.Count} open";
                    s.Live = d;
                    break;
                }
                case "note":
                {
                    var d = s.Live as LiveNoteData ?? new LiveNoteData();
                    var note = data.Notes.FirstOrDefault(n => n.Id == Id(s));
                    d.Note = note;
                    d.Title = note?.DisplayTitle ?? "Note not found";
                    d.Items.Clear();
                    if (note?.IsItemList == true) foreach (var i in note.Items) d.Items.Add(i);
                    d.Subtitle = note == null ? "It was deleted or is private" : note.IsItemList ? $"{note.CheckedCount} of {note.ItemCount} done" : note.UpdatedText;
                    s.Live = d;
                    break;
                }
                case "project":
                {
                    var d = s.Live as LiveProjectData ?? new LiveProjectData();
                    var p = data.Projects.FirstOrDefault(x => x.Id == Id(s));
                    d.Project = p;
                    d.Title = p?.Name ?? "Project not found";
                    if (p != null)
                    {
                        var tasks = p.TaskListId == null ? [] : data.Tasks.Where(t => t.ListId == p.TaskListId).ToList();
                        var miles = data.Milestones.Where(m => m.ProjectId == p.Id).OrderBy(m => m.SortOrder).ToList();
                        int total = tasks.Count + miles.Count, done = tasks.Count(t => t.IsDone) + miles.Count(m => m.IsDone);
                        d.Progress = p.Status == ProjectStatus.Done ? 1 : total == 0 ? 0 : (double)done / total;
                        var next = miles.FirstOrDefault(m => !m.IsDone);
                        d.Subtitle = $"{p.Status} · {Math.Round(d.Progress * 100)}%";
                        d.Detail = next != null ? $"Next: {next.Title}" + (next.DueDate is { } due ? $" · {Format.FriendlyDay(due)}" : "") : p.DueText;
                    }
                    s.Live = d;
                    break;
                }
                case "countdown":
                {
                    var d = s.Live as LiveCountdownData ?? new LiveCountdownData();
                    d.Countdown = data.Countdowns.FirstOrDefault(c => c.Id == Id(s));
                    d.Countdown?.Refresh();
                    d.Title = d.Countdown?.Title ?? "Countdown not found";
                    d.Subtitle = d.Countdown?.DateText ?? "";
                    s.Live = d;
                    break;
                }
                case "clock":
                {
                    if (s.Live is not LiveClockData)
                    {
                        TimeZoneInfo zone;
                        try { zone = s.LiveRef.Length == 0 ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(s.LiveRef); }
                        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { zone = TimeZoneInfo.Local; }
                        var clock = new LiveClockData(zone) { Title = s.LiveRef.Length == 0 ? "Here" : ClockViewModel.CityName(zone) };
                        clock.Tick();
                        s.Live = clock;
                    }
                    break;
                }
                case "weather":
                {
                    var d = s.Live as LiveWeatherData ?? new LiveWeatherData();
                    d.Report = weather;
                    d.Unit = _weather.Unit;
                    d.Title = weather?.Location.Name ?? "Weather";
                    d.Subtitle = weather == null ? "Set your city on the dashboard" : weather.Description;
                    s.Live = d;
                    break;
                }
                case "music":
                    if (s.Live is not LiveMusicData) s.Live = new LiveMusicData(_music) { Title = "Music", Subtitle = "Now playing" };
                    break;
                case "game":
                {
                    var d = s.Live as LiveGameData ?? new LiveGameData();
                    var g = data.Games.FirstOrDefault(x => x.Id == Id(s));
                    d.Game = g;
                    d.Title = g?.Title ?? "Game not found";
                    d.Subtitle = g == null ? "It was deleted or is private" : string.Join(" · ", new[] { g.StatusText, g.Platform }.Where(x => x.Length > 0));
                    d.Detail = g == null ? "" : g.Hours > 0
                        ? $"{g.HoursText} played" + (g.LastPlayedAt is { } last ? $" · last session {Format.Relative(last).ToLowerInvariant()}" : "")
                        : "Not played yet";
                    s.Live = d;
                    break;
                }
                case "book":
                {
                    var d = s.Live as LiveBookData ?? new LiveBookData();
                    var b = data.Books.FirstOrDefault(x => x.Id == Id(s));
                    d.Book = b;
                    d.Title = b?.Title ?? "Book not found";
                    d.Subtitle = b == null ? "It was deleted or is private" : b.Author;
                    d.Detail = b?.ProgressText ?? "";
                    s.Live = d;
                    break;
                }
                case "show":
                {
                    var d = s.Live as LiveShowData ?? new LiveShowData();
                    var w = data.Shows.FirstOrDefault(x => x.Id == Id(s));
                    d.Item = w;
                    d.Title = w?.Title ?? "Not found";
                    d.Subtitle = w == null ? "It was deleted or is private" : w.Subtitle;
                    d.Detail = w == null ? "" : w.IsShow && w.NextEpisode is { } next ? $"Next: {next.Label}" : w.ProgressText;
                    s.Live = d;
                    break;
                }
            }
        }
    }

    [RelayCommand]
    private async Task AddLiveCard()
    {
        if (SelectedBoard == null) return;
        var kinds = new List<PickOption>
        {
            new("Tasks", "Today's tasks or a task list — tick them off right here", Glyphs.Tasks, "tasks", "teal"),
            new("Note", "A note or checklist that stays in sync", Glyphs.Notes, "note", "amber"),
            new("Project", "Progress and the next milestone", Glyphs.Projects, "project", "green"),
            new("Countdown", "Days left until a date", Glyphs.Flag, "countdown", "rose"),
            new("Clock", "The time here or in another city", Glyphs.Clock, "clock", "sky"),
            new("Weather", "Current weather for your city", Glyphs.Sun, "weather", "amber"),
            new("Music", "What's playing, with controls", Glyphs.Music, "music", "teal"),
            new("Game", "Cover, status and hours played", Glyphs.Game, "game", "sky"),
            new("Book", "How far you've read", Glyphs.Book, "book", "teal"),
            new("Show or movie", "The next episode to watch", Glyphs.Monitor, "show", "rose"),
        };
        if (await _dialogs.ShowAsync(new PickDialogViewModel("Add a live card", "Live cards show information from the rest of Satchel and update by themselves.", kinds))
            is not PickOption { Value: string kind }) return;

        LiveTarget? target = null;
        switch (kind)
        {
            case "tasks":
            {
                var lists = await _store.ListAsync<TaskList>(q => q.OrderBy(l => l.SortOrder));
                var options = new List<PickOption> { new("Today", "Due today and overdue", Glyphs.CalendarDay, "today", "teal") };
                options.AddRange(lists.Select(l => new PickOption(l.Name, "Task list", Glyphs.List, $"list:{l.Id}", l.Color)));
                if (await _dialogs.ShowAsync(new PickDialogViewModel("Which tasks?", null, options)) is PickOption { Value: string r })
                    target = new LiveTarget("tasks", r, "", 260, 300);
                break;
            }
            case "note":
            {
                var notes = await _store.ListAsync<Note>(q => q.Where(n => !n.IsTrashed && !n.IsArchived).OrderByDescending(n => n.UpdatedAt));
                var options = notes.OrderByDescending(n => n.IsItemList).ThenByDescending(n => n.UpdatedAt)
                    .Select(n => new PickOption(n.DisplayTitle, n.IsItemList ? $"{n.TypeName} · {n.ProgressText}" : n.TypeName, n.TypeGlyph, $"{n.Id}", "amber")).ToList();
                if (options.Count == 0) { _toasts.Info("Write a note first"); return; }
                if (await _dialogs.ShowAsync(new PickDialogViewModel("Which note?", null, options, "Search notes")) is PickOption { Value: string r })
                    target = new LiveTarget("note", r, "", 260, 280);
                break;
            }
            case "project":
            {
                var projects = await _store.ListAsync<Project>(q => q.OrderBy(p => p.SortOrder));
                if (projects.Count == 0) { _toasts.Info("Create a project first"); return; }
                if (await _dialogs.ShowAsync(new PickDialogViewModel("Which project?", null,
                        projects.Select(p => new PickOption(p.Name, p.Status.ToString(), Glyphs.Projects, $"{p.Id}", p.Color)))) is PickOption { Value: string r })
                    target = new LiveTarget("project", r, "", 280, 170);
                break;
            }
            case "countdown":
            {
                var items = await _store.ListAsync<Countdown>();
                if (items.Count == 0) { _toasts.Info("Create a countdown on the Clock page first"); _nav.NavigateTo("Clock", "countdown"); return; }
                if (await _dialogs.ShowAsync(new PickDialogViewModel("Which countdown?", null,
                        items.Select(c => new PickOption(c.Title, c.DateText, Glyphs.Flag, $"{c.Id}", c.Color)))) is PickOption { Value: string r })
                    target = new LiveTarget("countdown", r, "", 220, 170);
                break;
            }
            case "clock":
            {
                var options = new List<PickOption> { new("Here", TimeZoneInfo.Local.DisplayName, Glyphs.Home, "", "sky") };
                options.AddRange(TimeZoneInfo.GetSystemTimeZones().Select(z => new PickOption(ClockViewModel.CityName(z), z.DisplayName, Glyphs.World, z.Id, "sky")));
                if (await _dialogs.ShowAsync(new PickDialogViewModel("Which clock?", null, options, "City or zone")) is PickOption { Value: string r })
                    target = new LiveTarget("clock", r, "", 200, 230);
                break;
            }
            case "weather": target = new LiveTarget("weather", "", "", 240, 180); break;
            case "music": target = new LiveTarget("music", "", "", 280, 120); break;
            case "game" or "book" or "show":
            {
                var options = await _store.RunAsync(db => kind switch
                {
                    "game" => db.Games.OrderBy(g => g.SortOrder).Select(g => new { g.Id, g.Title, Sub = g.Platform, g.Color }).AsEnumerable()
                        .Select(g => new PickOption(g.Title, g.Sub, Glyphs.Game, $"{g.Id}", g.Color)).ToList(),
                    "book" => db.Books.OrderBy(b => b.SortOrder).Select(b => new { b.Id, b.Title, Sub = b.Author, b.Color }).AsEnumerable()
                        .Select(b => new PickOption(b.Title, b.Sub, Glyphs.Book, $"{b.Id}", b.Color)).ToList(),
                    _ => db.WatchItems.OrderBy(w => w.SortOrder).Select(w => new { w.Id, w.Title, w.Year, w.Color }).AsEnumerable()
                        .Select(w => new PickOption(w.Title, w.Year > 0 ? $"{w.Year}" : "", Glyphs.Monitor, $"{w.Id}", w.Color)).ToList()
                });
                var noun = kind switch { "game" => "game", "book" => "book", _ => "show or movie" };
                if (options.Count == 0) { _toasts.Info($"Add a {noun} first"); return; }
                if (await _dialogs.ShowAsync(new PickDialogViewModel($"Which {noun}?", null, options, "Search")) is PickOption { Value: string r })
                    target = new LiveTarget(kind, r, "", 300, 150);
                break;
            }
        }
        if (target == null) return;
        await AddAsync(ViewCenter, StickyKind.Live, s =>
        {
            s.LiveKind = target.Kind;
            s.LiveRef = target.Ref;
            s.Color = "white";
            s.Width = target.Width;
            s.Height = target.Height;
        });
        await RefreshLiveAsync();
    }

    [RelayCommand]
    private async Task ToggleLiveTask(TaskItem? task)
    {
        if (task == null) return;
        task.IsDone = !task.IsDone;
        await _store.UpdateAsync(task);
        if (task.IsDone && await TaskRepeat.OnCompletedAsync(_store, task) is { } next)
            _toasts.Show($"Repeats — next on {Format.FriendlyDay(next.DueDate!.Value)}", ToastKind.Success, durationMs: 2400);
        await Task.Delay(Anim.Enabled ? 420 : 0);
        await RefreshLiveAsync();
    }

    [RelayCommand]
    private async Task ToggleLiveItem(ChecklistItem? item)
    {
        if (item == null) return;
        var data = Stickies.Select(s => s.Live).OfType<LiveNoteData>().FirstOrDefault(d => d.Items.Contains(item));
        if (data?.Note is not { } note) return;
        item.IsChecked = !item.IsChecked;
        note.Content = Note.SerializeItems(data.Items);
        note.Touch();
        await _store.UpdateAsync(note);
        data.Subtitle = $"{note.CheckedCount} of {note.ItemCount} done";
    }

    [RelayCommand]
    private void OpenLive(StickyNote? sticky)
    {
        if (sticky == null) return;
        int.TryParse(sticky.LiveRef.Split(':').Last(), out var id);
        switch (sticky.LiveKind)
        {
            case "tasks": _nav.NavigateTo("Tasks", sticky.LiveRef.StartsWith("list:") ? $"list:{id}" : null); break;
            case "note": _nav.NavigateTo("Notes", id); break;
            case "project": _nav.NavigateTo("Projects", id); break;
            case "countdown" or "clock": _nav.NavigateTo("Clock"); break;
            case "weather": _nav.NavigateTo("Dashboard"); break;
            case "music": _nav.NavigateTo("Music"); break;
            case "game": _nav.NavigateTo("Games", id); break;
            case "book": _nav.NavigateTo("Reading", id); break;
            case "show": _nav.NavigateTo("Watching", id); break;
        }
    }

    // ===================== video study panel =====================

    [ObservableProperty] private bool _isStudyOpen;
    [ObservableProperty] private string? _studyVideoId;
    [ObservableProperty] private string _studyTitle = "";
    [ObservableProperty] private double _studyTime;
    [ObservableProperty] private string _studyNote = "";

    public string StudyTimeText => YouTube.FormatTime(StudyTime);
    public bool HasVideoCards => Stickies.Any(s => s.IsVideo);

    /// <summary>The view loads a video into the study player (id, start seconds).</summary>
    public event Action<string, double>? StudyLoadRequested;
    public event Action<double>? StudySeekRequested;

    partial void OnStudyTimeChanged(double value) => OnPropertyChanged(nameof(StudyTimeText));

    public void ReportStudyTime(double seconds) => StudyTime = seconds;

    private void OpenStudy(string videoId, string title, double start)
    {
        StudyTitle = title;
        IsStudyOpen = true;
        if (StudyVideoId == videoId)
        {
            StudySeekRequested?.Invoke(start);
            return;
        }
        StudyVideoId = videoId;
        StudyTime = start;
        StudyLoadRequested?.Invoke(videoId, start);
    }

    /// <summary>"Study" toggle: opens the first video on the board next to the canvas.</summary>
    [RelayCommand]
    private void ToggleStudy()
    {
        if (IsStudyOpen)
        {
            IsStudyOpen = false;
            return;
        }
        var card = Stickies.FirstOrDefault(s => s.IsVideo && s.VideoId == StudyVideoId) ?? Stickies.FirstOrDefault(s => s.IsVideo);
        if (card?.VideoId is not { } id)
        {
            _toasts.Info("Add a YouTube link to this board first (Add ▾ → Link or YouTube video)");
            return;
        }
        OpenStudy(id, card.Caption, YouTube.StartSeconds(card.Url));
    }

    [RelayCommand]
    private void StudyVideo(StickyNote? card)
    {
        if (card?.VideoId is { } id) OpenStudy(id, card.Caption, YouTube.StartSeconds(card.Url));
    }

    /// <summary>The ▶ 1:23 chip on a note: jump the study player (or open it) at that moment.</summary>
    [RelayCommand]
    private void PlayTimeLink(StickyNote? note)
    {
        if (note?.TimeVideoId is not { } id) return;
        if (!_settings.Current.VideoPlayInApp)
        {
            UiCommands.OpenUrl.Execute(note.Url);
            return;
        }
        var card = Stickies.FirstOrDefault(s => s.VideoId == id);
        OpenStudy(id, card?.Caption ?? StudyTitle, note.TimeSeconds);
    }

    [RelayCommand]
    private void CloseStudy() => IsStudyOpen = false;

    /// <summary>Adds a note at the player's current time: a sticky next to the video card, strung to it, and a timestamped note in Videos.</summary>
    [RelayCommand]
    private async Task AddStudyNote()
    {
        if (SelectedBoard == null || StudyVideoId is not { } id) return;
        var text = StudyNote.Trim();
        if (text.Length == 0) return;
        double seconds = Math.Floor(StudyTime);
        var parts = text.Split(' ', 2);
        if (parts.Length == 2 && parts[0].Contains(':') && YouTube.ParseTime(parts[0]) is { } explicitTime)
        {
            seconds = explicitTime;
            text = parts[1];
        }
        var time = YouTube.FormatTime(seconds);
        var card = Stickies.FirstOrDefault(s => s.VideoId == id);
        int index = Stickies.Count(s => s.TimeVideoId == id);
        var colors = new[] { "yellow", "pink", "green", "blue", "orange", "teal" };
        Point at = card != null
            ? new Point(card.X + card.Width + 170 + (index / 5) * 250, card.Y + 60 + (index % 5) * 215)
            : ViewCenter;
        var sticky = await AddAsync(at, StickyKind.Text, s =>
        {
            s.Color = colors[index % colors.Length];
            s.Text = $"{time} — {text}";
            s.Content = PlainToRich(text);
            s.Url = YouTube.WatchUrl(id, seconds);
            s.Width = 220;
            s.Height = 170;
        });
        if (sticky != null && card != null) await ConnectAsync(card, sticky);
        StudyNote = "";
        // Keep the Videos page's notes in sync when the video is saved there.
        // (YoutubeId is encrypted, so it's matched in memory.)
        var videos = await _store.RunAsync(db => db.Videos.Select(v => new { v.Id, v.YoutubeId }).ToList());
        if (videos.FirstOrDefault(v => v.YoutubeId == id) is { } video)
            await _store.AddAsync(new VideoNote { VideoId = video.Id, Seconds = seconds, Text = text });
    }
}
