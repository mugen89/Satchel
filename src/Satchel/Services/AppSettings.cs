using CommunityToolkit.Mvvm.ComponentModel;
using Satchel.Security;

namespace Satchel.Services;

public sealed class WidgetState
{
    public string Key { get; set; } = "";
    public bool Visible { get; set; } = true;
    /// <summary>Width in dashboard columns.</summary>
    public int Span { get; set; } = 1;
    /// <summary>Fixed height in pixels; 0 = as tall as the content.</summary>
    public double Height { get; set; }
}

/// <summary>A named dashboard setup (which widgets, their order and size). With an icon it gets a shortcut in the top bar.</summary>
public sealed class DashboardProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Main";
    /// <summary>Icon glyph, or empty for none.</summary>
    public string Icon { get; set; } = "";
    public List<WidgetState> Layout { get; set; } = [];
}

/// <summary>A clock on the dashboard / Clock page.</summary>
public sealed class WorldClock
{
    public string ZoneId { get; set; } = "";
    public string Label { get; set; } = "";
}

/// <summary>A countdown timer (kept in settings so it survives restarts).</summary>
public sealed class TimerState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Label { get; set; } = "";
    public double Seconds { get; set; }
    /// <summary>When it rings (while running).</summary>
    public DateTime? EndsAt { get; set; }
    /// <summary>Seconds left while paused.</summary>
    public double Remaining { get; set; }
}

public sealed class StopwatchState
{
    public DateTime? StartedAt { get; set; }
    public double ElapsedBefore { get; set; }
    public List<double> Laps { get; set; } = [];
}

public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>User preferences. Stored as plain JSON (they're needed before unlock, e.g. the theme).</summary>
public partial class AppSettings : ObservableObject
{
    // Appearance
    [ObservableProperty] private string _theme = "daylight";
    [ObservableProperty] private string? _accent;
    [ObservableProperty] private string _fontFamily = "Segoe UI Variable Text, Segoe UI";
    [ObservableProperty] private double _uiScale = 1.0;
    [ObservableProperty] private double _cornerRadius = 10;
    [ObservableProperty] private bool _animationsEnabled = true;
    [ObservableProperty] private bool _ambientEffects = true;
    [ObservableProperty] private bool _sidebarCollapsed;
    [ObservableProperty] private bool _cardShadows = true;

    // General
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _startPage = "Dashboard";
    [ObservableProperty] private bool _weekStartsMonday = true;
    [ObservableProperty] private bool _use24HourClock = true;
    [ObservableProperty] private string _dateFormat = "";
    /// <summary>"en-GB", "en-US" or "system" — language used for day/month names and date order.</summary>
    [ObservableProperty] private string _culture = "en-GB";
    [ObservableProperty] private bool _confirmDeletes = true;
    [ObservableProperty] private bool _showTasksInCalendar = true;
    [ObservableProperty] private bool _showBirthdaysInCalendar = true;
    [ObservableProperty] private bool _reminderSound = true;

    // Security
    [ObservableProperty] private int _autoLockMinutes = 10;
    [ObservableProperty] private int _clipboardClearSeconds = 30;
    [ObservableProperty] private bool _lockOnMinimize;
    [ObservableProperty] private bool _lockOnSessionLock = true;

    // Data
    [ObservableProperty] private bool _autoBackup = true;
    [ObservableProperty] private int _backupKeepCount = 10;
    [ObservableProperty] private DateTime? _lastBackupAt;

    // Privacy
    /// <summary>Show items marked private (they're hidden from every list, search and widget otherwise).</summary>
    [ObservableProperty] private bool _showPrivate;
    /// <summary>Hide private items again every time Satchel is unlocked.</summary>
    [ObservableProperty] private bool _hidePrivateOnUnlock = true;
    /// <summary>Ask for the master password before showing private items.</summary>
    [ObservableProperty] private bool _privateNeedsPassword;

    // Navigation & network
    /// <summary>Comma-separated page keys hidden from the sidebar.</summary>
    [ObservableProperty] private string _hiddenTabs = "";
    /// <summary>Comma-separated page keys in the order chosen in Settings (empty = default order).</summary>
    [ObservableProperty] private string _tabOrder = "";
    [ObservableProperty] private bool _showSectionHeaders = true;
    /// <summary>Comma-separated sidebar sections folded away (their tabs are hidden until the header is clicked again).</summary>
    [ObservableProperty] private string _collapsedSections = "";
    /// <summary>Master switch for everything that touches the internet (video feeds, link previews, book/game lookup).</summary>
    [ObservableProperty] private bool _onlineFeatures = true;

    // Updates (GitHub releases)
    /// <summary>"owner/name" of the GitHub repository with Satchel's releases; empty = the one built into the app.</summary>
    [ObservableProperty] private string _updateRepository = "";
    /// <summary>Look for a new version once a day, after unlocking (needs online features).</summary>
    [ObservableProperty] private bool _checkUpdatesOnStart = true;
    [ObservableProperty] private DateTime? _lastUpdateCheck;

    // Videos
    [ObservableProperty] private int _videoCheckMinutes = 60;
    [ObservableProperty] private bool _videoHideShorts;
    [ObservableProperty] private bool _videoNotify = true;
    [ObservableProperty] private bool _videoPlayInApp = true;
    /// <summary>Block ads and trackers in the in-app YouTube players (videos, music, boards).</summary>
    [ObservableProperty] private bool _blockVideoAds = true;

    // Music
    [ObservableProperty] private double _musicVolume = 70;
    [ObservableProperty] private bool _musicShuffle;
    /// <summary>"off", "all" or "one".</summary>
    [ObservableProperty] private string _musicRepeat = "all";
    [ObservableProperty] private bool _musicShowVideo;

    // Feeds
    [ObservableProperty] private int _feedCheckMinutes = 60;
    [ObservableProperty] private bool _feedNotify = true;

    // Clock, weather & money
    [ObservableProperty] private bool _weatherFahrenheit;
    [ObservableProperty] private bool _timerSound = true;
    [ObservableProperty] private string _currency = "EUR";
    public List<WorldClock> WorldClocks { get; set; } = [];
    public List<TimerState> Timers { get; set; } = [];
    /// <summary>Payments already announced, as "subscriptionId:yyyy-MM-dd" (kept here so editing a subscription can't reset it).</summary>
    public List<string> RemindedPayments { get; set; } = [];
    public StopwatchState Stopwatch { get; set; } = new();

    // Reading
    [ObservableProperty] private int _readingGoal = 12;

    // Library reader
    [ObservableProperty] private string _readerTheme = "app";
    [ObservableProperty] private string _readerFont = "serif";
    [ObservableProperty] private double _readerFontSize = 17;
    [ObservableProperty] private double _readerLineHeight = 1.65;
    [ObservableProperty] private double _readerWidth = 720;
    [ObservableProperty] private bool _readerJustify;
    [ObservableProperty] private bool _readerShowToc = true;
    /// <summary>"scroll" (continuous) or "pages" (paged, like a PDF).</summary>
    [ObservableProperty] private string _readerLayout = "scroll";
    /// <summary>"A4", "Letter", "A5" or "Fit".</summary>
    [ObservableProperty] private string _readerPageSize = "A4";
    /// <summary>"narrow", "normal" or "wide".</summary>
    [ObservableProperty] private string _readerMargins = "normal";
    [ObservableProperty] private bool _readerTwoPages = true;
    /// <summary>Ids of the Library folders that are folded in the folder pane (comma-separated).</summary>
    [ObservableProperty] private string _libraryCollapsedFolders = "";

    // Sketch
    [ObservableProperty] private string _sketchColor = "#1F2328";
    [ObservableProperty] private double _sketchSize = 3;

    // View state
    [ObservableProperty] private string _gamesView = "Board";
    [ObservableProperty] private string _calendarView = "Month";
    [ObservableProperty] private string _notesView = "Grid";
    [ObservableProperty] private string _notesSort = "Custom";
    [ObservableProperty] private string _tasksView = "List";
    [ObservableProperty] private string _vaultSort = "Custom";
    [ObservableProperty] private double _boardZoom = 1.0;
    [ObservableProperty] private string _photosThumbSize = "M";
    [ObservableProperty] private string _bookmarksView = "Cards";
    [ObservableProperty] private string _watchingView = "Board";

    /// <summary>The single layout used before dashboard profiles (moved into the first profile on start).</summary>
    public List<WidgetState> DashboardLayout { get; set; } = [];
    public List<DashboardProfile> DashboardProfiles { get; set; } = [];
    /// <summary>Your own shortcuts (Settings → Key bindings): action → keys, e.g. "page:Board" → "Ctrl+Shift+B". "" = none.</summary>
    public Dictionary<string, string> KeyBindings { get; set; } = [];
    [ObservableProperty] private string _activeDashboard = "";
    public PasswordOptions Generator { get; set; } = new();
    public WindowPlacement? Window { get; set; }
}
