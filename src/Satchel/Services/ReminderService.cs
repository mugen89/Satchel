using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;

namespace Satchel.Services;

public sealed record ReminderInfo(string Title, string Message, string Page, object? Parameter);

/// <summary>Checks upcoming events and tasks every 30 seconds while unlocked and raises reminders.</summary>
public sealed class ReminderService
{
    private readonly DataStore _store;
    private readonly SettingsService _settings;
    private readonly ToastService _toasts;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly HashSet<string> _fired = [];
    private bool _checking;

    public event Action<ReminderInfo>? OpenRequested;

    public ReminderService(DataStore store, SettingsService settings, ToastService toasts)
    {
        _store = store;
        _settings = settings;
        _toasts = toasts;
        _timer.Tick += async (_, _) => await CheckAsync();
    }

    public void Start()
    {
        _timer.Start();
        _ = CheckAsync();
    }

    public void Stop()
    {
        _timer.Stop();
        _fired.Clear();
    }

    private async Task CheckAsync()
    {
        if (_checking || !Security.Crypto.IsUnlocked) return;
        _checking = true;
        try
        {
            var now = DateTime.Now;
            var events = await _store.ListAsync<CalendarEvent>(q => q.Where(e => e.ReminderMinutes != null));
            foreach (var e in events)
            {
                foreach (var (start, _) in Recurrence.Occurrences(e, now.AddHours(-2), now.AddDays(2)))
                {
                    var trigger = start.AddMinutes(-(e.ReminderMinutes ?? 0));
                    if (trigger > now || now - trigger > TimeSpan.FromMinutes(45) || start < now.AddMinutes(-15)) continue;
                    var key = $"e{e.Id}:{start:O}";
                    if (!_fired.Add(key)) continue;
                    var minutes = (int)Math.Round((start - now).TotalMinutes);
                    var when = minutes <= 0 ? "now" : minutes < 60 ? $"in {minutes} min" : $"at {Format.Time(start)}";
                    Raise(new ReminderInfo(e.Title, $"{(e.IsAllDay ? "Today" : Format.Time(start))} · starts {when}", "Calendar", start.Date));
                }
            }

            var tasks = await _store.ListAsync<TaskItem>(q => q.Where(t => t.ReminderAt != null && t.State != TaskState.Done));
            foreach (var t in tasks)
            {
                if (t.ReminderAt is not { } at || at > now || now - at > TimeSpan.FromHours(1)) continue;
                if (!_fired.Add($"t{t.Id}:{at:O}")) continue;
                Raise(new ReminderInfo(t.Title, t.HasDue ? $"Task due {t.DueText}" : "Task reminder", "Tasks", t.Id));
            }

            // Subscriptions and bills: once per due date, RemindDays ahead (0 = never). Remembered in the
            // settings so a payment isn't announced again after every unlock or restart.
            var subs = await _store.ListAsync<Subscription>(q => q.Where(s => s.IsActive && s.RemindDays > 0));
            var announced = _settings.Current.RemindedPayments;
            foreach (var s in subs)
            {
                int days = s.DaysUntilDue;
                var key = $"{s.Id}:{s.NextDue:yyyy-MM-dd}";
                if (days > s.RemindDays || days < -7 || announced.Contains(key) || !_fired.Add("s" + key)) continue;
                // Keep the list short: drop entries for due dates more than two months old.
                var cutoff = DateTime.Today.AddDays(-60).ToString("yyyy-MM-dd");
                announced = [.. announced.Where(k => string.CompareOrdinal(k[(k.IndexOf(':') + 1)..], cutoff) >= 0), key];
                _settings.Current.RemindedPayments = announced;
                _settings.ScheduleSave();
                var when = days switch
                {
                    < -1 => $"was due {-days} days ago",
                    -1 => "was due yesterday",
                    0 => "is due today",
                    1 => "is due tomorrow",
                    _ => $"is due in {days} days"
                };
                var what = s.Kind == MoneyKind.Bill ? "Bill" : "Payment";
                Raise(new ReminderInfo(s.Name, $"{what} of {Subscription.FormatMoney(s.Amount, s.Currency)} {when}{(s.AutoPay ? " · auto-pay" : "")}", "Money", s.Id));
            }
        }
        catch
        {
            // Reminders are best effort.
        }
        finally
        {
            _checking = false;
        }
    }

    private void Raise(ReminderInfo info)
    {
        _toasts.Show(info.Message, ToastKind.Info, "Open", () => OpenRequested?.Invoke(info), durationMs: 15000, title: "🔔 " + info.Title);
        if (_settings.Current.ReminderSound) SystemSounds.Asterisk.Play();
        Flash();
    }

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

    private static void Flash()
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
