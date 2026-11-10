using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Satchel.Core;
using Satchel.Services;
using Satchel.ViewModels;

namespace Satchel.Views.Pages;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _vm;
    private bool _scrollingToSection;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm != null) _vm.SectionRequested -= ScrollTo;
            _vm = e.NewValue as SettingsViewModel;
            if (_vm != null) _vm.SectionRequested += ScrollTo;
            // Opened straight at a section (e.g. Key bindings from search) before this page existed.
            if (_vm?.PendingSection is { } pending) ScrollTo(pending);
        };
        Scroll.ScrollChanged += (_, _) => TrackSection();
    }

    private void ScrollTo(string section) => ScrollTo(section, 0);

    private void ScrollTo(string section, int attempt)
    {
        if (FindName(section) is not FrameworkElement target)
        {
            _scrollingToSection = false;
            return;
        }
        _scrollingToSection = true; // the nav keeps showing the requested section meanwhile
        if ((!target.IsLoaded || target.ActualHeight == 0) && attempt < 5)
        {
            // The page is still being laid out: scroll once it is.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => ScrollTo(section, attempt + 1));
            return;
        }
        if (_vm != null) _vm.PendingSection = null;
        var offset = target.TranslatePoint(new Point(0, 0), (UIElement)Scroll.Content).Y;
        Scroll.ScrollToVerticalOffset(Math.Max(0, offset - 4));
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => _scrollingToSection = false);
    }

    /// <summary>Highlights the nav item for the section currently in view.</summary>
    private void TrackSection()
    {
        if (_vm == null || _scrollingToSection) return;
        string current = "Appearance";
        foreach (var name in new[] { "Appearance", "General", "Bindings", "Keys", "Online", "Security", "Privacy", "Data", "Updates", "About" })
        {
            if (FindName(name) is not FrameworkElement el) continue;
            var y = el.TranslatePoint(new Point(0, 0), Scroll).Y;
            if (y <= 120) current = name;
        }
        if (Scroll.VerticalOffset >= Scroll.ScrollableHeight - 2) current = "About";
        if (_vm.ActiveSection != current)
        {
            _vm.SectionRequested -= ScrollTo;
            _vm.ActiveSection = current;
            _vm.SectionRequested += ScrollTo;
        }
    }

    private void OnAccentClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AccentChoice choice) _vm?.SetAccentCommand.Execute(choice.Key);
    }

    private void OnScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => _vm?.ScaleChangedCommand.Execute(null);

    // ----- key bindings: after a click, a box listens for the keys you press -----

    private void OnKeyBoxClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: KeyBindingRow row } box || _vm == null) return;
        _vm.RecordKeyCommand.Execute(row);
        if (!row.IsRecording) return;
        KeyBindingService.Recorder = box;
        Keyboard.Focus(box);
    }

    private void OnKeyBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Button { DataContext: KeyBindingRow { IsRecording: true } row } || _vm == null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        // Tab (or Shift+Tab) stops listening and moves on, like anywhere else.
        if (key == Key.Tab && (mods & ~ModifierKeys.Shift) == ModifierKeys.None)
        {
            _vm.CancelKeyRecording();
            return;
        }
        e.Handled = true;
        if (key == Key.Escape && mods == ModifierKeys.None) _vm.CancelKeyRecording();
        else if (KeyChord.IsModifierKey(key)) SettingsViewModel.PreviewKeys(row, mods);
        else _vm.CommitKey(row, KeyChord.From(mods, key));
    }

    private void OnKeyBoxKeyUp(object sender, KeyEventArgs e)
    {
        if (sender is not Button { DataContext: KeyBindingRow { IsRecording: true } row }) return;
        e.Handled = true; // a lone Alt mustn't open the window menu
        SettingsViewModel.PreviewKeys(row, Keyboard.Modifiers);
    }

    private void OnKeyBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is Button { DataContext: KeyBindingRow { IsRecording: true } }) _vm?.CancelKeyRecording();
    }
}
