using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Behaviors;
using Satchel.Core;
using Satchel.Services;

namespace Satchel.ViewModels;

/// <summary>One version in the "What's new" feed.</summary>
public sealed record ReleaseItem(ReleaseInfo Release, bool IsInstalled, bool IsNew)
{
    public string VersionText => Release.VersionText;
    public string Title => Release.Title;
    public string DateText => Release.PublishedAt is { } d ? Format.Date(d) : "";
    public IReadOnlyList<ReleaseNotes.Line> Lines { get; } = ReleaseNotes.Parse(Release.Notes);
    public bool HasNotes => Lines.Count > 0;
}

/// <summary>Settings → Updates: the version, checking GitHub for a newer one, installing it, and the release notes.</summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly UpdateService _updates;
    private readonly SettingsService _settings;
    private readonly DialogService _dialogs;
    private readonly BackupService _backups;
    private CancellationTokenSource? _download;

    public UpdatesViewModel(UpdateService updates, SettingsService settings, DialogService dialogs, BackupService backups)
    {
        _updates = updates;
        _settings = settings;
        _dialogs = dialogs;
        _backups = backups;
        _repositoryText = settings.Current.UpdateRepository.Length > 0 ? settings.Current.UpdateRepository : AppInfo.DefaultUpdateRepository;
        _available = updates.AvailableUpdate;
        RebuildFeed();
        _status = _available != null ? $"{AppInfo.Name} {_available.VersionText} is available." : "";
    }

    public AppSettings S => _settings.Current;
    public string VersionText => $"{AppInfo.Name} {AppInfo.Version}";
    public ObservableCollection<ReleaseItem> Feed { get; } = [];
    public bool HasFeed => Feed.Count > 0;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(RepositoryHint))] private string _repositoryText;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasStatus))] private string _status;
    [ObservableProperty] private bool _statusIsError;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanCheck), nameof(CanInstall))] private bool _isChecking;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanCheck), nameof(CanInstall))] private bool _isDownloading;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanCheck), nameof(CanInstall))] private bool _isInstalling;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanInstall), nameof(InstallText), nameof(HasAvailable))] private ReleaseInfo? _available;

    public bool HasStatus => Status.Length > 0;
    public bool IsBusy => IsChecking || IsDownloading || IsInstalling;
    public bool CanCheck => !IsBusy;
    public bool HasAvailable => Available != null;
    public bool CanInstall => Available != null && !IsBusy;
    public string InstallText => Available != null ? $"Install {Available.VersionText}" : "Install";

    public string LastCheckedText => S.LastUpdateCheck is { } d ? $"Last checked {Format.Relative(d)}" : "Not checked yet";

    /// <summary>Under the repository box: what it's set to, or why it isn't valid.</summary>
    public string RepositoryHint =>
        string.IsNullOrWhiteSpace(RepositoryText) ? "Where new versions are published — “owner/name”, e.g. yourname/Satchel."
        : UpdateService.NormalizeRepository(RepositoryText) is { } repo ? $"Releases: github.com/{repo}/releases"
        : "That doesn't look like a GitHub repository — use “owner/name”, e.g. yourname/Satchel.";

    public bool OnlineOff => !S.OnlineFeatures;

    public void Refresh()
    {
        OnPropertyChanged(nameof(LastCheckedText));
        OnPropertyChanged(nameof(OnlineOff));
    }

    private void RebuildFeed()
    {
        Feed.Clear();
        foreach (var r in _updates.Releases)
            Feed.Add(new ReleaseItem(r, r.Version == UpdateService.CurrentVersion, r.Version > UpdateService.CurrentVersion));
        OnPropertyChanged(nameof(HasFeed));
    }

    partial void OnRepositoryTextChanged(string value)
    {
        // Saved as soon as it's a valid name (or emptied, to use the built-in one).
        if (string.IsNullOrWhiteSpace(value)) S.UpdateRepository = "";
        else if (UpdateService.NormalizeRepository(value) is { } repo)
        {
            S.UpdateRepository = repo;
            // A pasted address ("https://github.com/owner/name.git") shows as the short name it was saved as.
            if (value.Trim() != repo) RepositoryText = repo;
        }
    }

    [RelayCommand]
    private void TurnOnOnline()
    {
        S.OnlineFeatures = true;
        OnPropertyChanged(nameof(OnlineOff));
        Status = "";
    }

    [RelayCommand]
    private async Task Check()
    {
        if (IsBusy) return;
        IsChecking = true;
        StatusIsError = false;
        Status = "Checking GitHub for a newer version…";
        try
        {
            var result = await _updates.CheckAsync();
            StatusIsError = !result.Ok;
            Status = result.Message;
            Available = result.Ok ? result.Available : Available;
            if (result.Ok) RebuildFeed();
        }
        finally
        {
            IsChecking = false;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task Install()
    {
        if (Available is not { } release || IsBusy) return;
        if (UpdateService.CannotInstallReason() is { } reason)
        {
            StatusIsError = true;
            Status = reason;
            return;
        }
        var size = release.ZipSize > 0 ? $" ({Format.Bytes(release.ZipSize)})" : "";
        if (!await _dialogs.ConfirmAsync($"Install {AppInfo.Name} {release.VersionText}?",
                $"Satchel downloads the new version{size}, checks that it arrived intact, then closes and starts again. " +
                "Your data isn't changed — a backup of it is made first, just in case. You'll unlock again with your master password.",
                "Download and install")) return;

        _download = new CancellationTokenSource();
        IsDownloading = true;
        StatusIsError = false;
        Status = "Downloading…";
        Progress = 0;
        ProgressText = "";
        try
        {
            var progress = new Progress<(long Done, long Total)>(p =>
            {
                Progress = p.Total > 0 ? (double)p.Done / p.Total : 0;
                ProgressText = p.Total > 0 ? $"{Format.Bytes(p.Done)} of {Format.Bytes(p.Total)}" : Format.Bytes(p.Done);
            });
            var staged = await _updates.DownloadAsync(release, progress, _download.Token);
            IsDownloading = false;
            IsInstalling = true;
            Status = "Making a backup of your data…";
            try { await _backups.CreateBackupAsync("before-update"); }
            catch (Exception ex) { App.Log("Backup before update", ex); }
            Status = "Installing…";
            // Flush everything that's open, then swap the files (slow on a flash drive — off the UI thread).
            if (App.Services.GetService(typeof(MainWindowViewModel)) is MainWindowViewModel main) await main.FlushAsync();
            await Task.Run(() => UpdateService.InstallFiles(staged, release));
            Status = $"Restarting with {release.VersionText}…";
            UpdateService.StartNewVersion();
            // The new version waits until this window has closed (and saved).
            await Task.Delay(300);
            Application.Current.MainWindow?.Close();
        }
        catch (OperationCanceledException)
        {
            StatusIsError = false;
            Status = "Download cancelled.";
        }
        catch (UpdateException ex)
        {
            StatusIsError = true;
            Status = ex.Message;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.IO.IOException or UnauthorizedAccessException or System.IO.InvalidDataException)
        {
            App.Log("Update", ex);
            StatusIsError = true;
            Status = ex is System.Net.Http.HttpRequestException ? "The download failed — check your connection and try again." : "The update couldn't be unpacked or installed: " + ex.Message;
        }
        finally
        {
            IsDownloading = false;
            IsInstalling = false;
            _download?.Dispose();
            _download = null;
        }
    }

    [RelayCommand]
    private void CancelDownload() => _download?.Cancel();

    [RelayCommand]
    private void OpenReleases()
    {
        if (_updates.ReleasesPageUrl is { Length: > 0 } url) UiCommands.OpenUrl.Execute(url);
    }

    [RelayCommand]
    private void OpenRelease(ReleaseItem? item)
    {
        if (item?.Release.PageUrl is { Length: > 0 } url) UiCommands.OpenUrl.Execute(url);
    }
}
