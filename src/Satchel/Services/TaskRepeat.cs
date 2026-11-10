using System.Globalization;
using Satchel.Data;
using Satchel.Models;
using Satchel.ViewModels;

namespace Satchel.Services;

/// <summary>
/// Repeating tasks. When a repeating task is completed, the next one is created with the following due date
/// (same list, priority, tags, subtasks and reminder offset); the completed one stays in the history.
/// </summary>
public static class TaskRepeat
{
    public static IReadOnlyList<Choice<string>> Choices { get; } =
    [
        new("", "Doesn't repeat", Core.Glyphs.Close),
        new("daily", "Every day", Core.Glyphs.Repeat),
        new("weekdays", "Every weekday (Mon–Fri)", Core.Glyphs.Repeat),
        new("weekly", "Every week", Core.Glyphs.Repeat),
        new("biweekly", "Every 2 weeks", Core.Glyphs.Repeat),
        new("monthly", "Every month", Core.Glyphs.Repeat),
        new("months:3", "Every 3 months", Core.Glyphs.Repeat),
        new("yearly", "Every year", Core.Glyphs.Repeat),
    ];

    public static string Describe(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule)) return "";
        if (Choices.FirstOrDefault(c => c.Value == rule) is { } known) return known.Label;
        var (unit, n) = Split(rule);
        return unit switch
        {
            "days" => n == 1 ? "Every day" : $"Every {n} days",
            "weeks" => n == 1 ? "Every week" : $"Every {n} weeks",
            "months" => n == 1 ? "Every month" : $"Every {n} months",
            "years" => n == 1 ? "Every year" : $"Every {n} years",
            _ => "Repeats"
        };
    }

    private static (string Unit, int N) Split(string rule)
    {
        var parts = rule.Split(':');
        int n = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Max(1, v) : 1;
        return (parts[0], n);
    }

    /// <summary>The occurrence after <paramref name="from"/>.</summary>
    public static DateTime Next(string rule, DateTime from)
    {
        switch (rule)
        {
            case "daily": return from.AddDays(1);
            case "weekly": return from.AddDays(7);
            case "biweekly": return from.AddDays(14);
            case "monthly": return from.AddMonths(1);
            case "yearly": return from.AddYears(1);
            case "weekdays":
                var d = from.AddDays(1);
                while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(1);
                return d;
        }
        var (unit, n) = Split(rule);
        return unit switch
        {
            "days" => from.AddDays(n),
            "weeks" => from.AddDays(7 * n),
            "months" => from.AddMonths(n),
            "years" => from.AddYears(n),
            _ => from.AddDays(1)
        };
    }

    /// <summary>Parses quick-add words ("every day", "daily", "every monday", "every 2 weeks"…). Returns the rule and the text without it.</summary>
    public static (string Rule, string Text, DateTime? FirstDue) ParseQuickAdd(string text)
    {
        var lowered = " " + text.ToLowerInvariant() + " ";
        (string Phrase, string Rule)[] phrases =
        [
            (" every weekday ", "weekdays"), (" weekdays ", "weekdays"), (" every day ", "daily"), (" daily ", "daily"), (" every week ", "weekly"),
            (" weekly ", "weekly"), (" every other week ", "biweekly"), (" every 2 weeks ", "biweekly"), (" every month ", "monthly"), (" monthly ", "monthly"),
            (" every year ", "yearly"), (" yearly ", "yearly"), (" annually ", "yearly")
        ];
        foreach (var (phrase, rule) in phrases)
        {
            int i = lowered.IndexOf(phrase, StringComparison.Ordinal);
            if (i < 0) continue;
            var rest = (lowered[..i] + " " + lowered[(i + phrase.Length)..]).Trim();
            return (rule, Restore(text, rest), null);
        }
        // "every monday" → weekly, first due on the next Monday.
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            var phrase = $" every {day.ToString().ToLowerInvariant()} ";
            int i = lowered.IndexOf(phrase, StringComparison.Ordinal);
            if (i < 0) continue;
            var first = DateTime.Today;
            while (first.DayOfWeek != day) first = first.AddDays(1);
            var rest = (lowered[..i] + " " + lowered[(i + phrase.Length)..]).Trim();
            return ("weekly", Restore(text, rest), first);
        }
        return ("", text, null);
    }

    /// <summary>Keeps the user's capitalisation: removes the phrase from the original text.</summary>
    private static string Restore(string original, string loweredRest)
    {
        var words = original.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var keep = loweredRest.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var result = new List<string>();
        int k = 0;
        foreach (var w in words)
        {
            if (k < keep.Count && w.Equals(keep[k], StringComparison.OrdinalIgnoreCase)) { result.Add(w); k++; }
        }
        return string.Join(' ', result);
    }

    /// <summary>
    /// Call after a task was marked done. For repeating tasks the next occurrence is created and returned (and the finished one
    /// leaves the series, so un-checking it never creates duplicates).
    /// </summary>
    public static async Task<TaskItem?> OnCompletedAsync(DataStore store, TaskItem task)
    {
        if (!task.IsDone || string.IsNullOrEmpty(task.Repeat)) return null;
        var rule = task.Repeat;
        var baseDate = task.DueDate ?? DateTime.Today;
        var next = Next(rule, baseDate);
        // Overdue repeating tasks catch up to the next date from today.
        while (next.Date < DateTime.Today) next = Next(rule, next);
        var copy = new TaskItem
        {
            Title = task.Title,
            Notes = task.Notes,
            ListId = task.ListId,
            Priority = task.Priority,
            Tags = task.Tags,
            Repeat = rule,
            DueDate = task.DueHasTime ? next : next.Date,
            DueHasTime = task.DueHasTime,
            SortOrder = task.SortOrder,
            IsPrivate = task.IsPrivate,
            Subtasks = TaskItem.SerializeSubtasks(task.SubtaskItems.Select(s => new Subtask { Text = s.Text, IsDone = false }))
        };
        if (task.ReminderAt is { } reminder && task.DueDate is { } due)
        {
            var offset = (task.DueHasTime ? due : due.Date.AddHours(9)) - reminder;
            copy.ReminderAt = (copy.DueHasTime ? copy.DueDate!.Value : copy.DueDate!.Value.Date.AddHours(9)) - offset;
        }
        task.Repeat = "";
        await store.RunAsync(db =>
        {
            db.Update(task);
            db.Add(copy);
            db.SaveChanges();
        });
        return copy;
    }
}
