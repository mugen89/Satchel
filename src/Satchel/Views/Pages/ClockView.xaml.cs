using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Satchel.ViewModels;

namespace Satchel.Views.Pages;

public partial class ClockView : UserControl
{
    public ClockView()
    {
        InitializeComponent();
        Loaded += (_, _) => Focus();
    }

    /// <summary>Space / L / R drive the stopwatch (unless you're typing).</summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ClockViewModel vm || Keyboard.FocusedElement is TextBoxBase || Keyboard.Modifiers != ModifierKeys.None) return;
        switch (e.Key)
        {
            case Key.Space:
                vm.Timers.ToggleStopwatch();
                e.Handled = true;
                break;
            case Key.L:
                vm.Timers.Lap();
                e.Handled = true;
                break;
            case Key.R:
                vm.Timers.ResetStopwatch();
                e.Handled = true;
                break;
        }
    }
}
