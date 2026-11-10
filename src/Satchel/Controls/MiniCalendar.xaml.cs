using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Satchel.Core;

namespace Satchel.Controls;

public sealed partial class MiniDay : ObservableObject
{
    public DateTime Date { get; init; }
    public int Day => Date.Day;
    public bool IsCurrentMonth { get; init; }
    public bool IsToday { get; init; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isMarked;
}

public partial class MiniCalendar : UserControl
{
    private readonly List<MiniDay> _days = [];

    public static readonly DependencyProperty SelectedDateProperty = DependencyProperty.Register(
        nameof(SelectedDate), typeof(DateTime?), typeof(MiniCalendar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((MiniCalendar)d).OnSelectedChanged()));
    public DateTime? SelectedDate { get => (DateTime?)GetValue(SelectedDateProperty); set => SetValue(SelectedDateProperty, value); }

    public static readonly DependencyProperty DisplayMonthProperty = DependencyProperty.Register(
        nameof(DisplayMonth), typeof(DateTime), typeof(MiniCalendar),
        new FrameworkPropertyMetadata(DateTime.Today, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((MiniCalendar)d).Rebuild()));
    public DateTime DisplayMonth { get => (DateTime)GetValue(DisplayMonthProperty); set => SetValue(DisplayMonthProperty, value); }

    public static readonly DependencyProperty MarkedDatesProperty = DependencyProperty.Register(
        nameof(MarkedDates), typeof(IEnumerable), typeof(MiniCalendar), new PropertyMetadata(null, OnMarkedDatesChanged));
    public IEnumerable? MarkedDates { get => (IEnumerable?)GetValue(MarkedDatesProperty); set => SetValue(MarkedDatesProperty, value); }

    public event EventHandler<DateTime>? DateClicked;

    public MiniCalendar()
    {
        InitializeComponent();
        WeekdayHeader.ItemsSource = Format.WeekdayInitials().ToList();
        Rebuild();
    }

    private static void OnMarkedDatesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (MiniCalendar)d;
        if (e.OldValue is INotifyCollectionChanged oldCol) oldCol.CollectionChanged -= c.OnMarksChanged;
        if (e.NewValue is INotifyCollectionChanged newCol) newCol.CollectionChanged += c.OnMarksChanged;
        c.ApplyMarks();
    }

    private void OnMarksChanged(object? sender, NotifyCollectionChangedEventArgs e) => ApplyMarks();

    public void Rebuild()
    {
        if (WeekdayHeader == null) return;
        WeekdayHeader.ItemsSource = Format.WeekdayInitials().ToList();
        var month = new DateTime(DisplayMonth.Year, DisplayMonth.Month, 1);
        TitleText.Text = month.ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        var start = Format.StartOfWeek(month);
        _days.Clear();
        for (int i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            _days.Add(new MiniDay
            {
                Date = date,
                IsCurrentMonth = date.Month == month.Month,
                IsToday = date == DateTime.Today,
                IsSelected = SelectedDate?.Date == date
            });
        }
        DaysGrid.ItemsSource = _days.ToList();
        ApplyMarks();
    }

    private void ApplyMarks()
    {
        var marks = new HashSet<DateTime>();
        if (MarkedDates != null)
            foreach (var o in MarkedDates)
                if (o is DateTime dt) marks.Add(dt.Date);
        foreach (var d in _days) d.IsMarked = marks.Contains(d.Date);
    }

    private void OnSelectedChanged()
    {
        if (SelectedDate is { } sel && (sel.Year != DisplayMonth.Year || sel.Month != DisplayMonth.Month))
        {
            DisplayMonth = new DateTime(sel.Year, sel.Month, 1);
            return;
        }
        foreach (var d in _days) d.IsSelected = SelectedDate?.Date == d.Date;
    }

    private void OnPrev(object sender, RoutedEventArgs e) => DisplayMonth = new DateTime(DisplayMonth.Year, DisplayMonth.Month, 1).AddMonths(-1);
    private void OnNext(object sender, RoutedEventArgs e) => DisplayMonth = new DateTime(DisplayMonth.Year, DisplayMonth.Month, 1).AddMonths(1);

    private void OnDayClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not MiniDay day) return;
        SelectedDate = day.Date;
        foreach (var d in _days) d.IsSelected = d.Date == day.Date;
        DateClicked?.Invoke(this, day.Date);
    }
}
