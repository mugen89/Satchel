using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Satchel.Models;
using Satchel.ViewModels;

namespace Satchel.Views.Pages;

public partial class NoteEditorView : UserControl
{
    private NoteEditorViewModel? _vm;

    public NoteEditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm != null)
            {
                _vm.FocusItemRequested -= FocusItem;
                _vm.FlushRequested -= FlushEditors;
            }
            _vm = e.NewValue as NoteEditorViewModel;
            if (_vm == null) return;
            _vm.FocusItemRequested += FocusItem;
            _vm.FlushRequested += FlushEditors;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, InitialFocus);
        };
        Unloaded += (_, _) => Rich.Flush();
    }

    private void FlushEditors()
    {
        if (_vm?.IsText == true) Rich.Flush();
    }

    private void InitialFocus()
    {
        if (_vm == null) return;
        if (string.IsNullOrWhiteSpace(_vm.Note.Title) && _vm.Note.Type != NoteType.Journal)
        {
            TitleBox.Focus();
            return;
        }
        if (_vm.IsList) NewItemBox.Focus();
        else if (_vm.IsText) Rich.FocusEditor();
    }

    private void OnTitleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _vm == null) return;
        e.Handled = true;
        if (_vm.IsList) NewItemBox.Focus();
        else if (_vm.IsText) Rich.FocusEditor();
    }

    private void OnColorButton(object sender, RoutedEventArgs e) => ColorPopup.IsOpen = true;

    private void OnItemKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null || sender is not TextBox tb || tb.DataContext is not ChecklistItem item) return;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Enter when !shift:
                e.Handled = true;
                _vm.InsertAfter(item);
                break;
            case Key.Back when tb.Text.Length == 0:
                e.Handled = true;
                _vm.RemoveAndFocusPrevious(item);
                break;
            case Key.Tab:
                e.Handled = true;
                _vm.Indent(item, shift ? -1 : 1);
                break;
            case Key.Up when tb.GetLineIndexFromCharacterIndex(tb.CaretIndex) == 0:
                e.Handled = MoveFocus(item, -1);
                break;
            case Key.Down when tb.GetLineIndexFromCharacterIndex(tb.CaretIndex) >= tb.LineCount - 1:
                if (!MoveFocus(item, 1)) NewItemBox.Focus();
                e.Handled = true;
                break;
        }
    }

    private bool MoveFocus(ChecklistItem item, int delta)
    {
        if (_vm == null) return false;
        int index = _vm.Items.IndexOf(item) + delta;
        if (index < 0 || index >= _vm.Items.Count) return false;
        FocusItem(_vm.Items[index]);
        return true;
    }

    private void FocusItem(ChecklistItem item)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (ItemsList.ItemContainerGenerator.ContainerFromItem(item) is not DependencyObject container) return;
            var tb = FindChild<TextBox>(container);
            if (tb == null) return;
            tb.Focus();
            tb.CaretIndex = tb.Text.Length;
            tb.BringIntoView();
        });
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var nested = FindChild<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }
}
