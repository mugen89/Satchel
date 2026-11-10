using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Satchel.Models;

public enum RecurrenceFrequency
{
    None = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3,
    Yearly = 4
}

public partial class CalendarCategory : Entity
{
    [ObservableProperty][property: Encrypted] private string _name = "";
    [ObservableProperty] private string _color = "teal";
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private double _sortOrder;
}

public partial class CalendarEvent : Entity
{
    [ObservableProperty] private int? _categoryId;
    [ObservableProperty][property: Encrypted] private string _title = "";
    [ObservableProperty][property: Encrypted] private string _description = "";
    [ObservableProperty][property: Encrypted] private string _location = "";
    [ObservableProperty] private DateTime _start = DateTime.Today.AddHours(9);
    [ObservableProperty] private DateTime _end = DateTime.Today.AddHours(10);
    [ObservableProperty] private bool _isAllDay;
    /// <summary>Optional color override (palette key). Null = use the calendar's color.</summary>
    [ObservableProperty] private string? _color;

    [ObservableProperty] private RecurrenceFrequency _recurrence;
    [ObservableProperty] private int _recurrenceInterval = 1;
    /// <summary>Weekly recurrence days as a bit mask (bit 0 = Sunday … bit 6 = Saturday).</summary>
    [ObservableProperty] private int _recurrenceDays;
    [ObservableProperty] private DateTime? _recurrenceUntil;
    [ObservableProperty] private int? _recurrenceCount;
    /// <summary>Skipped occurrence dates, "yyyy-MM-dd;yyyy-MM-dd".</summary>
    [ObservableProperty] private string _recurrenceExceptions = "";
    [ObservableProperty] private int? _reminderMinutes;

    [NotMapped] public bool IsRecurring => Recurrence != RecurrenceFrequency.None;
    [NotMapped] public TimeSpan Duration => End - Start;

    public HashSet<DateTime> GetExceptions()
    {
        var set = new HashSet<DateTime>();
        foreach (var part in RecurrenceExceptions.Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (DateTime.TryParseExact(part, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                set.Add(d.Date);
        return set;
    }

    public void AddException(DateTime date)
    {
        var set = GetExceptions();
        set.Add(date.Date);
        RecurrenceExceptions = string.Join(';', set.OrderBy(d => d).Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    }

    public CalendarEvent CloneDetached() => new()
    {
        CategoryId = CategoryId,
        Title = Title,
        Description = Description,
        Location = Location,
        Start = Start,
        End = End,
        IsAllDay = IsAllDay,
        Color = Color,
        ReminderMinutes = ReminderMinutes
    };
}
