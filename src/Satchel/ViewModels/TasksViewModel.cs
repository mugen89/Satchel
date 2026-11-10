using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Behaviors;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Services;

#pragma warning disable MVVMTK0034 // direct field access is intentional to avoid re-triggering filters

namespace Satchel.ViewModels;

public sealed partial class TaskGroup : ObservableObject
{
    public TaskGroup(string title, bool isOverdue = false)
    {
        Title = title;
        IsOverdue = isOverdue;
    }

    public string Title { get; }
    public bool IsOverdue { get; }
    public bool HasTitle => !string.IsNullOrEmpty(Title);
    public ObservableCollection<TaskItem> Items { get; } = [];
}

public sealed partial class TasksViewModel : PageViewModel, IDisposable
{
    private readonly INavigator _nav;
    private readonly DataStore _store;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly SettingsService _settings;
    private readonly List<TaskItem> _all = [];
    private readonly DispatcherTimer _refreshTimer;
    private bool _loaded;

    public TasksViewModel(INavigator nav, DataStore store, DialogService dialogs, ToastService toasts, SettingsService settings)
    {
        _nav = nav;
        _store = store;
        _dialogs = dialogs;
        _toasts = toasts;
        _settings = settings;

        TodayFilter = new SideFilter("today", "My day", Glyphs.Sun);
        UpcomingFilter = new SideFilter("upcoming", "Upcoming", Glyphs.CalendarWeek);
        AllFilter = new SideFilter("all", "All tasks", Glyphs.Tasks);
        CompletedFilter = new SideFilter("completed", "Completed", Glyphs.Completed);
        InboxFilter = new SideFilter("inbox", "Inbox", Glyphs.Import);
        SmartFilters = [TodayFilter, UpcomingFilter, AllFilter, CompletedFilter];
        _selectedFilter = TodayFilter;
        TodayFilter.IsSelected = true;
        _isBoardView = settings.Current.TasksView == "Board";

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _refreshTimer.Tick += (_, _) => { _refreshTimer.Stop(); ApplyFilter(); };
    }

    public override string Title => "Tasks";

    public SideFilter TodayFilter { get; }
    public SideFilter UpcomingFilter { get; }
    public SideFilter AllFilter { get; }
    public SideFilter CompletedFilter { get; }
    public SideFilter InboxFilter { get; }
    public ObservableCollection<SideFilter> SmartFilters { get; }
    public ObservableCollection<SideFilter> ListFilters { get; } = [];
    public ObservableCollection<TaskList> Lists { get; } = [];
    public List<TaskList> ListChoices { get; private set; } = [];

    public ObservableCollection<TaskGroup> Groups { get; } = [];
    public ObservableCollection<TaskItem> CompletedTasks { get; } = [];
    public ObservableCollection<TaskItem> TodoColumn { get; } = [];
    public ObservableCollection<TaskItem> ProgressColumn { get; } = [];
    public ObservableCollection<TaskItem> DoneColumn { get; } = [];

    [ObservableProperty] private SideFilter _selectedFilter;
    [ObservableProperty] private string _quickAddText = "";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isBoardView;
    [ObservableProperty] private bool _showCompleted;
    [ObservableProperty] private TaskDetailViewModel? _detail;

    public bool HasDetail => Detail != null;
    public bool IsSmartView => SelectedFilter == TodayFilter || SelectedFilter == UpcomingFilter;
    public bool IsCompletedView => SelectedFilter == CompletedFilter;
    public bool CanReorder => !IsSmartView && !IsCompletedView && string.IsNullOrWhiteSpace(SearchText);
    public bool HasCompleted => CompletedTasks.Count > 0;
    public bool ShowCompletedSection => !IsCompletedView && HasCompleted;
    public bool IsEmpty => Groups.All(g => g.Items.Count == 0) && (!ShowCompletedSection || IsCompletedView && CompletedTasks.Count == 0);
    public string HeaderTitle => SelectedFilter.Title;
    public string HeaderSubtitle => SelectedFilter == TodayFilter ? DateTime.Today.ToString("dddd, d MMMM", CultureInfo.CurrentCulture) : $"{OpenCount} open";
    public int OpenCount => Groups.Sum(g => g.Items.Count);
    public string CompletedHeader => $"Completed ({CompletedTasks.Count})";
    public string QuickAddHint => "Add a task — try “Call Ana tomorrow 5pm !high #work”";

    public string EmptyTitle => SelectedFilter == TodayFilter ? "Nothing due today 🎉" : IsCompletedView ? "Nothing completed yet" : "No tasks here";
    public string EmptyText => SelectedFilter == TodayFilter ? "Enjoy the calm — or add something above." : "Type a task above and press Enter.";

    partial void OnSelectedFilterChanged(SideFilter value)
    {
        foreach (var f in SmartFilters.Concat(ListFilters)) f.IsSelected = ReferenceEquals(f, value);
        InboxFilter.IsSelected = ReferenceEquals(InboxFilter, value);
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnQuickAddTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) { QuickAddPreview = ""; return; }
        var (repeatRule, repeatRest, _) = TaskRepeat.ParseQuickAdd(value);
        var p = TaskParser.Parse(repeatRest, Lists.Select(l => l.Name));
        var parts = new List<string>();
        if (p.Due.HasValue) parts.Add("📅 " + (p.HasTime ? $"{Format.FriendlyDay(p.Due.Value)} {Format.Time(p.Due.Value)}" : Format.FriendlyDay(p.Due.Value)));
        if (p.Priority != TaskPriority.None) parts.Add("⚑ " + p.Priority);
        if (p.ListName != null) parts.Add("☰ " + p.ListName);
        parts.AddRange(p.Tags.Select(t => "#" + t));
        if (repeatRule.Length > 0) parts.Add("🔁 " + TaskRepeat.Describe(repeatRule));
        QuickAddPreview = parts.Count == 0 ? "" : string.Join("   ", parts);
    }

    [ObservableProperty] private string _quickAddPreview = "";

    partial void OnIsBoardViewChanged(bool value)
    {
        _settings.Current.TasksView = value ? "Board" : "List";
        ApplyFilter();
    }

    partial void OnShowCompletedChanged(bool value) => OnPropertyChanged(nameof(ShowCompletedSection));

    partial void OnDetailChanged(TaskDetailViewModel? value) => OnPropertyChanged(nameof(HasDetail));

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_loaded) await LoadAsync();
        else ApplyFilter();
        if (parameter is string mode)
        {
            if (mode == "board") { IsBoardView = true; SelectedFilter = AllFilter; }
            else if (mode == "list") IsBoardView = false;
            else if (mode == "all") SelectedFilter = AllFilter;
            else if (mode == "focus")
                _ = System.Windows.Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    () => QuickAddFocusRequested?.Invoke());
        }
        if (parameter is int id && _all.FirstOrDefault(t => t.Id == id) is { } task)
        {
            if (!Visible(task) && !task.IsDone) SelectedFilter = AllFilter;
            await SelectAsync(task);
        }
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var tasks = await _store.ListAsync<TaskItem>();
            var lists = await _store.ListAsync<TaskList>(q => q.OrderBy(l => l.SortOrder));
            _all.Clear();
            _all.AddRange(tasks);
            Lists.Clear();
            foreach (var l in lists) Lists.Add(l);
            RebuildListFilters();
            _loaded = true;
            ApplyFilter();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void RebuildListFilters()
    {
        var selectedKey = SelectedFilter.Key;
        ListFilters.Clear();
        ListFilters.Add(InboxFilter);
        foreach (var l in Lists) ListFilters.Add(new SideFilter($"list:{l.Id}", l.Name, l.Icon, l, l.Color));
        ListChoices = [new TaskList { Id = 0, Name = "Inbox", Icon = Glyphs.Import, Color = "slate" }, .. Lists];
        OnPropertyChanged(nameof(ListChoices));
        if (selectedKey.StartsWith("list:"))
        {
            var again = ListFilters.FirstOrDefault(f => f.Key == selectedKey);
            if (again != null) { _selectedFilter = again; again.IsSelected = true; }
            else SelectedFilter = TodayFilter;
        }
    }

    public void RefreshCounts()
    {
        var open = _all.Where(t => !t.IsDone).ToList();
        TodayFilter.Count = open.Count(t => t.DueDate.HasValue && t.DueDate.Value.Date <= DateTime.Today);
        UpcomingFilter.Count = open.Count(t => t.DueDate.HasValue && t.DueDate.Value.Date > DateTime.Today && t.DueDate.Value.Date <= DateTime.Today.AddDays(7));
        AllFilter.Count = open.Count;
        CompletedFilter.Count = 0;
        InboxFilter.Count = open.Count(t => t.ListId == null);
        foreach (var f in ListFilters)
            if (f.Payload is TaskList l) f.Count = open.Count(t => t.ListId == l.Id);
    }

    private bool Visible(TaskItem t)
    {
        var f = SelectedFilter;
        if (f == TodayFilter) return t.DueDate.HasValue && t.DueDate.Value.Date <= DateTime.Today || t.State == TaskState.InProgress && !t.DueDate.HasValue;
        if (f == UpcomingFilter) return t.DueDate.HasValue && t.DueDate.Value.Date <= DateTime.Today.AddDays(7);
        if (f == InboxFilter) return t.ListId == null;
        if (f.Payload is TaskList l) return t.ListId == l.Id;
        return true;
    }

    private bool MatchesSearch(TaskItem t)
    {
        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return terms.All(term => t.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                                 t.Notes.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                                 t.Tags.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    public void ScheduleRefresh()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    public void ApplyFilter()
    {
        if (!_loaded) return;
        foreach (var t in _all)
        {
            t.RefreshComputed();
            t.ListName = ListName(t.ListId);
            t.ListColor = ListColor(t.ListId);
        }
        var visible = _all.Where(t => Visible(t) && MatchesSearch(t)).ToList();
        var open = visible.Where(t => !t.IsDone).OrderBy(t => t.SortOrder).ThenBy(t => t.CreatedAt).ToList();
        var done = visible.Where(t => t.IsDone).OrderByDescending(t => t.CompletedAt ?? t.UpdatedAt).ToList();

        // List view groups
        var newGroups = new List<(string Title, bool Overdue, List<TaskItem> Items)>();
        if (IsCompletedView)
        {
            foreach (var g in done.GroupBy(t => (t.CompletedAt ?? t.UpdatedAt).Date).OrderByDescending(g => g.Key))
                newGroups.Add((Format.FriendlyDay(g.Key), false, g.ToList()));
        }
        else if (IsSmartView)
        {
            var overdue = open.Where(t => t.IsOverdue).OrderBy(t => t.DueDate).ToList();
            if (overdue.Count > 0) newGroups.Add(("Overdue", true, overdue));
            foreach (var g in open.Where(t => !t.IsOverdue && t.DueDate.HasValue).GroupBy(t => t.DueDate!.Value.Date).OrderBy(g => g.Key))
                newGroups.Add((Format.FriendlyDay(g.Key), false, g.OrderBy(t => t.DueDate).ToList()));
            var undated = open.Where(t => !t.DueDate.HasValue).ToList();
            if (undated.Count > 0) newGroups.Add(("In progress", false, undated));
        }
        else
        {
            newGroups.Add(("", false, open));
        }
        SyncGroups(newGroups);
        Sync(CompletedTasks, IsCompletedView ? [] : done);

        // Board columns
        var boardSource = IsCompletedView ? done : visible;
        Sync(TodoColumn, boardSource.Where(t => t.State == TaskState.Todo).OrderBy(t => t.SortOrder).ToList());
        Sync(ProgressColumn, boardSource.Where(t => t.State == TaskState.InProgress).OrderBy(t => t.SortOrder).ToList());
        Sync(DoneColumn, boardSource.Where(t => t.State == TaskState.Done).OrderByDescending(t => t.CompletedAt).Take(50).ToList());

        RefreshCounts();
        foreach (var name in new[] { nameof(IsSmartView), nameof(IsCompletedView), nameof(CanReorder), nameof(HasCompleted), nameof(ShowCompletedSection),
                     nameof(IsEmpty), nameof(HeaderTitle), nameof(HeaderSubtitle), nameof(OpenCount), nameof(CompletedHeader), nameof(EmptyTitle), nameof(EmptyText) })
            OnPropertyChanged(name);
    }

    private void SyncGroups(List<(string Title, bool Overdue, List<TaskItem> Items)> target)
    {
        bool sameShape = Groups.Count == target.Count && Groups.Zip(target).All(p => p.First.Title == p.Second.Title);
        if (!sameShape)
        {
            Groups.Clear();
            foreach (var (title, overdue, items) in target)
            {
                var g = new TaskGroup(title, overdue);
                foreach (var i in items) g.Items.Add(i);
                Groups.Add(g);
            }
            return;
        }
        for (int i = 0; i < target.Count; i++) Sync(Groups[i].Items, target[i].Items);
    }

    private static void Sync(ObservableCollection<TaskItem> target, List<TaskItem> items)
    {
        for (int i = target.Count - 1; i >= 0; i--)
            if (!items.Contains(target[i])) target.RemoveAt(i);
        for (int i = 0; i < items.Count; i++)
        {
            int existing = target.IndexOf(items[i]);
            if (existing == -1) target.Insert(i, items[i]);
            else if (existing != i) target.Move(existing, i);
        }
    }

    // ----- commands -----

    [RelayCommand]
    private void SelectFilter(SideFilter? filter)
    {
        if (filter != null) SelectedFilter = filter;
    }

    [RelayCommand]
    private async Task QuickAdd()
    {
        var text = QuickAddText.Trim();
        if (text.Length == 0) return;
        // "water the plants every monday", "standup weekdays 9am", "pay rent monthly"…
        var (rule, rest, firstDue) = TaskRepeat.ParseQuickAdd(text);
        var parsed = TaskParser.Parse(rest, Lists.Select(l => l.Name));
        int? listId = SelectedFilter.Payload is TaskList current ? current.Id : null;
        if (parsed.ListName != null) listId = Lists.First(l => l.Name == parsed.ListName).Id;
        var due = parsed.Due ?? firstDue;
        if (due == null && (SelectedFilter == TodayFilter || rule.Length > 0)) due = DateTime.Today;
        var task = new TaskItem
        {
            Title = parsed.Title,
            ListId = listId,
            DueDate = due,
            DueHasTime = parsed.HasTime,
            Priority = parsed.Priority,
            Tags = string.Join(", ", parsed.Tags),
            Repeat = rule,
            SortOrder = (_all.Count == 0 ? 0 : _all.Min(t => t.SortOrder)) - 1
        };
        await _store.AddAsync(task);
        _all.Add(task);
        QuickAddText = "";
        ApplyFilter();
        if (!Visible(task))
            _toasts.Show($"Added to {(listId == null ? "Inbox" : Lists.First(l => l.Id == listId).Name)}" + (due.HasValue ? $" · due {task.DueText}" : ""), ToastKind.Success);
    }

    public override void OnNewShortcut() => QuickAddFocusRequested?.Invoke();
    public event Action? QuickAddFocusRequested;

    [RelayCommand]
    private Task Select(TaskItem? task) => task == null ? Task.CompletedTask : SelectAsync(task);

    public async Task SelectAsync(TaskItem task)
    {
        if (Detail?.Task == task) return;
        await CloseDetailAsync();
        Detail = new TaskDetailViewModel(task, this, _store);
    }

    [RelayCommand]
    private Task CloseDetail() => CloseDetailAsync();

    private async Task CloseDetailAsync()
    {
        var d = Detail;
        if (d == null) return;
        Detail = null;
        await d.SaveAsync();
        d.Dispose();
        ApplyFilter();
    }

    public override async Task FlushAsync()
    {
        if (Detail != null) await Detail.SaveAsync();
    }

    [RelayCommand]
    private async Task ToggleDone(TaskItem? task)
    {
        if (task == null) return;
        task.IsDone = !task.IsDone;
        task.UpdatedAt = DateTime.Now;
        await _store.UpdateAsync(task);
        var next = task.IsDone ? await HandleCompletedAsync(task, announce: false) : null;
        await Task.Delay(Anim.Enabled ? 420 : 0);
        ApplyFilter();
        if (task.IsDone)
            _toasts.Show(next != null ? $"Completed “{Truncate(task.Title)}” — next one {Core.Format.FriendlyDay(next.DueDate!.Value).ToLowerInvariant()}" : $"Completed “{Truncate(task.Title)}”",
                ToastKind.Success, "Undo", async () =>
            {
                task.IsDone = false;
                if (next != null)
                {
                    // Undo the repeat too: the series moves back to this task.
                    task.Repeat = next.Repeat;
                    _all.Remove(next);
                    await _store.DeleteAsync(next);
                }
                await _store.UpdateAsync(task);
                ApplyFilter();
            });
    }

    /// <summary>A task was just completed: repeating tasks get their next occurrence.</summary>
    public async Task<TaskItem?> HandleCompletedAsync(TaskItem task, bool announce = true)
    {
        var next = await TaskRepeat.OnCompletedAsync(_store, task);
        if (next == null) return null;
        _all.Add(next);
        if (announce)
        {
            ApplyFilter();
            _toasts.Show($"Repeats — the next one is due {Core.Format.FriendlyDay(next.DueDate!.Value).ToLowerInvariant()}", ToastKind.Success, durationMs: 2600);
        }
        return next;
    }

    private static string Truncate(string s) => s.Length > 40 ? s[..40] + "…" : s;

    [RelayCommand]
    private async Task Delete(TaskItem? task)
    {
        if (task == null) return;
        if (Detail?.Task == task) { Detail.Dispose(); Detail = null; }
        await _store.DeleteAsync(task);
        _all.Remove(task);
        ApplyFilter();
        _toasts.Show("Task deleted", ToastKind.Info, "Undo", async () =>
        {
            task.Id = 0;
            await _store.AddAsync(task);
            _all.Add(task);
            ApplyFilter();
        });
    }

    [RelayCommand]
    private async Task ClearCompleted()
    {
        var done = _all.Where(t => t.IsDone && Visible(t)).ToList();
        if (done.Count == 0) return;
        if (!await _dialogs.ConfirmAsync("Clear completed tasks?", $"{done.Count} completed task(s) will be deleted.", "Delete", danger: true)) return;
        await _store.DeleteRangeAsync(done);
        foreach (var t in done) _all.Remove(t);
        ApplyFilter();
    }

    [RelayCommand]
    private async Task SetPriority(object? parameter)
    {
        if (parameter is not object[] { Length: 2 } a || a[0] is not TaskItem task || a[1] is not TaskPriority p) return;
        task.Priority = p;
        await _store.UpdateAsync(task);
    }

    public async Task SetDueAsync(TaskItem task, DateTime? due)
    {
        task.DueDate = due;
        task.DueHasTime = false;
        await _store.UpdateAsync(task);
        ApplyFilter();
    }

    [RelayCommand]
    private async Task Reorder(ReorderEventArgs? args)
    {
        if (args == null) return;
        if (args.Item is TaskItem moved && args.ChangedList && args.TargetTag is string tag && Enum.TryParse<TaskState>(tag, out var state))
        {
            moved.State = state;
            await _store.UpdateAsync(moved);
        }
        var changed = new List<TaskItem>();
        void Renumber(IEnumerable<TaskItem> items)
        {
            int i = 0;
            foreach (var t in items) { t.SortOrder = i++; changed.Add(t); }
        }
        if (IsBoardView)
        {
            Renumber(TodoColumn);
            Renumber(ProgressColumn);
        }
        else
        {
            foreach (var g in Groups) Renumber(g.Items);
        }
        await _store.SaveOrderAsync(changed, t => t.SortOrder);
        if (args.ChangedList) ScheduleRefresh();
        RefreshCounts();
    }

    [RelayCommand]
    private async Task DropOnList(DropEventArgs? args)
    {
        if (args?.Data is not TaskItem task || args.Target is not SideFilter f) return;
        int? id = f == InboxFilter ? null : f.Payload is TaskList l ? l.Id : task.ListId;
        if (f == TodayFilter) { await SetDueAsync(task, DateTime.Today); _toasts.Success("Due today"); return; }
        if (f == CompletedFilter) { if (!task.IsDone) await ToggleDone(task); return; }
        if (task.ListId == id) return;
        task.ListId = id;
        await _store.UpdateAsync(task);
        ApplyFilter();
        _toasts.Success($"Moved to {f.Title}");
    }

    [RelayCommand]
    private async Task NewList()
    {
        var name = await _dialogs.PromptAsync("New list", null, "", "e.g. Groceries, Home, Project X", "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        var colors = Palettes.Tags.Select(t => t.Key).ToList();
        var list = new TaskList { Name = name, Color = colors[(Lists.Count + 3) % colors.Count], Icon = Glyphs.List, SortOrder = Lists.Count };
        await _store.AddAsync(list);
        Lists.Add(list);
        RebuildListFilters();
        SelectedFilter = ListFilters.First(f => f.Payload == list);
    }

    [RelayCommand]
    private async Task RenameList(SideFilter? filter)
    {
        if (filter?.Payload is not TaskList list) return;
        var name = await _dialogs.PromptAsync("Rename list", null, list.Name, "List name", "Rename");
        if (string.IsNullOrWhiteSpace(name)) return;
        list.Name = name;
        filter.Title = name;
        await _store.UpdateAsync(list);
        OnPropertyChanged(nameof(HeaderTitle));
    }

    [RelayCommand]
    private async Task SetListColor(object? parameter)
    {
        if (parameter is not object[] { Length: 2 } a || a[0] is not SideFilter { Payload: TaskList list } filter || a[1] is not string color) return;
        list.Color = color;
        filter.ColorKey = color;
        await _store.UpdateAsync(list);
    }

    [RelayCommand]
    private async Task DeleteList(SideFilter? filter)
    {
        if (filter?.Payload is not TaskList list) return;
        int count = _all.Count(t => t.ListId == list.Id);
        if (!await _dialogs.ConfirmAsync("Delete list", count == 0 ? $"Delete “{list.Name}”?" : $"Delete “{list.Name}”? Its {count} task(s) will move to the Inbox.", "Delete", danger: true)) return;
        await _store.DeleteAsync(list);
        foreach (var t in _all.Where(t => t.ListId == list.Id)) t.ListId = null;
        Lists.Remove(list);
        if (SelectedFilter == filter) SelectedFilter = InboxFilter;
        RebuildListFilters();
        ApplyFilter();
    }

    [RelayCommand]
    private async Task ReorderLists(ReorderEventArgs? args)
    {
        // Inbox is always first; keep it pinned there.
        if (ListFilters.IndexOf(InboxFilter) != 0) ListFilters.Move(ListFilters.IndexOf(InboxFilter), 0);
        var ordered = ListFilters.Where(f => f.Payload is TaskList).Select(f => (TaskList)f.Payload!).ToList();
        for (int i = 0; i < ordered.Count; i++) ordered[i].SortOrder = i;
        Lists.Clear();
        foreach (var l in ordered) Lists.Add(l);
        ListChoices = [new TaskList { Id = 0, Name = "Inbox", Icon = Glyphs.Import, Color = "slate" }, .. Lists];
        await _store.SaveOrderAsync(ordered, l => l.SortOrder);
    }

    [RelayCommand]
    private void ToggleCompleted() => ShowCompleted = !ShowCompleted;

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    public string ListName(int? id) => id == null ? "Inbox" : Lists.FirstOrDefault(l => l.Id == id)?.Name ?? "";
    public string ListColor(int? id) => id == null ? "slate" : Lists.FirstOrDefault(l => l.Id == id)?.Color ?? "slate";

    public void Dispose()
    {
        _refreshTimer.Stop();
        Detail?.Dispose();
    }
}
