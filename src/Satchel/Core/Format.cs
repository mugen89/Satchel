using System.Globalization;

namespace Satchel.Core;

/// <summary>Formatting helpers that respect the user's date/time preferences.</summary>
public static class Format
{
    public static bool Use24Hour { get; set; } = true;
    public static string DatePattern { get; set; } = "";
    public static bool WeekStartsMonday { get; set; } = true;

    public static DayOfWeek FirstDayOfWeek => WeekStartsMonday ? DayOfWeek.Monday : DayOfWeek.Sunday;

    public static string TimePattern => Use24Hour ? "HH:mm" : "h:mm tt";

    public static string Time(DateTime t) => t.ToString(TimePattern, CultureInfo.CurrentCulture);

    public static string Date(DateTime d) => string.IsNullOrWhiteSpace(DatePattern)
        ? d.ToString("d", CultureInfo.CurrentCulture)
        : d.ToString(DatePattern, CultureInfo.CurrentCulture);

    public static string DateAndTime(DateTime d) => $"{Date(d)} {Time(d)}";

    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
    }

    /// <summary>"Today", "Tomorrow", "Yesterday", weekday within a week, else short date.</summary>
    public static string FriendlyDay(DateTime d)
    {
        var today = System.DateTime.Today;
        var diff = (d.Date - today).Days;
        return diff switch
        {
            0 => "Today",
            1 => "Tomorrow",
            -1 => "Yesterday",
            > 1 and < 7 => d.ToString("dddd", CultureInfo.CurrentCulture),
            _ => d.Year == today.Year ? d.ToString("ddd, d MMM", CultureInfo.CurrentCulture) : Date(d)
        };
    }

    public static string Relative(DateTime when)
    {
        var span = System.DateTime.Now - when;
        if (span.TotalSeconds < 0) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 2) return "yesterday";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays} days ago";
        if (span.TotalDays < 31) return $"{(int)(span.TotalDays / 7)} wk ago";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)} mo ago";
        var years = (int)(span.TotalDays / 365);
        return years == 1 ? "1 year ago" : $"{years} years ago";
    }

    public static string Greeting()
    {
        var h = System.DateTime.Now.Hour;
        return h switch
        {
            < 5 => "Good night",
            < 12 => "Good morning",
            < 18 => "Good afternoon",
            _ => "Good evening"
        };
    }

    public static DateTime StartOfWeek(DateTime d)
    {
        int diff = (7 + (d.DayOfWeek - FirstDayOfWeek)) % 7;
        return d.Date.AddDays(-diff);
    }

    public static IEnumerable<string> WeekdayInitials()
    {
        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        for (int i = 0; i < 7; i++)
        {
            var dow = ((int)FirstDayOfWeek + i) % 7;
            var n = names[dow];
            yield return n.Length > 2 ? n[..2] : n;
        }
    }

    public static IEnumerable<string> WeekdayShortNames()
    {
        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        for (int i = 0; i < 7; i++)
            yield return names[((int)FirstDayOfWeek + i) % 7];
    }
}
