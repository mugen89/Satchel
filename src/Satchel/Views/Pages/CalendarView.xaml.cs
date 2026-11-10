using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Satchel.Core;
using Satchel.ViewModels;

namespace Satchel.Views.Pages;

public partial class CalendarView : UserControl
{
    private CalendarViewModel? _vm;

    public CalendarView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
            _vm = e.NewValue as CalendarViewModel;
            if (_vm != null) _vm.PropertyChanged += OnVmChanged;
        };
        Loaded += (_, _) => ScrollToMorning();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CalendarViewModel.PeriodTitle):
                Anim.EnterUp(PeriodText, 6, 220);
                break;
            case nameof(CalendarViewModel.IsWeek) when _vm?.IsWeek == true:
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, ScrollToMorning);
                Anim.EnterUp(WeekView, 10, 260);
                break;
            case nameof(CalendarViewModel.IsMonth) when _vm?.IsMonth == true:
                Anim.EnterUp(MonthView, 10, 260);
                break;
        }
    }

    private void ScrollToMorning()
    {
        if (_vm == null) return;
        var hour = Math.Max(0, Math.Min(DateTime.Now.Hour - 1, 7));
        WeekScroll.ScrollToVerticalOffset(hour * CalendarViewModel.HourHeight);
    }

    private void OnChipMouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnChipClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Thumb) return;
        if ((sender as FrameworkElement)?.DataContext is Occurrence o) _vm?.OpenCommand.Execute(o);
        e.Handled = true;
    }

    private void OnDayCellMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && (sender as FrameworkElement)?.DataContext is MonthDay day)
        {
            _vm?.NewEventCommand.Execute(day.Date);
            e.Handled = true;
        }
    }

    private void OnMoreClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MonthDay day) _vm?.ShowDayCommand.Execute(day.Date);
        e.Handled = true;
    }

    private void OnWeekColumnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || sender is not FrameworkElement { DataContext: WeekColumn col } element) return;
        var y = e.GetPosition(element).Y;
        var minutes = Math.Clamp(Math.Floor(y / CalendarViewModel.HourHeight * 2) * 30, 0, 23 * 60 + 30);
        _vm?.NewEventCommand.Execute(col.Date.AddMinutes(minutes));
        e.Handled = true;
    }

    private static Border? BlockOf(DependencyObject d)
    {
        while (d != null && !(d is Border { Name: "block" })) d = VisualTreeHelper.GetParent(d);
        return d as Border;
    }

    private void OnResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if (BlockOf((DependencyObject)sender) is { } block)
            block.Height = Math.Max(20, block.Height + e.VerticalChange);
    }

    private async void OnResizeCompleted(object sender, DragCompletedEventArgs e)
    {
        if (BlockOf((DependencyObject)sender) is { DataContext: Occurrence o } block && _vm != null)
            await _vm.ResizeAsync(o, block.Height + 2);
    }

    private async void OnCalendarColorClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: ColorOption option } item || _vm == null) return;
        if (ItemsControl.ItemsControlFromItemContainer(item) is MenuItem { DataContext: CalendarFilter filter })
            await _vm.SetCalendarColorAsync(filter, option.Key);
    }
}
