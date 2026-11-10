using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Satchel.Core;
using Satchel.ViewModels;

namespace Satchel.Views;

public partial class LockView : UserControl
{
    private LockViewModel? _vm;

    public LockView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm != null)
            {
                _vm.ShakeRequested -= OnShake;
                _vm.PropertyChanged -= OnVmChanged;
            }
            _vm = e.NewValue as LockViewModel;
            if (_vm != null)
            {
                _vm.ShakeRequested += OnShake;
                _vm.PropertyChanged += OnVmChanged;
            }
        };
    }

    private void OnShake() => Anim.Shake(Card);

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LockViewModel.Mode)) return;
        Anim.EnterUp(Panels, 10, 260);
        // Focus the first input of the newly visible panel.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            foreach (var child in Panels.Children.OfType<FrameworkElement>())
            {
                if (child.Visibility != Visibility.Visible) continue;
                var input = FindFirstInput(child);
                if (input is Behaviors.PasswordInputHost host) host.FocusInput();
                else input?.Focus();
            }
        });
    }

    private static FrameworkElement? FindFirstInput(DependencyObject root)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Controls.PasswordInput pi) return pi;
            if (child is TextBox tb && !tb.IsReadOnly) return tb;
            var nested = FindFirstInput(child);
            if (nested != null) return nested;
        }
        return null;
    }
}
