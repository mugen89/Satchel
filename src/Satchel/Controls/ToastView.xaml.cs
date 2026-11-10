using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Satchel.Core;
using Satchel.Services;

namespace Satchel.Controls;

public partial class ToastView : UserControl
{
    private ToastViewModel? _vm;

    public ToastView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Card.BeginAnimation(OpacityProperty, Anim.To(1, 200, Anim.EaseOut, 0));
            Shift.BeginAnimation(TranslateTransform.XProperty, Anim.To(0, 380, Anim.EaseOutQuint, 60));
        };
        DataContextChanged += (_, e) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
            _vm = e.NewValue as ToastViewModel;
            if (_vm == null) return;
            _vm.PropertyChanged += OnVmChanged;
            var (fg, bg) = _vm.Kind switch
            {
                ToastKind.Success => ("SuccessBrush", "SuccessSubtleBrush"),
                ToastKind.Warning => ("WarningBrush", "WarningSubtleBrush"),
                ToastKind.Error => ("DangerBrush", "DangerSubtleBrush"),
                _ => ("InfoBrush", "InfoSubtleBrush")
            };
            Icon.SetResourceReference(TextBlock.ForegroundProperty, fg);
            IconBg.SetResourceReference(Border.BackgroundProperty, bg);
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ToastViewModel.IsClosing) || _vm?.IsClosing != true) return;
        Card.BeginAnimation(OpacityProperty, Anim.To(0, 200, Anim.EaseOut));
        Shift.BeginAnimation(TranslateTransform.XProperty, Anim.To(40, 200, Anim.EaseOut));
    }
}
