using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Core;
using Satchel.Services;

namespace Satchel.ViewModels.Dialogs;

public sealed partial class ConfirmDialogViewModel : DialogViewModel
{
    public ConfirmDialogViewModel(string title, string message, string confirmText, string cancelText, bool danger)
    {
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        CancelText = cancelText;
        IsDanger = danger;
    }

    public string Title { get; }
    public string Message { get; }
    public string ConfirmText { get; }
    public string CancelText { get; }
    public bool IsDanger { get; }
    public string Glyph => IsDanger ? Glyphs.Warning : Glyphs.Info;
    public override double DialogWidth => 420;

    [RelayCommand]
    private void Confirm() => Close(true);
}

public sealed partial class PromptDialogViewModel : DialogViewModel
{
    public PromptDialogViewModel(string title, string? message, string initial, string placeholder, string confirmText)
    {
        Title = title;
        Message = message;
        Placeholder = placeholder;
        ConfirmText = confirmText;
        _value = initial;
    }

    public string Title { get; }
    public string? Message { get; }
    public string Placeholder { get; }
    public string ConfirmText { get; }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public override double DialogWidth => 420;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _value;

    private bool CanConfirm => !string.IsNullOrWhiteSpace(Value);

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() => Close(Value.Trim());
}
