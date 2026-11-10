using System.Collections;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Satchel.Core;
using Satchel.Models;
using Satchel.Services;

namespace Satchel.Converters;

public abstract class ConverterBase : IValueConverter
{
    public abstract object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture);
    public virtual object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;

    protected static bool IsInvert(object? parameter) =>
        parameter is string s && (s.Equals("invert", StringComparison.OrdinalIgnoreCase) || s.Equals("not", StringComparison.OrdinalIgnoreCase));
}

/// <summary>true → Visible. Parameter "invert" flips it.</summary>
public sealed class BoolToVisibilityConverter : ConverterBase
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is true;
        if (IsInvert(parameter)) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
}

public sealed class NotConverter : ConverterBase
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public override object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>non-null / non-empty string / non-zero count / non-empty collection → Visible.</summary>
public sealed class HasValueToVisibilityConverter : ConverterBase
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool has = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            long l => l != 0,
            double d => d != 0,
            bool b => b,
            ICollection c => c.Count > 0,
            IEnumerable e => e.GetEnumerator().MoveNext(),
            _ => true
        };
        if (IsInvert(parameter)) has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
}

public sealed class HasValueConverter : ConverterBase
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool has = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            ICollection c => c.Count > 0,
            _ => true
        };
        return IsInvert(parameter) ? !has : has;
    }
}

/// <summary>value.ToString() == parameter → true. ConvertBack returns parameter (radio buttons → enum/string).</summary>
public sealed class EqualsConverter : ConverterBase
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value != null && parameter != null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);

    public override object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter == null) return Binding.DoNothing;
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (underlying.IsEnum) return Enum.Parse(underlying, parameter.ToString()!);
        if (underlying == typeof(int)) return int.Parse(parameter.ToString()!, CultureInfo.InvariantCulture);
        if (underlying == typeof(double)) return double.Parse(parameter.ToString()!, CultureInfo.InvariantCulture);
        return parameter.ToString();
    }
}

public sealed class EqualsToVisibilityConverter : ConverterBase
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var p = parameter?.ToString() ?? "";
        bool invert = p.StartsWith('!');
        if (invert) p = p[1..];
        var options = p.Split('|');
        bool eq = value != null && options.Any(o => string.Equals(value.ToString(), o, StringComparison.OrdinalIgnoreCase));
        if (invert) eq = !eq;
        return eq ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>Palette key → shared theme-aware brush. Parameter: "subtle" | "note" | "sticky".</summary>
public sealed class PaletteBrushConverter : ConverterBase
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value?.ToString();
        return (parameter as string) switch
        {
            "subtle" => ThemeBrushes.TagSubtle(key),
            "note" => ThemeBrushes.Note(key),
            "sticky" => ThemeBrushes.Sticky(key),
            _ => ThemeBrushes.Tag(key)
        };
    }
}

/// <summary>byte[] (PNG/JPEG) → cached BitmapSource.</summary>
public sealed class BytesToImageConverter : ConverterBase
{
    private static readonly ConditionalWeakTable<byte[], BitmapSource> Cache = new();

    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is byte[] bytes ? ToImage(bytes) : null;

    public static BitmapSource? ToImage(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        if (Cache.TryGetValue(bytes, out var cached)) return cached;
        try
        {
            var img = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            img.StreamSource = ms;
            img.EndInit();
            img.Freeze();
            Cache.AddOrUpdate(bytes, img);
            return img;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>"Jane Doe" → "JD".</summary>
public sealed class InitialsConverter : ConverterBase
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Initials(value?.ToString());

    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";
        var parts = name.Split([' ', '.', '-', '_', '@'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        var first = char.ToUpperInvariant(parts[0].FirstOrDefault(char.IsLetterOrDigit));
        if (parts.Length == 1 || first == '\0') return first == '\0' ? "?" : first.ToString();
        var second = char.ToUpperInvariant(parts[1].FirstOrDefault(char.IsLetterOrDigit));
        return second == '\0' ? first.ToString() : $"{first}{second}";
    }
}

/// <summary>Date formatting. Parameter: time | date | friendly | relative | datetime | day | month | weekday | monthday | full.</summary>
public sealed class DateFormatConverter : ConverterBase
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DateTime d) return "";
        return (parameter as string) switch
        {
            "time" => Format.Time(d),
            "date" => Format.Date(d),
            "friendly" => Format.FriendlyDay(d),
            "relative" => Format.Relative(d),
            "datetime" => Format.DateAndTime(d),
            "day" => d.Day.ToString(CultureInfo.CurrentCulture),
            "month" => d.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
            "monthshort" => d.ToString("MMM", CultureInfo.CurrentCulture).ToUpperInvariant(),
            "weekday" => d.ToString("dddd", CultureInfo.CurrentCulture),
            "weekdayshort" => d.ToString("ddd", CultureInfo.CurrentCulture),
            "monthday" => d.ToString("d MMMM", CultureInfo.CurrentCulture),
            "full" => d.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture),
            _ => Format.Date(d)
        };
    }
}

/// <summary>value * parameter (double).</summary>
public sealed class MultiplyConverter : ConverterBase
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double v = System.Convert.ToDouble(value ?? 0, CultureInfo.InvariantCulture);
        double f = double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 1;
        return v * f;
    }
}

/// <summary>[fraction 0..1, total width] → width.</summary>
public sealed class ProgressWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return 0.0;
        double fraction = values[0] is double d ? d : System.Convert.ToDouble(values[0] is IConvertible ? values[0] : 0, CultureInfo.InvariantCulture);
        double width = values[1] is double w ? w : 0;
        return Math.Max(0, Math.Min(1, fraction)) * width;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Strength score (0–4, -1 empty) → semantic brush.</summary>
public sealed class StrengthBrushConverter : ConverterBase
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int score = value is int i ? i : -1;
        var key = score switch
        {
            0 => "DangerBrush",
            1 => "DangerBrush",
            2 => "WarningBrush",
            3 => "SuccessBrush",
            4 => "SuccessBrush",
            _ => "StrokeBrush"
        };
        return Application.Current.TryFindResource(key);
    }
}

public sealed class PriorityBrushConverter : ConverterBase
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var p = value is TaskPriority tp ? tp : TaskPriority.None;
        var key = p switch
        {
            TaskPriority.Urgent => "DangerBrush",
            TaskPriority.High => "WarningBrush",
            TaskPriority.Medium => "InfoBrush",
            TaskPriority.Low => "TextMutedBrush",
            _ => "TextFaintBrush"
        };
        return Application.Current.TryFindResource(key);
    }
}

/// <summary>bool → one of two glyphs/strings: parameter "trueValue|falseValue".</summary>
public sealed class BoolToStringConverter : ConverterBase
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter?.ToString() ?? "|").Split('|');
        return value is true ? parts[0] : (parts.Length > 1 ? parts[1] : "");
    }
}

/// <summary>Enum / object → display string via ToString, with spaces before capitals.</summary>
public sealed class HumanizeConverter : ConverterBase
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is AccountKind k) return VaultAccount.KindName(k);
        var s = value?.ToString() ?? "";
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && !char.IsUpper(s[i - 1])) sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}

public sealed class AccountKindGlyphConverter : ConverterBase
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AccountKind k ? VaultAccount.KindGlyph(k) : Glyphs.Key;
}

/// <summary>Count → "N" or "" when zero (for badges).</summary>
public sealed class CountBadgeConverter : ConverterBase
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i && i > 0 ? (i > 99 ? "99+" : i.ToString(CultureInfo.CurrentCulture)) : "";
}

/// <summary>Double → CornerRadius (uniform).</summary>
public sealed class CornerRadiusConverter : ConverterBase
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double v = value is double d ? d : 0;
        double f = double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 1;
        return new CornerRadius(v * f);
    }
}

/// <summary>Percent text for doubles in 0..1 or scale values ("110%").</summary>
public sealed class PercentConverter : ConverterBase
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double d ? $"{Math.Round(d * 100)}%" : "";
}

public sealed class ColorKeyToColorConverter : ConverterBase
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Palettes.TagColor(value?.ToString(), ThemeState.Instance.IsDark);
}

/// <summary>Hex string → SolidColorBrush (for previews of literal colors).</summary>
public sealed class HexToBrushConverter : ConverterBase
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ColorUtil.TryParse(value?.ToString(), out var c) ? ColorUtil.Brush(c) : Brushes.Transparent;
}

/// <summary>Packs several bound values into an object[] (for multi-value command parameters).</summary>
public sealed class ArrayConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) => values.Clone();
    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>true when the two bound values are the same object instance.</summary>
public sealed class SameObjectConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length >= 2 && values[0] != null && ReferenceEquals(values[0], values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>true when all bound values are equal (compares ToString()).</summary>
public sealed class AllEqualConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return false;
        var first = values[0]?.ToString();
        return values.Skip(1).All(v => v?.ToString() == first);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>bool → GridLength. Parameter "1.2*|0" (true|false), "*" means star, a number means pixels.</summary>
public sealed class BoolToGridLengthConverter : ConverterBase
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter?.ToString() ?? "*|0").Split('|');
        var spec = value is true ? parts[0] : (parts.Length > 1 ? parts[1] : "0");
        return Parse(spec);
    }

    private static GridLength Parse(string spec)
    {
        spec = spec.Trim();
        if (spec.Equals("auto", StringComparison.OrdinalIgnoreCase)) return GridLength.Auto;
        if (spec.EndsWith('*'))
        {
            var n = spec.Length == 1 ? 1 : double.Parse(spec[..^1], CultureInfo.InvariantCulture);
            return new GridLength(n, GridUnitType.Star);
        }
        return new GridLength(double.Parse(spec, CultureInfo.InvariantCulture));
    }
}

/// <summary>Builds a uniform Thickness from a double; parameter "left|top|right|bottom" picks sides.</summary>
public sealed class IndentConverter : ConverterBase
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int level = value is int i ? i : 0;
        return new Thickness(level * 22, 0, 0, 0);
    }
}

/// <summary>Rating (int) + star number (parameter) → filled or outline star glyph.</summary>
public sealed class RatingStarConverter : ConverterBase
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int rating = value is int i ? i : 0;
        int n = int.TryParse(parameter?.ToString(), out var p) ? p : 1;
        return rating >= n ? Core.Glyphs.StarFilled : Core.Glyphs.Star;
    }
}

/// <summary>Rating (int) + star number (parameter) → true when the star is filled.</summary>
public sealed class RatingFilledConverter : ConverterBase
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int rating = value is int i ? i : 0;
        int n = int.TryParse(parameter?.ToString(), out var p) ? p : 1;
        return rating >= n;
    }
}

/// <summary>Rating (int) + star number (parameter) → gold brush for filled stars, faint otherwise.</summary>
public sealed class RatingBrushConverter : ConverterBase
{
    public override object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int rating = value is int i ? i : 0;
        int n = int.TryParse(parameter?.ToString(), out var p) ? p : 1;
        return System.Windows.Application.Current?.TryFindResource(rating >= n ? "WarningBrush" : "TextFaintBrush");
    }
}
