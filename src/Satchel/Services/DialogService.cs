using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Satchel.Services;

/// <summary>Base for in-window modal dialogs. Views are resolved by DataTemplate.</summary>
public abstract partial class DialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<object?> _completion = new();

    [ObservableProperty] private bool _isClosing;

    internal DialogService? Host { get; set; }

    public Task<object?> Completion => _completion.Task;

    public virtual double DialogWidth => 460;
    public virtual bool CloseOnBackdrop => true;
    public virtual bool CloseOnEscape => true;

    [RelayCommand]
    public void Cancel() => Close(null);

    public void Close(object? result)
    {
        if (_completion.Task.IsCompleted) return;
        OnClosing(result);
        _completion.TrySetResult(result);
        Host?.Remove(this);
    }

    protected virtual void OnClosing(object? result) { }
}

public sealed class DialogService
{
    public ObservableCollection<DialogViewModel> Dialogs { get; } = [];

    public bool HasDialog => Dialogs.Count > 0;

    public async Task<object?> ShowAsync(DialogViewModel dialog)
    {
        dialog.Host = this;
        Dialogs.Add(dialog);
        return await dialog.Completion;
    }

    public async Task<T?> ShowAsync<T>(DialogViewModel dialog)
    {
        var result = await ShowAsync(dialog);
        return result is T t ? t : default;
    }

    internal async void Remove(DialogViewModel dialog)
    {
        dialog.IsClosing = true;
        await Task.Delay(Core.Anim.Enabled ? 160 : 0);
        Dialogs.Remove(dialog);
    }

    public bool CloseTop()
    {
        var top = Dialogs.LastOrDefault(d => !d.IsClosing);
        if (top == null || !top.CloseOnEscape) return false;
        top.Cancel();
        return true;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK", bool danger = false, string cancelText = "Cancel")
    {
        var result = await ShowAsync(new ViewModels.Dialogs.ConfirmDialogViewModel(title, message, confirmText, cancelText, danger));
        return result is true;
    }

    public async Task<string?> PromptAsync(string title, string? message = null, string initial = "", string placeholder = "", string confirmText = "Save")
    {
        var result = await ShowAsync(new ViewModels.Dialogs.PromptDialogViewModel(title, message, initial, placeholder, confirmText));
        return result as string;
    }
}
