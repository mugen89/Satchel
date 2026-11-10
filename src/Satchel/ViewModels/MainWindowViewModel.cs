using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Satchel.Security;
using Satchel.Services;

namespace Satchel.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IServiceProvider _services;
    private readonly SessionService _session;
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly ReminderService _reminders;
    private readonly BackupService _backups;
    private readonly VideoFeedService _videos;
    private readonly PrivacyService _privacy;
    private readonly FeedService _feeds;

    public MainWindowViewModel(IServiceProvider services, SessionService session, SettingsService settings,
        DialogService dialogs, ToastService toasts, ReminderService reminders, BackupService backups, VideoFeedService videos,
        PrivacyService privacy, MusicService music, TimerService timers, FeedService feeds)
    {
        _privacy = privacy;
        _feeds = feeds;
        _feeds.OpenRequested += () => Shell?.NavigateTo("Feeds");
        _feeds.UnreadChanged += count => Shell?.SetBadge("Feeds", count);
        Music = music;
        Timers = timers;
        _services = services;
        _session = session;
        _settings = settings;
        _dialogs = dialogs;
        _toasts = toasts;
        _reminders = reminders;
        _backups = backups;
        _videos = videos;
        _videos.OpenRequested += () => Shell?.NavigateTo("Videos", "New");

        _session.Unlocked += OnUnlocked;
        _session.Locked += OnLocked;
        SecureClipboard.Copied += message => _toasts.Show(message, ToastKind.Success, durationMs: 1800);
        _reminders.OpenRequested += info => Shell?.NavigateTo(info.Page, info.Parameter);
        _currentView = CreateLock();
    }

    public AppSettings Settings => _settings.Current;
    public MusicService Music { get; }
    public TimerService Timers { get; }
    public ObservableCollection<DialogViewModel> Dialogs => _dialogs.Dialogs;
    public ObservableCollection<ToastViewModel> Toasts => _toasts.Toasts;

    [ObservableProperty] private object? _currentView;
    [ObservableProperty] private CommandPaletteViewModel? _palette;

    public ShellViewModel? Shell => CurrentView as ShellViewModel;
    public bool IsUnlocked => CurrentView is ShellViewModel;

    partial void OnCurrentViewChanged(object? value)
    {
        OnPropertyChanged(nameof(Shell));
        OnPropertyChanged(nameof(IsUnlocked));
    }

    private LockViewModel CreateLock() => ActivatorUtilities.CreateInstance<LockViewModel>(_services);

    private async void OnUnlocked()
    {
        _privacy.OnUnlocked();
        var shell = ActivatorUtilities.CreateInstance<ShellViewModel>(_services);
        shell.PaletteRequested += OpenPalette;
        CurrentView = shell;
        await shell.StartAsync();
        _reminders.Start();
        _videos.Start();
        _feeds.Start();
        _ = _backups.AutoBackupIfDueAsync();
        _ = AfterUnlockUpdatesAsync();
    }

    private bool _updateNoticeShown;
    private string? _notifiedVersion;

    /// <summary>"Updated to …" once after an update; otherwise, once a day, a quiet check for a newer version.</summary>
    private async Task AfterUnlockUpdatesAsync()
    {
        try
        {
            var updates = _services.GetService(typeof(UpdateService)) as UpdateService;
            if (updates == null) return;
            if (UpdateService.JustUpdated is { } done && !_updateNoticeShown)
            {
                _updateNoticeShown = true;
                _toasts.Show($"Updated to {Core.AppInfo.Name} {done.To}. Everything you had is still here.", ToastKind.Success, "What's new",
                    () => Shell?.NavigateTo("Settings", "Updates"), durationMs: 10000);
                return;
            }
            var s = _settings.Current;
            if (!s.CheckUpdatesOnStart || updates.CannotCheckReason != null) return;
            ReleaseInfo? found = updates.AvailableUpdate;
            if (s.LastUpdateCheck is not { } last || DateTime.Now - last > TimeSpan.FromHours(20))
            {
                await Task.Delay(TimeSpan.FromSeconds(8)); // let the start settle first
                if (Shell == null) return;
                found = (await updates.CheckAsync()).Available;
            }
            if (found != null && Shell != null && _notifiedVersion != found.VersionText)
            {
                _notifiedVersion = found.VersionText;
                _toasts.Show($"{Core.AppInfo.Name} {found.VersionText} is available.", ToastKind.Info, "See what's new",
                    () => Shell?.NavigateTo("Settings", "Updates"), durationMs: 10000);
            }
        }
        catch (Exception ex)
        {
            App.Log("Update check after unlock", ex);
        }
    }

    private void OnLocked()
    {
        _reminders.Stop();
        _videos.Stop();
        _feeds.Stop();
        Palette = null;
        // Cancel every open dialog (removal is animated/async, so iterate over a snapshot).
        foreach (var dialog in _dialogs.Dialogs.ToList()) dialog.Cancel();
        _dialogs.Dialogs.Clear();
        if (CurrentView is ShellViewModel shell)
        {
            shell.PaletteRequested -= OpenPalette;
            shell.Dispose();
        }
        CurrentView = CreateLock();
        GC.Collect();
    }

    public void OpenPalette()
    {
        if (Shell == null) return;
        if (Palette != null) { Palette = null; return; }
        var vm = ActivatorUtilities.CreateInstance<CommandPaletteViewModel>(_services, Shell, this);
        vm.CloseRequested += () => Palette = null;
        Palette = vm;
    }

    public async Task LockAsync() => await _session.LockAsync();

    /// <summary>Ctrl+Shift+T: "25 Focus", "10m", "1h30 Bread"…</summary>
    public async Task StartTimerPromptAsync()
    {
        var text = await _dialogs.PromptAsync("Start a timer", "How long? For example 25, 10m, 1h30 or 90s — add a label after a space: “25 Focus”.",
            "", "25 Focus", "Start");
        if (string.IsNullOrWhiteSpace(text)) return;
        var parts = text.Trim().Split(' ', 2);
        var duration = TimerService.ParseDuration(parts[0]);
        var label = parts.Length > 1 ? parts[1] : "";
        if (duration == null && parts.Length > 1 && TimerService.ParseDuration(text) is { } whole) { duration = whole; label = ""; }
        if (duration == null) { _toasts.Warning("Couldn't read that duration — try 25, 10m or 1h30"); return; }
        var timer = Timers.Start(duration.Value.TotalSeconds, label);
        _toasts.Show($"{timer.Label} — rings at {Core.Format.Time(DateTime.Now + duration.Value)}", ToastKind.Success, "Open", () => Shell?.NavigateTo("Clock"), durationMs: 3000);
    }

    public async Task FlushAsync()
    {
        if (Shell != null) await Shell.FlushAsync();
    }
}
