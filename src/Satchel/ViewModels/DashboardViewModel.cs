using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Behaviors;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Security;
using Satchel.Services;
using Satchel.ViewModels.Dialogs;

namespace Satchel.ViewModels;

/// <summary>Everything the widgets need, loaded (and decrypted) once per refresh.</summary>
public sealed class DashboardData
{
    public List<Note> Notes { get; init; } = [];
    public List<TaskItem> Tasks { get; init; } = [];
    public List<TaskList> Lists { get; init; } = [];
    public List<CalendarEvent> Events { get; init; } = [];
    public List<CalendarCategory> Calendars { get; init; } = [];
    public List<VaultProfile> Profiles { get; init; } = [];
    public List<VaultAccount> Accounts { get; init; } = [];
    public List<Contact> Contacts { get; init; } = [];
    public List<Project> Projects { get; init; } = [];
    public List<Milestone> Milestones { get; init; } = [];
    public List<Book> Books { get; init; } = [];
    public List<Game> Games { get; init; } = [];
    public List<Video> NewVideos { get; init; } = [];
    public int ChannelCount { get; init; }
    public List<Countdown> Countdowns { get; init; } = [];
}

public abstract partial class WidgetViewModel : ObservableObject
{
    private DashboardData? _data;
    private int _appliedCapacity = -1;

    protected WidgetViewModel(DashboardViewModel owner) => Owner = owner;

    public DashboardViewModel Owner { get; }
    public abstract string Key { get; }
    public abstract string Title { get; }
    public abstract string Glyph { get; }
    public virtual int DefaultSpan => 1;
    public virtual string? ActionText => null;
    public virtual string? ActionPage => null;
    public bool HasAction => ActionText != null;

    /// <summary>Width in dashboard columns.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private int _span = 1;

    /// <summary>Fixed height in pixels (set by dragging the corner); 0 = as tall as the content.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrameHeight), nameof(IsFixedHeight), nameof(SizeText))]
    private double _height;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWiggling))]
    private bool _isResizing;

    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isEmpty;

    public double FrameHeight => Height > 0 ? Height : double.NaN;
    public bool IsFixedHeight => Height > 0;
    public bool IsWiggling => Owner.IsCustomizing && !IsResizing;
    public string SizeText => (Span == 1 ? "1 column" : $"{Span} columns") + " · " + (Height > 0 ? $"{Height:0} px tall" : "fits the content");

    /// <summary>Rows a list widget shows when it fits its content.</summary>
    protected virtual int DefaultCapacity => 6;

    /// <summary>Rows to show: a few while the widget fits its content, more once it has a fixed (scrollable) height.</summary>
    protected virtual int Capacity => IsFixedHeight ? Math.Max(DefaultCapacity, 30) : DefaultCapacity;

    /// <summary>Fills the widget from freshly loaded data (kept, so a resize can show more or fewer rows).</summary>
    public void Apply(DashboardData data)
    {
        _data = data;
        _appliedCapacity = Capacity;
        Update(data);
    }

    partial void OnHeightChanged(double value) => Reapply();
    partial void OnSpanChanged(int value) => Reapply();

    private void Reapply()
    {
        if (_data == null || Capacity == _appliedCapacity) return;
        _appliedCapacity = Capacity;
        try { Update(_data); } catch { /* one widget must not break the dashboard */ }
    }

    /// <summary>Customize mode started or ended.</summary>
    public void NotifyCustomizing() => OnPropertyChanged(nameof(IsWiggling));

    public abstract void Update(DashboardData data);

    [RelayCommand]
    private void Action()
    {
        if (ActionPage != null) Owner.Navigator.NavigateTo(ActionPage);
    }
}

public sealed partial class GreetingWidget : WidgetViewModel
{
    public GreetingWidget(DashboardViewModel owner) : base(owner) { }
    public override string Key => "greeting";
    public override string Title => "Welcome";
    public override string Glyph => Glyphs.Sun;
    public override int DefaultSpan => 2;

    [ObservableProperty] private string _greeting = "";
    [ObservableProperty] private string _timeText = "";
    [ObservableProperty] private string _dateText = "";
    [ObservableProperty] private string _summary = "";

    public void Tick(string userName)
    {
        var now = DateTime.Now;
        Greeting = $"{Format.Greeting()}, {userName}";
        TimeText = Format.Time(now);
        DateText = now.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture);
    }

    public override void Update(DashboardData data)
    {
        var today = DateTime.Today;
        int dueToday = data.Tasks.Count(t => !t.IsDone && t.DueDate.HasValue && t.DueDate.Value.Date <= today);
        int events = data.Events.Sum(e => Recurrence.Occurrences(e, today, today.AddDays(1)).Count());
        var parts = new List<string>
        {
            dueToday == 0 ? "No tasks due today" : $"{dueToday} task{(dueToday == 1 ? "" : "s")} due today",
            events == 0 ? "no events" : $"{events} event{(events == 1 ? "" : "s")} on the calendar"
        };
        Summary = string.Join(" · ", parts) + ".";
    }
}

public sealed record AgendaRow(string Time, string Title, string ColorKey, string Day, object? Target, string Page);

public sealed partial class AgendaWidget : WidgetViewModel
{
    public AgendaWidget(DashboardViewModel owner) : base(owner) { }
    public override string Key => "agenda";
    public override string Title => "Up next";
    public override string Glyph => Glyphs.Calendar;
    public override string ActionText => "Calendar";
    public override string ActionPage => "Calendar";
    public ObservableCollection<AgendaRow> Rows { get; } = [];

    public override void Update(DashboardData data)
    {
        Rows.Clear();
        var colors = data.Calendars.ToDictionary(c => c.Id, c => c.Color);
        var hidden = data.Calendars.Where(c => !c.IsVisible).Select(c => c.Id).ToHashSet();
        var now = DateTime.Now;
        var items = new List<(DateTime Start, AgendaRow Row)>();
        foreach (var e in data.Events)
        {
            if (e.CategoryId is int cid && hidden.Contains(cid)) continue;
            foreach (var (start, end) in Recurrence.Occurrences(e, DateTime.Today, DateTime.Today.AddDays(7)))
            {
                if (!e.IsAllDay && end < now) continue;
                var color = e.Color ?? (e.CategoryId is int id && colors.TryGetValue(id, out var c) ? c : "slate");
                items.Add((start, new AgendaRow(e.IsAllDay ? "All day" : Format.Time(start), e.Title, color, Format.FriendlyDay(start), start.Date, "Calendar")));
            }
        }
        foreach (var (_, row) in items.OrderBy(i => i.Start).Take(Capacity)) Rows.Add(row);
        IsEmpty = Rows.Count == 0;
    }

    [RelayCommand]
    private void Open(AgendaRow? row)
    {
        if (row != null) Owner.Navigator.NavigateTo(row.Page, row.Target);
    }
}

public sealed partial class TasksWidget : WidgetViewModel
{
    public TasksWidget(DashboardViewModel owner) : base(owner) { }
    public override string Key => "tasks";
    public override string Title => "Focus tasks";
    public override string Glyph => Glyphs.Tasks;
    public override string ActionText => "All tasks";
    public override string ActionPage => "Tasks";
    public ObservableCollection<TaskItem> Items { get; } = [];
    [ObservableProperty] private string _newTask = "";

    public override void Update(DashboardData data)
    {
        Items.Clear();
        var today = DateTime.Today;
        var picks = data.Tasks.Where(t => !t.IsDone)
            .OrderBy(t => t.IsOverdue ? 0 : t.DueDate.HasValue && t.DueDate.Value.Date <= today ? 1 : t.State == TaskState.InProgress ? 2 : t.DueDate.HasValue ? 3 : 4)
            .ThenByDescending(t => t.Priority).ThenBy(t => t.DueDate ?? DateTime.MaxValue).Take(Capacity);
        foreach (var t in picks) Items.Add(t);
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private async Task Toggle(TaskItem? task)
    {
        if (task == null) return;
        task.IsDone = !task.IsDone;
        await Owner.Store.UpdateAsync(task);
        if (task.IsDone && await TaskRepeat.OnCompletedAsync(Owner.Store, task) is { } next)
            Owner.Toasts.Show($"Repeats — the next one is due {Format.FriendlyDay(next.DueDate!.Value).ToLowerInvariant()}", ToastKind.Success, durationMs: 2600);
        await Task.Delay(Anim.Enabled ? 450 : 0);
        if (task.IsDone) Items.Remove(task);
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private async Task Add()
    {
        var text = NewTask.Trim();
        if (text.Length == 0) return;
        var parsed = TaskParser.Parse(text, []);
        var task = new TaskItem { Title = parsed.Title, DueDate = parsed.Due ?? DateTime.Today, DueHasTime = parsed.HasTime, Priority = parsed.Priority, SortOrder = -1 };
        await Owner.Store.AddAsync(task);
        Items.Insert(0, task);
        NewTask = "";
        IsEmpty = false;
    }

    [RelayCommand]
    private void Open(TaskItem? task)
    {
        if (task != null) Owner.Navigator.NavigateTo("Tasks", task.Id);
    }
}

public sealed partial class PinnedNotesWidget : WidgetViewModel
{
    public PinnedNotesWidget(DashboardViewModel owner) : base(owner) { }
    public override string Key => "pinned";
    public override string Title => "Pinned notes";
    public override string Glyph => Glyphs.Pin;
    public override int DefaultSpan => 2;
    public override string ActionText => "Notes";
    public override string ActionPage => "Notes";
    public ObservableCollection<Note> Items { get; } = [];

    public override void Update(DashboardData data)
    {
        Items.Clear();
        var notes = data.Notes.Where(n => !n.IsTrashed && !n.IsArchived).ToList();
        var pinned = notes.Where(n => n.IsPinned).OrderBy(n => n.SortOrder).ToList();
        if (pinned.Count < 3) pinned.AddRange(notes.Where(n => !n.IsPinned).OrderByDescending(n => n.UpdatedAt).Take(3 - pinned.Count));
        foreach (var n in pinned.Take(Capacity)) Items.Add(n);
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private void Open(Note? note)
    {
        if (note != null) Owner.Navigator.NavigateTo("Notes", note.Id);
    }
}

public sealed partial class QuickNoteWidget : WidgetViewModel
{
    public QuickNoteWidget(DashboardViewModel owner) : base(owner) { }
    public override string Key => "quicknote";
    public override string Title => "Quick capture";
    public override string Glyph => Glyphs.Edit;
    [ObservableProperty] private string _text = "";

    public override void Update(DashboardData data) { }

    [RelayCommand]
    private async Task Save()
    {
        var text = Text.Trim();
        if (text.Length == 0) return;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var title = lines[0].Length > 60 ? lines[0][..60] + "…" : lines[0];
        var body = lines.Length > 1 ? string.Join("\n", lines.Skip(1)).Trim() : "";
        var note = new Note { Type = NoteType.Text, Title = title, Content = body, PlainText = body, SortOrder = -1, Tags = "quick" };
        await Owner.Store.AddAsync(note);
        Text = "";
        Owner.Toasts.Show("Saved to Notes", ToastKind.Success, "Open", () => Owner.Navigator.NavigateTo("Notes", note.Id));
    }
}

public sealed partial class FavoriteAccount : ObservableObject
{
    public required VaultAccount Account { get; init; }
    public required VaultProfile Profile { get; init; }
    public string Title => $"{Profile.Name}";
    public string Subtitle => Account.DisplayName + (string.IsNullOrWhiteSpace(Account.Username + Account.Email) ? "" : " · " + (string.IsNullOrWhiteSpace(Account.Username) ? Account.Email : Account.Username));
    public bool HasLogin => !string.IsNullOrWhiteSpace(Account.Username) || !string.IsNullOrWhiteSpace(Account.Email);
    public bool HasPassword => !string.IsNullOrWhiteSpace(Account.Password);
    public bool HasTotp => Totp.IsValid(Account.TotpSecret);

    [RelayCommand]
    private void CopyLogin() => SecureClipboard.Copy(string.IsNullOrWhiteSpace(Account.Username) ? Account.Email : Account.Username, false, "Username copied");

    [RelayCommand]
    private void CopyPassword() => SecureClipboard.Copy(Account.Password, true, "Password copied — clears automatically");
}

public sealed partial class FavoritesWidget : WidgetViewModel
{
    public FavoritesWidget(DashboardViewModel owner) : base(owner) { }
    public override string Key => "favorites";
    public override string Title => "Vault favorites";
    public override string Glyph => Glyphs.Star;
    public override string ActionText => "Vault";
    public override string ActionPage => "Vault";
    public ObservableCollection<FavoriteAccount> Items { get; } = [];
    protected override int DefaultCapacity => 5;

    public override void Update(DashboardData data)
    {
        Items.Clear();
        var profiles = data.Profiles.ToDictionary(p => p.Id);
        var favs = data.Accounts.Where(a => a.IsFavorite || (profiles.TryGetValue(a.ProfileId, out var p) && p.IsFavorite))
            .OrderByDescending(a => a.LastUsedAt ?? DateTime.MinValue).Take(Capacity);
        foreach (var a in favs)
            if (profiles.TryGetValue(a.ProfileId, out var p)) Items.Add(new FavoriteAccount { Account = a, Profile = p });
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private void Open(FavoriteAccount? fav)
    {
        if (fav != null) Owner.Navigator.NavigateTo("Vault", fav.Profile.Id);
    }
}

public sealed partial class SecurityWidget : WidgetViewModel
{
    public SecurityWidget(DashboardViewModel owner) : base(owner) { }
    public override string Key => "security";
    public override string Title => "Password health";
    public override string Glyph => Glyphs.Shield;
    public override string ActionText => "Review";
    public override string ActionPage => "Vault";

    [ObservableProperty] private int _score = 100;
    [ObservableProperty] private int _weak;
    [ObservableProperty] private int _reused;
    [ObservableProperty] private int _expiring;
    [ObservableProperty] private int _total;
    public double Fraction => Score / 100.0;
    public string Verdict => Total == 0 ? "Add your first password" : Score >= 90 ? "Excellent" : Score >= 70 ? "Good" : Score >= 50 ? "Needs attention" : "At risk";

    public override void Update(DashboardData data)
    {
        var withPw = data.Accounts.Where(a => !string.IsNullOrEmpty(a.Password)).ToList();
        var reuse = withPw.GroupBy(a => a.Password).ToDictionary(g => g.Key, g => g.Count());
        Total = withPw.Count;
        Weak = withPw.Count(a => PasswordStrength.Evaluate(a.Password).Score < 2);
        Reused = withPw.Count(a => reuse[a.Password] > 1);
        Expiring = data.Accounts.Count(a => a.ExpiresAt.HasValue && a.ExpiresAt.Value <= DateTime.Today.AddDays(30));
        var yearAgo = DateTime.Now.AddDays(-365);
        int flagged = withPw.Count(a => PasswordStrength.Evaluate(a.Password).Score < 2 || reuse[a.Password] > 1 || (a.PasswordChangedAt ?? a.CreatedAt) < yearAgo);
        Score = Total == 0 ? 100 : (int)Math.Round(100.0 * (Total - flagged) / Total);
        OnPropertyChanged(nameof(Fraction));
        OnPropertyChanged(nameof(Verdict));
    }
}

public sealed partial class CalendarWidget : WidgetViewModel
{
    public CalendarWidget(DashboardViewModel owner) : base(owner) { }
    public override string Key => "calendar";
    public override string Title => "Calendar";
    public override string Glyph => Glyphs.CalendarDay;
    public ObservableCollection<DateTime> Marks { get; } = [];
    [ObservableProperty] private DateTime? _selected = DateTime.Today;

    partial void OnSelectedChanged(DateTime? value)
    {
        if (value.HasValue && value.Value != DateTime.Today) Owner.Navigator.NavigateTo("Calendar", value.Value);
    }

    public override void Update(DashboardData data)
    {
        Marks.Clear();
        var start = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddDays(-7);
        foreach (var e in data.Events)
            foreach (var (s, _) in Recurrence.Occurrences(e, start, start.AddDays(50)))
                if (!Marks.Contains(s.Date)) Marks.Add(s.Date);
    }
}

public sealed record StatTile(string Value, string Label, string Glyph, string ColorKey, string Page);

public sealed partial class StatsWidget : WidgetViewModel
{
    public StatsWidget(DashboardViewModel owner) : base(owner) { }
    public override string Key => "stats";
    public override string Title => "At a glance";
    public override string Glyph => Glyphs.Pulse;
    public ObservableCollection<StatTile> Tiles { get; } = [];

    public override void Update(DashboardData data)
    {
        Tiles.Clear();
        var weekStart = Format.StartOfWeek(DateTime.Today);
        Tiles.Add(new StatTile(data.Notes.Count(n => !n.IsTrashed).ToString(), "Notes", Glyphs.Notes, "teal", "Notes"));
        Tiles.Add(new StatTile(data.Tasks.Count(t => t.IsDone && t.CompletedAt >= weekStart).ToString(), "Done this week", Glyphs.Completed, "green", "Tasks"));
        Tiles.Add(new StatTile(data.Events.Sum(e => Recurrence.Occurrences(e, weekStart, weekStart.AddDays(7)).Count()).ToString(), "Events this week", Glyphs.Calendar, "sky", "Calendar"));
        Tiles.Add(new StatTile(data.Accounts.Count.ToString(), "Vault accounts", Glyphs.Key, "amber", "Vault"));
    }

    [RelayCommand]
    private void Open(StatTile? tile)
    {
        if (tile != null) Owner.Navigator.NavigateTo(tile.Page);
    }
}

public sealed partial class BirthdaysWidget : WidgetViewModel
{
    public BirthdaysWidget(DashboardViewModel owner) : base(owner) { }
    public override string Key => "birthdays";
    public override string Title => "Birthdays";
    public override string Glyph => Glyphs.Heart;
    public override string ActionText => "Contacts";
    public override string ActionPage => "Contacts";
    public ObservableCollection<Contact> Items { get; } = [];
    protected override int DefaultCapacity => 5;

    public override void Update(DashboardData data)
    {
        Items.Clear();
        // A taller widget looks further ahead.
        int days = IsFixedHeight ? 120 : 45;
        foreach (var c in data.Contacts.Where(c => c.Birthday.HasValue && (c.NextBirthday - DateTime.Today).TotalDays <= days).OrderBy(c => c.NextBirthday).Take(Capacity))
            Items.Add(c);
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private void Open(Contact? c)
    {
        if (c != null) Owner.Navigator.NavigateTo("Contacts", c.Id);
    }
}

public sealed partial class DashboardViewModel : PageViewModel, IDisposable
{
    public const double MinWidgetHeight = 140;
    public const double MaxWidgetHeight = 1400;

    private readonly SettingsService _settings;
    private readonly DashboardProfileService _profiles;
    private readonly DispatcherTimer _clock;
    private readonly List<WidgetViewModel> _all;
    private string _layoutProfileId;

    private readonly ClocksWidget _clocksWidget;
    private static readonly HashSet<string> OptInWidgets = ["clocks", "rediscover", "music"];

    public DashboardViewModel(INavigator nav, DataStore store, SettingsService settings, ToastService toasts, DialogService dialogs, WeatherService weather, MusicService music,
        DashboardProfileService profiles)
    {
        Navigator = nav;
        Store = store;
        Toasts = toasts;
        Dialogs = dialogs;
        Weather = weather;
        Music = music;
        _settings = settings;
        _profiles = profiles;
        _layoutProfileId = profiles.Active.Id;
        _profiles.Changed += OnProfilesChanged;
        Greeting = new GreetingWidget(this);
        _clocksWidget = new ClocksWidget(this);
        _all =
        [
            Greeting, new AgendaWidget(this), new TasksWidget(this), new PinnedNotesWidget(this), new QuickNoteWidget(this),
            new FavoritesWidget(this), new SecurityWidget(this), new CalendarWidget(this), new StatsWidget(this), new BirthdaysWidget(this),
            new ProjectsWidget(this), new ReadingWidget(this), new VideosWidget(this), new GamesWidget(this),
            new WeatherWidget(this), _clocksWidget, new CountdownsWidget(this), new QuoteWidget(this), new RediscoverWidget(this), new MusicWidget(this)
        ];
        ApplyLayout();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) =>
        {
            Greeting.Tick(UserName);
            if (_clocksWidget.IsVisible) _clocksWidget.Tick();
        };
        _clock.Start();
        Greeting.Tick(UserName);
    }

    public override string Title => "Dashboard";
    public INavigator Navigator { get; }
    public DataStore Store { get; }
    public ToastService Toasts { get; }
    public DialogService Dialogs { get; }
    public WeatherService Weather { get; }
    public MusicService Music { get; }
    public SettingsService Settings => _settings;
    public GreetingWidget Greeting { get; }
    public ObservableCollection<WidgetViewModel> Widgets { get; } = [];
    public ObservableCollection<WidgetViewModel> HiddenWidgets { get; } = [];
    public string UserName => string.IsNullOrWhiteSpace(_settings.Current.UserName) ? Environment.UserName : _settings.Current.UserName;
    public bool AmbientEffects => _settings.Current.AmbientEffects;

    [ObservableProperty] private bool _isCustomizing;

    partial void OnIsCustomizingChanged(bool value)
    {
        foreach (var w in _all) w.NotifyCustomizing();
    }

    public bool HasHidden => HiddenWidgets.Count > 0;

    // ----- profiles -----

    public IReadOnlyList<DashboardProfile> Profiles => _profiles.All;
    public DashboardProfile ActiveProfile => _profiles.Active;
    public string ProfileName => _profiles.Active.Name;
    public string ProfileIcon => _profiles.Active.Icon;
    public bool HasProfileIcon => ProfileIcon.Length > 0;

    private void OnProfilesChanged()
    {
        if (_profiles.Active.Id != _layoutProfileId)
        {
            _layoutProfileId = _profiles.Active.Id;
            ApplyLayout();
        }
        OnPropertyChanged(nameof(Profiles));
        OnPropertyChanged(nameof(ActiveProfile));
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(ProfileIcon));
        OnPropertyChanged(nameof(HasProfileIcon));
    }

    [RelayCommand]
    private void SwitchProfile(DashboardProfile? profile)
    {
        if (profile != null) _profiles.Activate(profile.Id);
    }

    [RelayCommand]
    private async Task NewProfile()
    {
        var dialog = new DashboardProfileDialogViewModel(null, ProfileName, _profiles);
        if (await Dialogs.ShowAsync(dialog) is not true) return;
        var layout = dialog.Start switch
        {
            "copy" => CurrentLayout(),
            "empty" => _all.Select(w => new WidgetState { Key = w.Key, Visible = false, Span = w.DefaultSpan }).ToList(),
            _ => []
        };
        _profiles.Add(dialog.Name, dialog.Icon, layout);
        if (dialog.Start == "empty")
        {
            IsCustomizing = true;
            Toasts.Success($"“{ProfileName}” is ready — pick its widgets from the bar above");
        }
        else Toasts.Success(string.IsNullOrEmpty(dialog.Icon) ? $"Switched to “{ProfileName}”" : $"Switched to “{ProfileName}” — its icon is in the top bar");
    }

    [RelayCommand]
    private async Task EditProfile()
    {
        var profile = ActiveProfile;
        var dialog = new DashboardProfileDialogViewModel(profile, profile.Name, _profiles);
        var result = await Dialogs.ShowAsync(dialog);
        if (result is "delete") await DeleteProfile();
        else if (result is true) _profiles.Update(profile, dialog.Name, dialog.Icon);
    }

    [RelayCommand]
    private void DuplicateProfile()
    {
        var name = $"{ProfileName} copy";
        for (int i = 2; _profiles.NameExists(name); i++) name = $"{ProfileName} copy {i}";
        _profiles.Add(name, "", CurrentLayout());
        Toasts.Success($"Switched to “{name}”");
    }

    [RelayCommand]
    private async Task DeleteProfile()
    {
        var profile = ActiveProfile;
        if (Profiles.Count <= 1) { Toasts.Info("This is your only dashboard profile"); return; }
        if (!await Dialogs.ConfirmAsync($"Delete “{profile.Name}”?", "Only this widget setup is removed — your notes, tasks and everything else stay.", "Delete", danger: true)) return;
        _profiles.Remove(profile);
        Toasts.Show($"Deleted — showing “{ProfileName}”", ToastKind.Info, durationMs: 2200);
    }

    // ----- layout -----

    private void ApplyLayout()
    {
        Widgets.Clear();
        HiddenWidgets.Clear();
        var layout = _profiles.Active.Layout;
        var ordered = new List<WidgetViewModel>();
        foreach (var state in layout)
        {
            var w = _all.FirstOrDefault(x => x.Key == state.Key);
            if (w == null || ordered.Contains(w)) continue;
            w.IsVisible = state.Visible;
            w.Span = Math.Clamp(state.Span, 1, 6);
            w.Height = state.Height > 0 ? Math.Clamp(state.Height, MinWidgetHeight, MaxWidgetHeight) : 0;
            ordered.Add(w);
        }
        foreach (var w in _all.Where(w => !ordered.Contains(w)))
        {
            w.Span = w.DefaultSpan;
            w.Height = 0;
            // Newer, optional widgets start in "Add widgets" so the dashboard doesn't suddenly fill up.
            w.IsVisible = !OptInWidgets.Contains(w.Key) && (w.Key != "birthdays" || layout.Count == 0);
            ordered.Add(w);
        }
        foreach (var w in ordered)
        {
            if (w.IsVisible) Widgets.Add(w);
            else HiddenWidgets.Add(w);
        }
        OnPropertyChanged(nameof(HasHidden));
    }

    private List<WidgetState> CurrentLayout() => Widgets.Concat(HiddenWidgets)
        .Select(w => new WidgetState { Key = w.Key, Visible = w.IsVisible, Span = w.Span, Height = w.Height }).ToList();

    private void SaveLayout() => _profiles.SaveLayout(CurrentLayout());

    // ----- resizing (the corner grip; the view works out columns and pixels) -----

    public void BeginResize(WidgetViewModel w) => w.IsResizing = true;

    public void EndResize(WidgetViewModel w)
    {
        w.IsResizing = false;
        SaveLayout();
    }

    /// <summary>Back to "as tall as the content".</summary>
    [RelayCommand]
    private void FitHeight(WidgetViewModel? w)
    {
        if (w == null || w.Height == 0) return;
        w.Height = 0;
        SaveLayout();
    }

    public override bool OnEscape()
    {
        if (!IsCustomizing) return false;
        IsCustomizing = false;
        return true;
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        OnPropertyChanged(nameof(UserName));
        OnPropertyChanged(nameof(AmbientEffects));
        Greeting.Tick(UserName);
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var data = await Store.RunAsync(db => new DashboardData
            {
                Notes = db.Notes.ToList(),
                Tasks = db.Tasks.ToList(),
                Lists = db.TaskLists.ToList(),
                Events = db.Events.ToList(),
                Calendars = db.CalendarCategories.ToList(),
                Profiles = db.Profiles.ToList(),
                Accounts = db.Accounts.ToList(),
                Contacts = db.Contacts.ToList(),
                Projects = db.Projects.ToList(),
                Milestones = db.Milestones.ToList(),
                Books = db.Books.Where(b => b.Status == BookStatus.Reading).ToList(),
                Games = db.Games.Where(g => g.Status == GameStatus.Playing).ToList(),
                NewVideos = db.Videos.Where(v => v.State == VideoState.New && v.ChannelId != null && !v.IsShort).OrderByDescending(v => v.PublishedAt).Take(16).ToList(),
                ChannelCount = db.VideoChannels.Count(),
                Countdowns = db.Countdowns.ToList()
            });
            foreach (var w in _all)
            {
                try { w.Apply(data); } catch { /* one widget must not break the dashboard */ }
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ToggleCustomize() => IsCustomizing = !IsCustomizing;

    [RelayCommand]
    private void HideWidget(WidgetViewModel? w)
    {
        if (w == null) return;
        w.IsVisible = false;
        Widgets.Remove(w);
        HiddenWidgets.Add(w);
        OnPropertyChanged(nameof(HasHidden));
        SaveLayout();
    }

    [RelayCommand]
    private void ShowWidget(WidgetViewModel? w)
    {
        if (w == null) return;
        w.IsVisible = true;
        HiddenWidgets.Remove(w);
        Widgets.Add(w);
        OnPropertyChanged(nameof(HasHidden));
        SaveLayout();
    }

    [RelayCommand]
    private void ReorderWidgets(ReorderEventArgs? args) => SaveLayout();

    /// <summary>The active profile goes back to the default widgets, order and sizes.</summary>
    [RelayCommand]
    private void ResetLayout()
    {
        _profiles.SaveLayout([]);
        ApplyLayout();
        SaveLayout();
    }

    [RelayCommand] private void NewNote() => Navigator.NavigateTo("Notes", "new");
    [RelayCommand] private void NewTask() => Navigator.NavigateTo("Tasks", "focus");
    [RelayCommand] private void NewEvent() => Navigator.NavigateTo("Calendar", "new");
    [RelayCommand] private void Generator() => Navigator.NavigateTo("Vault", "generator");

    public void Dispose()
    {
        _clock.Stop();
        _profiles.Changed -= OnProfilesChanged;
    }
}
