using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Satchel.Core;
using Satchel.Models;
using Satchel.ViewModels;

namespace Satchel.Views.Pages;

public partial class TasksView : UserControl
{
    private TasksViewModel? _vm;

    public TasksView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm != null)
            {
                _vm.PropertyChanged -= OnVmChanged;
                _vm.SearchFocusRequested -= FocusSearch;
                _vm.QuickAddFocusRequested -= FocusQuickAdd;
            }
            _vm = e.NewValue as TasksViewModel;
            if (_vm == null) return;
            _vm.PropertyChanged += OnVmChanged;
            _vm.SearchFocusRequested += FocusSearch;
            _vm.QuickAddFocusRequested += FocusQuickAdd;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TasksViewModel.Detail) && _vm?.Detail != null)
        {
            var tt = Anim.EnsureTranslate(DetailPanel);
            DetailPanel.BeginAnimation(OpacityProperty, Anim.To(1, 220, Anim.EaseOut, 0));
            tt.BeginAnimation(TranslateTransform.XProperty, Anim.To(0, 380, Anim.EaseOutQuint, 40));
        }
    }

    private void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void FocusQuickAdd() => QuickAddBox.Focus();

    private void OnQuickAddFocus(object sender, RoutedEventArgs e) =>
        QuickAddBorder.SetResourceReference(Border.BorderBrushProperty, QuickAddBox.IsKeyboardFocused ? "AccentBrush" : "StrokeBrush");

    private static TaskItem? TaskFromSubmenu(MenuItem item)
    {
        DependencyObject? d = item;
        while (d != null)
        {
            if (d is ContextMenu menu) return menu.DataContext as TaskItem;
            if (d is FrameworkElement { DataContext: TaskItem t } && d is MenuItem && d != item) return t;
            d = LogicalTreeHelper.GetParent(d) ?? (d is MenuItem mi ? ItemsControl.ItemsControlFromItemContainer(mi) : null);
        }
        return null;
    }

    private async void OnDueClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } item || _vm == null || TaskFromSubmenu(item) is not { } task) return;
        int days = int.Parse(tag, CultureInfo.InvariantCulture);
        await _vm.SetDueAsync(task, days < 0 ? null : DateTime.Today.AddDays(days));
    }

    private void OnPriorityClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: Choice<TaskPriority> choice } item || _vm == null) return;
        if (ItemsControl.ItemsControlFromItemContainer(item) is MenuItem { DataContext: TaskItem task })
            _vm.SetPriorityCommand.Execute(new object[] { task, choice.Value });
    }

    private void OnMoveClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: TaskList list } item || _vm == null) return;
        if (ItemsControl.ItemsControlFromItemContainer(item) is MenuItem { DataContext: TaskItem task })
        {
            var filter = list.Id == 0 ? _vm.InboxFilter : _vm.ListFilters.FirstOrDefault(f => f.Payload == list);
            if (filter != null) _vm.DropOnListCommand.Execute(new Behaviors.DropEventArgs { Data = task, Target = filter });
        }
    }

    private void OnListColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ColorOption option } item || _vm == null) return;
        if (ItemsControl.ItemsControlFromItemContainer(item) is MenuItem { DataContext: SideFilter filter })
            _vm.SetListColorCommand.Execute(new object[] { filter, option.Key });
    }
}
