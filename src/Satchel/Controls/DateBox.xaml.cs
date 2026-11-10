using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Satchel.Core;

namespace Satchel.Controls;

/// <summary>Date input with a themed calendar popup and quick picks.</summary>
public partial class DateBox : UserControl
{
    public static readonly DependencyProperty SelectedDateProperty = DependencyProperty.Register(
        nameof(SelectedDate), typeof(DateTime?), typeof(DateBox),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((DateBox)d).UpdateDisplay()));
    public DateTime? SelectedDate { get => (DateTime?)GetValue(SelectedDateProperty); set => SetValue(SelectedDateProperty, value); }

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(DateBox), new PropertyMetadata("No date", (d, _) => ((DateBox)d).UpdateDisplay()));
    public string Placeholder { get => (string)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }

    public static readonly DependencyProperty AllowClearProperty = DependencyProperty.Register(
        nameof(AllowClear), typeof(bool), typeof(DateBox), new PropertyMetadata(true, (d, _) => ((DateBox)d).UpdateDisplay()));
    public bool AllowClear { get => (bool)GetValue(AllowClearProperty); set => SetValue(AllowClearProperty, value); }

    /// <summary>When true the time-of-day of the existing value is preserved when picking a new date.</summary>
    public static readonly DependencyProperty KeepTimeProperty = DependencyProperty.Register(
        nameof(KeepTime), typeof(bool), typeof(DateBox), new PropertyMetadata(true));
    public bool KeepTime { get => (bool)GetValue(KeepTimeProperty); set => SetValue(KeepTimeProperty, value); }

    public DateBox()
    {
        InitializeComponent();
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        if (Display == null) return;
        if (SelectedDate is { } d)
        {
            Display.Text = d.ToString("ddd, d MMM yyyy", CultureInfo.CurrentCulture);
            Display.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        }
        else
        {
            Display.Text = Placeholder;
            Display.SetResourceReference(TextBlock.ForegroundProperty, "TextFaintBrush");
        }
        ClearButton.Visibility = AllowClear && SelectedDate.HasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Calendar.SelectedDate = SelectedDate;
        var month = SelectedDate ?? DateTime.Today;
        Calendar.DisplayMonth = new DateTime(month.Year, month.Month, 1);
    }

    private void Pick(DateTime date)
    {
        var time = KeepTime && SelectedDate.HasValue ? SelectedDate.Value.TimeOfDay : TimeSpan.Zero;
        SelectedDate = date.Date + time;
        Toggle.IsChecked = false;
    }

    private void OnDatePicked(object? sender, DateTime date) => Pick(date);

    private void OnQuick(object sender, RoutedEventArgs e)
    {
        var days = int.Parse((string)((Button)sender).Tag, CultureInfo.InvariantCulture);
        Pick(DateTime.Today.AddDays(days));
    }

    private void OnClear(object sender, RoutedEventArgs e) => SelectedDate = null;
}
