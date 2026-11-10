using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Satchel.Core;
using Satchel.ViewModels;

namespace Satchel.Views;

public partial class ShellView : UserControl
{
    private const double ExpandedWidth = 232;
    private const double CollapsedWidth = 72;
    private ShellViewModel? _vm;

    public ShellView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
            _vm = e.NewValue as ShellViewModel;
            if (_vm == null) return;
            _vm.PropertyChanged += OnVmChanged;
            ApplySidebar(animate: false);
        };
        Loaded += (_, _) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => MoveIndicator(false));
        NavList.SizeChanged += (_, _) => MoveIndicator(false);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShellViewModel.IsSidebarCollapsed):
                ApplySidebar(animate: true);
                break;
            case nameof(ShellViewModel.SelectedItem):
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => MoveIndicator(true));
                break;
        }
    }

    private void ApplySidebar(bool animate)
    {
        if (_vm == null) return;
        bool collapsed = _vm.IsSidebarCollapsed;
        double target = collapsed ? CollapsedWidth : ExpandedWidth;
        if (animate && Anim.Enabled)
            Sidebar.BeginAnimation(WidthProperty, new DoubleAnimation(target, Anim.Ms(260)) { EasingFunction = Anim.EaseOutQuint });
        else
        {
            Sidebar.BeginAnimation(WidthProperty, null);
            Sidebar.Width = target;
        }
        var labelOpacity = collapsed ? 0 : 1;
        foreach (var el in new FrameworkElement[] { BrandText, LockLabel, CollapseLabel, DriveBadge })
            el.BeginAnimation(OpacityProperty, Anim.To(labelOpacity, 180));
        DriveBadge.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapseIcon.Text = collapsed ? Glyphs.ChevronRight : Glyphs.ChevronLeft;
        CollapseLabel.Text = collapsed ? "Expand" : "Collapse";
    }

    private void MoveIndicator(bool animate)
    {
        if (_vm?.SelectedItem is not { } item) return;
        if (NavList.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container || !container.IsLoaded)
        {
            Indicator.BeginAnimation(OpacityProperty, Anim.To(0, 150));
            return;
        }
        var y = container.TranslatePoint(new Point(0, 0), NavHost).Y + (container.ActualHeight - Indicator.Height) / 2;
        Indicator.BeginAnimation(OpacityProperty, Anim.To(1, 150));
        if (animate && Anim.Enabled)
            IndicatorShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(y, Anim.Ms(320)) { EasingFunction = Anim.Back });
        else
        {
            IndicatorShift.BeginAnimation(TranslateTransform.YProperty, null);
            IndicatorShift.Y = y;
        }
    }
}
