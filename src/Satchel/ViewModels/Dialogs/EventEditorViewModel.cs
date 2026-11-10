using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Core;
using Satchel.Models;
using Satchel.Services;

namespace Satchel.ViewModels.Dialogs;

public sealed partial class WeekdayToggle : ObservableObject
{
    public DayOfWeek Day { get; init; }
    public string Label { get; init; } = "";
    [ObservableProperty] private bool _isOn;
}

public sealed partial class EventEditorViewModel : DialogViewModel
{
    private bool _syncing;
    private TimeSpan _duration;

    public EventEditorViewModel(CalendarEvent e, IReadOnlyList<CalendarCategory> categories, DateTime? occurrenceStart)
    {
        Categories = categories;
        IsNew = e.IsNew;
        IsRecurringExisting = !e.IsNew && e.IsRecurring && occurrenceStart.HasValue;
        OccurrenceText = occurrenceStart.HasValue ? Format.FriendlyDay(occurrenceStart.Value) : "";

        _title = e.Title;
        _location = e.Location;
        _description = e.Description;
        _category = categories.FirstOrDefault(c => c.Id == (e.CategoryId ?? 0)) ?? categories.FirstOrDefault();
        _isAllDay = e.IsAllDay;
        _startDate = e.Start.Date;
        _startTime = e.IsAllDay ? TimeSpan.FromHours(9) : e.Start.TimeOfDay;
        _endDate = e.End.Date;
        _endTime = e.IsAllDay ? TimeSpan.FromHours(10) : e.End.TimeOfDay;
        _duration = e.IsAllDay ? TimeSpan.FromHours(1) : e.End - e.Start;
        if (_duration <= TimeSpan.Zero) _duration = TimeSpan.FromHours(1);
        _recurrence = e.Recurrence;
        _interval = Math.Max(1, e.RecurrenceInterval);
        _until = e.RecurrenceUntil;
        _count = e.RecurrenceCount ?? 10;
        _endMode = e.RecurrenceCount is > 0 ? "count" : e.RecurrenceUntil.HasValue ? "until" : "never";
        _reminder = e.ReminderMinutes ?? -1;
        _colorKey = e.Color;
        _isPrivate = e.IsPrivate;

        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        int mask = e.RecurrenceDays == 0 ? 1 << (int)e.Start.DayOfWeek : e.RecurrenceDays;
        for (int i = 0; i < 7; i++)
        {
            var dow = (DayOfWeek)(((int)Format.FirstDayOfWeek + i) % 7);
            Weekdays.Add(new WeekdayToggle { Day = dow, Label = names[(int)dow], IsOn = (mask & (1 << (int)dow)) != 0 });
        }
    }

    public override double DialogWidth => 560;

    public IReadOnlyList<CalendarCategory> Categories { get; }
    public ObservableCollection<WeekdayToggle> Weekdays { get; } = [];
    public bool IsNew { get; }
    public bool IsRecurringExisting { get; }
    public string OccurrenceText { get; }
    public string HeaderText => IsNew ? "New event" : "Edit event";

    public static IReadOnlyList<Choice<RecurrenceFrequency>> RepeatChoices { get; } =
    [
        new(RecurrenceFrequency.None, "Does not repeat"), new(RecurrenceFrequency.Daily, "Daily"), new(RecurrenceFrequency.Weekly, "Weekly"),
        new(RecurrenceFrequency.Monthly, "Monthly"), new(RecurrenceFrequency.Yearly, "Yearly")
    ];

    public static IReadOnlyList<Choice<int>> ReminderChoices { get; } =
    [
        new(-1, "No reminder"), new(0, "At start time"), new(5, "5 minutes before"), new(10, "10 minutes before"), new(15, "15 minutes before"),
        new(30, "30 minutes before"), new(60, "1 hour before"), new(120, "2 hours before"), new(1440, "1 day before"), new(10080, "1 week before")
    ];

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _location;
    [ObservableProperty] private string _description;
    [ObservableProperty] private CalendarCategory? _category;
    [ObservableProperty] private bool _isAllDay;
    [ObservableProperty] private DateTime? _startDate;
    [ObservableProperty] private TimeSpan _startTime;
    [ObservableProperty] private DateTime? _endDate;
    [ObservableProperty] private TimeSpan _endTime;
    [ObservableProperty] private RecurrenceFrequency _recurrence;
    [ObservableProperty] private int _interval;
    [ObservableProperty] private string _endMode;
    [ObservableProperty] private DateTime? _until;
    [ObservableProperty] private int _count;
    [ObservableProperty] private int _reminder;
    [ObservableProperty] private string? _colorKey;
    [ObservableProperty] private bool _isPrivate;
    [ObservableProperty] private string? _error;

    public bool IsRepeating => Recurrence != RecurrenceFrequency.None;
    public bool IsWeekly => Recurrence == RecurrenceFrequency.Weekly;
    public bool HasCustomColor => !string.IsNullOrEmpty(ColorKey);
    public string IntervalUnit => Recurrence switch
    {
        RecurrenceFrequency.Daily => Interval == 1 ? "day" : "days",
        RecurrenceFrequency.Weekly => Interval == 1 ? "week" : "weeks",
        RecurrenceFrequency.Monthly => Interval == 1 ? "month" : "months",
        RecurrenceFrequency.Yearly => Interval == 1 ? "year" : "years",
        _ => ""
    };

    public string Summary
    {
        get
        {
            var probe = new CalendarEvent { Start = (StartDate ?? DateTime.Today) + StartTime, Recurrence = Recurrence, RecurrenceInterval = Interval, RecurrenceDays = DaysMask() };
            if (EndMode == "until") probe.RecurrenceUntil = Until;
            if (EndMode == "count") probe.RecurrenceCount = Count;
            return Services.Recurrence.Describe(probe);
        }
    }

    public string EffectiveColorKey => ColorKey ?? Category?.Color ?? "teal";

    partial void OnRecurrenceChanged(RecurrenceFrequency value) => RaiseRepeat();
    partial void OnIntervalChanged(int value) => RaiseRepeat();
    partial void OnEndModeChanged(string value) => RaiseRepeat();
    partial void OnUntilChanged(DateTime? value) => RaiseRepeat();
    partial void OnCountChanged(int value) => RaiseRepeat();
    partial void OnColorKeyChanged(string? value) { OnPropertyChanged(nameof(HasCustomColor)); OnPropertyChanged(nameof(EffectiveColorKey)); }
    partial void OnCategoryChanged(CalendarCategory? value) => OnPropertyChanged(nameof(EffectiveColorKey));

    private void RaiseRepeat()
    {
        OnPropertyChanged(nameof(IsRepeating));
        OnPropertyChanged(nameof(IsWeekly));
        OnPropertyChanged(nameof(IntervalUnit));
        OnPropertyChanged(nameof(Summary));
    }

    partial void OnStartDateChanged(DateTime? value) => KeepDuration();
    partial void OnStartTimeChanged(TimeSpan value) => KeepDuration();

    private void KeepDuration()
    {
        if (_syncing || StartDate == null) return;
        _syncing = true;
        var end = StartDate.Value + StartTime + _duration;
        if (IsAllDay && EndDate < StartDate) EndDate = StartDate;
        else if (!IsAllDay) { EndDate = end.Date; EndTime = end.TimeOfDay; }
        _syncing = false;
        OnPropertyChanged(nameof(Summary));
    }

    partial void OnEndDateChanged(DateTime? value) => UpdateDuration();
    partial void OnEndTimeChanged(TimeSpan value) => UpdateDuration();

    private void UpdateDuration()
    {
        if (_syncing || StartDate == null || EndDate == null) return;
        var d = (EndDate.Value + EndTime) - (StartDate.Value + StartTime);
        if (d > TimeSpan.Zero) _duration = d;
    }

    private int DaysMask() => Weekdays.Where(w => w.IsOn).Aggregate(0, (m, w) => m | (1 << (int)w.Day));

    [RelayCommand]
    private void ToggleWeekday(WeekdayToggle? day)
    {
        if (day == null) return;
        day.IsOn = !day.IsOn;
        OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand]
    private void SetColor(string? key) => ColorKey = string.IsNullOrEmpty(key) ? null : key;

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Title)) { Error = "Give the event a title."; return; }
        if (StartDate == null) { Error = "Pick a start date."; return; }
        var start = IsAllDay ? StartDate.Value.Date : StartDate.Value + StartTime;
        var end = IsAllDay ? (EndDate ?? StartDate).Value.Date : (EndDate ?? StartDate).Value + EndTime;
        if (end < start) { Error = "The event ends before it starts."; return; }
        if (IsWeekly && DaysMask() == 0) { Error = "Pick at least one weekday."; return; }
        Close("save");
    }

    [RelayCommand]
    private void Delete() => Close("delete");

    [RelayCommand]
    private void DeleteOne() => Close("delete-one");

    public void ApplyTo(CalendarEvent e)
    {
        e.Title = Title.Trim();
        e.IsPrivate = IsPrivate;
        e.Location = Location?.Trim() ?? "";
        e.Description = Description ?? "";
        e.CategoryId = Category == null || Category.Id == 0 ? null : Category.Id;
        e.IsAllDay = IsAllDay;
        var startDate = StartDate ?? DateTime.Today;
        var endDate = EndDate ?? startDate;
        e.Start = IsAllDay ? startDate.Date : startDate.Date + StartTime;
        e.End = IsAllDay ? endDate.Date : endDate.Date + EndTime;
        if (e.End < e.Start) e.End = e.Start;
        e.Recurrence = Recurrence;
        e.RecurrenceInterval = Math.Max(1, Interval);
        e.RecurrenceDays = Recurrence == RecurrenceFrequency.Weekly ? DaysMask() : 0;
        e.RecurrenceUntil = Recurrence != RecurrenceFrequency.None && EndMode == "until" ? Until : null;
        e.RecurrenceCount = Recurrence != RecurrenceFrequency.None && EndMode == "count" ? Math.Max(1, Count) : null;
        e.ReminderMinutes = Reminder < 0 ? null : Reminder;
        e.Color = ColorKey;
    }
}
