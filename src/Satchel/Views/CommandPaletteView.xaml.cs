using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Satchel.Core;
using Satchel.ViewModels;

namespace Satchel.Views;

public partial class CommandPaletteView : UserControl
{
    public CommandPaletteView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Backdrop.BeginAnimation(OpacityProperty, Anim.To(0.7, 160, Anim.EaseOut, 0));
            Card.BeginAnimation(OpacityProperty, Anim.To(1, 180, Anim.EaseOut, 0));
            Shift.BeginAnimation(TranslateTransform.YProperty, Anim.To(0, 300, Anim.EaseOutQuint, -14));
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => { QueryBox.Focus(); QueryBox.SelectAll(); });
        };
    }

    private CommandPaletteViewModel? Vm => DataContext as CommandPaletteViewModel;

    private void OnBackdrop(object sender, MouseButtonEventArgs e) => Vm?.Close();

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm == null) return;
        switch (e.Key)
        {
            case Key.Down:
                Vm.Move(1);
                List.ScrollIntoView(Vm.Selected);
                e.Handled = true;
                break;
            case Key.Up:
                Vm.Move(-1);
                List.ScrollIntoView(Vm.Selected);
                e.Handled = true;
                break;
            case Key.Enter:
                Vm.ExecuteCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                Vm.Close();
                e.Handled = true;
                break;
        }
    }

    private void OnListClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(List, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: PaletteItem item })
            Vm?.ExecuteCommand.Execute(item);
    }
}
