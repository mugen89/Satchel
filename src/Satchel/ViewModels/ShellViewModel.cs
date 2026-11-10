using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Satchel.Core;
using Satchel.Models;
using Satchel.Services;

namespace Satchel.ViewModels;

/// <summary>A dashboard profile's shortcut in the top bar.</summary>
public sealed record DashboardShortcut(string Id, string Name, string Icon, bool IsActive, string Tooltip);

/// <summary>Navigation shell shown while unlocked. Recreated for every session.</summary>
public sealed partial class ShellViewModel : ObservableObject, INavigator, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly SessionService _session;
    private readonly SettingsService _settings;
    private readonly PrivacyService _privacy;
    private readonly Dictionary<string, PageViewModel> _pages = new();
    private readonly List<string> _back = [];
    private readonly List<string> _forward = [];
    private bool _navigating;

    private readonly VideoFeedService _videos;
    private readonly DashboardProfileService _dashboards;
    private readonly KeyBindingService _keys;

    public ShellViewModel(IServiceProvider services, SessionService session, SettingsService settings, VideoFeedService videos, PrivacyService privacy,
        MusicService music, TimerService timers, PodcastService podcast, DashboardProfileService dashboards, KeyBindingService keys)
    {
        _services = services;
        _session = session;
        _settings = settings;
        _videos = videos;
        _privacy = privacy;
        _dashboards = dashboards;
        _keys = keys;
        _keys.Changed += ApplyKeyBindings;
        Music = music;
        Podcast = podcast;
        Timers = timers;
        _videos.UnseenChanged += OnUnseenVideos;
        _dashboards.Changed += RebuildDashboardShortcuts;

        AllNavItems =
        [
            new NavItem("Dashboard", "Dashboard", Glyphs.Home, typeof(DashboardViewModel)),
            new NavItem("Today", "Today", Glyphs.CalendarDay, typeof(TodayViewModel)),
            new NavItem("Notes", "Notes", Glyphs.Notes, typeof(NotesViewModel), "Organize"),
            new NavItem("Tasks", "Tasks", Glyphs.Tasks, typeof(TasksViewModel), "Organize"),
            new NavItem("Calendar", "Calendar", Glyphs.Calendar, typeof(CalendarViewModel), "Organize"),
            new NavItem("Projects", "Projects", Glyphs.Projects, typeof(ProjectsViewModel), "Organize"),
            new NavItem("Vault", "Vault", Glyphs.Shield, typeof(VaultViewModel), "Keep"),
            new NavItem("Contacts", "Contacts", Glyphs.People, typeof(ContactsViewModel), "Keep"),
            new NavItem("Money", "Money", Glyphs.Card, typeof(MoneyViewModel), "Keep"),
            new NavItem("Gamble", "Gamble", Glyphs.Market, typeof(GambleViewModel), "Keep"),
            new NavItem("Bookmarks", "Bookmarks", Glyphs.Bookmark, typeof(BookmarksViewModel), "Keep"),
            new NavItem("Board", "Board", Glyphs.Tiles, typeof(BoardViewModel), "Create"),
            new NavItem("Sketch", "Sketch", Glyphs.Pen, typeof(SketchViewModel), "Create"),
            new NavItem("Library", "Library", Glyphs.Library, typeof(LibraryViewModel), "Create"),
            new NavItem("Sheets", "Sheets", Glyphs.Sheet, typeof(SheetsViewModel), "Create"),
            new NavItem("Photos", "Photos", Glyphs.Photo, typeof(PhotosViewModel), "Create"),
            new NavItem("Reading", "Reading", Glyphs.Book, typeof(ReadingViewModel), "Track"),
            new NavItem("Games", "Games", Glyphs.Game, typeof(GamesViewModel), "Track"),
            new NavItem("Watching", "Watching", Glyphs.Monitor, typeof(WatchingViewModel), "Track"),
            new NavItem("Collections", "Collections", Glyphs.Grid9, typeof(CollectionsViewModel), "Track"),
            new NavItem("Videos", "Videos", Glyphs.Video, typeof(VideosViewModel), "Media"),
            new NavItem("Music", "Music", Glyphs.Music, typeof(MusicViewModel), "Media"),
            new NavItem("Feeds", "Feeds", Glyphs.Feed, typeof(FeedsViewModel), "Media"),
            new NavItem("Flashcards", "Flashcards", Glyphs.Education, typeof(FlashcardsViewModel), "Tools"),
            new NavItem("Clock", "Clock", Glyphs.Timer, typeof(ClockViewModel), "Tools"),
            new NavItem("Insights", "Insights", Glyphs.Pulse, typeof(InsightsViewModel), "Tools"),
        ];
        SettingsItem = new NavItem("Settings", "Settings", Glyphs.Settings, typeof(SettingsViewModel), shortcut: "Ctrl+,");

        RebuildNav();
        RebuildDashboardShortcuts();
        _session.Locking += FlushAllAsync;
        _settings.Changed += OnSettingChanged;
        _privacy.VisibilityChanged += OnPrivacyVisibilityChanged;
        _privacy.ItemChanged += OnPrivacyItemChanged;
        DriveText = AppPaths.DriveLabel;
        IsRemovable = AppPaths.IsRemovableDrive;
    }

    /// <summary>Every page, including ones hidden from the sidebar (still reachable by links and search).</summary>
    public IReadOnlyList<NavItem> AllNavItems { get; }

    /// <summary>Pages shown in the sidebar, in order (Ctrl+1…9 follow this list).</summary>
    public ObservableCollection<NavItem> NavItems { get; } = [];

    /// <summary>Sidebar rows: section headers and items.</summary>
    public ObservableCollection<object> NavRows { get; } = [];

    public NavItem SettingsItem { get; }
    public MusicService Music { get; }
    public PodcastService Podcast { get; }
    public TimerService Timers { get; }
    public string AppName => AppInfo.Name;
    public string DriveText { get; }
    public bool IsRemovable { get; }
    public string UserName => string.IsNullOrWhiteSpace(_settings.Current.UserName) ? Environment.UserName : _settings.Current.UserName;
    public bool CanGoBack => _back.Count > 0;

    /// <summary>Dashboard profiles that have an icon: one-click shortcuts in the top bar.</summary>
    public ObservableCollection<DashboardShortcut> DashboardShortcuts { get; } = [];
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>The canonical section order of the sidebar.</summary>
    public static IReadOnlyList<string> Sections { get; } = ["", "Organize", "Keep", "Create", "Track", "Media", "Tools"];

    [ObservableProperty] private PageViewModel? _currentPage;
    [ObservableProperty] private NavItem? _selectedItem;

    public bool IsSidebarCollapsed
    {
        get => _settings.Current.SidebarCollapsed;
        set
        {
            if (_settings.Current.SidebarCollapsed == value) return;
            _settings.Current.SidebarCollapsed = value;
            OnPropertyChanged();
        }
    }

    public event Action? PaletteRequested;

    public static IReadOnlySet<string> HiddenKeys(string hiddenTabs) =>
        hiddenTabs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>All pages in the user's order (Dashboard always first).</summary>
    public static List<NavItem> Ordered(IEnumerable<NavItem> items, string tabOrder)
    {
        var list = items.ToList();
        var order = tabOrder.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        int Rank(NavItem n)
        {
            if (n.Key == "Dashboard") return -1;
            int i = order.FindIndex(k => k.Equals(n.Key, StringComparison.OrdinalIgnoreCase));
            // Pages added in an update (not in the saved order yet) keep their default place relative to their neighbours.
            return i >= 0 ? i * 100 : DefaultRank(n, list, order);
        }
        return list.Select((n, i) => (n, i)).OrderBy(x => Rank(x.n)).ThenBy(x => x.i).Select(x => x.n).ToList();
    }

    private static int DefaultRank(NavItem n, List<NavItem> all, List<string> order)
    {
        int index = all.IndexOf(n);
        for (int j = index - 1; j >= 0; j--)
        {
            int k = order.FindIndex(o => o.Equals(all[j].Key, StringComparison.OrdinalIgnoreCase));
            if (k >= 0) return k * 100 + 50;
        }
        return index;
    }

    private void RebuildNav()
    {
        var hidden = HiddenKeys(_settings.Current.HiddenTabs);
        var ordered = Ordered(AllNavItems, _settings.Current.TabOrder).Where(i => i.Key == "Dashboard" || !hidden.Contains(i.Key)).ToList();
        bool headers = _settings.Current.ShowSectionHeaders;
        if (headers)
        {
            // Group by section; sections appear in the order of their first page.
            var sectionOrder = ordered.Select(i => i.Section).Distinct().ToList();
            ordered = ordered.OrderBy(i => sectionOrder.IndexOf(i.Section)).ThenBy(i => ordered.IndexOf(i)).ToList();
        }
        NavItems.Clear();
        NavRows.Clear();
        string? section = null;
        foreach (var item in ordered)
        {
            if (headers && item.Section != section)
            {
                section = item.Section;
                if (section.Length > 0) NavRows.Add(new NavSection(section));
            }
            NavItems.Add(item);
            NavRows.Add(item);
        }
        ApplyFolding();
        ApplyKeyBindings();
    }

    /// <summary>
    /// The keys shown in the sidebar tooltips: Ctrl+1…9 by place in the sidebar (except a number you gave to something yourself),
    /// plus your own keys for each tab.
    /// </summary>
    private void ApplyKeyBindings()
    {
        foreach (var item in AllNavItems) item.Shortcut = "";
        for (int i = 0; i < NavItems.Count && i < 9; i++)
            if (_keys.ActionFor(new KeyChord(System.Windows.Input.ModifierKeys.Control, System.Windows.Input.Key.D1 + i)) == null) NavItems[i].Shortcut = $"Ctrl+{i + 1}";
        foreach (var item in AllNavItems.Append(SettingsItem)) item.CustomKeys = _keys.Text(KeyBindingService.PageAction(item.Key));
    }

    /// <summary>Runs your own key binding for these keys (a tab, or showing / hiding private items). False when the keys aren't bound.</summary>
    public bool TryRunKeyBinding(KeyChord chord)
    {
        if (_keys.ActionFor(chord) is not { } action) return false;
        if (action == KeyBindingService.PrivacyAction)
        {
            _ = _privacy.ToggleVisibleAsync();
            return true;
        }
        if (KeyBindingService.PageOf(action) is not { } page || (page != SettingsItem.Key && FindNav(page) == null)) return false;
        NavigateTo(page);
        return true;
    }

    /// <summary>Hides the tabs of collapsed sections — except the open page, so you always see where you are.</summary>
    private void ApplyFolding()
    {
        bool headers = _settings.Current.ShowSectionHeaders;
        var collapsed = HiddenKeys(_settings.Current.CollapsedSections);
        foreach (var item in AllNavItems)
            item.IsFoldedAway = headers && item.Section.Length > 0 && collapsed.Contains(item.Section) && !item.IsSelected;
        foreach (var header in NavRows.OfType<NavSection>())
        {
            var hidden = NavItems.Where(n => n.Section == header.Title && n.IsFoldedAway).ToList();
            header.IsCollapsed = collapsed.Contains(header.Title);
            header.HiddenCount = hidden.Count;
            header.HasHiddenBadge = hidden.Any(n => n.Badge != null);
        }
    }

    [RelayCommand]
    private void ToggleSection(NavSection? header)
    {
        if (header == null) return;
        var collapsed = HiddenKeys(_settings.Current.CollapsedSections).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!collapsed.Remove(header.Title)) collapsed.Add(header.Title);
        _settings.Current.CollapsedSections = string.Join(",", Sections.Where(collapsed.Contains));
    }

    private void OnSettingChanged(string name)
    {
        if (name == nameof(AppSettings.SidebarCollapsed)) OnPropertyChanged(nameof(IsSidebarCollapsed));
        if (name == nameof(AppSettings.UserName)) OnPropertyChanged(nameof(UserName));
        if (name is nameof(AppSettings.HiddenTabs) or nameof(AppSettings.TabOrder) or nameof(AppSettings.ShowSectionHeaders))
        {
            RebuildNav();
            OnPropertyChanged(nameof(SelectedItem));
        }
        if (name == nameof(AppSettings.CollapsedSections)) ApplyFolding();
    }

    public async Task StartAsync()
    {
        var start = _settings.Current.StartPage;
        if (!NavItems.Any(n => n.Key == start) && start != "Settings") start = "Dashboard";
        await NavigateAsync(start, null);
    }

    [RelayCommand]
    private void Navigate(NavItem? item)
    {
        if (item != null) NavigateTo(item.Key);
    }

    public void NavigateTo(string pageKey, object? parameter = null) => _ = NavigateAsync(pageKey, parameter);

    public Task NavigateAsync(string key, object? parameter) => NavigateCoreAsync(key, parameter, history: true);

    private (string Key, object? Parameter, bool History)? _queued;

    private async Task NavigateCoreAsync(string key, object? parameter, bool history)
    {
        if (_navigating)
        {
            // Another switch is in progress: remember the latest request instead of dropping it.
            _queued = (key, parameter, history);
            return;
        }
        _navigating = true;
        PageViewModel? shown = null;
        try
        {
            var item = key == SettingsItem.Key ? SettingsItem : AllNavItems.FirstOrDefault(n => n.Key == key);
            if (item == null) return;

            if (CurrentPage != null && !ReferenceEquals(SelectedItem, item))
            {
                await CurrentPage.OnNavigatedFromAsync();
                if (history && SelectedItem != null)
                {
                    _back.Add(SelectedItem.Key);
                    if (_back.Count > 50) _back.RemoveAt(0);
                    _forward.Clear();
                }
            }

            foreach (var n in AllNavItems) n.IsSelected = ReferenceEquals(n, item);
            SettingsItem.IsSelected = ReferenceEquals(SettingsItem, item);
            SelectedItem = item;
            ApplyFolding();

            if (!_pages.TryGetValue(key, out var page))
            {
                page = (PageViewModel)ActivatorUtilities.CreateInstance(_services, item.PageType, this);
                _pages[key] = page;
            }
            if (!ReferenceEquals(CurrentPage, page)) CurrentPage = page;
            shown = page;
        }
        finally
        {
            _navigating = false;
            OnPropertyChanged(nameof(CanGoBack));
            OnPropertyChanged(nameof(CanGoForward));
        }
        if (_queued is { } next)
        {
            _queued = null;
            await NavigateCoreAsync(next.Key, next.Parameter, next.History);
            return;
        }
        // Loading happens outside the lock, so a slow page never blocks the next click.
        if (shown != null) await shown.OnNavigatedToAsync(parameter);
    }

    [RelayCommand]
    public async Task GoBack()
    {
        if (_back.Count == 0 || SelectedItem == null) return;
        var target = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _forward.Add(SelectedItem.Key);
        await NavigateCoreAsync(target, null, history: false);
    }

    [RelayCommand]
    public async Task GoForward()
    {
        if (_forward.Count == 0 || SelectedItem == null) return;
        var target = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        _back.Add(SelectedItem.Key);
        await NavigateCoreAsync(target, null, history: false);
    }

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab: the next or previous page in the sidebar.</summary>
    public void NavigateRelative(int delta)
    {
        if (NavItems.Count == 0) return;
        int i = SelectedItem == null ? -1 : NavItems.IndexOf(SelectedItem);
        if (i < 0) i = delta > 0 ? -1 : 0;
        NavigateTo(NavItems[(i + delta + NavItems.Count) % NavItems.Count].Key);
    }

    public T? GetPage<T>(string key) where T : PageViewModel => _pages.TryGetValue(key, out var p) ? p as T : null;

    public NavItem? FindNav(string key) => AllNavItems.FirstOrDefault(n => n.Key == key);

    /// <summary>True when the page is in the sidebar (not hidden in Settings).</summary>
    public bool IsVisible(string key) => NavItems.Any(n => n.Key == key);

    private void OnUnseenVideos(int count) => SetBadge("Videos", count);

    public void SetBadge(string key, int count)
    {
        if (FindNav(key) is { } nav) nav.Badge = count > 0 ? (count > 99 ? "99+" : count.ToString()) : null;
        ApplyFolding();
    }

    // ----- dashboard profiles -----

    private void RebuildDashboardShortcuts()
    {
        DashboardShortcuts.Clear();
        var all = _dashboards.All;
        var active = _dashboards.Active;
        for (int i = 0; i < all.Count; i++)
        {
            var p = all[i];
            if (string.IsNullOrEmpty(p.Icon)) continue;
            var tooltip = i < 9 ? $"{p.Name} dashboard  (Alt+{i + 1})" : $"{p.Name} dashboard";
            DashboardShortcuts.Add(new DashboardShortcut(p.Id, p.Name, p.Icon, ReferenceEquals(p, active), tooltip));
        }
    }

    [RelayCommand]
    private void OpenDashboardProfile(string? id)
    {
        if (id == null) return;
        _dashboards.Activate(id);
        if (SelectedItem?.Key != "Dashboard") NavigateTo("Dashboard");
    }

    /// <summary>Alt+1…9: the n-th dashboard profile (0-based). False when there's no such profile.</summary>
    public bool OpenDashboardProfileAt(int index)
    {
        if (!_dashboards.ActivateAt(index)) return false;
        if (SelectedItem?.Key != "Dashboard") NavigateTo("Dashboard");
        return true;
    }

    // ----- privacy -----

    private async void OnPrivacyVisibilityChanged() => await ReloadPagesAsync(all: true);

    private async void OnPrivacyItemChanged(Entity item)
    {
        // Something just became private while private items are hidden: the page reloads without it.
        if (item.IsPrivate && !_privacy.ShowPrivate) await ReloadPagesAsync(all: false);
    }

    /// <summary>Drops cached pages (all, or just the current one) and shows the current page again with fresh data.</summary>
    public async Task ReloadPagesAsync(bool all)
    {
        if (SelectedItem == null) return;
        var key = SelectedItem.Key;
        foreach (var (k, page) in _pages.ToList())
        {
            if (!all && k != key) continue;
            try { await page.FlushAsync(); } catch { /* reload anyway */ }
            (page as IDisposable)?.Dispose();
            _pages.Remove(k);
        }
        await NavigateCoreAsync(key, null, history: false);
    }

    public void ShowPalette() => PaletteRequested?.Invoke();

    [RelayCommand]
    private void OpenPalette() => ShowPalette();

    [RelayCommand]
    private async Task Lock() => await _session.LockAsync();

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    [RelayCommand]
    private void OpenSettings() => NavigateTo("Settings");

    [RelayCommand]
    private void OpenPage(string? key)
    {
        if (key != null) NavigateTo(key);
    }

    public void NavigateByIndex(int index)
    {
        if (index >= 0 && index < NavItems.Count) NavigateTo(NavItems[index].Key);
    }

    private async Task FlushAllAsync()
    {
        foreach (var page in _pages.Values)
        {
            try { await page.FlushAsync(); } catch { /* keep locking */ }
        }
    }

    public Task FlushAsync() => FlushAllAsync();

    public void Dispose()
    {
        _session.Locking -= FlushAllAsync;
        _settings.Changed -= OnSettingChanged;
        _videos.UnseenChanged -= OnUnseenVideos;
        _dashboards.Changed -= RebuildDashboardShortcuts;
        _keys.Changed -= ApplyKeyBindings;
        _privacy.VisibilityChanged -= OnPrivacyVisibilityChanged;
        _privacy.ItemChanged -= OnPrivacyItemChanged;
        foreach (var page in _pages.Values) (page as IDisposable)?.Dispose();
        _pages.Clear();
        CurrentPage = null;
    }
}
