using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Satchel.Core;
using Satchel.Security;
using Satchel.Services;

namespace Satchel.ViewModels;

public enum LockMode
{
    Unlock,
    Setup,
    ShowRecovery,
    Recover,
    ResetPassword
}

public sealed partial class LockViewModel : ObservableObject
{
    private readonly SessionService _session;
    private readonly SeedService _seed;
    private readonly ToastService _toasts;
    private int _failedAttempts;

    public LockViewModel(SessionService session, SeedService seed, ToastService toasts)
    {
        _session = session;
        _seed = seed;
        _toasts = toasts;
        _mode = session.IsSetUp ? LockMode.Unlock : LockMode.Setup;
    }

    public string AppName => AppInfo.Name;
    public string Tagline => AppInfo.Tagline;
    public string Version => $"v{AppInfo.Version}";
    public string DataLocation => AppPaths.DataDirectory;
    public string DriveText => AppPaths.DriveLabel;
    public string? PasswordHint => _session.Hint;
    public bool HasHint => !string.IsNullOrWhiteSpace(PasswordHint);
    public string Greeting => Format.Greeting();

    [ObservableProperty] private LockMode _mode;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _confirm = "";
    [ObservableProperty] private string _hint = "";
    [ObservableProperty] private string _recoveryInput = "";
    [ObservableProperty] private string _recoveryKey = "";
    [ObservableProperty] private bool _recoverySaved;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _showHint;

    public event Action? ShakeRequested;

    partial void OnModeChanged(LockMode value)
    {
        Error = null;
        Password = "";
        Confirm = "";
    }

    partial void OnPasswordChanged(string value) => Error = null;

    [RelayCommand]
    private async Task Unlock()
    {
        if (IsBusy) return;
        if (string.IsNullOrEmpty(Password)) { Fail("Enter your master password."); return; }
        IsBusy = true;
        try
        {
            if (_failedAttempts >= 5) await Task.Delay(Math.Min(10, _failedAttempts - 4) * 1000);
            if (await _session.UnlockAsync(Password))
            {
                _failedAttempts = 0;
                Password = "";
                _session.BeginSession();
            }
            else
            {
                _failedAttempts++;
                Password = "";
                Fail(_failedAttempts >= 3 && HasHint ? "Incorrect password. Need a nudge? Show your hint below." : "Incorrect password. Try again.");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Create()
    {
        if (IsBusy) return;
        if (!ValidateNewPassword()) return;
        IsBusy = true;
        try
        {
            RecoveryKey = await _session.CreateVaultAsync(Password, Hint);
            Password = "";
            Confirm = "";
            Mode = LockMode.ShowRecovery;
        }
        catch (Exception ex)
        {
            Fail("Couldn't create the vault: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool ValidateNewPassword()
    {
        if (Password.Length < 8) { Fail("Use at least 8 characters — longer is better."); return false; }
        if (PasswordStrength.Evaluate(Password).Score < 1) { Fail("That password is too easy to guess. Try a short phrase."); return false; }
        if (Password != Confirm) { Fail("The passwords don't match."); return false; }
        return true;
    }

    [RelayCommand]
    private void CopyRecoveryKey() => SecureClipboard.Copy(RecoveryKey, sensitive: true, "Recovery key copied");

    [RelayCommand]
    private void SaveRecoveryKey()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save recovery key",
            FileName = $"{AppInfo.Name} recovery key.txt",
            Filter = "Text file (*.txt)|*.txt"
        };
        if (dialog.ShowDialog() != true) return;
        var text = $"{AppInfo.Name} recovery key\r\n\r\n{RecoveryKey}\r\n\r\nCreated {DateTime.Now:f}.\r\n" +
                   "Keep this somewhere safe and NOT on the same drive as your vault.\r\n" +
                   "It can unlock your data if you forget your master password.\r\n";
        File.WriteAllText(dialog.FileName, text);
        if (Path.GetPathRoot(dialog.FileName) == Path.GetPathRoot(AppPaths.DataDirectory))
            _toasts.Warning("Saved — but that's the same drive as your vault. Move it somewhere else.");
        else
            _toasts.Success("Recovery key saved.");
    }

    [RelayCommand]
    private async Task FinishSetup()
    {
        if (!RecoverySaved) { Fail("Please confirm you've stored the recovery key."); return; }
        IsBusy = true;
        try
        {
            await _seed.SeedAsync();
        }
        catch
        {
            // Sample content is optional.
        }
        finally
        {
            IsBusy = false;
        }
        RecoveryKey = "";
        _session.BeginSession();
    }

    [RelayCommand]
    private void StartRecovery() => Mode = LockMode.Recover;

    [RelayCommand]
    private void BackToUnlock() => Mode = LockMode.Unlock;

    [RelayCommand]
    private void ToggleHint() => ShowHint = !ShowHint;

    [RelayCommand]
    private async Task VerifyRecovery()
    {
        if (IsBusy) return;
        if (KeyManager.NormalizeRecoveryKey(RecoveryInput).Length != 25) { Fail("A recovery key has 25 characters (5 groups of 5)."); return; }
        IsBusy = true;
        try
        {
            if (await _session.VerifyRecoveryKeyAsync(RecoveryInput))
            {
                RecoveryInput = "";
                Mode = LockMode.ResetPassword;
            }
            else Fail("That recovery key doesn't match this vault.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ResetPassword()
    {
        if (IsBusy || !ValidateNewPassword()) return;
        IsBusy = true;
        try
        {
            if (await _session.ResetPasswordWithRecoveryAsync(Password))
            {
                _session.SetHint(Hint);
                Password = "";
                Confirm = "";
                _session.BeginSession();
                _toasts.Success("Master password updated. Your recovery key still works.");
            }
            else Fail("Something went wrong — start the recovery again.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Fail(string message)
    {
        Error = message;
        ShakeRequested?.Invoke();
    }
}
