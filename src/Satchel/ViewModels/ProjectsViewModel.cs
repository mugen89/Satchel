using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Behaviors;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Services;

namespace Satchel.ViewModels;

/// <summary>
/// Projects: goals with milestones, a task board, notes, a corkboard, resources and a running log.
/// Each project owns (optionally) a task list, a notebook and a board, so its pieces also show up in those pages.
/// </summary>
public sealed partial class ProjectsViewModel : PageViewModel, IDisposable
{
    private readonly INavigator _nav;
    private readonly DataStore _store;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly SettingsService _settings;
    private readonly WebService _web;
    private readonly DispatcherTimer _saveTimer;
    private readonly HashSet<Entity> _dirty = [];
    private bool _loaded;

    public ProjectsViewModel(INavigator nav, DataStore store, DialogService dialogs, ToastService toasts, SettingsService settings, WebService web)
    {
        _nav = nav;
        _store = store;
        _dialogs = dialogs;
        _toasts = toasts;
        _settings = settings;
        _web = web;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += async (_, _) => await FlushAsync();
    }

    public override string Title => "Projects";

    public ObservableCollection<Project> All { get; } = [];
    public ObservableCollection<Project> Visible { get; } = [];
    public ObservableCollection<Milestone> Milestones { get; } = [];
    public ObservableCollection<ProjectResource> Resources { get; } = [];
    public ObservableCollection<ProjectUpdate> Updates { get; } = [];
    public ObservableCollection<TaskItem> TodoTasks { get; } = [];
    public ObservableCollection<TaskItem> DoingTasks { get; } = [];
    public ObservableCollection<TaskItem> DoneTasks { get; } = [];
    public ObservableCollection<Note> Notes { get; } = [];

    [ObservableProperty] private string _filter = "Active";
    [ObservableProperty] private Project? _selected;
    [ObservableProperty] private string _tab = "Overview";
    [ObservableProperty] private string _newMilestone = "";
    [ObservableProperty] private string _newTask = "";
    [ObservableProperty] private string _newResource = "";
    [ObservableProperty] private string _newUpdate = "";
    [ObservableProperty] private UpdateKind _newUpdateKind;

    public bool IsDetail => Selected != null;
    public bool IsEmpty => All.Count == 0;
    public int ActiveCount => All.Count(p => p.Status is ProjectStatus.Active or ProjectStatus.Planning or ProjectStatus.OnHold);

    public static IReadOnlyList<Choice<ProjectStatus>> Statuses { get; } =
    [
        new(ProjectStatus.Idea, "Idea", Glyphs.Lightbulb), new(ProjectStatus.Planning, "Planning", Glyphs.Edit),
        new(ProjectStatus.Active, "Active", Glyphs.Lightning), new(ProjectStatus.OnHold, "On hold", Glyphs.Pause), new(ProjectStatus.Done, "Done", Glyphs.Completed)
    ];

    public static IReadOnlyList<Choice<UpdateKind>> UpdateKinds { get; } =
    [
        new(UpdateKind.Note, "Update", Glyphs.Edit), new(UpdateKind.Win, "Win", Glyphs.StarFilled),
        new(UpdateKind.Blocker, "Blocker", Glyphs.Warning), new(UpdateKind.Decision, "Decision", Glyphs.Flag)
    ];

    public static IReadOnlyList<string> Icons { get; } =
    [
        Glyphs.Lightbulb, Glyphs.Lightning, Glyphs.Home, Glyphs.Work, Glyphs.Code, Glyphs.Game, Glyphs.Music, Glyphs.Camera, Glyphs.Airplane,
        Glyphs.Car, Glyphs.Health, Glyphs.Education, Glyphs.Book, Glyphs.Shop, Glyphs.Heart, Glyphs.Puzzle, Glyphs.Flag, Glyphs.Palette, Glyphs.Globe, Glyphs.Calculator
    ];

    partial void OnFilterChanged(string value) => ApplyFilter();

    partial void OnSelectedChanged(Project? oldValue, Project? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= OnProjectChanged;
        if (newValue != null) newValue.PropertyChanged += OnProjectChanged;
        OnPropertyChanged(nameof(IsDetail));
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_loaded) await LoadAsync();
        else await RefreshStatsAsync();
        if (parameter is int id && All.FirstOrDefault(p => p.Id == id) is { } project) await OpenProject(project);
        else if (parameter is "new") await NewProject();
        else if (Selected != null) await LoadDetailAsync(Selected);
    }

    public override async Task OnNavigatedFromAsync() => await FlushAsync();

    private async Task LoadAsync()
    {
        var projects = await _store.ListAsync<Project>(q => q.OrderByDescending(p => p.IsPinned).ThenBy(p => p.SortOrder));
        All.Clear();
        foreach (var p in projects) All.Add(p);
        _loaded = true;
        await RefreshStatsAsync();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task RefreshStatsAsync()
    {
        var lists = All.Where(p => p.TaskListId.HasValue).Select(p => p.TaskListId!.Value).ToList();
        var books = All.Where(p => p.NotebookId.HasValue).Select(p => p.NotebookId!.Value).ToList();
        var (tasks, milestones, notes) = await _store.RunAsync(db => (
            db.Tasks.Where(t => t.ListId != null && lists.Contains(t.ListId.Value)).Select(t => new { t.ListId, t.State }).ToList(),
            db.Milestones.Select(m => new { m.ProjectId, m.IsDone, m.DueDate, m.Title, m.SortOrder }).ToList(),
            db.Notes.Where(n => n.NotebookId != null && books.Contains(n.NotebookId.Value) && !n.IsTrashed).Select(n => n.NotebookId).ToList()));
        foreach (var p in All)
        {
            var t = tasks.Where(x => x.ListId == p.TaskListId).ToList();
            p.TaskTotal = t.Count;
            p.TaskDone = t.Count(x => x.State == TaskState.Done);
            var m = milestones.Where(x => x.ProjectId == p.Id).ToList();
            p.MilestoneTotal = m.Count;
            p.MilestoneDone = m.Count(x => x.IsDone);
            p.NextMilestone = m.Where(x => !x.IsDone).OrderBy(x => x.DueDate ?? DateTime.MaxValue).ThenBy(x => x.SortOrder).Select(x => x.Title).FirstOrDefault() ?? "";
            p.NoteCount = notes.Count(n => n == p.NotebookId);
        }
        ApplyFilter();
        OnPropertyChanged(nameof(ActiveCount));
    }

    private void ApplyFilter()
    {
        IEnumerable<Project> items = Filter switch
        {
            "Ideas" => All.Where(p => p.Status == ProjectStatus.Idea),
            "Done" => All.Where(p => p.Status == ProjectStatus.Done),
            "All" => All,
            _ => All.Where(p => p.Status is ProjectStatus.Active or ProjectStatus.Planning or ProjectStatus.OnHold)
        };
        var list = items.OrderByDescending(p => p.IsPinned).ThenBy(p => p.Status == ProjectStatus.OnHold).ThenBy(p => p.SortOrder).ToList();
        Visible.Clear();
        foreach (var p in list) Visible.Add(p);
    }

    // ----- project list -----

    [RelayCommand]
    private async Task NewProject()
    {
        var name = await _dialogs.PromptAsync("New project", "Give it a name — you can add a goal, milestones, tasks, notes and a board next.", "", "e.g. Renovate the balcony", "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        var color = Palettes.Tags[Random.Shared.Next(Math.Min(8, Palettes.Tags.Count))].Key;
        var list = new TaskList { Name = name, Color = color, Icon = Glyphs.Flag, SortOrder = 1000 };
        var notebook = new Notebook { Name = name, Color = color, SortOrder = 1000 };
        var board = new Board { Name = name, SortOrder = 1000 };
        await _store.RunAsync(db =>
        {
            db.AddRange(list, notebook, board);
            db.SaveChanges();
        });
        var project = new Project
        {
            Name = name,
            Color = color,
            Icon = Glyphs.Lightbulb,
            Status = ProjectStatus.Planning,
            StartDate = DateTime.Today,
            TaskListId = list.Id,
            NotebookId = notebook.Id,
            BoardId = board.Id,
            SortOrder = All.Count == 0 ? 0 : All.Max(p => p.SortOrder) + 1
        };
        await _store.AddAsync(project);
        All.Add(project);
        OnPropertyChanged(nameof(IsEmpty));
        if (Filter is "Ideas" or "Done") Filter = "Active";
        ApplyFilter();
        await OpenProject(project);
        Tab = "Overview";
    }

    [RelayCommand]
    private async Task OpenProject(Project? project)
    {
        if (project == null) return;
        await FlushAsync();
        Selected = project;
        await LoadDetailAsync(project);
    }

    [RelayCommand]
    private async Task Back()
    {
        await FlushAsync();
        Selected = null;
        await RefreshStatsAsync();
    }

    private async Task LoadDetailAsync(Project p)
    {
        int id = p.Id;
        int? listId = p.TaskListId, bookId = p.NotebookId;
        var (milestones, resources, updates, tasks, notes) = await _store.RunAsync(db => (
            db.Milestones.Where(m => m.ProjectId == id).OrderBy(m => m.SortOrder).ToList(),
            db.ProjectResources.Where(r => r.ProjectId == id).OrderBy(r => r.SortOrder).ToList(),
            db.ProjectUpdates.Where(u => u.ProjectId == id).OrderByDescending(u => u.Date).ToList(),
            listId == null ? new List<TaskItem>() : db.Tasks.Where(t => t.ListId == listId).OrderBy(t => t.SortOrder).ToList(),
            bookId == null ? new List<Note>() : db.Notes.Where(n => n.NotebookId == bookId && !n.IsTrashed).OrderByDescending(n => n.UpdatedAt).ToList()));
        if (!ReferenceEquals(Selected, p)) return;

        foreach (var m in Milestones) m.PropertyChanged -= OnChildChanged;
        foreach (var t in AllTasks()) t.PropertyChanged -= OnTaskChanged;
        Reset(Milestones, milestones);
        Reset(Resources, resources);
        Reset(Updates, updates);
        Reset(TodoTasks, tasks.Where(t => t.State == TaskState.Todo));
        Reset(DoingTasks, tasks.Where(t => t.State == TaskState.InProgress));
        Reset(DoneTasks, tasks.Where(t => t.State == TaskState.Done).OrderByDescending(t => t.CompletedAt));
        Reset(Notes, notes);
        foreach (var m in Milestones) m.PropertyChanged += OnChildChanged;
        foreach (var t in AllTasks()) t.PropertyChanged += OnTaskChanged;
        UpdateSelectedStats();
    }

    private static void Reset<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    private IEnumerable<TaskItem> AllTasks() => TodoTasks.Concat(DoingTasks).Concat(DoneTasks);

    private void UpdateSelectedStats()
    {
        if (Selected is not { } p) return;
        var tasks = AllTasks().ToList();
        p.TaskTotal = tasks.Count;
        p.TaskDone = DoneTasks.Count;
        p.MilestoneTotal = Milestones.Count;
        p.MilestoneDone = Milestones.Count(m => m.IsDone);
        p.NextMilestone = Milestones.Where(m => !m.IsDone).OrderBy(m => m.DueDate ?? DateTime.MaxValue).Select(m => m.Title).FirstOrDefault() ?? "";
        p.NoteCount = Notes.Count;
    }

    // ----- saving -----

    private void OnProjectChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not Project p) return;
        if (e.PropertyName is nameof(Project.Name) or nameof(Project.Description) or nameof(Project.Status) or nameof(Project.Color) or nameof(Project.Icon)
            or nameof(Project.StartDate) or nameof(Project.DueDate) or nameof(Project.Cover) or nameof(Project.IsPinned) or nameof(Project.Tags))
        {
            if (e.PropertyName == nameof(Project.Status)) p.CompletedAt = p.Status == ProjectStatus.Done ? DateTime.Now : null;
            MarkDirty(p);
        }
    }

    private void OnChildChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not Entity entity || e.PropertyName is nameof(Entity.UpdatedAt) or nameof(Milestone.IsOverdue)) return;
        if (sender is Milestone m && e.PropertyName == nameof(Milestone.IsDone))
        {
            m.DoneAt = m.IsDone ? DateTime.Now : null;
            UpdateSelectedStats();
            if (m.IsDone) _toasts.Success($"Milestone reached: {m.Title}");
        }
        MarkDirty(entity);
    }

    private void OnTaskChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is TaskItem t && e.PropertyName == nameof(TaskItem.Title)) MarkDirty(t);
    }

    private void MarkDirty(Entity entity)
    {
        _dirty.Add(entity);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public override async Task FlushAsync()
    {
        _saveTimer.Stop();
        if (_dirty.Count == 0) return;
        var batch = _dirty.ToList();
        _dirty.Clear();
        foreach (var e in batch) e.UpdatedAt = DateTime.Now;
        await _store.RunAsync(db =>
        {
            db.UpdateRange(batch);
            db.SaveChanges();
        });
    }

    public override void OnNewShortcut()
    {
        if (Selected == null) _ = NewProject();
    }

    // ----- project actions -----

    [RelayCommand]
    private void SetStatus(ProjectStatus status)
    {
        if (Selected != null) Selected.Status = status;
    }

    [RelayCommand]
    private void SetIcon(string? glyph)
    {
        if (Selected != null && glyph != null) Selected.Icon = glyph;
    }

    [RelayCommand]
    private void TogglePin()
    {
        if (Selected != null) Selected.IsPinned = !Selected.IsPinned;
    }

    [RelayCommand]
    private void PickCover()
    {
        if (Selected == null) return;
        var img = ImageService.PickImage(1200, photo: true);
        if (img != null) Selected.Cover = img;
    }

    [RelayCommand]
    private void PasteCover()
    {
        if (Selected == null) return;
        var img = ImageService.FromClipboard(1200, photo: true);
        if (img != null) Selected.Cover = img;
        else _toasts.Info("Copy an image first");
    }

    [RelayCommand]
    private void RemoveCover()
    {
        if (Selected != null) Selected.Cover = null;
    }

    [RelayCommand]
    private async Task DeleteProject()
    {
        if (Selected is not { } p) return;
        if (!await _dialogs.ConfirmAsync("Delete project?",
                $"“{p.Name}” with its milestones, links and log will be deleted. Its task list, notebook and board are kept — you can remove them separately.",
                "Delete", danger: true)) return;
        _dirty.Remove(p);
        await _store.DeleteAsync(p);
        All.Remove(p);
        Selected = null;
        OnPropertyChanged(nameof(IsEmpty));
        await RefreshStatsAsync();
        _toasts.Info("Project deleted");
    }

    [RelayCommand]
    private async Task OpenBoard()
    {
        if (Selected is not { } p) return;
        if (p.BoardId == null)
        {
            var board = new Board { Name = p.Name, SortOrder = 1000 };
            await _store.AddAsync(board);
            p.BoardId = board.Id;
            MarkDirty(p);
            await FlushAsync();
        }
        if ((_nav as ShellViewModel)?.GetPage<BoardViewModel>("Board") is { } boards) await boards.ReloadBoardsAsync(p.BoardId);
        _nav.NavigateTo("Board", p.BoardId);
    }

    // ----- milestones -----

    [RelayCommand]
    private async Task AddMilestone()
    {
        if (Selected is not { } p || string.IsNullOrWhiteSpace(NewMilestone)) return;
        var m = new Milestone { ProjectId = p.Id, Title = NewMilestone.Trim(), SortOrder = Milestones.Count == 0 ? 0 : Milestones.Max(x => x.SortOrder) + 1 };
        await _store.AddAsync(m);
        m.PropertyChanged += OnChildChanged;
        Milestones.Add(m);
        NewMilestone = "";
        UpdateSelectedStats();
    }

    [RelayCommand]
    private async Task DeleteMilestone(Milestone? m)
    {
        if (m == null) return;
        m.PropertyChanged -= OnChildChanged;
        _dirty.Remove(m);
        Milestones.Remove(m);
        await _store.DeleteAsync(m);
        UpdateSelectedStats();
    }

    [RelayCommand]
    private async Task ReorderMilestones(ReorderEventArgs? args)
    {
        int i = 0;
        foreach (var m in Milestones) m.SortOrder = i++;
        await _store.SaveOrderAsync(Milestones, m => m.SortOrder);
    }

    // ----- resources -----

    [RelayCommand]
    private async Task AddResource()
    {
        if (Selected is not { } p || string.IsNullOrWhiteSpace(NewResource)) return;
        var text = NewResource.Trim();
        bool isUrl = BoardViewModel.LooksLikeUrl(text);
        var url = isUrl ? (text.Contains("://") ? text : "https://" + text) : "";
        var r = new ProjectResource
        {
            ProjectId = p.Id,
            Url = url,
            Title = isUrl ? (Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : url) : text,
            SortOrder = Resources.Count
        };
        await _store.AddAsync(r);
        Resources.Add(r);
        NewResource = "";
        if (isUrl && _web.IsAvailable && await _web.GetPreviewAsync(url) is { Title.Length: > 0 } preview)
        {
            r.Title = preview.Title;
            await _store.UpdateAsync(r);
        }
    }

    [RelayCommand]
    private async Task DeleteResource(ProjectResource? r)
    {
        if (r == null) return;
        Resources.Remove(r);
        await _store.DeleteAsync(r);
    }

    [RelayCommand]
    private void OpenResource(ProjectResource? r)
    {
        if (r != null && !string.IsNullOrWhiteSpace(r.Url)) UiCommands.OpenUrl.Execute(r.Url);
    }

    // ----- log -----

    [RelayCommand]
    private async Task AddUpdate()
    {
        if (Selected is not { } p || string.IsNullOrWhiteSpace(NewUpdate)) return;
        var u = new ProjectUpdate { ProjectId = p.Id, Text = NewUpdate.Trim(), Kind = NewUpdateKind, Date = DateTime.Now };
        await _store.AddAsync(u);
        Updates.Insert(0, u);
        NewUpdate = "";
        NewUpdateKind = UpdateKind.Note;
    }

    [RelayCommand]
    private void SetUpdateKind(UpdateKind kind) => NewUpdateKind = kind;

    [RelayCommand]
    private async Task DeleteUpdate(ProjectUpdate? u)
    {
        if (u == null) return;
        Updates.Remove(u);
        await _store.DeleteAsync(u);
    }

    // ----- tasks -----

    private async Task<int> EnsureListAsync(Project p)
    {
        if (p.TaskListId is { } id) return id;
        var list = new TaskList { Name = p.Name, Color = p.Color, Icon = Glyphs.Flag, SortOrder = 1000 };
        await _store.AddAsync(list);
        p.TaskListId = list.Id;
        MarkDirty(p);
        return list.Id;
    }

    [RelayCommand]
    private async Task AddTask()
    {
        if (Selected is not { } p || string.IsNullOrWhiteSpace(NewTask)) return;
        var parsed = TaskParser.Parse(NewTask.Trim(), []);
        var task = new TaskItem
        {
            ListId = await EnsureListAsync(p),
            Title = parsed.Title,
            DueDate = parsed.Due,
            DueHasTime = parsed.HasTime,
            Priority = parsed.Priority,
            SortOrder = TodoTasks.Count == 0 ? 0 : TodoTasks.Min(t => t.SortOrder) - 1
        };
        await _store.AddAsync(task);
        task.PropertyChanged += OnTaskChanged;
        TodoTasks.Insert(0, task);
        NewTask = "";
        UpdateSelectedStats();
    }

    [RelayCommand]
    private async Task ToggleTask(TaskItem? task)
    {
        if (task == null) return;
        bool done = task.State != TaskState.Done;
        TodoTasks.Remove(task);
        DoingTasks.Remove(task);
        DoneTasks.Remove(task);
        task.State = done ? TaskState.Done : TaskState.Todo;
        task.CompletedAt = done ? DateTime.Now : null;
        if (done) DoneTasks.Insert(0, task); else TodoTasks.Insert(0, task);
        await _store.UpdateAsync(task);
        UpdateSelectedStats();
    }

    [RelayCommand]
    private async Task ReorderTasks(ReorderEventArgs? args)
    {
        if (args == null) return;
        if (args.Item is TaskItem moved && args.ChangedList && args.TargetTag is string tag && Enum.TryParse<TaskState>(tag, out var state))
        {
            moved.State = state;
            moved.CompletedAt = state == TaskState.Done ? DateTime.Now : null;
            await _store.UpdateAsync(moved);
        }
        var changed = new List<TaskItem>();
        foreach (var column in new[] { TodoTasks, DoingTasks, DoneTasks })
        {
            int i = 0;
            foreach (var t in column) { t.SortOrder = i++; changed.Add(t); }
        }
        await _store.SaveOrderAsync(changed, t => t.SortOrder);
        UpdateSelectedStats();
    }

    [RelayCommand]
    private void OpenTask(TaskItem? task)
    {
        if (task != null) _nav.NavigateTo("Tasks", task.Id);
    }

    // ----- notes -----

    [RelayCommand]
    private async Task NewNote()
    {
        if (Selected is not { } p) return;
        if (p.NotebookId == null)
        {
            var nb = new Notebook { Name = p.Name, Color = p.Color, SortOrder = 1000 };
            await _store.AddAsync(nb);
            p.NotebookId = nb.Id;
            MarkDirty(p);
        }
        await FlushAsync();
        var note = new Note { NotebookId = p.NotebookId, Type = NoteType.Text, Color = "default" };
        await _store.AddAsync(note);
        _nav.NavigateTo("Notes", note.Id);
    }

    [RelayCommand]
    private void OpenNote(Note? note)
    {
        if (note != null) _nav.NavigateTo("Notes", note.Id);
    }

    [RelayCommand]
    private void SetTab(string? tab) => Tab = tab ?? "Overview";

    public void Dispose()
    {
        _saveTimer.Stop();
        if (Selected != null) Selected.PropertyChanged -= OnProjectChanged;
    }
}
