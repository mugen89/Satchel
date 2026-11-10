using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Satchel.Core;
using Satchel.Models;

namespace Satchel.ViewModels;

public enum OccurrenceKind { Event, Task, Birthday }

/// <summary>A concrete appearance of an event/task/birthday on the calendar.</summary>
public sealed partial class Occurrence : ObservableObject
{
    public OccurrenceKind Kind { get; init; }
    public CalendarEvent? Event { get; init; }
    public TaskItem? Task { get; init; }
    public Contact? Contact { get; init; }
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public bool IsAllDay { get; init; }
    public string Title { get; init; } = "";
    public string ColorKey { get; init; } = "teal";
    public string Location { get; init; } = "";

    // Week-view layout (pixels / fractions), filled in by the page.
    public double Top { get; set; }
    public double Height { get; set; }
    public double Left { get; set; }
    public double Width { get; set; } = 1;

    public bool IsRecurring => Event?.IsRecurring == true;
    public bool IsDone => Task?.IsDone == true;
    public bool IsDraggable => Kind != OccurrenceKind.Birthday;
    public object? DragPayload => IsDraggable ? this : null;
    public bool IsSolid => IsAllDay && Kind == OccurrenceKind.Event;
    public string Tooltip => $"{Title}\n{RangeText}{(HasLocation ? "\n" + Location : "")}";
    public bool HasLocation => !string.IsNullOrWhiteSpace(Location);

    public string TimeText => IsAllDay ? "All day" : Format.Time(Start);
    public string RangeText => IsAllDay
        ? (End.Date > Start.Date ? $"{Format.FriendlyDay(Start)} – {Format.FriendlyDay(End)}" : "All day")
        : $"{Format.Time(Start)} – {Format.Time(End)}";

    public string Glyph => Kind switch
    {
        OccurrenceKind.Task => IsDone ? Glyphs.CheckboxChecked : Glyphs.Checkbox,
        OccurrenceKind.Birthday => Glyphs.Heart,
        _ => IsRecurring ? Glyphs.Repeat : ""
    };

    public bool HasGlyph => Glyph.Length > 0;
    public string ChipText => IsAllDay || Kind != OccurrenceKind.Event ? Title : $"{Format.Time(Start)} {Title}";
    public bool IsShort => (End - Start).TotalMinutes <= 40;
}

public sealed partial class MonthDay : ObservableObject
{
    public DateTime Date { get; init; }
    public bool IsCurrentMonth { get; init; }
    public bool IsToday { get; init; }
    public bool IsWeekend { get; init; }
    public string DayText => Date.Day == 1 ? Date.ToString("d MMM") : Date.Day.ToString();
    public ObservableCollection<Occurrence> Items { get; } = [];
    [ObservableProperty] private int _more;
    public string MoreText => $"+{More} more";
    partial void OnMoreChanged(int value) => OnPropertyChanged(nameof(MoreText));
}

public sealed class WeekColumn
{
    public DateTime Date { get; init; }
    public bool IsToday { get; init; }
    public bool IsWeekend { get; init; }
    public string DayName => Date.ToString("ddd");
    public string DayNumber => Date.Day.ToString();
    public List<Occurrence> AllDay { get; } = [];
    public List<Occurrence> Timed { get; } = [];
}

public sealed class AgendaGroup
{
    public DateTime Date { get; init; }
    public string Title => Format.FriendlyDay(Date);
    public string Subtitle => Date.ToString("dddd, d MMMM yyyy");
    public bool IsToday => Date == DateTime.Today;
    public List<Occurrence> Items { get; } = [];
}

public sealed partial class CalendarFilter : ObservableObject
{
    public CalendarFilter(CalendarCategory? category, string title, string colorKey, bool visible)
    {
        Category = category;
        _title = title;
        _colorKey = colorKey;
        _isVisible = visible;
    }

    public CalendarCategory? Category { get; }
    [ObservableProperty] private string _title;
    [ObservableProperty] private string _colorKey;
    [ObservableProperty] private bool _isVisible;
}
