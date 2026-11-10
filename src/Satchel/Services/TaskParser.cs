using System.Globalization;
using System.Text.RegularExpressions;
using Satchel.Models;

namespace Satchel.Services;

public sealed record ParsedTask(string Title, DateTime? Due, bool HasTime, TaskPriority Priority, string? ListName, IReadOnlyList<string> Tags);

/// <summary>
/// Tiny natural-language parser for quick-add:
///   "Pay rent tomorrow !high #home", "Call Ana fri 17:30", "Dentist in 3 days at 9am !!".
/// </summary>
public static partial class TaskParser
{
    private static readonly string[] Weekdays = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    public static ParsedTask Parse(string input, IEnumerable<string> listNames)
    {
        var text = " " + input.Trim() + " ";
        DateTime? date = null;
        TimeSpan? time = null;
        var priority = TaskPriority.None;
        string? list = null;
        var tags = new List<string>();
        var today = DateTime.Today;

        // Priority: !urgent !high !med !low or !!! / !! / !
        text = PriorityRegex().Replace(text, m =>
        {
            var token = m.Groups[1].Value.ToLowerInvariant();
            priority = token switch
            {
                "urgent" or "!!" => TaskPriority.Urgent,
                "high" or "!" => TaskPriority.High,
                "med" or "medium" => TaskPriority.Medium,
                "low" => TaskPriority.Low,
                _ => TaskPriority.High
            };
            return " ";
        });

        // #list or #tag
        var names = listNames.ToList();
        text = HashRegex().Replace(text, m =>
        {
            var token = m.Groups[1].Value;
            var match = names.FirstOrDefault(n => string.Equals(n.Replace(" ", ""), token, StringComparison.OrdinalIgnoreCase));
            if (match != null && list == null) list = match;
            else tags.Add(token);
            return " ";
        });

        // Time: "at 5pm", "5:30pm", "17:30", "at 9"
        text = TimeRegex().Replace(text, m =>
        {
            int h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            int min = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
            var ampm = m.Groups["ap"].Value.ToLowerInvariant();
            bool hasAt = m.Groups["at"].Success;
            if (!m.Groups["m"].Success && string.IsNullOrEmpty(ampm) && !hasAt) return m.Value; // a bare number isn't a time
            if (ampm.StartsWith('p') && h < 12) h += 12;
            if (ampm.StartsWith('a') && h == 12) h = 0;
            if (h > 23 || min > 59) return m.Value;
            time = new TimeSpan(h, min, 0);
            return " ";
        });

        // Relative dates
        text = Replace(text, @"\b(today|tod)\b", _ => date = today);
        text = Replace(text, @"\btonight\b", _ => { date = today; time ??= new TimeSpan(20, 0, 0); });
        text = Replace(text, @"\b(tomorrow|tmrw|tmr)\b", _ => date = today.AddDays(1));
        text = Replace(text, @"\bnext week\b", _ => date = today.AddDays(7 - (int)today.DayOfWeek + 1));
        text = Replace(text, @"\bnext month\b", _ => date = new DateTime(today.Year, today.Month, 1).AddMonths(1));
        text = Replace(text, @"\bin (\d{1,3}) (day|days|week|weeks)\b", m =>
        {
            int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            date = today.AddDays(m.Groups[2].Value.StartsWith("week") ? n * 7 : n);
        });
        text = Replace(text, @"\b(next )?(sun|mon|tue|tues|wed|thu|thur|thurs|fri|sat)(day|nesday|sday|urday|rsday)?\b", m =>
        {
            var key = m.Groups[2].Value[..3];
            int dow = Array.FindIndex(Weekdays, w => w.StartsWith(key, StringComparison.Ordinal));
            int diff = ((dow - (int)today.DayOfWeek) + 7) % 7;
            if (diff == 0 || m.Groups[1].Success) diff += diff == 0 ? 7 : 0;
            if (m.Groups[1].Success && diff < 7) diff += 7;
            date = today.AddDays(diff);
        });
        // Explicit dates: 25.12, 25/12, 2026-12-25
        text = Replace(text, @"\b(\d{4})-(\d{1,2})-(\d{1,2})\b", m =>
        {
            if (TryDate(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), out var d)) date = d;
        });
        text = Replace(text, @"\b(\d{1,2})[./](\d{1,2})(?:[./](\d{2,4}))?\b", m =>
        {
            int a = int.Parse(m.Groups[1].Value), b = int.Parse(m.Groups[2].Value);
            int year = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : today.Year;
            if (year < 100) year += 2000;
            bool monthFirst = CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern.StartsWith("M", StringComparison.OrdinalIgnoreCase);
            int day = monthFirst ? b : a, month = monthFirst ? a : b;
            if (TryDate(year, month, day, out var d))
            {
                if (!m.Groups[3].Success && d < today) d = d.AddYears(1);
                date = d;
            }
        });

        if (time.HasValue && !date.HasValue) date = DateTime.Now.TimeOfDay > time.Value ? today.AddDays(1) : today;
        var title = SpaceRegex().Replace(text, " ").Trim();
        if (title.Length == 0) title = input.Trim();
        DateTime? due = date.HasValue ? date.Value.Date + (time ?? TimeSpan.Zero) : null;
        return new ParsedTask(title, due, time.HasValue, priority, list, tags);
    }

    private static bool TryDate(int year, int month, int day, out DateTime date)
    {
        date = default;
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(Math.Clamp(year, 1, 9999), month)) return false;
        date = new DateTime(year, month, day);
        return true;
    }

    private static string Replace(string text, string pattern, Action<Match> onMatch)
    {
        var regex = new Regex(pattern, RegexOptions.IgnoreCase);
        var m = regex.Match(text);
        if (!m.Success) return text;
        onMatch(m);
        return text.Remove(m.Index, m.Length).Insert(m.Index, " ");
    }

    [GeneratedRegex(@"(?<=\s)!(urgent|high|med|medium|low|!!|!)?(?=\s)", RegexOptions.IgnoreCase)]
    private static partial Regex PriorityRegex();

    [GeneratedRegex(@"(?<=\s)#([\p{L}\p{N}_-]+)(?=\s)")]
    private static partial Regex HashRegex();

    [GeneratedRegex(@"(?<=\s)(?<at>at\s+)?(?<h>\d{1,2})(?::(?<m>\d{2}))?\s?(?<ap>am|pm|a|p)?(?=\s)", RegexOptions.IgnoreCase)]
    private static partial Regex TimeRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();
}
