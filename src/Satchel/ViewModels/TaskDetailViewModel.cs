using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Behaviors;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;

namespace Satchel.ViewModels;

/// <summary>Side panel editing one task (auto-saves).</summary>
public sealed partial class TaskDetailViewModel : ObservableObject, IDisposable
{
    private static readonly HashSet<string> Saved =
    [
        nameof(TaskItem.Title), nameof(TaskItem.Notes), nameof(TaskItem.State), nameof(TaskItem.Priority), nameof(TaskItem.DueDate),
        nameof(TaskItem.DueHasTime), nameof(TaskItem.Subtasks), nameof(TaskItem.Tags), nameof(TaskItem.ListId), nameof(TaskItem.ReminderAt),
        nameof(TaskItem.Repeat)
    ];

    public static IReadOnlyList<Choice<string>> RepeatChoices => Services.TaskRepeat.Choices;

    private readonly DataStore _store;
    private readonly TasksViewModel _owner;
    private readonly DispatcherTimer _timer;
    private bool _dirty;
    private bool _syncing;

    public TaskDetailViewModel(TaskItem task, TasksViewModel owner, DataStore store)
    {
        Task = task;
        _owner = owner;
        _store = store;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _timer.Tick += async (_, _) => await SaveAsync();
        foreach (var s in TaskItem.ParseSubtasks(task.Subtasks)) Track(s, Subtasks.Count);
        Subtasks.CollectionChanged += OnSubtasksChanged;
        Task.PropertyChanged += OnTaskChanged;
        _reminderMinutes = DeriveReminder();
    }

    public TaskItem Task { get; }
    public TasksViewModel Owner => _owner;
    public ObservableCollection<Subtask> Subtasks { get; } = [];
    public IReadOnlyList<TaskList> ListChoices => _owner.ListChoices;

    public static IReadOnlyList<Choice<TaskState>> States { get; } =
    [
        new(TaskState.Todo, "To do"), new(TaskState.InProgress, "In progress"), new(TaskState.Done, "Done")
    ];

    public static IReadOnlyList<Choice<TaskPriority>> Priorities { get; } =
    [
        new(TaskPriority.None, "None"), new(TaskPriority.Low, "Low"), new(TaskPriority.Medium, "Medium"),
        new(TaskPriority.High, "High"), new(TaskPriority.Urgent, "Urgent")
    ];

    public static IReadOnlyList<Choice<int>> ReminderChoices { get; } =
    [
        new(-1, "No reminder"), new(0, "At due time"), new(10, "10 minutes before"), new(30, "30 minutes before"),
        new(60, "1 hour before"), new(1440, "1 day before")
    ];

    [ObservableProperty] private string _newSubtaskText = "";
    [ObservableProperty] private int _reminderMinutes;

    public TaskList? SelectedList
    {
        get => ListChoices.FirstOrDefault(l => l.Id == (Task.ListId ?? 0));
        set
        {
            int? id = value == null || value.Id == 0 ? null : value.Id;
            if (Task.ListId == id) return;
            Task.ListId = id;
            OnPropertyChanged();
        }
    }

    public DateTime? DueDay
    {
        get => Task.DueDate?.Date;
        set
        {
            if (value == null)
            {
                Task.DueDate = null;
                Task.DueHasTime = false;
            }
            else
            {
                var time = Task.DueHasTime && Task.DueDate.HasValue ? Task.DueDate.Value.TimeOfDay : TimeSpan.Zero;
                Task.DueDate = value.Value.Date + time;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDue));
            ApplyReminder();
        }
    }

    public bool HasDue => Task.DueDate.HasValue;

    public bool HasTime
    {
        get => Task.DueHasTime;
        set
        {
            if (value && !Task.DueDate.HasValue) Task.DueDate = DateTime.Today;
            Task.DueHasTime = value;
            if (value && Task.DueDate is { } d && d.TimeOfDay == TimeSpan.Zero) Task.DueDate = d.Date.AddHours(9);
            if (!value && Task.DueDate is { } d2) Task.DueDate = d2.Date;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DueTime));
            OnPropertyChanged(nameof(DueDay));
            OnPropertyChanged(nameof(HasDue));
            ApplyReminder();
        }
    }

    public TimeSpan DueTime
    {
        get => Task.DueDate?.TimeOfDay ?? TimeSpan.FromHours(9);
        set
        {
            if (!Task.DueDate.HasValue) return;
            Task.DueDate = Task.DueDate.Value.Date + value;
            OnPropertyChanged();
            ApplyReminder();
        }
    }

    public string MetaText
    {
        get
        {
            var text = $"Created {Format.Relative(Task.CreatedAt)}";
            if (Task.CompletedAt.HasValue) text += $" · completed {Format.Relative(Task.CompletedAt.Value)}";
            return text;
        }
    }

    public int SubtasksDone => Subtasks.Count(s => s.IsDone);
    public string SubtaskProgress => Subtasks.Count == 0 ? "" : $"{SubtasksDone}/{Subtasks.Count}";

    partial void OnReminderMinutesChanged(int value) => ApplyReminder();

    private int DeriveReminder()
    {
        if (Task.ReminderAt is not { } at || Task.DueDate is not { } due) return -1;
        var minutes = (int)Math.Round((due - at).TotalMinutes);
        return ReminderChoices.Any(c => c.Value == minutes) ? minutes : 0;
    }

    private void ApplyReminder()
    {
        if (ReminderMinutes < 0 || Task.DueDate is not { } due)
        {
            Task.ReminderAt = null;
            return;
        }
        var baseTime = Task.DueHasTime ? due : due.Date.AddHours(9);
        Task.ReminderAt = baseTime.AddMinutes(-ReminderMinutes);
    }

    private void OnTaskChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TaskItem.CompletedAt) or nameof(TaskItem.State)) OnPropertyChanged(nameof(MetaText));
        if (_syncing || e.PropertyName == null || !Saved.Contains(e.PropertyName)) return;
        _dirty = true;
        _timer.Stop();
        _timer.Start();
        if (e.PropertyName is nameof(TaskItem.State) or nameof(TaskItem.ListId) or nameof(TaskItem.DueDate) or nameof(TaskItem.Priority))
            _owner.ScheduleRefresh();
        if (e.PropertyName == nameof(TaskItem.State) && Task.IsDone && Task.IsRepeating) _ = CompleteRepeatingAsync();
        if (e.PropertyName == nameof(TaskItem.Repeat) && Task.IsRepeating && Task.DueDate == null) Task.DueDate = DateTime.Today;
    }

    private async Task CompleteRepeatingAsync()
    {
        await SaveAsync();
        await _owner.HandleCompletedAsync(Task);
    }

    public async Task SaveAsync()
    {
        _timer.Stop();
        if (!_dirty) return;
        _dirty = false;
        Task.UpdatedAt = DateTime.Now;
        await _store.UpdateAsync(Task);
        _owner.RefreshCounts();
    }

    private void Track(Subtask s, int index)
    {
        s.PropertyChanged += OnSubtaskChanged;
        Subtasks.Insert(index, s);
    }

    private void OnSubtasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
            foreach (Subtask s in e.OldItems) s.PropertyChanged -= OnSubtaskChanged;
        SyncSubtasks();
    }

    private void OnSubtaskChanged(object? sender, PropertyChangedEventArgs e) => SyncSubtasks();

    private void SyncSubtasks()
    {
        _syncing = true;
        Task.Subtasks = Subtasks.Count == 0 ? "" : TaskItem.SerializeSubtasks(Subtasks);
        _syncing = false;
        _dirty = true;
        _timer.Stop();
        _timer.Start();
        OnPropertyChanged(nameof(SubtasksDone));
        OnPropertyChanged(nameof(SubtaskProgress));
    }

    [RelayCommand]
    private void AddSubtask()
    {
        var text = NewSubtaskText.Trim();
        if (text.Length == 0) return;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            Track(new Subtask { Text = line.Trim() }, Subtasks.Count);
        NewSubtaskText = "";
    }

    [RelayCommand]
    private void RemoveSubtask(Subtask? s)
    {
        if (s != null) Subtasks.Remove(s);
    }

    [RelayCommand]
    private void ReorderSubtasks(ReorderEventArgs? args) => SyncSubtasks();

    [RelayCommand]
    private void SetState(TaskState state) => Task.State = state;

    [RelayCommand]
    private void SetPriority(TaskPriority priority) => Task.Priority = priority;

    public void Dispose()
    {
        _timer.Stop();
        Task.PropertyChanged -= OnTaskChanged;
        Subtasks.CollectionChanged -= OnSubtasksChanged;
        foreach (var s in Subtasks) s.PropertyChanged -= OnSubtaskChanged;
    }
}
