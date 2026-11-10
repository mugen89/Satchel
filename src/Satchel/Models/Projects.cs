using System.ComponentModel.DataAnnotations.Schema;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Satchel.Models;

public enum ProjectStatus { Idea = 0, Planning = 1, Active = 2, OnHold = 3, Done = 4 }

/// <summary>
/// A project ties together a task list, a notebook and a board (all optional, created on demand),
/// plus milestones, resources and a running log of updates.
/// </summary>
public partial class Project : Entity
{
    [ObservableProperty][property: Encrypted] private string _name = "";
    [ObservableProperty][property: Encrypted] private string _description = "";
    [ObservableProperty][property: Encrypted] private string _tags = "";
    [ObservableProperty][property: Encrypted] private byte[]? _cover;
    [ObservableProperty] private ProjectStatus _status = ProjectStatus.Planning;
    [ObservableProperty] private string _color = "teal";
    [ObservableProperty] private string _icon = "";
    [ObservableProperty] private DateTime? _startDate;
    [ObservableProperty] private DateTime? _dueDate;
    [ObservableProperty] private DateTime? _completedAt;
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private double _sortOrder;
    [ObservableProperty] private int? _taskListId;
    [ObservableProperty] private int? _notebookId;
    [ObservableProperty] private int? _boardId;

    // ----- computed by the view model -----
    [ObservableProperty][property: NotMapped] private int _taskTotal;
    [ObservableProperty][property: NotMapped] private int _taskDone;
    [ObservableProperty][property: NotMapped] private int _milestoneTotal;
    [ObservableProperty][property: NotMapped] private int _milestoneDone;
    [ObservableProperty][property: NotMapped] private int _noteCount;
    [ObservableProperty][property: NotMapped] private string _nextMilestone = "";

    [NotMapped] public double Progress
    {
        get
        {
            int total = TaskTotal + MilestoneTotal;
            if (Status == ProjectStatus.Done) return 1;
            return total == 0 ? 0 : (double)(TaskDone + MilestoneDone) / total;
        }
    }

    [NotMapped] public bool IsOverdue => DueDate.HasValue && DueDate.Value.Date < DateTime.Today && Status != ProjectStatus.Done;

    [NotMapped] public string DueText
    {
        get
        {
            if (!DueDate.HasValue) return "";
            var days = (DueDate.Value.Date - DateTime.Today).Days;
            if (Status == ProjectStatus.Done) return $"Due {DueDate.Value:d MMM}";
            return days switch
            {
                < -1 => $"{-days} days overdue",
                -1 => "1 day overdue",
                0 => "Due today",
                1 => "Due tomorrow",
                < 14 => $"Due in {days} days",
                _ => $"Due {DueDate.Value:d MMM yyyy}"
            };
        }
    }

    partial void OnTaskTotalChanged(int value) => OnPropertyChanged(nameof(Progress));
    partial void OnTaskDoneChanged(int value) => OnPropertyChanged(nameof(Progress));
    partial void OnMilestoneTotalChanged(int value) => OnPropertyChanged(nameof(Progress));
    partial void OnMilestoneDoneChanged(int value) => OnPropertyChanged(nameof(Progress));
    partial void OnStatusChanged(ProjectStatus value)
    {
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(IsOverdue));
        OnPropertyChanged(nameof(DueText));
    }
    partial void OnDueDateChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(IsOverdue));
        OnPropertyChanged(nameof(DueText));
    }
}

public partial class Milestone : Entity
{
    [ObservableProperty] private int _projectId;
    [ObservableProperty][property: Encrypted] private string _title = "";
    [ObservableProperty] private DateTime? _dueDate;
    [ObservableProperty] private bool _isDone;
    [ObservableProperty] private DateTime? _doneAt;
    [ObservableProperty] private double _sortOrder;

    [NotMapped] public bool IsOverdue => !IsDone && DueDate.HasValue && DueDate.Value.Date < DateTime.Today;
    partial void OnIsDoneChanged(bool value) => OnPropertyChanged(nameof(IsOverdue));
    partial void OnDueDateChanged(DateTime? value) => OnPropertyChanged(nameof(IsOverdue));
}

public partial class ProjectResource : Entity
{
    [ObservableProperty] private int _projectId;
    [ObservableProperty][property: Encrypted] private string _title = "";
    [ObservableProperty][property: Encrypted] private string _url = "";
    [ObservableProperty][property: Encrypted] private string _note = "";
    [ObservableProperty] private double _sortOrder;
}

public enum UpdateKind { Note = 0, Win = 1, Blocker = 2, Decision = 3 }

/// <summary>An entry in a project's log: progress notes, wins, blockers and decisions.</summary>
public partial class ProjectUpdate : Entity
{
    [ObservableProperty] private int _projectId;
    [ObservableProperty][property: Encrypted] private string _text = "";
    [ObservableProperty] private UpdateKind _kind;
    [ObservableProperty] private DateTime _date = DateTime.Now;
}
