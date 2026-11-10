using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Behaviors;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Services;
using Satchel.ViewModels.Dialogs;

namespace Satchel.ViewModels;

public sealed partial class CalendarViewModel : PageViewModel, IDisposable
{
    public const double HourHeight = 52;

    private readonly INavigator _nav;
    private readonly DataStore _store;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _clock;
    private List<CalendarEvent> _events = [];
    private List<TaskItem> _tasks = [];
    private List<Contact> _contacts = [];
    private bool _loaded;

    public CalendarViewModel(INavigator nav, DataStore store, DialogService dialogs, ToastService toasts, SettingsService settings)
    {
        _nav = nav;
        _store = store;
        _dialogs = dialogs;
        _toasts = toasts;
        _settings = settings;
        _viewMode = settings.Current.CalendarView is "Week" or "Agenda" ? settings.Current.CalendarView : "Month";
        _clock = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _clock.Tick += (_, _) => OnPropertyChanged(nameof(NowTop));
        _clock.Start();
        WeekdayNames = Format.WeekdayShortNames().ToList();
        Hours = Enumerable.Range(0, 24).Select(h => Format.Time(DateTime.Today.AddHours(h))).ToList();
    }

    public override string Title => "Calendar";

    public ObservableCollection<MonthDay> Days { get; } = [];
    public ObservableCollection<WeekColumn> WeekColumns { get; } = [];
    public ObservableCollection<AgendaGroup> Agenda { get; } = [];
    public ObservableCollection<CalendarFilter> Calendars { get; } = [];
    public ObservableCollection<Occurrence> Upcoming { get; } = [];
    public ObservableCollection<DateTime> MarkedDates { get; } = [];
    public List<string> WeekdayNames { get; private set; }
    public List<string> Hours { get; }
    public List<CalendarCategory> CategoryChoices { get; private set; } = [];

    [ObservableProperty] private string _viewMode;
    [ObservableProperty] private DateTime _anchor = DateTime.Today;
    [ObservableProperty] private DateTime? _miniSelected = DateTime.Today;

    public bool IsMonth => ViewMode == "Month";
    public bool IsWeek => ViewMode == "Week";
    public bool IsAgenda => ViewMode == "Agenda";
    public double GridHeight => HourHeight * 24;
    public double NowTop => DateTime.Now.TimeOfDay.TotalMinutes / 60 * HourHeight;
    public bool HasUpcoming => Upcoming.Count > 0;

    public string PeriodTitle
    {
        get
        {
            var culture = CultureInfo.CurrentCulture;
            if (IsMonth) return Anchor.ToString("MMMM yyyy", culture);
            if (IsWeek)
            {
                var start = Format.StartOfWeek(Anchor);
                var end = start.AddDays(6);
                return start.Month == end.Month
                    ? $"{start.Day} – {end.Day} {end.ToString("MMMM yyyy", culture)}"
                    : $"{start.ToString("d MMM", culture)} – {end.ToString("d MMM yyyy", culture)}";
            }
            return $"From {Anchor.ToString("d MMMM", culture)}";
        }
    }

    partial void OnViewModeChanged(string value)
    {
        _settings.Current.CalendarView = value;
        OnPropertyChanged(nameof(IsMonth));
        OnPropertyChanged(nameof(IsWeek));
        OnPropertyChanged(nameof(IsAgenda));
        Rebuild();
    }

    partial void OnAnchorChanged(DateTime value)
    {
        if (MiniSelected?.Date != value.Date) MiniSelected = value.Date;
        Rebuild();
    }

    partial void OnMiniSelectedChanged(DateTime? value)
    {
        if (value.HasValue && value.Value.Date != Anchor.Date) Anchor = value.Value.Date;
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        WeekdayNames = Format.WeekdayShortNames().ToList();
        OnPropertyChanged(nameof(WeekdayNames));
        await LoadAsync();
        if (parameter is DateTime date) Anchor = date.Date;
        else if (parameter is "Month" or "Week" or "Agenda") ViewMode = (string)parameter;
        else if (parameter is "new") await NewEvent(null);
        else if (parameter is "import") await ImportIcs();
        else if (parameter is "export") await ExportIcs();
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var categories = await _store.ListAsync<CalendarCategory>(q => q.OrderBy(c => c.SortOrder));
            _events = await _store.ListAsync<CalendarEvent>();
            _tasks = _settings.Current.ShowTasksInCalendar ? await _store.ListAsync<TaskItem>(q => q.Where(t => t.DueDate != null)) : [];
            _contacts = _settings.Current.ShowBirthdaysInCalendar ? await _store.ListAsync<Contact>(q => q.Where(c => c.Birthday != null)) : [];
            CategoryChoices = [new CalendarCategory { Id = 0, Name = "No calendar", Color = "slate" }, .. categories];
            OnPropertyChanged(nameof(CategoryChoices));

            Calendars.Clear();
            foreach (var c in categories) Calendars.Add(new CalendarFilter(c, c.Name, c.Color, c.IsVisible));
            _loaded = true;
            Rebuild();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private Dictionary<int, CalendarCategory> CategoryMap() =>
        Calendars.Where(c => c.Category != null).ToDictionary(c => c.Category!.Id, c => c.Category!);

    /// <summary>All visible occurrences in a date range.</summary>
    private List<Occurrence> Collect(DateTime from, DateTime to)
    {
        var map = CategoryMap();
        var hidden = Calendars.Where(c => !c.IsVisible && c.Category != null).Select(c => c.Category!.Id).ToHashSet();
        var list = new List<Occurrence>();

        foreach (var e in _events)
        {
            if (e.CategoryId is int cid && hidden.Contains(cid)) continue;
            var color = e.Color ?? (e.CategoryId is int id && map.TryGetValue(id, out var cat) ? cat.Color : "slate");
            foreach (var (start, end) in Recurrence.Occurrences(e, from, to))
                list.Add(new Occurrence { Kind = OccurrenceKind.Event, Event = e, Start = start, End = end, IsAllDay = e.IsAllDay, Title = string.IsNullOrWhiteSpace(e.Title) ? "(no title)" : e.Title, ColorKey = color, Location = e.Location });
        }

        foreach (var t in _tasks)
        {
            if (t.DueDate is not { } due || due.Date < from.Date || due.Date >= to) continue;
            list.Add(new Occurrence { Kind = OccurrenceKind.Task, Task = t, Start = due, End = t.DueHasTime ? due.AddMinutes(30) : due, IsAllDay = !t.DueHasTime, Title = t.Title, ColorKey = "slate" });
        }

        foreach (var c in _contacts)
        {
            if (c.Birthday is not { } b) continue;
            for (int year = from.Year; year <= to.Year; year++)
            {
                var d = new DateTime(year, b.Month, Math.Min(b.Day, DateTime.DaysInMonth(year, b.Month)));
                if (d < from.Date || d >= to) continue;
                var age = b.Year > 1900 ? $" ({year - b.Year})" : "";
                list.Add(new Occurrence { Kind = OccurrenceKind.Birthday, Contact = c, Start = d, End = d, IsAllDay = true, Title = $"{c.FullName}'s birthday{age}", ColorKey = "rose" });
            }
        }

        return list.OrderBy(o => o.IsAllDay ? 0 : 1).ThenBy(o => o.Start).ThenBy(o => o.Title).ToList();
    }

    public void Rebuild()
    {
        if (!_loaded) return;
        OnPropertyChanged(nameof(PeriodTitle));
        if (IsMonth) BuildMonth();
        else if (IsWeek) BuildWeek();
        else BuildAgenda();
        BuildUpcoming();
        BuildMarks();
    }

    private void BuildMonth()
    {
        var first = new DateTime(Anchor.Year, Anchor.Month, 1);
        var start = Format.StartOfWeek(first);
        var occurrences = Collect(start, start.AddDays(42));
        Days.Clear();
        for (int i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            var day = new MonthDay
            {
                Date = date,
                IsCurrentMonth = date.Month == first.Month,
                IsToday = date == DateTime.Today,
                IsWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
            };
            var items = occurrences.Where(o => CoversDay(o, date)).ToList();
            foreach (var o in items.Take(3)) day.Items.Add(o);
            day.More = Math.Max(0, items.Count - 3);
            Days.Add(day);
        }
    }

    private static bool CoversDay(Occurrence o, DateTime date) =>
        o.IsAllDay ? o.Start.Date <= date && o.End.Date >= date
                   : o.Start.Date == date || (o.Start.Date < date && o.End > date);

    private void BuildWeek()
    {
        var start = Format.StartOfWeek(Anchor);
        var occurrences = Collect(start, start.AddDays(7));
        WeekColumns.Clear();
        for (int i = 0; i < 7; i++)
        {
            var date = start.AddDays(i);
            var col = new WeekColumn { Date = date, IsToday = date == DateTime.Today, IsWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday };
            foreach (var o in occurrences.Where(o => CoversDay(o, date)))
            {
                if (o.IsAllDay) col.AllDay.Add(o);
                else if (o.Start.Date == date) col.Timed.Add(o);
            }
            LayoutDay(col.Timed);
            WeekColumns.Add(col);
        }
    }

    /// <summary>Positions overlapping timed events side by side.</summary>
    private static void LayoutDay(List<Occurrence> items)
    {
        var sorted = items.OrderBy(o => o.Start).ThenByDescending(o => o.End - o.Start).ToList();
        var cluster = new List<Occurrence>();
        var columnsEnd = new List<DateTime>();
        DateTime clusterEnd = DateTime.MinValue;

        void Flush()
        {
            int count = Math.Max(1, columnsEnd.Count);
            foreach (var o in cluster) o.Width = 1.0 / count;
            cluster.Clear();
            columnsEnd.Clear();
        }

        foreach (var o in sorted)
        {
            if (cluster.Count > 0 && o.Start >= clusterEnd) Flush();
            int col = columnsEnd.FindIndex(end => end <= o.Start);
            if (col < 0) { col = columnsEnd.Count; columnsEnd.Add(o.End); }
            else columnsEnd[col] = o.End;
            o.Left = col;
            cluster.Add(o);
            if (o.End > clusterEnd) clusterEnd = o.End;

            var minutes = o.Start.TimeOfDay.TotalMinutes;
            var dayEnd = o.Start.Date.AddDays(1);
            var visibleEnd = o.End < dayEnd ? o.End : dayEnd;
            var duration = Math.Max(20, (visibleEnd - o.Start).TotalMinutes);
            o.Top = minutes / 60 * HourHeight;
            o.Height = Math.Max(22, duration / 60 * HourHeight - 2);
        }
        Flush();
        // Convert column index into a fraction once widths are known.
        foreach (var o in sorted) o.Left *= o.Width;
    }

    private void BuildAgenda()
    {
        var from = Anchor.Date;
        var occurrences = Collect(from, from.AddDays(60));
        Agenda.Clear();
        foreach (var g in occurrences.GroupBy(o => o.Start.Date < from ? from : o.Start.Date).OrderBy(g => g.Key))
        {
            var group = new AgendaGroup { Date = g.Key };
            group.Items.AddRange(g);
            Agenda.Add(group);
        }
    }

    private void BuildUpcoming()
    {
        Upcoming.Clear();
        foreach (var o in Collect(DateTime.Today, DateTime.Today.AddDays(8)).Where(o => o.Kind != OccurrenceKind.Task || !o.IsDone)
                     .Where(o => o.IsAllDay || o.End >= DateTime.Now).Take(8))
            Upcoming.Add(o);
        OnPropertyChanged(nameof(HasUpcoming));
    }

    private void BuildMarks()
    {
        var first = new DateTime(Anchor.Year, Anchor.Month, 1).AddDays(-7);
        var dates = Collect(first, first.AddDays(50)).SelectMany(o =>
            o.IsAllDay && o.End.Date > o.Start.Date
                ? Enumerable.Range(0, (o.End.Date - o.Start.Date).Days + 1).Select(i => o.Start.Date.AddDays(i))
                : new[] { o.Start.Date }).Distinct().ToList();
        MarkedDates.Clear();
        foreach (var d in dates) MarkedDates.Add(d);
    }

    // ----- navigation -----

    [RelayCommand]
    private void Previous() => Anchor = IsMonth ? new DateTime(Anchor.Year, Anchor.Month, 1).AddMonths(-1) : Anchor.AddDays(IsWeek ? -7 : -30);

    [RelayCommand]
    private void Next() => Anchor = IsMonth ? new DateTime(Anchor.Year, Anchor.Month, 1).AddMonths(1) : Anchor.AddDays(IsWeek ? 7 : 30);

    [RelayCommand]
    private void Today() => Anchor = DateTime.Today;

    [RelayCommand]
    private void SetView(string? mode)
    {
        if (!string.IsNullOrEmpty(mode)) ViewMode = mode;
    }

    [RelayCommand]
    private void ShowDay(DateTime date)
    {
        Anchor = date;
        ViewMode = "Week";
    }

    // ----- events -----

    public override void OnNewShortcut() => _ = NewEvent(null);

    [RelayCommand]
    private async Task NewEvent(object? at)
    {
        DateTime start;
        bool allDay = false;
        if (at is DateTime dt)
        {
            if (dt.TimeOfDay == TimeSpan.Zero && !IsWeek) { start = dt.Date.AddHours(9); }
            else start = dt;
        }
        else if (at is MonthDay md) start = md.Date.AddHours(9);
        else
        {
            var now = DateTime.Now;
            start = (Anchor.Date == DateTime.Today ? now.Date.AddHours(now.Hour + 1) : Anchor.Date.AddHours(9));
        }
        var e = new CalendarEvent
        {
            Start = start,
            End = start.AddHours(1),
            IsAllDay = allDay,
            CategoryId = Calendars.FirstOrDefault(c => c.IsVisible)?.Category?.Id
        };
        await EditAsync(e, null);
    }

    [RelayCommand]
    private async Task Open(Occurrence? o)
    {
        if (o == null) return;
        switch (o.Kind)
        {
            case OccurrenceKind.Task when o.Task != null:
                _nav.NavigateTo("Tasks", o.Task.Id);
                break;
            case OccurrenceKind.Birthday when o.Contact != null:
                _nav.NavigateTo("Contacts", o.Contact.Id);
                break;
            case OccurrenceKind.Event when o.Event != null:
                await EditAsync(o.Event, o);
                break;
        }
    }

    private async Task EditAsync(CalendarEvent e, Occurrence? occurrence)
    {
        var vm = new EventEditorViewModel(e, CategoryChoices, occurrence?.Start);
        var result = await _dialogs.ShowAsync(vm);
        switch (result as string)
        {
            case "save":
                vm.ApplyTo(e);
                if (e.IsNew)
                {
                    await _store.AddAsync(e);
                    _events.Add(e);
                    _toasts.Success("Event added");
                }
                else
                {
                    e.UpdatedAt = DateTime.Now;
                    await _store.UpdateAsync(e);
                }
                if (!IsVisibleInPeriod(e.Start)) Anchor = e.Start.Date;
                Rebuild();
                break;
            case "delete":
                if (!e.IsNew)
                {
                    await _store.DeleteAsync(e);
                    _events.Remove(e);
                    Rebuild();
                    _toasts.Show("Event deleted", ToastKind.Info, "Undo", async () =>
                    {
                        e.Id = 0;
                        await _store.AddAsync(e);
                        _events.Add(e);
                        Rebuild();
                    });
                }
                break;
            case "delete-one":
                if (occurrence != null)
                {
                    e.AddException(occurrence.Start);
                    await _store.UpdateAsync(e);
                    Rebuild();
                    _toasts.Success("Occurrence removed");
                }
                break;
        }
    }

    private bool IsVisibleInPeriod(DateTime date)
    {
        if (IsMonth) return date.Year == Anchor.Year && date.Month == Anchor.Month;
        if (IsWeek) { var s = Format.StartOfWeek(Anchor); return date >= s && date < s.AddDays(7); }
        return date >= Anchor.Date;
    }

    /// <summary>Drop in month view / all-day row: move to another day keeping the time.</summary>
    [RelayCommand]
    private async Task MoveToDay(DropEventArgs? args)
    {
        if (args?.Data is not Occurrence o) return;
        DateTime? target = args.Target switch
        {
            MonthDay md => md.Date,
            WeekColumn wc => wc.Date,
            _ => null
        };
        if (target == null || target.Value == o.Start.Date) return;
        await MoveAsync(o, target.Value + o.Start.TimeOfDay);
    }

    /// <summary>Drop in the week time grid: new day and time from the pointer position.</summary>
    [RelayCommand]
    private async Task MoveInWeek(DropEventArgs? args)
    {
        if (args?.Data is not Occurrence o || args.Target is not WeekColumn col) return;
        var minutes = (args.Position.Y - args.GrabOffset.Y) / HourHeight * 60;
        minutes = Math.Clamp(Math.Round(minutes / 15) * 15, 0, 24 * 60 - 15);
        var newStart = col.Date.AddMinutes(minutes);
        if (newStart == o.Start) return;
        await MoveAsync(o, newStart, keepAllDay: false);
    }

    public async Task ResizeAsync(Occurrence o, double newHeightPx)
    {
        if (o.Event == null) return;
        var minutes = Math.Max(15, Math.Round(newHeightPx / HourHeight * 60 / 15) * 15);
        var e = o.Event;
        if (e.IsRecurring)
        {
            e.End = e.Start.AddMinutes(minutes);
        }
        else
        {
            e.End = e.Start.AddMinutes(minutes);
        }
        await _store.UpdateAsync(e);
        Rebuild();
    }

    private async Task MoveAsync(Occurrence o, DateTime newStart, bool keepAllDay = true)
    {
        switch (o.Kind)
        {
            case OccurrenceKind.Task when o.Task != null:
                o.Task.DueDate = o.Task.DueHasTime ? newStart : newStart.Date;
                await _store.UpdateAsync(o.Task);
                Rebuild();
                _toasts.Success($"Task moved to {Format.FriendlyDay(newStart)}");
                return;
            case OccurrenceKind.Event when o.Event != null:
                var e = o.Event;
                var duration = o.End - o.Start;
                if (e.IsRecurring)
                {
                    // Detach just this occurrence: skip it in the series and create a standalone copy.
                    e.AddException(o.Start);
                    await _store.UpdateAsync(e);
                    var copy = e.CloneDetached();
                    copy.Start = newStart;
                    copy.End = newStart + duration;
                    if (!keepAllDay) copy.IsAllDay = false;
                    await _store.AddAsync(copy);
                    _events.Add(copy);
                    _toasts.Info("Moved this occurrence only");
                }
                else
                {
                    e.Start = newStart;
                    e.End = newStart + duration;
                    if (!keepAllDay && e.IsAllDay)
                    {
                        e.IsAllDay = false;
                        e.End = newStart.AddHours(1);
                    }
                    e.UpdatedAt = DateTime.Now;
                    await _store.UpdateAsync(e);
                }
                Rebuild();
                return;
        }
    }

    // ----- calendars -----

    [RelayCommand]
    private async Task ToggleCalendar(CalendarFilter? filter)
    {
        if (filter?.Category == null) return;
        filter.Category.IsVisible = filter.IsVisible;
        await _store.UpdateAsync(filter.Category);
        Rebuild();
    }

    [RelayCommand]
    private async Task NewCalendar()
    {
        var name = await _dialogs.PromptAsync("New calendar", "Calendars group events and can be shown or hidden.", "", "e.g. Sport, Travel, School", "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        var colors = Palettes.Tags.Select(t => t.Key).ToList();
        var cat = new CalendarCategory { Name = name, Color = colors[(Calendars.Count + 4) % colors.Count], SortOrder = Calendars.Count };
        await _store.AddAsync(cat);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task RenameCalendar(CalendarFilter? filter)
    {
        if (filter?.Category == null) return;
        var name = await _dialogs.PromptAsync("Rename calendar", null, filter.Category.Name, "Calendar name", "Rename");
        if (string.IsNullOrWhiteSpace(name)) return;
        filter.Category.Name = name;
        filter.Title = name;
        await _store.UpdateAsync(filter.Category);
        await LoadAsync();
    }

    public async Task SetCalendarColorAsync(CalendarFilter filter, string color)
    {
        if (filter.Category == null) return;
        filter.Category.Color = color;
        filter.ColorKey = color;
        await _store.UpdateAsync(filter.Category);
        Rebuild();
    }

    [RelayCommand]
    private async Task DeleteCalendar(CalendarFilter? filter)
    {
        if (filter?.Category == null) return;
        int count = _events.Count(e => e.CategoryId == filter.Category.Id);
        if (!await _dialogs.ConfirmAsync("Delete calendar", count == 0
                ? $"Delete “{filter.Category.Name}”?"
                : $"Delete “{filter.Category.Name}” and its {count} event(s)?", "Delete", danger: true)) return;
        var toDelete = _events.Where(e => e.CategoryId == filter.Category.Id).ToList();
        await _store.DeleteRangeAsync(toDelete);
        await _store.DeleteAsync(filter.Category);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ImportIcs()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Import calendar", Filter = "iCalendar (*.ics)|*.ics" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var imported = IcsService.Parse(await System.IO.File.ReadAllTextAsync(dialog.FileName));
            int? cat = Calendars.FirstOrDefault()?.Category?.Id;
            foreach (var e in imported) e.CategoryId = cat;
            await _store.AddRangeAsync(imported);
            await LoadAsync();
            _toasts.Success($"Imported {imported.Count} event(s)");
        }
        catch (Exception ex)
        {
            _toasts.Error("Couldn't read that file: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task ExportIcs()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Export calendar", FileName = "Satchel calendar.ics", Filter = "iCalendar (*.ics)|*.ics" };
        if (dialog.ShowDialog() != true) return;
        await System.IO.File.WriteAllTextAsync(dialog.FileName, IcsService.Build(_events));
        _toasts.Success($"Exported {_events.Count} event(s) (unencrypted)");
    }

    public void Dispose() => _clock.Stop();
}
