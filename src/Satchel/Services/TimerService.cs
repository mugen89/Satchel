using System.Collections.ObjectModel;
using System.Globalization;
using System.Media;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Core;

namespace Satchel.Services;

/// <summary>A countdown timer. Runs in the background (any page, even while locked) and rings when it reaches zero.</summary>
public sealed partial class CountdownTimer : ObservableObject
{
    public CountdownTimer(TimerState state)
    {
        State = state;
        _remaining = state.EndsAt is { } end ? Math.Max(0, (end - DateTime.Now).TotalSeconds) : (state.Remaining > 0 ? state.Remaining : state.Seconds);
        _isRunning = state.EndsAt != null;
    }

    public TimerState State { get; }
    public string Label => string.IsNullOrWhiteSpace(State.Label) ? Format(State.Seconds) + " timer" : State.Label;
    public double Total => State.Seconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemainingText), nameof(Fraction), nameof(ShortText))]
    private double _remaining;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isRinging;

    /// <summary>Share of the time still left (1 → 0), for progress rings.</summary>
    public double Fraction => Total > 0 ? Math.Clamp(Remaining / Total, 0, 1) : 0;
    public string RemainingText => Format(Math.Ceiling(Remaining));
    public string ShortText => Format(Math.Ceiling(Remaining));
    public string StatusText => IsRinging ? "Time's up!" : IsRunning && State.EndsAt is { } end ? $"Rings at {Core.Format.Time(end)}" : Remaining <= 0 ? "Done" : "Paused";

    public void RaiseLabel() => OnPropertyChanged(nameof(Label));

    public static string Format(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}

public sealed record LapRow(int Number, string Split, string Total, bool IsBest, bool IsWorst);

/// <summary>Countdown timers and a stopwatch that keep running while you use other pages (and across restarts).</summary>
public sealed partial class TimerService : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly ToastService _toasts;
    private readonly DispatcherTimer _tick;

    public TimerService(SettingsService settings, ToastService toasts)
    {
        _settings = settings;
        _toasts = toasts;
        _tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _tick.Tick += (_, _) => Tick();
        foreach (var state in settings.Current.Timers) Timers.Add(new CountdownTimer(state));
        foreach (var t in Timers.Where(t => t.IsRunning && t.Remaining <= 0)) Ring(t, missed: true);
        RebuildLaps();
        UpdateTicking();
        RaiseSummary();
    }

    public ObservableCollection<CountdownTimer> Timers { get; } = [];
    public ObservableCollection<LapRow> Laps { get; } = [];

    private StopwatchState Watch => _settings.Current.Stopwatch;

    public bool StopwatchRunning => Watch.StartedAt != null;
    public double StopwatchSeconds => Watch.ElapsedBefore + (Watch.StartedAt is { } s ? (DateTime.Now - s).TotalSeconds : 0);
    public string StopwatchText => FormatPrecise(StopwatchSeconds);
    public bool HasStopwatch => StopwatchRunning || StopwatchSeconds > 0;

    /// <summary>The timer that rings next (for the top-bar pill).</summary>
    public CountdownTimer? Next => Timers.Where(t => t.IsRinging).FirstOrDefault() ?? Timers.Where(t => t.IsRunning).OrderBy(t => t.Remaining).FirstOrDefault();
    public bool HasActive => Next != null || StopwatchRunning;
    public string PillText => Next is { } t ? (t.IsRinging ? $"{t.Label} — time's up" : $"{t.ShortText} · {t.Label}") : StopwatchRunning ? $"{FormatShort(StopwatchSeconds)} · Stopwatch" : "";
    public bool PillRinging => Next?.IsRinging == true;

    public event Action<CountdownTimer>? Rang;

    // ----- countdown timers -----

    public CountdownTimer Start(double seconds, string label = "")
    {
        seconds = Math.Clamp(seconds, 1, 100 * 3600);
        var state = new TimerState { Label = label.Trim(), Seconds = seconds, EndsAt = DateTime.Now.AddSeconds(seconds) };
        var timer = new CountdownTimer(state);
        Timers.Insert(0, timer);
        Save();
        UpdateTicking();
        RaiseSummary();
        return timer;
    }

    [RelayCommand]
    public void Pause(CountdownTimer? t)
    {
        if (t is not { IsRunning: true }) return;
        t.State.Remaining = Math.Max(0, (t.State.EndsAt!.Value - DateTime.Now).TotalSeconds);
        t.State.EndsAt = null;
        t.Remaining = t.State.Remaining;
        t.IsRunning = false;
        Save();
        RaiseSummary();
    }

    [RelayCommand]
    public void Resume(CountdownTimer? t)
    {
        if (t == null || t.IsRunning) return;
        if (t.Remaining <= 0) t.Remaining = t.Total;
        t.IsRinging = false;
        t.State.EndsAt = DateTime.Now.AddSeconds(t.Remaining);
        t.IsRunning = true;
        Save();
        UpdateTicking();
        RaiseSummary();
    }

    [RelayCommand]
    public void Toggle(CountdownTimer? t)
    {
        if (t == null) return;
        if (t.IsRunning) Pause(t);
        else Resume(t);
    }

    [RelayCommand]
    public void Restart(CountdownTimer? t)
    {
        if (t == null) return;
        t.IsRinging = false;
        t.Remaining = t.Total;
        t.State.Remaining = t.Total;
        t.State.EndsAt = DateTime.Now.AddSeconds(t.Total);
        t.IsRunning = true;
        Save();
        UpdateTicking();
        RaiseSummary();
    }

    [RelayCommand]
    public void Reset(CountdownTimer? t)
    {
        if (t == null) return;
        t.IsRinging = false;
        t.IsRunning = false;
        t.State.EndsAt = null;
        t.State.Remaining = t.Total;
        t.Remaining = t.Total;
        Save();
        RaiseSummary();
    }

    [RelayCommand]
    public void AddMinute(CountdownTimer? t)
    {
        if (t == null) return;
        t.IsRinging = false;
        if (t.IsRunning && t.State.EndsAt is { } end) t.State.EndsAt = end.AddMinutes(1);
        else if (!t.IsRunning) t.State.Remaining = t.Remaining + 60;
        if (t.Remaining + 60 > t.State.Seconds) t.State.Seconds = t.Remaining + 60;
        t.Remaining += 60;
        if (!t.IsRunning && t.Remaining > 0) Resume(t);
        Save();
        RaiseSummary();
    }

    [RelayCommand]
    public void Remove(CountdownTimer? t)
    {
        if (t == null) return;
        Timers.Remove(t);
        Save();
        UpdateTicking();
        RaiseSummary();
    }

    [RelayCommand]
    public void Dismiss(CountdownTimer? t)
    {
        if (t == null) return;
        t.IsRinging = false;
        RaiseSummary();
    }

    /// <summary>Stops the ringing on all timers (top-bar pill click).</summary>
    public void DismissAll()
    {
        foreach (var t in Timers.Where(t => t.IsRinging)) t.IsRinging = false;
        RaiseSummary();
    }

    // ----- stopwatch -----

    [RelayCommand]
    public void ToggleStopwatch()
    {
        if (Watch.StartedAt is { } s)
        {
            Watch.ElapsedBefore += (DateTime.Now - s).TotalSeconds;
            Watch.StartedAt = null;
        }
        else Watch.StartedAt = DateTime.Now;
        _settings.ScheduleSave();
        UpdateTicking();
        RaiseStopwatch();
        RaiseSummary();
    }

    [RelayCommand]
    public void Lap()
    {
        if (!StopwatchRunning) return;
        Watch.Laps.Add(StopwatchSeconds);
        _settings.ScheduleSave();
        RebuildLaps();
    }

    [RelayCommand]
    public void ResetStopwatch()
    {
        Watch.StartedAt = null;
        Watch.ElapsedBefore = 0;
        Watch.Laps.Clear();
        _settings.ScheduleSave();
        RebuildLaps();
        UpdateTicking();
        RaiseStopwatch();
        RaiseSummary();
    }

    private void RebuildLaps()
    {
        Laps.Clear();
        var totals = Watch.Laps;
        var splits = totals.Select((t, i) => t - (i == 0 ? 0 : totals[i - 1])).ToList();
        double best = splits.Count > 1 ? splits.Min() : -1, worst = splits.Count > 1 ? splits.Max() : -1;
        for (int i = totals.Count - 1; i >= 0; i--)
            Laps.Add(new LapRow(i + 1, FormatPrecise(splits[i]), FormatPrecise(totals[i]), splits[i] == best, splits[i] == worst));
    }

    // ----- ticking -----

    private void UpdateTicking()
    {
        bool needed = StopwatchRunning || Timers.Any(t => t.IsRunning);
        if (needed && !_tick.IsEnabled) _tick.Start();
        else if (!needed && _tick.IsEnabled) _tick.Stop();
    }

    private void Tick()
    {
        foreach (var t in Timers.Where(t => t.IsRunning).ToList())
        {
            var left = Math.Max(0, (t.State.EndsAt!.Value - DateTime.Now).TotalSeconds);
            t.Remaining = left;
            if (left <= 0) Ring(t, missed: false);
        }
        if (StopwatchRunning) RaiseStopwatch();
        RaiseSummary();
        UpdateTicking();
    }

    private void Ring(CountdownTimer t, bool missed)
    {
        t.IsRunning = false;
        t.IsRinging = !missed;
        t.State.EndsAt = null;
        t.State.Remaining = 0;
        t.Remaining = 0;
        Save();
        Rang?.Invoke(t);
        var when = missed ? "while Satchel was closed" : "";
        _toasts.Show(missed ? $"Rang {when}." : "Time's up!", ToastKind.Info, "Restart", () => Restart(t), durationMs: missed ? 6000 : 20000, title: "⏰ " + t.Label);
        if (missed) return;
        if (_settings.Current.TimerSound) _ = ChimeAsync();
        WindowFlash.Flash();
    }

    private static async Task ChimeAsync()
    {
        for (int i = 0; i < 3; i++)
        {
            SystemSounds.Exclamation.Play();
            await Task.Delay(900);
        }
    }

    private void RaiseStopwatch()
    {
        OnPropertyChanged(nameof(StopwatchRunning));
        OnPropertyChanged(nameof(StopwatchSeconds));
        OnPropertyChanged(nameof(StopwatchText));
        OnPropertyChanged(nameof(HasStopwatch));
    }

    private void RaiseSummary()
    {
        OnPropertyChanged(nameof(Next));
        OnPropertyChanged(nameof(HasActive));
        OnPropertyChanged(nameof(PillText));
        OnPropertyChanged(nameof(PillRinging));
    }

    private void Save()
    {
        _settings.Current.Timers = Timers.Select(t => t.State).ToList();
        _settings.ScheduleSave();
    }

    // ----- parsing & formatting -----

    /// <summary>"25" (minutes), "90s", "1h30", "1h 30m", "2.5m", "5:00", "1:30:00" → a duration.</summary>
    public static TimeSpan? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().ToLowerInvariant().Replace(',', '.');
        if (text.Contains(':'))
        {
            var parts = text.Split(':');
            double total = 0;
            foreach (var p in parts)
            {
                if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return null;
                total = total * 60 + v;
            }
            return total > 0 ? TimeSpan.FromSeconds(total) : null;
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes)) return minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;
        double seconds = 0;
        bool any = false;
        foreach (Match m in DurationPart().Matches(text))
        {
            any = true;
            var value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var unit = m.Groups[2].Value;
            seconds += unit.StartsWith('h') ? value * 3600 : unit.StartsWith('s') ? value : value * 60;
        }
        return any && seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(h(?:ours?|rs?)?|m(?:in(?:ute)?s?)?|s(?:ec(?:ond)?s?)?)?")]
    private static partial Regex DurationPart();

    public static string FormatPrecise(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}" : $"{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}";
    }

    public static string FormatShort(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    }
}

/// <summary>Flashes Satchel's taskbar button when it isn't the active window.</summary>
public static class WindowFlash
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    public static void Flash()
    {
        var window = Application.Current?.MainWindow;
        if (window == null || window.IsActive) return;
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Hwnd = new WindowInteropHelper(window).Handle,
            Flags = 3 | 12, // FLASHW_ALL | FLASHW_TIMERNOFG
            Count = 5,
            Timeout = 0
        };
        FlashWindowEx(ref info);
    }
}
