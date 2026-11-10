using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Satchel.Core;
using Satchel.Security;
using Satchel.Services;

namespace Satchel.ViewModels.Dialogs;

public sealed partial class ChangePasswordDialogViewModel : DialogViewModel
{
    private readonly SessionService _session;

    public ChangePasswordDialogViewModel(SessionService session) => _session = session;

    public override double DialogWidth => 460;
    public override bool CloseOnBackdrop => false;

    [ObservableProperty] private string _current = "";
    [ObservableProperty] private string _newPassword = "";
    [ObservableProperty] private string _confirm = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    private async Task Save()
    {
        if (IsBusy) return;
        if (NewPassword.Length < 8) { Error = "Use at least 8 characters."; return; }
        if (PasswordStrength.Evaluate(NewPassword).Score < 1) { Error = "That password is too easy to guess."; return; }
        if (NewPassword != Confirm) { Error = "The new passwords don't match."; return; }
        IsBusy = true;
        try
        {
            if (await _session.ChangePasswordAsync(Current, NewPassword)) Close(true);
            else Error = "Your current password is incorrect.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed partial class RecoveryKeyDialogViewModel : DialogViewModel
{
    private readonly SessionService _session;
    private readonly ToastService _toasts;

    public RecoveryKeyDialogViewModel(SessionService session, ToastService toasts)
    {
        _session = session;
        _toasts = toasts;
    }

    public override double DialogWidth => 480;
    public override bool CloseOnBackdrop => false;

    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _recoveryKey = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;

    public bool HasKey => RecoveryKey.Length > 0;
    partial void OnRecoveryKeyChanged(string value) => OnPropertyChanged(nameof(HasKey));

    [RelayCommand]
    private async Task Generate()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var key = await _session.RegenerateRecoveryKeyAsync(Password);
            if (key == null) { Error = "Incorrect master password."; return; }
            Password = "";
            Error = null;
            RecoveryKey = key;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Copy() => SecureClipboard.Copy(RecoveryKey, true, "Recovery key copied");

    [RelayCommand]
    private void SaveToFile()
    {
        var dialog = new SaveFileDialog { Title = "Save recovery key", FileName = $"{AppInfo.Name} recovery key.txt", Filter = "Text file (*.txt)|*.txt" };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, $"{AppInfo.Name} recovery key\r\n\r\n{RecoveryKey}\r\n\r\nCreated {DateTime.Now:f}. Keep it away from the vault's drive.\r\n");
        _toasts.Success("Recovery key saved");
    }

    [RelayCommand]
    private void Done() => Close(true);

    protected override void OnClosing(object? result) => RecoveryKey = "";
}
