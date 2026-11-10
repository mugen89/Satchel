using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Satchel.Core;

namespace Satchel.Models;

public enum TaskState
{
    Todo = 0,
    InProgress = 1,
    Done = 2
}

public enum TaskPriority
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Urgent = 4
}

public partial class TaskList : Entity
{
    [ObservableProperty][property: Encrypted] private string _name = "";
    [ObservableProperty] private string _color = "teal";
    [ObservableProperty] private string _icon = Glyphs.List;
    [ObservableProperty] private double _sortOrder;

    [ObservableProperty][property: NotMapped] private int _openCount;
}

public partial class Subtask : ObservableObject
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isDone;
}

public partial class TaskItem : Entity
{
    [ObservableProperty] private int? _listId;
    [ObservableProperty][property: Encrypted] private string _title = "";
    [ObservableProperty][property: Encrypted] private string _notes = "";
    [ObservableProperty] private TaskState _state;
    [ObservableProperty] private TaskPriority _priority;
    [ObservableProperty] private DateTime? _dueDate;
    [ObservableProperty] private bool _dueHasTime;
    [ObservableProperty] private DateTime? _completedAt;
    [ObservableProperty][property: Encrypted] private string _subtasks = "";
    [ObservableProperty][property: Encrypted] private string _tags = "";
    [ObservableProperty] private double _sortOrder;
    [ObservableProperty] private DateTime? _reminderAt;
    /// <summary>Repeat rule ("" = once): daily, weekdays, weekly, biweekly, monthly, yearly, or "days:N" / "weeks:N" / "months:N".</summary>
    [ObservableProperty] private string _repeat = "";

    private List<Subtask>? _parsedSubtasks;

    /// <summary>Display helpers filled in by the Tasks page (list name and color for smart views).</summary>
    [ObservableProperty][property: NotMapped] private string _listName = "";
    [ObservableProperty][property: NotMapped] private string _listColor = "slate";

    [NotMapped] public double SubtaskProgress => SubtaskItems.Count == 0 ? 0 : (double)SubtaskItems.Count(s => s.IsDone) / SubtaskItems.Count;

    [NotMapped]
    public bool IsDone
    {
        get => State == TaskState.Done;
        set
        {
            if (value == IsDone) return;
            State = value ? TaskState.Done : TaskState.Todo;
        }
    }

    [NotMapped] public bool IsOverdue => !IsDone && DueDate.HasValue && (DueHasTime ? DueDate.Value < DateTime.Now : DueDate.Value.Date < DateTime.Today);
    [NotMapped] public bool IsDueToday => DueDate.HasValue && DueDate.Value.Date == DateTime.Today;
    [NotMapped] public bool HasDue => DueDate.HasValue;

    [NotMapped] public string DueText
    {
        get
        {
            if (!DueDate.HasValue) return "";
            var day = Format.FriendlyDay(DueDate.Value);
            return DueHasTime ? $"{day} {Format.Time(DueDate.Value)}" : day;
        }
    }

    [NotMapped] public IReadOnlyList<Subtask> SubtaskItems => _parsedSubtasks ??= ParseSubtasks(Subtasks);
    [NotMapped] public bool HasSubtasks => SubtaskItems.Count > 0;
    [NotMapped] public string SubtaskText => $"{SubtaskItems.Count(s => s.IsDone)}/{SubtaskItems.Count}";
    [NotMapped] public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);
    [NotMapped] public IReadOnlyList<string> TagList => Note.SplitTags(Tags);
    [NotMapped] public bool HasTags => TagList.Count > 0;

    [NotMapped] public string PriorityName => Priority switch
    {
        TaskPriority.Low => "Low",
        TaskPriority.Medium => "Medium",
        TaskPriority.High => "High",
        TaskPriority.Urgent => "Urgent",
        _ => "None"
    };

    [NotMapped] public bool HasPriority => Priority != TaskPriority.None;
    [NotMapped] public bool IsRepeating => !string.IsNullOrEmpty(Repeat);
    [NotMapped] public string RepeatText => Services.TaskRepeat.Describe(Repeat);

    partial void OnStateChanged(TaskState value)
    {
        CompletedAt = value == TaskState.Done ? (CompletedAt ?? DateTime.Now) : null;
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsOverdue));
    }

    partial void OnDueDateChanged(DateTime? value) => RaiseDue();
    partial void OnDueHasTimeChanged(bool value) => RaiseDue();

    private void RaiseDue()
    {
        OnPropertyChanged(nameof(IsOverdue));
        OnPropertyChanged(nameof(IsDueToday));
        OnPropertyChanged(nameof(HasDue));
        OnPropertyChanged(nameof(DueText));
    }

    partial void OnSubtasksChanged(string value)
    {
        _parsedSubtasks = null;
        OnPropertyChanged(nameof(SubtaskItems));
        OnPropertyChanged(nameof(HasSubtasks));
        OnPropertyChanged(nameof(SubtaskText));
        OnPropertyChanged(nameof(SubtaskProgress));
    }

    partial void OnNotesChanged(string value) => OnPropertyChanged(nameof(HasNotes));

    partial void OnRepeatChanged(string value)
    {
        OnPropertyChanged(nameof(IsRepeating));
        OnPropertyChanged(nameof(RepeatText));
    }

    partial void OnTagsChanged(string value)
    {
        OnPropertyChanged(nameof(TagList));
        OnPropertyChanged(nameof(HasTags));
    }

    partial void OnPriorityChanged(TaskPriority value)
    {
        OnPropertyChanged(nameof(PriorityName));
        OnPropertyChanged(nameof(HasPriority));
    }

    public void RefreshComputed()
    {
        RaiseDue();
        OnSubtasksChanged(Subtasks);
    }

    public static List<Subtask> ParseSubtasks(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<Subtask>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string SerializeSubtasks(IEnumerable<Subtask> items) => JsonSerializer.Serialize(items);
}
