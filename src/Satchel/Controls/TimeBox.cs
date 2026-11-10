using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Satchel.Core;

namespace Satchel.Controls;

/// <summary>Editable time picker: pick from a list or type "9", "930", "9:30 pm", "21.30".</summary>
public sealed class TimeBox : ComboBox
{
    private bool _updating;

    public static readonly DependencyProperty TimeProperty = DependencyProperty.Register(
        nameof(Time), typeof(TimeSpan), typeof(TimeBox),
        new FrameworkPropertyMetadata(TimeSpan.FromHours(9), FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((TimeBox)d).ShowTime()));
    public TimeSpan Time { get => (TimeSpan)GetValue(TimeProperty); set => SetValue(TimeProperty, value); }

    public TimeBox()
    {
        // Subclasses don't pick up implicit styles automatically.
        SetResourceReference(StyleProperty, typeof(ComboBox));
        IsEditable = true;
        IsTextSearchEnabled = false;
        MinWidth = 110;
        var items = new List<string>();
        for (int m = 0; m < 24 * 60; m += 30)
            items.Add(Format.Time(DateTime.Today.AddMinutes(m)));
        ItemsSource = items;
        Loaded += (_, _) => ShowTime();
        SelectionChanged += (_, _) =>
        {
            if (_updating || SelectedItem is not string s) return;
            if (TryParse(s, out var t)) SetTime(t);
        };
        LostKeyboardFocus += (_, _) => Commit();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); } };
    }

    private void Commit()
    {
        if (_updating) return;
        if (TryParse(Text, out var t)) SetTime(t);
        else ShowTime();
    }

    private void SetTime(TimeSpan t)
    {
        _updating = true;
        Time = t;
        _updating = false;
        ShowTime();
    }

    private void ShowTime()
    {
        _updating = true;
        var text = Format.Time(DateTime.Today.Add(Time));
        SelectedItem = (ItemsSource as IList<string>)?.FirstOrDefault(i => i == text);
        Text = text;
        _updating = false;
    }

    public static bool TryParse(string? input, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(input)) return false;
        var s = input.Trim().ToLowerInvariant().Replace(" ", "");
        bool pm = s.EndsWith("pm") || s.EndsWith('p');
        bool am = s.EndsWith("am") || s.EndsWith('a');
        s = s.TrimEnd('m', 'p', 'a');

        int hours, minutes = 0;
        var parts = s.Split([':', '.', 'h'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
        {
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minutes)) return false;
        }
        else if (parts.Length == 1 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            if (parts[0].Length <= 2) hours = n;
            else { hours = n / 100; minutes = n % 100; }
        }
        else return false;

        if (pm && hours < 12) hours += 12;
        if (am && hours == 12) hours = 0;
        if (hours is < 0 or > 23 || minutes is < 0 or > 59) return false;
        time = new TimeSpan(hours, minutes, 0);
        return true;
    }
}
