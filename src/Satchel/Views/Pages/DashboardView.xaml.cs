using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Satchel.Controls;
using Satchel.Core;
using Satchel.ViewModels;

namespace Satchel.Views.Pages;

public partial class DashboardView : UserControl
{
    // ----- resizing with the corner grip -----
    private WidgetViewModel? _resizing;
    private MasonryPanel? _resizePanel;
    private Point _resizeStart;
    private double _startWidth, _startHeight;
    private bool _heightTouched;

    public DashboardView() => InitializeComponent();

    private void OnQuickNoteKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && (sender as FrameworkElement)?.DataContext is QuickNoteWidget w)
        {
            if (sender is TextBox tb) tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            w.SaveCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnFavoriteClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is FavoriteAccount fav && DataContext is DashboardViewModel vm)
            vm.Navigator.NavigateTo("Vault", fav.Profile.Id);
    }

    /// <summary>Width snaps to whole columns, height to 10 px. Dragging only sideways keeps "fits the content".</summary>
    private void OnResizeStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not FrameworkElement grip || grip.DataContext is not WidgetViewModel w || DataContext is not DashboardViewModel vm) return;
        var frame = Ancestor<Grid>(grip, g => g.Name == "frame");
        var container = Ancestor<ContentPresenter>(grip, c => VisualTreeHelper.GetParent(c) is MasonryPanel);
        _resizePanel = container == null ? null : VisualTreeHelper.GetParent(container) as MasonryPanel;
        if (frame == null || _resizePanel == null) return;
        _resizing = w;
        _resizeStart = Mouse.GetPosition(_resizePanel);
        _startWidth = frame.ActualWidth;
        _startHeight = frame.ActualHeight;
        _heightTouched = w.IsFixedHeight;
        vm.BeginResize(w);
    }

    private void OnResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if (_resizing is not { } w || _resizePanel is not { } panel) return;
        var p = Mouse.GetPosition(panel);
        double dx = p.X - _resizeStart.X, dy = p.Y - _resizeStart.Y;
        double column = Math.Max(1, panel.ColumnWidth), gap = panel.Spacing;
        int span = (int)Math.Round((_startWidth + dx + gap) / (column + gap));
        w.Span = Math.Clamp(span, 1, Math.Max(1, panel.Columns));
        if (Math.Abs(dy) > 6) _heightTouched = true;
        if (_heightTouched)
            w.Height = Math.Clamp(Math.Round((_startHeight + dy) / 10) * 10, DashboardViewModel.MinWidgetHeight, DashboardViewModel.MaxWidgetHeight);
    }

    private void OnResizeCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_resizing is { } w && DataContext is DashboardViewModel vm) vm.EndResize(w);
        _resizing = null;
        _resizePanel = null;
    }

    private void OnResizeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WidgetViewModel w && DataContext is DashboardViewModel vm) vm.FitHeightCommand.Execute(w);
        e.Handled = true;
    }

    // ----- profiles menu -----

    private void OnProfileMenu(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DashboardViewModel vm || sender is not Button button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        var profiles = vm.Profiles;
        for (int i = 0; i < profiles.Count; i++)
        {
            var p = profiles[i];
            menu.Items.Add(new MenuItem
            {
                Header = p.Name,
                Icon = p.Icon,
                IsChecked = ReferenceEquals(p, vm.ActiveProfile),
                InputGestureText = i < 9 ? $"Alt+{i + 1}" : "",
                Command = vm.SwitchProfileCommand,
                CommandParameter = p
            });
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "New profile…", Icon = Glyphs.Add, Command = vm.NewProfileCommand });
        menu.Items.Add(new MenuItem { Header = $"Edit “{vm.ProfileName}”…", Icon = Glyphs.Edit, Command = vm.EditProfileCommand });
        menu.Items.Add(new MenuItem { Header = "Duplicate", Icon = Glyphs.Copy, Command = vm.DuplicateProfileCommand });
        if (profiles.Count > 1)
            menu.Items.Add(new MenuItem { Header = "Delete…", Icon = Glyphs.Delete, Command = vm.DeleteProfileCommand, Style = (Style)FindResource("DangerMenuItem") });
        menu.IsOpen = true;
    }

    private static T? Ancestor<T>(DependencyObject from, Func<T, bool> match) where T : DependencyObject
    {
        for (var d = VisualTreeHelper.GetParent(from); d != null; d = VisualTreeHelper.GetParent(d))
            if (d is T t && match(t)) return t;
        return null;
    }
}
