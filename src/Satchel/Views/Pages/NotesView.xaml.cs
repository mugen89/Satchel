using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Satchel.Core;
using Satchel.Models;
using Satchel.ViewModels;

namespace Satchel.Views.Pages;

public partial class NotesView : UserControl
{
    private NotesViewModel? _vm;

    public NotesView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm != null)
            {
                _vm.PropertyChanged -= OnVmChanged;
                _vm.SearchFocusRequested -= FocusSearch;
            }
            _vm = e.NewValue as NotesViewModel;
            if (_vm != null)
            {
                _vm.PropertyChanged += OnVmChanged;
                _vm.SearchFocusRequested += FocusSearch;
                HookEditor();
            }
        };
    }

    private NoteEditorViewModel? _editor;

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NotesViewModel.Editor))
        {
            HookEditor();
            if (_vm?.Editor != null)
            {
                var tt = Anim.EnsureTranslate(EditorPanel);
                EditorPanel.BeginAnimation(OpacityProperty, Anim.To(1, 220, Anim.EaseOut, 0));
                tt.BeginAnimation(TranslateTransform.XProperty, Anim.To(0, 380, Anim.EaseOutQuint, 50));
            }
        }
    }

    private void HookEditor()
    {
        if (_editor != null) _editor.PropertyChanged -= OnEditorChanged;
        _editor = _vm?.Editor;
        if (_editor != null) _editor.PropertyChanged += OnEditorChanged;
        ApplyExpanded();
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NoteEditorViewModel.IsExpanded)) ApplyExpanded();
    }

    private void ApplyExpanded()
    {
        bool expanded = _editor?.IsExpanded == true;
        ListColumn.MinWidth = expanded ? 0 : 280;
        ListColumn.Width = expanded ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    }

    private void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private static T? ParentOf<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private async void OnColorMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ColorOption option } item || _vm == null) return;
        if (ItemsControl.ItemsControlFromItemContainer(item) is MenuItem { DataContext: Note note })
            await _vm.SetColorAsync(note, option.Key);
    }

    private void OnMoveMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: Notebook nb } item || _vm == null) return;
        if (ItemsControl.ItemsControlFromItemContainer(item) is MenuItem { DataContext: Note note })
            _vm.MoveToNotebookCommand.Execute(new object[] { note, nb });
    }

    private void OnNotebookColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ColorOption option } item || _vm == null) return;
        if (ItemsControl.ItemsControlFromItemContainer(item) is MenuItem { DataContext: SideFilter filter })
            _vm.SetNotebookColorCommand.Execute(new object[] { filter, option.Key });
    }

    private void OnNewTypeClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: Choice<NoteType> choice }) _vm?.NewNoteCommand.Execute(choice.Value);
    }

    private async void OnCardItemClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not CheckBox { Tag: int index } box || _vm == null) return;
        var list = ParentOf<ItemsControl>(box);
        if (list?.DataContext is Note note) await _vm.ToggleCardItemAsync(note, index);
    }
}
