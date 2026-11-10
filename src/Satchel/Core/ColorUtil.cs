using System.Globalization;
using System.Windows.Media;

namespace Satchel.Core;

public static class ColorUtil
{
    public static Color Parse(string hex)
    {
        if (TryParse(hex, out var c)) return c;
        return Colors.Gray;
    }

    public static bool TryParse(string? hex, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s.Select(ch => $"{ch}{ch}"));
        if (s.Length == 6) s = "FF" + s;
        if (s.Length != 8 || !uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        color = Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color Blend(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (byte)(from.A + (to.A - from.A) * amount),
            (byte)(from.R + (to.R - from.R) * amount),
            (byte)(from.G + (to.G - from.G) * amount),
            (byte)(from.B + (to.B - from.B) * amount));
    }

    public static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), c.R, c.G, c.B);

    public static Color Lighten(Color c, double amount) => Blend(c, Colors.White, amount);
    public static Color Darken(Color c, double amount) => Blend(c, Colors.Black, amount);

    public static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    public static double Contrast(Color a, Color b)
    {
        var la = Luminance(a) + 0.05;
        var lb = Luminance(b) + 0.05;
        return la > lb ? la / lb : lb / la;
    }

    /// <summary>White or near-black, whichever reads better on the given background.</summary>
    public static Color OnColor(Color background)
    {
        // Prefer white text (the common convention for saturated accents); fall back to dark on light colors.
        return Contrast(background, Colors.White) >= 3.0 ? Colors.White : Color.FromRgb(0x0E, 0x14, 0x18);
    }

    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public static SolidColorBrush Brush(string hex) => Brush(Parse(hex));
}
