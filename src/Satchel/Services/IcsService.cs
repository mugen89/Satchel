using System.Globalization;
using System.Text;
using Satchel.Models;

namespace Satchel.Services;

/// <summary>Minimal iCalendar (.ics) import/export for interoperability with Google/Outlook calendars.</summary>
public static class IcsService
{
    public static List<CalendarEvent> Parse(string text)
    {
        var lines = Unfold(text);
        var result = new List<CalendarEvent>();
        CalendarEvent? current = null;
        bool allDay = false;
        DateTime? end = null;

        foreach (var raw in lines)
        {
            var colon = raw.IndexOf(':');
            if (colon <= 0) continue;
            var head = raw[..colon];
            var value = raw[(colon + 1)..];
            var parts = head.Split(';');
            var name = parts[0].ToUpperInvariant();
            var parameters = parts.Skip(1).Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
                .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1], StringComparer.OrdinalIgnoreCase);

            if (name == "BEGIN" && value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                current = new CalendarEvent();
                allDay = false;
                end = null;
                continue;
            }
            if (current == null) continue;

            switch (name)
            {
                case "END" when value.Equals("VEVENT", StringComparison.OrdinalIgnoreCase):
                    current.IsAllDay = allDay;
                    if (allDay)
                        current.End = (end ?? current.Start.AddDays(1)).AddDays(-1).Date;
                    else
                        current.End = end ?? current.Start.AddHours(1);
                    if (current.End < current.Start) current.End = current.Start;
                    result.Add(current);
                    current = null;
                    break;
                case "SUMMARY":
                    current.Title = Unescape(value);
                    break;
                case "DESCRIPTION":
                    current.Description = Unescape(value);
                    break;
                case "LOCATION":
                    current.Location = Unescape(value);
                    break;
                case "DTSTART":
                    if (TryDate(value, parameters, out var start, out var isDate))
                    {
                        current.Start = start;
                        allDay = isDate;
                    }
                    break;
                case "DTEND":
                    if (TryDate(value, parameters, out var e, out _)) end = e;
                    break;
                case "RRULE":
                    ApplyRule(current, value);
                    break;
            }
        }
        return result;
    }

    private static void ApplyRule(CalendarEvent e, string rule)
    {
        foreach (var part in rule.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            switch (kv[0].ToUpperInvariant())
            {
                case "FREQ":
                    e.Recurrence = kv[1].ToUpperInvariant() switch
                    {
                        "DAILY" => RecurrenceFrequency.Daily,
                        "WEEKLY" => RecurrenceFrequency.Weekly,
                        "MONTHLY" => RecurrenceFrequency.Monthly,
                        "YEARLY" => RecurrenceFrequency.Yearly,
                        _ => RecurrenceFrequency.None
                    };
                    break;
                case "INTERVAL" when int.TryParse(kv[1], out var n):
                    e.RecurrenceInterval = Math.Max(1, n);
                    break;
                case "COUNT" when int.TryParse(kv[1], out var c):
                    e.RecurrenceCount = c;
                    break;
                case "UNTIL" when TryDate(kv[1], new Dictionary<string, string>(), out var until, out _):
                    e.RecurrenceUntil = until.Date;
                    break;
                case "BYDAY":
                    int mask = 0;
                    foreach (var d in kv[1].Split(','))
                    {
                        var code = d.Length >= 2 ? d[^2..].ToUpperInvariant() : "";
                        int idx = Array.IndexOf(DayCodes, code);
                        if (idx >= 0) mask |= 1 << idx;
                    }
                    e.RecurrenceDays = mask;
                    break;
            }
        }
    }

    private static readonly string[] DayCodes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    private static bool TryDate(string value, IDictionary<string, string> parameters, out DateTime date, out bool isDateOnly)
    {
        isDateOnly = parameters.TryGetValue("VALUE", out var v) && v.Equals("DATE", StringComparison.OrdinalIgnoreCase) || value.Length == 8;
        if (isDateOnly)
            return DateTime.TryParseExact(value[..Math.Min(8, value.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

        bool utc = value.EndsWith('Z');
        var core = value.TrimEnd('Z');
        if (!DateTime.TryParseExact(core, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return false;
        if (utc) date = DateTime.SpecifyKind(date, DateTimeKind.Utc).ToLocalTime();
        date = DateTime.SpecifyKind(date, DateTimeKind.Unspecified);
        return true;
    }

    private static List<string> Unfold(string text)
    {
        var lines = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if ((line.StartsWith(' ') || line.StartsWith('\t')) && lines.Count > 0) lines[^1] += line[1..];
            else lines.Add(line);
        }
        return lines;
    }

    private static string Unescape(string s) =>
        s.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");

    public static string Build(IEnumerable<CalendarEvent> events)
    {
        var sb = new StringBuilder();
        sb.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Satchel//Organizer//EN\r\nCALSCALE:GREGORIAN\r\n");
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        foreach (var e in events)
        {
            sb.Append("BEGIN:VEVENT\r\n");
            sb.Append($"UID:satchel-{e.Id}-{e.CreatedAt.Ticks}@local\r\n");
            sb.Append($"DTSTAMP:{stamp}\r\n");
            if (e.IsAllDay)
            {
                sb.Append($"DTSTART;VALUE=DATE:{e.Start:yyyyMMdd}\r\n");
                sb.Append($"DTEND;VALUE=DATE:{e.End.Date.AddDays(1):yyyyMMdd}\r\n");
            }
            else
            {
                sb.Append($"DTSTART:{e.Start.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)}\r\n");
                sb.Append($"DTEND:{e.End.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)}\r\n");
            }
            sb.Append($"SUMMARY:{Escape(e.Title)}\r\n");
            if (!string.IsNullOrWhiteSpace(e.Location)) sb.Append($"LOCATION:{Escape(e.Location)}\r\n");
            if (!string.IsNullOrWhiteSpace(e.Description)) sb.Append($"DESCRIPTION:{Escape(e.Description)}\r\n");
            if (e.IsRecurring)
            {
                var rule = $"FREQ={e.Recurrence.ToString().ToUpperInvariant()};INTERVAL={Math.Max(1, e.RecurrenceInterval)}";
                if (e.Recurrence == RecurrenceFrequency.Weekly && e.RecurrenceDays != 0)
                    rule += ";BYDAY=" + string.Join(',', Enumerable.Range(0, 7).Where(i => (e.RecurrenceDays & (1 << i)) != 0).Select(i => DayCodes[i]));
                if (e.RecurrenceCount is > 0) rule += $";COUNT={e.RecurrenceCount}";
                else if (e.RecurrenceUntil.HasValue) rule += $";UNTIL={e.RecurrenceUntil.Value:yyyyMMdd}";
                sb.Append($"RRULE:{rule}\r\n");
                foreach (var ex in e.GetExceptions())
                    sb.Append(e.IsAllDay ? $"EXDATE;VALUE=DATE:{ex:yyyyMMdd}\r\n" : $"EXDATE:{(ex + e.Start.TimeOfDay).ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)}\r\n");
            }
            sb.Append("END:VEVENT\r\n");
        }
        sb.Append("END:VCALENDAR\r\n");
        return sb.ToString();
    }
}
