using System.Globalization;
using Satchel.Core;
using Satchel.Models;

namespace Satchel.Services;

/// <summary>Expands recurring events into concrete occurrences.</summary>
public static class Recurrence
{
    private const int MaxIterations = 20000;

    public static IEnumerable<(DateTime Start, DateTime End)> Occurrences(CalendarEvent e, DateTime rangeStart, DateTime rangeEnd)
    {
        var duration = e.End - e.Start;
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

        if (e.Recurrence == RecurrenceFrequency.None)
        {
            if (Overlaps(e.Start, e.Start + duration, rangeStart, rangeEnd, e.IsAllDay)) yield return (e.Start, e.Start + duration);
            yield break;
        }

        var exceptions = e.GetExceptions();
        int interval = Math.Max(1, e.RecurrenceInterval);
        int maxCount = e.RecurrenceCount is > 0 ? e.RecurrenceCount.Value : int.MaxValue;
        DateTime until = e.RecurrenceUntil?.Date.AddDays(1) ?? DateTime.MaxValue;
        int count = 0;

        foreach (var start in Candidates(e, interval))
        {
            if (start >= until || count >= maxCount || start >= rangeEnd) yield break;
            count++;
            if (exceptions.Contains(start.Date)) continue;
            if (Overlaps(start, start + duration, rangeStart, rangeEnd, e.IsAllDay)) yield return (start, start + duration);
        }
    }

    /// <summary>All-day events cover whole days [Start.Date, End.Date + 1); timed events cover [Start, End).</summary>
    private static bool Overlaps(DateTime start, DateTime end, DateTime rangeStart, DateTime rangeEnd, bool allDay)
    {
        var effStart = allDay ? start.Date : start;
        var effEnd = allDay ? end.Date.AddDays(1) : (end > start ? end : start.AddTicks(1));
        return effStart < rangeEnd && effEnd > rangeStart;
    }

    private static IEnumerable<DateTime> Candidates(CalendarEvent e, int interval)
    {
        var first = e.Start;
        var time = first.TimeOfDay;
        int iterations = 0;

        switch (e.Recurrence)
        {
            case RecurrenceFrequency.Daily:
                for (var s = first; iterations++ < MaxIterations; s = s.AddDays(interval)) yield return s;
                break;

            case RecurrenceFrequency.Weekly:
            {
                int mask = e.RecurrenceDays == 0 ? 1 << (int)first.DayOfWeek : e.RecurrenceDays;
                var weekStart = first.Date.AddDays(-(int)first.DayOfWeek); // Sunday-based weeks for interval math
                for (var w = weekStart; iterations++ < MaxIterations; w = w.AddDays(7 * interval))
                {
                    for (int d = 0; d < 7; d++)
                    {
                        var date = w.AddDays(d);
                        if ((mask & (1 << (int)date.DayOfWeek)) == 0) continue;
                        var s = date + time;
                        if (s < first) continue;
                        yield return s;
                    }
                }
                break;
            }

            case RecurrenceFrequency.Monthly:
                for (int i = 0; iterations++ < MaxIterations; i += interval)
                {
                    var month = new DateTime(first.Year, first.Month, 1).AddMonths(i);
                    int day = Math.Min(first.Day, DateTime.DaysInMonth(month.Year, month.Month));
                    yield return new DateTime(month.Year, month.Month, day) + time;
                }
                break;

            case RecurrenceFrequency.Yearly:
                for (int i = 0; iterations++ < MaxIterations; i += interval)
                {
                    int year = first.Year + i;
                    if (year > 9998) yield break;
                    int day = Math.Min(first.Day, DateTime.DaysInMonth(year, first.Month));
                    yield return new DateTime(year, first.Month, day) + time;
                }
                break;
        }
    }

    public static string Describe(CalendarEvent e)
    {
        if (e.Recurrence == RecurrenceFrequency.None) return "Does not repeat";
        int n = Math.Max(1, e.RecurrenceInterval);
        var culture = CultureInfo.CurrentCulture;
        string text = e.Recurrence switch
        {
            RecurrenceFrequency.Daily => n == 1 ? "Every day" : $"Every {n} days",
            RecurrenceFrequency.Weekly => (n == 1 ? "Every week" : $"Every {n} weeks") + " on " + WeekdayList(e.RecurrenceDays == 0 ? 1 << (int)e.Start.DayOfWeek : e.RecurrenceDays),
            RecurrenceFrequency.Monthly => (n == 1 ? "Every month" : $"Every {n} months") + $" on day {e.Start.Day}",
            RecurrenceFrequency.Yearly => (n == 1 ? "Every year" : $"Every {n} years") + $" on {e.Start.ToString("d MMMM", culture)}",
            _ => ""
        };
        if (e.RecurrenceUntil.HasValue) text += $", until {Format.Date(e.RecurrenceUntil.Value)}";
        else if (e.RecurrenceCount is > 0) text += $", {e.RecurrenceCount} times";
        return text;
    }

    public static string WeekdayList(int mask)
    {
        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        var list = new List<string>();
        for (int i = 0; i < 7; i++)
        {
            int dow = ((int)Format.FirstDayOfWeek + i) % 7;
            if ((mask & (1 << dow)) != 0) list.Add(names[dow]);
        }
        return list.Count == 7 ? "every day" : string.Join(", ", list);
    }
}
