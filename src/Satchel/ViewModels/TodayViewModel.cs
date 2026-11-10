using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Services;

namespace Satchel.ViewModels;

public sealed record MemoryItem(string Title, string Subtitle, string Glyph, string Page, object? Parameter, byte[]? Image = null);

/// <summary>
/// Today: the day at a glance — agenda, tasks, the journal entry and mood, and the auto-diary (what you did, compiled from
/// everything Satchel knows). Step back through earlier days with the arrows.
/// </summary>
public sealed partial class TodayViewModel : PageViewModel
{
    private readonly INavigator _nav;
    private readonly DataStore _store;
    private readonly ToastService _toasts;
    private readonly WeatherService _weather;
    private int _version;

    public TodayViewModel(INavigator nav, DataStore store, ToastService toasts, WeatherService weather)
    {
        _nav = nav;
        _store = store;
        _toasts = toasts;
        _weather = weather;
    }

    public override string Title => "Today";

    public ObservableCollection<AgendaRow> Agenda { get; } = [];
    public ObservableCollection<TaskItem> Tasks { get; } = [];
    public ObservableCollection<TaskItem> Done { get; } = [];
    public ObservableCollection<DiaryEntry> Entries { get; } = [];
    public ObservableCollection<MemoryItem> Memories { get; } = [];
    public ObservableCollection<string> Celebrations { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DateTitle), nameof(RelativeText), nameof(IsToday), nameof(YearText))]
    private DateTime _date = DateTime.Today;

    [ObservableProperty] private Note? _journal;
    [ObservableProperty] private string _journalPreview = "";
    [ObservableProperty] private int _mood;
    [ObservableProperty] private string _journalLine = "";
    [ObservableProperty] private string _newTask = "";
    [ObservableProperty] private WeatherReport? _weatherNow;
    [ObservableProperty] private string _statsText = "";

    public string DateTitle => Date.ToString("dddd, d MMMM", CultureInfo.CurrentCulture);
    public string YearText => Date.Year == DateTime.Today.Year ? "" : Date.Year.ToString(CultureInfo.CurrentCulture);
    public bool IsToday => Date == DateTime.Today;
    public bool HasAgenda => Agenda.Count > 0;
    public bool HasTasks => Tasks.Count > 0;
    public bool HasDone => Done.Count > 0;
    public bool HasEntries => Entries.Count > 0;
    public bool HasMemories => Memories.Count > 0;
    public bool HasCelebrations => Celebrations.Count > 0;
    public bool HasJournal => !string.IsNullOrWhiteSpace(JournalPreview);
    public bool HasWeather => WeatherNow != null && IsToday;

    public string RelativeText
    {
        get
        {
            var days = (Date - DateTime.Today).Days;
            return days switch
            {
                0 => "Today",
                -1 => "Yesterday",
                1 => "Tomorrow",
                < 0 => $"{-days} days ago",
                _ => $"In {days} days"
            };
        }
    }

    public static IReadOnlyList<Choice<int>> Moods { get; } =
        [new(1, "😞", "Rough"), new(2, "😕", "Meh"), new(3, "😐", "Okay"), new(4, "🙂", "Good"), new(5, "😄", "Great")];

    partial void OnDateChanged(DateTime value) => _ = LoadAsync();

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is DateTime d && d.Date != Date) Date = d.Date;
        else await LoadAsync();
    }

    public override void OnNewShortcut() => NewTask = "";

    private async Task LoadAsync()
    {
        int version = ++_version;
        var date = Date;
        var next = date.AddDays(1);
        bool today = date == DateTime.Today;
        var data = await _store.RunAsync(db =>
        {
            var calendars = db.CalendarCategories.ToList();
            var colors = calendars.ToDictionary(c => c.Id, c => c.Color);
            var hidden = calendars.Where(c => !c.IsVisible).Select(c => c.Id).ToHashSet();
            var agenda = new List<(DateTime Start, AgendaRow Row)>();
            foreach (var e in db.Events.ToList())
            {
                if (e.CategoryId is int cid && hidden.Contains(cid)) continue;
                foreach (var (start, end) in Recurrence.Occurrences(e, date, next))
                {
                    var color = e.Color ?? (e.CategoryId is int id && colors.TryGetValue(id, out var c) ? c : "slate");
                    var time = e.IsAllDay ? "All day" : $"{Format.Time(start)} – {Format.Time(end)}";
                    agenda.Add((start, new AgendaRow(time, e.Title, color, e.Location, date, "Calendar")));
                }
            }
            var tasks = db.Tasks.Where(t => t.State != TaskState.Done && t.DueDate != null && t.DueDate < next).ToList()
                .Where(t => today ? true : t.DueDate!.Value.Date == date).OrderBy(t => t.DueDate).ThenByDescending(t => t.Priority).ToList();
            var done = db.Tasks.Where(t => t.CompletedAt >= date && t.CompletedAt < next).OrderBy(t => t.CompletedAt).ToList();
            var journal = db.Notes.Where(n => n.Type == NoteType.Journal && !n.IsTrashed && n.JournalDate >= date && n.JournalDate < next).OrderBy(n => n.CreatedAt).FirstOrDefault();
            var diary = Diary.Build(db, date);
            // On this day: journal entries and notes from earlier years, photos taken that day.
            var memories = new List<MemoryItem>();
            foreach (var n in db.Notes.Where(n => !n.IsTrashed).Select(n => new { n.Id, n.Title, n.Type, n.JournalDate, n.CreatedAt, n.PlainText }).ToList()
                         .Where(n => (n.JournalDate ?? n.CreatedAt).Month == date.Month && (n.JournalDate ?? n.CreatedAt).Day == date.Day && (n.JournalDate ?? n.CreatedAt).Year < date.Year)
                         .OrderByDescending(n => n.JournalDate ?? n.CreatedAt).Take(6))
            {
                var when = (n.JournalDate ?? n.CreatedAt).Year;
                var years = date.Year - when;
                var title = string.IsNullOrWhiteSpace(n.Title) ? (n.PlainText.Length > 60 ? n.PlainText[..60] + "…" : n.PlainText) : n.Title;
                memories.Add(new MemoryItem(title.Length == 0 ? "Untitled" : title, years == 1 ? "1 year ago" : $"{years} years ago",
                    n.Type == NoteType.Journal ? Glyphs.Journal : Glyphs.Notes, "Notes", n.Id));
            }
            foreach (var p in db.Photos.Where(p => p.TakenAt.Month == date.Month && p.TakenAt.Day == date.Day && p.TakenAt.Year < date.Year)
                         .Select(p => new { p.Id, p.Caption, p.TakenAt, p.Thumb }).OrderByDescending(p => p.TakenAt).Take(6).ToList())
            {
                var years = date.Year - p.TakenAt.Year;
                memories.Add(new MemoryItem(string.IsNullOrWhiteSpace(p.Caption) ? "Photo" : p.Caption, years == 1 ? "1 year ago" : $"{years} years ago", Glyphs.Photo, "Photos", p.Id, p.Thumb));
            }
            var celebrations = new List<string>();
            foreach (var c in db.Contacts.Where(c => c.Birthday != null).ToList().Where(c => c.Birthday!.Value.Month == date.Month && c.Birthday.Value.Day == date.Day))
                celebrations.Add(c.AgeNext is int age && date.Year == DateTime.Today.Year ? $"🎂 {c.FullName} turns {age}" : $"🎂 {c.FullName}'s birthday");
            foreach (var cd in db.Countdowns.ToList().Where(cd => cd.NextDate.Date == date))
                celebrations.Add($"{(string.IsNullOrEmpty(cd.Emoji) ? "⭐" : cd.Emoji)} {cd.Title}");
            return (agenda.OrderBy(a => a.Start).Select(a => a.Row).ToList(), tasks, done, journal, diary, memories, celebrations);
        });
        if (version != _version) return;

        Fill(Agenda, data.Item1);
        Fill(Tasks, data.tasks);
        Fill(Done, data.done);
        Fill(Entries, data.diary);
        Fill(Memories, data.memories);
        Fill(Celebrations, data.celebrations);
        SetJournal(data.journal);
        StatsText = string.Join(" · ", new[]
        {
            data.done.Count > 0 ? $"{data.done.Count} done" : "",
            Agenda.Count > 0 ? $"{Agenda.Count} event{(Agenda.Count == 1 ? "" : "s")}" : "",
            data.diary.Count > 0 ? $"{data.diary.Count} things in the diary" : ""
        }.Where(s => s.Length > 0));
        foreach (var n in new[] { nameof(HasAgenda), nameof(HasTasks), nameof(HasDone), nameof(HasEntries), nameof(HasMemories), nameof(HasCelebrations) }) OnPropertyChanged(n);

        WeatherNow = today ? _weather.Cached : null;
        OnPropertyChanged(nameof(HasWeather));
        // Never make navigation wait for the network.
        if (today) _ = LoadWeatherAsync(version);
    }

    private async Task LoadWeatherAsync(int version)
    {
        var report = await _weather.GetAsync();
        if (version != _version) return;
        WeatherNow = report;
        OnPropertyChanged(nameof(HasWeather));
    }

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    private void SetJournal(Note? note)
    {
        Journal = note;
        Mood = note?.Mood ?? 0;
        var text = note?.PlainText.Trim() ?? "";
        JournalPreview = text.Length > 600 ? text[..600] + "…" : text;
        OnPropertyChanged(nameof(HasJournal));
    }

    // ----- days -----

    [RelayCommand] private void PreviousDay() => Date = Date.AddDays(-1);
    [RelayCommand] private void NextDay() => Date = Date.AddDays(1);
    [RelayCommand] private void GoToday() => Date = DateTime.Today;

    [RelayCommand]
    private async Task Refresh() => await LoadAsync();

    // ----- tasks -----

    [RelayCommand]
    private async Task ToggleTask(TaskItem? task)
    {
        if (task == null) return;
        task.IsDone = !task.IsDone;
        await _store.UpdateAsync(task);
        if (task.IsDone && await TaskRepeat.OnCompletedAsync(_store, task) is { } nextTask)
            _toasts.Show($"Repeats — next on {Format.FriendlyDay(nextTask.DueDate!.Value)}", ToastKind.Success, durationMs: 2600);
        await Task.Delay(Anim.Enabled ? 380 : 0);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task AddTask()
    {
        var text = NewTask.Trim();
        if (text.Length == 0) return;
        var (rule, rest, firstDue) = TaskRepeat.ParseQuickAdd(text);
        var parsed = TaskParser.Parse(rest, []);
        var task = new TaskItem
        {
            Title = parsed.Title, DueDate = parsed.Due ?? firstDue ?? Date, DueHasTime = parsed.HasTime, Priority = parsed.Priority, Repeat = rule, SortOrder = -1
        };
        await _store.AddAsync(task);
        NewTask = "";
        await LoadAsync();
    }

    [RelayCommand]
    private void OpenTask(TaskItem? task)
    {
        if (task != null) _nav.NavigateTo("Tasks", task.Id);
    }

    // ----- journal -----

    private async Task<Note> EnsureJournalAsync()
    {
        if (Journal != null) return Journal;
        var note = new Note { Type = NoteType.Journal, JournalDate = Date, Title = "", Content = "", PlainText = "", SortOrder = -1 };
        await _store.AddAsync(note);
        Journal = note;
        return note;
    }

    [RelayCommand]
    private async Task SetMood(int mood)
    {
        var note = await EnsureJournalAsync();
        note.Mood = note.Mood == mood ? 0 : mood;
        Mood = note.Mood;
        note.Touch();
        await _store.UpdateAsync(note);
    }

    [RelayCommand]
    private async Task WriteJournal()
    {
        var note = await EnsureJournalAsync();
        _nav.NavigateTo("Notes", note.Id);
    }

    [RelayCommand]
    private async Task AddJournalLine()
    {
        var line = JournalLine.Trim();
        if (line.Length == 0) return;
        var note = await EnsureJournalAsync();
        var stamp = IsToday ? Format.Time(DateTime.Now) + "  " : "";
        var (content, plain) = RichTextUtil.Append(note.Content, [stamp + line]);
        note.Content = content;
        note.PlainText = plain;
        note.Touch();
        await _store.UpdateAsync(note);
        JournalLine = "";
        SetJournal(note);
    }

    [RelayCommand]
    private async Task SaveDiary()
    {
        if (Entries.Count == 0) { _toasts.Info("Nothing in the diary for this day yet"); return; }
        var note = await EnsureJournalAsync();
        var (content, plain) = RichTextUtil.Append(note.Content, Diary.ToLines(Entries), "What happened");
        note.Content = content;
        note.PlainText = plain;
        note.Touch();
        await _store.UpdateAsync(note);
        SetJournal(note);
        _toasts.Show("Saved to the journal", ToastKind.Success, "Open", () => _nav.NavigateTo("Notes", note.Id));
    }

    // ----- navigation -----

    [RelayCommand]
    private void OpenEntry(DiaryEntry? entry)
    {
        if (entry != null) _nav.NavigateTo(entry.Page, entry.Parameter);
    }

    [RelayCommand]
    private void OpenAgenda(AgendaRow? row)
    {
        if (row != null) _nav.NavigateTo("Calendar", Date);
    }

    [RelayCommand]
    private void OpenMemory(MemoryItem? item)
    {
        if (item != null) _nav.NavigateTo(item.Page, item.Parameter);
    }

    [RelayCommand]
    private void OpenWeather() => _nav.NavigateTo("Dashboard");
}
