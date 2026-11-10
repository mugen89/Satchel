using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Satchel.Core;
using Satchel.Services;

namespace Satchel.Controls;

/// <summary>Backdrop + animated card hosting a dialog view model.</summary>
public partial class DialogFrame : UserControl
{
    private DialogViewModel? _vm;

    public DialogFrame()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += (_, e) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
            _vm = e.NewValue as DialogViewModel;
            if (_vm != null) _vm.PropertyChanged += OnVmChanged;
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Backdrop.BeginAnimation(OpacityProperty, Anim.To(1, 180, Anim.EaseOut, 0));
        Card.BeginAnimation(OpacityProperty, Anim.To(1, 200, Anim.EaseOut, 0));
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim.To(1, 320, Anim.Back, 0.94));
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim.To(1, 320, Anim.Back, 0.94));
        CardShift.BeginAnimation(TranslateTransform.YProperty, Anim.To(0, 320, Anim.EaseOutQuint, 10));

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (!Card.IsKeyboardFocusWithin)
            {
                Card.Focus();
                Card.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }
        });
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DialogViewModel.IsClosing) || _vm?.IsClosing != true) return;
        IsHitTestVisible = false;
        Backdrop.BeginAnimation(OpacityProperty, Anim.To(0, 150, Anim.EaseOut));
        Card.BeginAnimation(OpacityProperty, Anim.To(0, 130, Anim.EaseOut));
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim.To(0.97, 150, Anim.EaseOut));
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim.To(0.97, 150, Anim.EaseOut));
    }

    private void OnBackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm is { CloseOnBackdrop: true }) _vm.Cancel();
    }
}
