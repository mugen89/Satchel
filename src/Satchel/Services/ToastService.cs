using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Core;

namespace Satchel.Services;

public enum ToastKind { Info, Success, Warning, Error }

public sealed partial class ToastViewModel : ObservableObject
{
    private readonly ToastService _owner;
    private readonly Action? _action;

    public ToastViewModel(ToastService owner, string message, ToastKind kind, string? actionText, Action? action, string? title)
    {
        _owner = owner;
        Message = message;
        Kind = kind;
        ActionText = actionText;
        Title = title;
        _action = action;
    }

    public string Message { get; }
    public string? Title { get; }
    public ToastKind Kind { get; }
    public string? ActionText { get; }
    public bool HasAction => !string.IsNullOrEmpty(ActionText);
    public bool HasTitle => !string.IsNullOrEmpty(Title);

    public string Glyph => Kind switch
    {
        ToastKind.Success => Glyphs.Completed,
        ToastKind.Warning => Glyphs.Warning,
        ToastKind.Error => Glyphs.Error,
        _ => Glyphs.Info
    };

    [ObservableProperty] private bool _isClosing;

    [RelayCommand]
    private void Act()
    {
        _action?.Invoke();
        Dismiss();
    }

    [RelayCommand]
    public void Dismiss() => _owner.Remove(this);
}

public sealed class ToastService
{
    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    public ToastViewModel Show(string message, ToastKind kind = ToastKind.Info, string? actionText = null, Action? action = null, int durationMs = 3600, string? title = null)
    {
        var toast = new ToastViewModel(this, message, kind, actionText, action, title);
        Toasts.Add(toast);
        while (Toasts.Count(t => !t.IsClosing) > 4)
            Remove(Toasts.First(t => !t.IsClosing));
        if (durationMs > 0) AutoDismiss(toast, actionText != null ? Math.Max(durationMs, 6000) : durationMs);
        return toast;
    }

    public void Success(string message) => Show(message, ToastKind.Success, durationMs: 2600);
    public void Info(string message) => Show(message);
    public void Warning(string message) => Show(message, ToastKind.Warning, durationMs: 5000);
    public void Error(string message) => Show(message, ToastKind.Error, durationMs: 7000);

    private async void AutoDismiss(ToastViewModel toast, int ms)
    {
        await Task.Delay(ms);
        Remove(toast);
    }

    internal async void Remove(ToastViewModel toast)
    {
        if (toast.IsClosing || !Toasts.Contains(toast)) return;
        toast.IsClosing = true;
        await Task.Delay(Anim.Enabled ? 220 : 0);
        Toasts.Remove(toast);
    }
}
