using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Services;
using Satchel.ViewModels.Dialogs;

namespace Satchel.ViewModels;

/// <summary>A world clock row (time in another time zone).</summary>
public sealed partial class WorldClockRow : ObservableObject
{
    public WorldClockRow(WorldClock clock, TimeZoneInfo zone)
    {
        Clock = clock;
        Zone = zone;
        Update();
    }

    public WorldClock Clock { get; }
    public TimeZoneInfo Zone { get; }
    public string Label => string.IsNullOrWhiteSpace(Clock.Label) ? ClockViewModel.CityName(Zone) : Clock.Label;

    [ObservableProperty] private string _time = "";
    [ObservableProperty] private string _dayText = "";
    [ObservableProperty] private string _offsetText = "";
    [ObservableProperty] private bool _isNight;
    /// <summary>0..1 position of the local time in the day (for the little day bar).</summary>
    [ObservableProperty] private double _dayFraction;

    public void Update()
    {
        var now = TimeZoneInfo.ConvertTime(DateTime.Now, TimeZoneInfo.Local, Zone);
        Time = Format.Time(now);
        var days = (now.Date - DateTime.Today).Days;
        DayText = days switch { 0 => "Today", 1 => "Tomorrow", -1 => "Yesterday", _ => now.ToString("ddd", CultureInfo.CurrentCulture) };
        var diff = Zone.GetUtcOffset(DateTime.UtcNow) - TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow);
        OffsetText = diff == TimeSpan.Zero ? "Same time" : $"{(diff > TimeSpan.Zero ? "+" : "−")}{Math.Abs(diff.TotalHours):0.#} h";
        IsNight = now.Hour < 6 || now.Hour >= 21;
        DayFraction = now.TimeOfDay.TotalHours / 24;
    }
}

/// <summary>Clock: countdown timers, a stopwatch with laps, world clocks and countdowns to dates.</summary>
public sealed partial class ClockViewModel : PageViewModel, IDisposable
{
    private readonly SettingsService _settings;
    private readonly DataStore _store;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly DispatcherTimer _tick;

    public ClockViewModel(INavigator nav, TimerService timers, SettingsService settings, DataStore store, DialogService dialogs, ToastService toasts)
    {
        Timers = timers;
        _settings = settings;
        _store = store;
        _dialogs = dialogs;
        _toasts = toasts;
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => TickClocks();
        BuildClocks();
        TickClocks();
        _tick.Start();
    }

    public override string Title => "Clock";
    public TimerService Timers { get; }
    public ObservableCollection<WorldClockRow> Clocks { get; } = [];
    public ObservableCollection<Countdown> Countdowns { get; } = [];
    public static IReadOnlyList<int> Presets { get; } = [1, 3, 5, 10, 15, 25, 45, 60];

    [ObservableProperty] private string _newTimer = "";
    [ObservableProperty] private string _localTime = "";
    [ObservableProperty] private string _localDate = "";
    [ObservableProperty] private string _localZone = "";

    public bool HasTimers => Timers.Timers.Count > 0;
    public bool HasCountdowns => Countdowns.Count > 0;
    public bool HasLaps => Timers.Laps.Count > 0;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        await LoadCountdownsAsync();
        Timers.Timers.CollectionChanged += OnTimersChanged;
        Timers.Laps.CollectionChanged += OnLapsChanged;
        OnPropertyChanged(nameof(HasTimers));
        OnPropertyChanged(nameof(HasLaps));
        if (parameter is "countdown") await AddCountdown();
    }

    public override Task OnNavigatedFromAsync()
    {
        Timers.Timers.CollectionChanged -= OnTimersChanged;
        Timers.Laps.CollectionChanged -= OnLapsChanged;
        return base.OnNavigatedFromAsync();
    }

    private void OnTimersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => OnPropertyChanged(nameof(HasTimers));
    private void OnLapsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => OnPropertyChanged(nameof(HasLaps));

    private void TickClocks()
    {
        var now = DateTime.Now;
        LocalTime = Format.Use24Hour ? now.ToString("HH:mm:ss", CultureInfo.CurrentCulture) : now.ToString("h:mm:ss tt", CultureInfo.CurrentCulture);
        LocalDate = now.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture);
        LocalZone = CityName(TimeZoneInfo.Local) + " · " + TimeZoneInfo.Local.StandardName;
        foreach (var c in Clocks) c.Update();
        if (now.Second == 0) foreach (var c in Countdowns) c.Refresh();
    }

    public override void OnNewShortcut() => StartCustom();

    // ----- timers -----

    [RelayCommand]
    private void StartPreset(int minutes)
    {
        Timers.Start(minutes * 60, "");
    }

    [RelayCommand]
    private void StartCustom()
    {
        var text = NewTimer.Trim();
        if (text.Length == 0) { _toasts.Info("Type a duration — 25, 10m, 1h30, 90s — and an optional label"); return; }
        var parts = text.Split(' ', 2);
        var duration = TimerService.ParseDuration(parts[0]);
        var label = parts.Length > 1 ? parts[1] : "";
        if (duration == null && TimerService.ParseDuration(text) is { } whole) { duration = whole; label = ""; }
        if (duration == null) { _toasts.Warning("Couldn't read that duration — try 25, 10m or 1h30"); return; }
        Timers.Start(duration.Value.TotalSeconds, label);
        NewTimer = "";
    }

    [RelayCommand]
    private async Task RenameTimer(CountdownTimer? timer)
    {
        if (timer == null) return;
        var label = await _dialogs.PromptAsync("Timer label", null, timer.State.Label, "e.g. Tea, Focus, Laundry", "Save");
        if (label == null) return;
        timer.State.Label = label.Trim();
        timer.RaiseLabel();
        _settings.ScheduleSave();
    }

    // ----- world clocks -----

    private void BuildClocks()
    {
        Clocks.Clear();
        foreach (var c in _settings.Current.WorldClocks)
        {
            try { Clocks.Add(new WorldClockRow(c, TimeZoneInfo.FindSystemTimeZoneById(c.ZoneId))); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
    }

    [RelayCommand]
    private async Task AddClock()
    {
        var options = TimeZoneInfo.GetSystemTimeZones()
            .Select(z => new PickOption(CityName(z), z.DisplayName, Glyphs.World, z))
            .ToList();
        var picked = await _dialogs.ShowAsync(new PickDialogViewModel("Add a clock", "Search for a city or a time zone.", options, "City or zone"));
        if (picked is not PickOption { Value: TimeZoneInfo zone }) return;
        var clock = new WorldClock { ZoneId = zone.Id, Label = CityName(zone) };
        _settings.Current.WorldClocks = [.. _settings.Current.WorldClocks, clock];
        _settings.ScheduleSave();
        Clocks.Add(new WorldClockRow(clock, zone));
    }

    [RelayCommand]
    private async Task RenameClock(WorldClockRow? row)
    {
        if (row == null) return;
        var label = await _dialogs.PromptAsync("Clock name", null, row.Label, "e.g. Mum, Office, Tokyo", "Save");
        if (string.IsNullOrWhiteSpace(label)) return;
        row.Clock.Label = label.Trim();
        _settings.ScheduleSave();
        BuildClocks();
    }

    [RelayCommand]
    private void RemoveClock(WorldClockRow? row)
    {
        if (row == null) return;
        _settings.Current.WorldClocks = _settings.Current.WorldClocks.Where(c => !ReferenceEquals(c, row.Clock)).ToList();
        _settings.ScheduleSave();
        Clocks.Remove(row);
    }

    /// <summary>"(UTC+01:00) Belgrade, Bratislava, Budapest…" → "Belgrade".</summary>
    public static string CityName(TimeZoneInfo zone)
    {
        var name = zone.DisplayName;
        var close = name.IndexOf(')');
        if (close >= 0) name = name[(close + 1)..].Trim();
        var comma = name.IndexOf(',');
        if (comma > 0) name = name[..comma];
        return name.Trim();
    }

    // ----- countdowns -----

    private async Task LoadCountdownsAsync()
    {
        var items = await _store.ListAsync<Countdown>();
        Countdowns.Clear();
        foreach (var c in items.OrderBy(c => c.IsPast).ThenBy(c => Math.Abs(c.DaysLeft))) Countdowns.Add(c);
        OnPropertyChanged(nameof(HasCountdowns));
    }

    [RelayCommand]
    private async Task AddCountdown()
    {
        var dialog = new CountdownDialogViewModel(new Countdown());
        if (await _dialogs.ShowAsync(dialog) is not true) return;
        await _store.AddAsync(dialog.Item);
        await LoadCountdownsAsync();
        _toasts.Success($"{dialog.Item.Title}: {dialog.Item.DaysText}");
    }

    [RelayCommand]
    private async Task EditCountdown(Countdown? item)
    {
        if (item == null) return;
        var dialog = new CountdownDialogViewModel(item);
        var result = await _dialogs.ShowAsync(dialog);
        if (result is "delete")
        {
            await _store.DeleteAsync(item);
            Countdowns.Remove(item);
            OnPropertyChanged(nameof(HasCountdowns));
            return;
        }
        if (result is not true) return;
        await _store.UpdateAsync(item);
        await LoadCountdownsAsync();
    }

    public void Dispose()
    {
        _tick.Stop();
        Timers.Timers.CollectionChanged -= OnTimersChanged;
        Timers.Laps.CollectionChanged -= OnLapsChanged;
    }
}
