using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Core;
using Satchel.Services;

namespace Satchel.ViewModels;

public sealed class PaletteItem
{
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    public required string Glyph { get; init; }
    public required string Kind { get; init; }
    public string? ColorKey { get; init; }
    public required Func<Task> Run { get; init; }
    public string Keywords { get; init; } = "";
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);
}

/// <summary>Ctrl+K: jump anywhere, run commands, search all content.</summary>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly SearchService _search;
    private readonly SettingsService _settings;
    private readonly SessionService _session;
    private readonly BackupService _backups;
    private readonly ToastService _toasts;
    private readonly MainWindowViewModel _main;
    private readonly DialogService _dialogs;
    private readonly PrivacyService _privacy;
    private readonly MusicService _music;
    private readonly TimerService _timers;
    private readonly DashboardProfileService _dashboards;
    private readonly KeyBindingService _keys;
    private readonly List<PaletteItem> _commands;
    private List<SearchResult> _index = [];

    public CommandPaletteViewModel(ShellViewModel shell, MainWindowViewModel main, SearchService search, SettingsService settings, SessionService session, BackupService backups,
        ToastService toasts, DialogService dialogs, PrivacyService privacy, MusicService music, TimerService timers, DashboardProfileService dashboards,
        KeyBindingService keys)
    {
        _dashboards = dashboards;
        _keys = keys;
        _main = main;
        _dialogs = dialogs;
        _privacy = privacy;
        _music = music;
        _timers = timers;
        _shell = shell;
        _search = search;
        _settings = settings;
        _session = session;
        _backups = backups;
        _toasts = toasts;
        _commands = BuildCommands();
        Refresh();
        _ = LoadIndexAsync();
    }

    public ObservableCollection<PaletteItem> Results { get; } = [];
    public event Action? CloseRequested;

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private PaletteItem? _selected;
    [ObservableProperty] private bool _isIndexing = true;

    public bool IsEmpty => Results.Count == 0;
    public string Hint => IsIndexing ? "Unlocking your content for search…" : "Type to search everything in Satchel — or run a command";

    partial void OnQueryChanged(string value) => Refresh();

    private async Task LoadIndexAsync()
    {
        try
        {
            _index = await _search.BuildIndexAsync();
        }
        catch
        {
            _index = [];
        }
        IsIndexing = false;
        OnPropertyChanged(nameof(Hint));
        if (!string.IsNullOrWhiteSpace(Query)) Refresh();
    }

    private List<PaletteItem> BuildCommands()
    {
        PaletteItem Go(string page, string glyph, string keywords = "") => new()
        {
            Title = $"Go to {page}", Glyph = glyph, Kind = "Page", Keywords = keywords, Subtitle = _keys.Text(KeyBindingService.PageAction(page)) ?? "",
            Run = () => { _shell.NavigateTo(page); return Task.CompletedTask; }
        };
        PaletteItem Nav(string title, string glyph, string page, object param, string keywords = "") => new()
        {
            Title = title, Glyph = glyph, Kind = "Create", Keywords = keywords,
            Run = () => { _shell.NavigateTo(page, param); return Task.CompletedTask; }
        };

        var list = new List<PaletteItem>
        {
            Nav("New note", Glyphs.Notes, "Notes", "new", "add write text"),
            Nav("New checklist", Glyphs.Checklist, "Notes", "new-checklist", "todo list"),
            Nav("New code snippet", Glyphs.Code, "Notes", "new-code", "snippet"),
            Nav("New journal entry", Glyphs.Journal, "Notes", "new-journal", "diary"),
            Nav("New task", Glyphs.Tasks, "Tasks", "focus", "todo add"),
            Nav("New event", Glyphs.Calendar, "Calendar", "new", "meeting appointment"),
            Nav("New vault profile", Glyphs.Shield, "Vault", "new", "password login account"),
            Nav("New contact", Glyphs.AddPerson, "Contacts", "new", "person"),
            Nav("New project", Glyphs.Projects, "Projects", "new", "plan goal milestone"),
            Nav("New Markdown document", Glyphs.Document, "Library", "new", "md write"),
            Nav("Import Markdown files", Glyphs.Import, "Library", "import", "md open"),
            Nav("Add a book", Glyphs.Book, "Reading", "new", "reading list"),
            Nav("Add a game", Glyphs.Game, "Games", "new", "backlog"),
            Nav("New sketch", Glyphs.Pen, "Sketch", "new", "draw doodle"),
            Nav("Add to the board from Satchel", Glyphs.Import, "Board", "insert", "insert board note photo video game book show bookmark pull"),
            Nav("New spreadsheet", Glyphs.Sheet, "Sheets", "new", "new spreadsheet sheet table budget excel calc"),
            Nav("Import a spreadsheet (Excel, CSV)", Glyphs.Import, "Sheets", "import", "import excel xlsx xls csv spreadsheet open"),
            Nav("Import documents into the Library", Glyphs.Import, "Library", "import", "import word docx doc rtf markdown md txt text excel library"),
            Nav("Save a YouTube video", Glyphs.Video, "Videos", "add", "watch later"),
            Nav("Follow a YouTube channel", Glyphs.AddPerson, "Videos", "follow", "subscribe"),
            Nav("Add a bookmark", Glyphs.Bookmark, "Bookmarks", "add", "link url save website"),
            Nav("Add a subscription or bill", Glyphs.Card, "Money", "new", "payment rent netflix spotify"),
            Nav("Add a show or movie", Glyphs.Monitor, "Watching", "new", "series film tv episode"),
            Nav("Add photos", Glyphs.Photo, "Photos", "import", "pictures images screenshots gallery"),
            Nav("Follow a site or podcast", Glyphs.Feed, "Feeds", "add", "rss atom news blog subscribe"),
            Nav("Add music from YouTube", Glyphs.Music, "Music", "add", "song playlist youtube music"),
            Nav("New countdown", Glyphs.Timer, "Clock", "countdown", "days until event birthday"),
            Nav("Study flashcards", Glyphs.Education, "Flashcards", "study", "review cards learn"),
            new() { Title = "Start a timer", Glyph = Glyphs.Timer, Kind = "Tool", Keywords = "countdown pomodoro focus alarm", Subtitle = "Ctrl+Shift+T",
                    Run = () => _main.StartTimerPromptAsync() },
            new() { Title = _timers.StopwatchRunning ? "Stop the stopwatch" : "Start the stopwatch", Glyph = Glyphs.Clock, Kind = "Tool", Keywords = "lap time",
                    Run = () => { _timers.ToggleStopwatch(); return Task.CompletedTask; } },
            new() { Title = "Keyboard shortcuts", Glyph = Glyphs.Help, Kind = "Help", Keywords = "keys cheat sheet hotkeys help", Subtitle = "F1 or Ctrl+/",
                    Run = () => _dialogs.ShowAsync(new Dialogs.ShortcutsDialogViewModel()) },
            new() { Title = _privacy.ShowPrivate ? "Hide private items" : "Show private items", Glyph = Glyphs.Hide, Kind = "Privacy", Keywords = "private secret hidden",
                    Subtitle = _keys.Text(KeyBindingService.PrivacyAction) ?? "", Run = () => _privacy.ToggleVisibleAsync() },
            new() { Title = "Key bindings", Glyph = Glyphs.Keyboard, Kind = "Settings", Keywords = "keys shortcuts hotkeys map tabs keyboard custom",
                    Run = () => { _shell.NavigateTo("Settings", "Bindings"); return Task.CompletedTask; } },
            new() { Title = "Check for updates", Glyph = Glyphs.Sync, Kind = "Settings", Keywords = "update upgrade new version release what's new changelog github",
                    Subtitle = $"Version {AppInfo.Version}", Run = () => { _shell.NavigateTo("Settings", "Updates"); return Task.CompletedTask; } },
            new() { Title = "Password generator", Glyph = Glyphs.Lightning, Kind = "Tool", Keywords = "generate random",
                    Run = () => { _shell.NavigateTo("Vault", "generator"); return Task.CompletedTask; } },
            Go("Dashboard", Glyphs.Home, "home"),
            Go("Notes", Glyphs.Notes),
            Go("Tasks", Glyphs.Tasks),
            Go("Calendar", Glyphs.Calendar),
            Go("Vault", Glyphs.Shield, "passwords"),
            Go("Board", Glyphs.Tiles, "sticky"),
            Go("Contacts", Glyphs.People),
            Go("Projects", Glyphs.Projects, "plans goals"),
            Go("Sketch", Glyphs.Pen, "draw"),
            Go("Library", Glyphs.Library, "markdown documents read word"),
            Go("Sheets", Glyphs.Sheet, "spreadsheets excel table budget formulas calc csv"),
            Go("Reading", Glyphs.Book, "books"),
            Go("Games", Glyphs.Game, "backlog play"),
            Go("Videos", Glyphs.Video, "youtube channels"),
            Go("Today", Glyphs.CalendarDay, "diary day weather"),
            Go("Money", Glyphs.Card, "subscriptions bills payments"),
            Go("Gamble", Glyphs.Market, "stocks trading invest shares bets betting"),
            Go("Bookmarks", Glyphs.Bookmark, "links websites read later"),
            Go("Photos", Glyphs.Photo, "pictures gallery screenshots albums"),
            Go("Watching", Glyphs.Monitor, "shows movies tv series"),
            Go("Collections", Glyphs.Grid9, "trackers lists inventory"),
            Go("Music", Glyphs.Music, "songs playlists youtube"),
            Go("Feeds", Glyphs.Feed, "rss news podcasts"),
            Go("Flashcards", Glyphs.Education, "cards decks study learn"),
            Go("Clock", Glyphs.Timer, "timer stopwatch world clocks countdowns"),
            Go("Insights", Glyphs.Pulse, "charts stats statistics activity"),
            Go("Settings", Glyphs.Settings, "preferences options"),
            new() { Title = "Lock now", Glyph = Glyphs.Lock, Kind = "Security", Keywords = "lock sign out", Run = () => _session.LockAsync() },
            new() { Title = "Back up now", Glyph = Glyphs.Save, Kind = "Data", Keywords = "backup save copy",
                    Run = async () => { await _backups.CreateBackupAsync(); _settings.Current.LastBackupAt = DateTime.Now; _toasts.Success("Backup created"); } },
            new() { Title = "Toggle compact sidebar", Glyph = Glyphs.Menu, Kind = "View", Keywords = "collapse expand",
                    Run = () => { _settings.Current.SidebarCollapsed = !_settings.Current.SidebarCollapsed; return Task.CompletedTask; } },
        };
        if (_music.HasTrack)
        {
            list.Add(new PaletteItem
            {
                Title = _music.IsPlaying ? "Pause music" : "Play music", Subtitle = _music.Title, Glyph = Glyphs.Music, Kind = "Music", Keywords = "song resume",
                Run = () => { _music.PlayPause(); return Task.CompletedTask; }
            });
            list.Add(new PaletteItem
            {
                Title = "Next song", Glyph = Glyphs.Music, Kind = "Music", Keywords = "skip track",
                Run = () => { _music.Next(); return Task.CompletedTask; }
            });
        }
        var profiles = _dashboards.All;
        if (profiles.Count > 1)
            for (int i = 0; i < profiles.Count; i++)
            {
                var index = i;
                var p = profiles[i];
                list.Add(new PaletteItem
                {
                    Title = $"Dashboard: {p.Name}", Glyph = string.IsNullOrEmpty(p.Icon) ? Glyphs.Home : p.Icon, Kind = "Dashboard",
                    Subtitle = i < 9 ? $"Alt+{i + 1}" : "", Keywords = "profile layout setup widgets switch",
                    Run = () => { _shell.OpenDashboardProfileAt(index); return Task.CompletedTask; }
                });
            }
        // Pages hidden in Settings stay out of "Go to" (their data is kept).
        list.RemoveAll(item => item.Kind == "Page" && item.Title.StartsWith("Go to ", StringComparison.Ordinal) && item.Title != "Go to Settings"
                               && !_shell.IsVisible(item.Title[6..]));
        foreach (var t in ThemeService.Themes)
            list.Add(new PaletteItem
            {
                Title = $"Theme: {t.Name}", Glyph = t.IsDark ? Glyphs.Moon : Glyphs.Sun, Kind = "Theme", Keywords = "appearance color dark light",
                Run = () => { _settings.Current.Theme = t.Key; return Task.CompletedTask; }
            });
        return list;
    }

    private void Refresh()
    {
        Results.Clear();
        var q = Query.Trim();
        if (q.Length == 0)
        {
            foreach (var c in _commands.Where(c => c.Kind is "Create" or "Tool" or "Security").Take(10)) Results.Add(c);
        }
        else
        {
            var terms = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var c in _commands.Where(c => terms.All(t => c.Title.Contains(t, StringComparison.OrdinalIgnoreCase) || c.Keywords.Contains(t, StringComparison.OrdinalIgnoreCase))).Take(6))
                Results.Add(c);
            foreach (var r in SearchService.Filter(_index, q, 30))
            {
                var result = r;
                Results.Add(new PaletteItem
                {
                    Title = r.Title,
                    Subtitle = r.Subtitle,
                    Glyph = string.IsNullOrEmpty(r.Glyph) ? Glyphs.Document : r.Glyph,
                    Kind = r.Kind,
                    ColorKey = r.ColorKey,
                    Run = () => { _shell.NavigateTo(result.Page, result.Parameter); return Task.CompletedTask; }
                });
            }
        }
        Selected = Results.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void Move(int delta)
    {
        if (Results.Count == 0) return;
        int i = Selected == null ? -1 : Results.IndexOf(Selected);
        i = (i + delta + Results.Count) % Results.Count;
        Selected = Results[i];
    }

    [RelayCommand]
    private async Task Execute(PaletteItem? item)
    {
        item ??= Selected;
        if (item == null) return;
        CloseRequested?.Invoke();
        await item.Run();
    }

    [RelayCommand]
    public void Close() => CloseRequested?.Invoke();
}
