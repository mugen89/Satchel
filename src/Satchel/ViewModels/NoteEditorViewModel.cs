using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Behaviors;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Security;

namespace Satchel.ViewModels;

/// <summary>Edits a single note with debounced auto-save.</summary>
public sealed partial class NoteEditorViewModel : ObservableObject, IDisposable
{
    private static readonly HashSet<string> SavedProperties =
    [
        nameof(Note.Title), nameof(Note.Content), nameof(Note.PlainText), nameof(Note.Tags), nameof(Note.Color),
        nameof(Note.NotebookId), nameof(Note.IsPinned), nameof(Note.Language), nameof(Note.JournalDate), nameof(Note.Mood), nameof(Note.Type)
    ];

    private readonly DataStore _store;
    private readonly NotesViewModel _owner;
    private readonly DispatcherTimer _timer;
    private bool _dirty;
    private bool _syncing;

    public NoteEditorViewModel(Note note, NotesViewModel owner, DataStore store)
    {
        Note = note;
        _owner = owner;
        _store = store;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _timer.Tick += async (_, _) => await SaveAsync();

        if (note.IsItemList)
        {
            foreach (var item in Note.ParseItems(note.Content)) AddTracked(item, Items.Count);
        }
        Items.CollectionChanged += OnItemsChanged;
        Note.PropertyChanged += OnNoteChanged;
    }

    public Note Note { get; }
    public NotesViewModel Owner => _owner;
    public ObservableCollection<ChecklistItem> Items { get; } = [];
    public IReadOnlyList<Notebook> NotebookChoices => _owner.NotebookChoices;
    public static IReadOnlyList<string> Languages { get; } =
        ["Plain text", "C#", "JavaScript", "TypeScript", "Python", "PowerShell", "Bash", "SQL", "JSON", "XML", "HTML", "CSS", "Go", "Rust", "Java", "C++", "YAML", "Markdown"];

    public static IReadOnlyList<Choice<int>> Moods { get; } =
    [
        new(1, "😞"), new(2, "😕"), new(3, "😐"), new(4, "🙂"), new(5, "😄")
    ];

    public bool IsText => Note.Type is NoteType.Text or NoteType.Journal;
    public bool IsJournal => Note.Type == NoteType.Journal;
    public bool IsChecklist => Note.Type == NoteType.Checklist;
    public bool IsList => Note.IsItemList;
    public bool IsCode => Note.Type == NoteType.Code;

    [ObservableProperty] private string _newItemText = "";
    [ObservableProperty] private bool _moveCheckedToBottom = true;
    [ObservableProperty] private bool _isExpanded;

    public Notebook? SelectedNotebook
    {
        get => NotebookChoices.FirstOrDefault(n => n.Id == (Note.NotebookId ?? 0));
        set
        {
            int? id = value == null || value.Id == 0 ? null : value.Id;
            if (Note.NotebookId == id) return;
            Note.NotebookId = id;
            OnPropertyChanged();
            _owner.RefreshCounts();
        }
    }

    public int CheckedCount => Items.Count(i => i.IsChecked);
    public double Progress => Items.Count == 0 ? 0 : (double)CheckedCount / Items.Count;
    public string ProgressText => Items.Count == 0 ? "No items yet" : $"{CheckedCount} of {Items.Count} done";

    public string StatsText
    {
        get
        {
            var text = Note.Type == NoteType.Code ? Note.Content : Note.PlainText;
            int words = string.IsNullOrWhiteSpace(text) ? 0 : Regex.Matches(text, @"\S+").Count;
            var edited = $"Edited {Format.Relative(Note.UpdatedAt)}";
            return Note.IsItemList ? $"{Items.Count} items · {edited}" : $"{words} words · {edited}";
        }
    }

    public string CreatedText => $"Created {Format.DateAndTime(Note.CreatedAt)}";

    public event Action<ChecklistItem>? FocusItemRequested;
    public event Action? Saved;

    private void OnNoteChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Note.UpdatedAt) || e.PropertyName == nameof(Note.PlainText) || e.PropertyName == nameof(Note.Content))
            OnPropertyChanged(nameof(StatsText));
        if (_syncing || e.PropertyName == null || !SavedProperties.Contains(e.PropertyName)) return;
        Schedule();
    }

    private void Schedule()
    {
        _dirty = true;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Raised before saving so the view can push un-debounced edits (rich text) into the note.</summary>
    public event Action? FlushRequested;

    public async Task SaveAsync()
    {
        FlushRequested?.Invoke();
        _timer.Stop();
        if (!_dirty) return;
        _dirty = false;
        Note.UpdatedAt = DateTime.Now;
        try
        {
            await _store.UpdateAsync(Note);
            Saved?.Invoke();
        }
        catch
        {
            _dirty = true; // retry on next change/flush
            throw;
        }
    }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Note.Title) &&
        (Note.IsItemList ? Items.All(i => string.IsNullOrWhiteSpace(i.Text)) : string.IsNullOrWhiteSpace(Note.Type == NoteType.Code ? Note.Content : Note.PlainText)) &&
        Note.Type != NoteType.Journal;

    // ----- checklist / list -----

    private void AddTracked(ChecklistItem item, int index)
    {
        item.PropertyChanged += OnItemChanged;
        Items.Insert(index, item);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (ChecklistItem old in e.OldItems)
                if (e.Action == NotifyCollectionChangedAction.Remove) old.PropertyChanged -= OnItemChanged;
        SyncItems();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is ChecklistItem item && e.PropertyName == nameof(ChecklistItem.IsChecked) && MoveCheckedToBottom && IsChecklist)
        {
            var from = Items.IndexOf(item);
            int to;
            if (item.IsChecked)
                to = Items.Count - 1;
            else
            {
                // place right after the last unchecked item
                to = Math.Min(Items.Count - 1, Items.Count(i => !i.IsChecked && i != item));
            }
            if (from >= 0 && to >= 0 && from != to)
            {
                Items.Move(from, to);
                return; // Move triggers SyncItems
            }
        }
        SyncItems();
    }

    private void SyncItems()
    {
        if (!Note.IsItemList) return;
        _syncing = true;
        Note.Content = Note.SerializeItems(Items);
        Note.PlainText = string.Join("\n", Items.Select(i => i.Text));
        _syncing = false;
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(StatsText));
        Schedule();
    }

    [RelayCommand]
    private void AddItem()
    {
        var text = NewItemText.Trim();
        if (text.Length == 0) return;
        // Paste of several lines → several items.
        var lines = text.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        int insertAt = IsChecklist && MoveCheckedToBottom ? Items.Count(i => !i.IsChecked) : Items.Count;
        foreach (var line in lines)
            AddTracked(new ChecklistItem { Text = line.Trim().TrimStart('-', '*', '•').Trim() }, insertAt++);
        NewItemText = "";
    }

    public ChecklistItem InsertAfter(ChecklistItem item)
    {
        var index = Items.IndexOf(item);
        var created = new ChecklistItem { Indent = item.Indent };
        AddTracked(created, index + 1);
        FocusItemRequested?.Invoke(created);
        return created;
    }

    public void RemoveAndFocusPrevious(ChecklistItem item)
    {
        var index = Items.IndexOf(item);
        if (index < 0) return;
        Items.RemoveAt(index);
        if (Items.Count > 0) FocusItemRequested?.Invoke(Items[Math.Max(0, index - 1)]);
    }

    public void Indent(ChecklistItem item, int delta)
    {
        item.Indent = Math.Clamp(item.Indent + delta, 0, 3);
        SyncItems();
    }

    [RelayCommand]
    private void RemoveItem(ChecklistItem? item)
    {
        if (item != null) Items.Remove(item);
    }

    [RelayCommand]
    private void ClearCompleted()
    {
        foreach (var done in Items.Where(i => i.IsChecked).ToList()) Items.Remove(done);
    }

    [RelayCommand]
    private void UncheckAll()
    {
        foreach (var i in Items) i.IsChecked = false;
    }

    [RelayCommand]
    private void ReorderItems(ReorderEventArgs? args) => SyncItems();

    // ----- misc -----

    [RelayCommand]
    private void CopyContent()
    {
        var text = Note.Type switch
        {
            NoteType.Code => Note.Content,
            NoteType.Checklist => string.Join(Environment.NewLine, Items.Select(i => $"{new string(' ', i.Indent * 2)}- [{(i.IsChecked ? "x" : " ")}] {i.Text}")),
            NoteType.List => string.Join(Environment.NewLine, Items.Select(i => $"{new string(' ', i.Indent * 2)}- {i.Text}")),
            _ => Note.PlainText
        };
        SecureClipboard.Copy(text, sensitive: false, "Note copied to clipboard");
    }

    [RelayCommand]
    private void SetMood(int mood) => Note.Mood = Note.Mood == mood ? 0 : mood;

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    public void Dispose()
    {
        _timer.Stop();
        Note.PropertyChanged -= OnNoteChanged;
        Items.CollectionChanged -= OnItemsChanged;
        foreach (var i in Items) i.PropertyChanged -= OnItemChanged;
    }
}
