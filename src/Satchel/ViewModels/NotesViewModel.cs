using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Satchel.Behaviors;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Services;

#pragma warning disable MVVMTK0034 // direct field access is intentional to avoid re-triggering filters

namespace Satchel.ViewModels;

public sealed partial class NotesViewModel : PageViewModel, IDisposable
{
    private readonly INavigator _nav;
    private readonly DataStore _store;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly SettingsService _settings;
    private readonly List<Note> _all = [];
    private bool _loaded;

    public NotesViewModel(INavigator nav, DataStore store, DialogService dialogs, ToastService toasts, SettingsService settings)
    {
        _nav = nav;
        _store = store;
        _dialogs = dialogs;
        _toasts = toasts;
        _settings = settings;
        AllFilter = new SideFilter("all", "All notes", Glyphs.Notes);
        JournalFilter = new SideFilter("journal", "Journal", Glyphs.Journal);
        ArchiveFilter = new SideFilter("archive", "Archive", Glyphs.Archive);
        TrashFilter = new SideFilter("trash", "Trash", Glyphs.Delete);
        LibraryFilters = [AllFilter, JournalFilter];
        BinFilters = [ArchiveFilter, TrashFilter];
        _selectedFilter = AllFilter;
        AllFilter.IsSelected = true;
        _isGridView = settings.Current.NotesView != "List";
        _sortMode = settings.Current.NotesSort;
    }

    public override string Title => "Notes";

    public SideFilter AllFilter { get; }
    public SideFilter JournalFilter { get; }
    public SideFilter ArchiveFilter { get; }
    public SideFilter TrashFilter { get; }
    public ObservableCollection<SideFilter> LibraryFilters { get; }
    public ObservableCollection<SideFilter> BinFilters { get; }
    public ObservableCollection<SideFilter> NotebookFilters { get; } = [];
    public ObservableCollection<SideFilter> TagFilters { get; } = [];
    public ObservableCollection<Notebook> Notebooks { get; } = [];
    public List<Notebook> NotebookChoices { get; private set; } = [];

    public ObservableCollection<Note> PinnedNotes { get; } = [];
    public ObservableCollection<Note> OtherNotes { get; } = [];

    public static IReadOnlyList<Choice<string>> SortChoices { get; } =
    [
        new("Custom", "Custom order"), new("Edited", "Last edited"), new("Created", "Date created"), new("Title", "Title")
    ];

    public static IReadOnlyList<Choice<NoteType>> NewTypes { get; } =
    [
        new(NoteType.Text, "Note", Glyphs.Document),
        new(NoteType.Checklist, "Checklist", Glyphs.Checklist),
        new(NoteType.List, "List", Glyphs.BulletedList),
        new(NoteType.Code, "Code snippet", Glyphs.Code),
        new(NoteType.Journal, "Journal entry", Glyphs.Journal),
    ];

    [ObservableProperty] private SideFilter _selectedFilter;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private NoteEditorViewModel? _editor;
    [ObservableProperty] private bool _isGridView;
    [ObservableProperty] private string _sortMode;

    public Choice<string>? SelectedSort
    {
        get => SortChoices.FirstOrDefault(s => s.Value == SortMode) ?? SortChoices[0];
        set { if (value != null) SortMode = value.Value; }
    }

    public bool IsTrashView => SelectedFilter == TrashFilter;
    public bool IsArchiveView => SelectedFilter == ArchiveFilter;
    public bool CanReorder => SortMode == "Custom" && string.IsNullOrWhiteSpace(SearchText) && !IsTrashView;
    public bool HasPinned => PinnedNotes.Count > 0;
    public bool HasOthers => OtherNotes.Count > 0;
    public bool ShowSectionHeaders => HasPinned && HasOthers;
    public bool IsEmpty => !HasPinned && !HasOthers;
    public bool HasEditor => Editor != null;
    public bool HasTrash => TrashFilter.Count > 0;

    public string EmptyTitle => !string.IsNullOrWhiteSpace(SearchText) ? "No matching notes"
        : IsTrashView ? "Trash is empty" : IsArchiveView ? "Nothing archived" : "No notes here yet";

    public string EmptyText => !string.IsNullOrWhiteSpace(SearchText) ? "Try another word or clear the search."
        : IsTrashView ? "Deleted notes stay here until you empty the trash."
        : IsArchiveView ? "Archive notes you want to keep but not see every day."
        : "Capture a thought, a checklist, a snippet of code — press Ctrl+N.";

    public string HeaderTitle => SelectedFilter.Title;

    partial void OnSelectedFilterChanged(SideFilter value)
    {
        foreach (var f in AllSideFilters()) f.IsSelected = ReferenceEquals(f, value);
        OnPropertyChanged(nameof(IsTrashView));
        OnPropertyChanged(nameof(IsArchiveView));
        OnPropertyChanged(nameof(HeaderTitle));
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSortModeChanged(string value)
    {
        _settings.Current.NotesSort = value;
        OnPropertyChanged(nameof(SelectedSort));
        ApplyFilter();
    }

    partial void OnIsGridViewChanged(bool value) => _settings.Current.NotesView = value ? "Grid" : "List";

    partial void OnEditorChanged(NoteEditorViewModel? oldValue, NoteEditorViewModel? newValue)
    {
        OnPropertyChanged(nameof(HasEditor));
    }

    private IEnumerable<SideFilter> AllSideFilters() => LibraryFilters.Concat(BinFilters).Concat(NotebookFilters).Concat(TagFilters);

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_loaded) await LoadAsync();
        if (parameter is int noteId && _all.FirstOrDefault(n => n.Id == noteId) is { } note)
        {
            if (note.IsTrashed) SelectedFilter = TrashFilter;
            else if (note.IsArchived) SelectedFilter = ArchiveFilter;
            else if (!Visible().Contains(note)) SelectedFilter = AllFilter;
            await OpenAsync(note);
        }
        else if (parameter is string s && s.StartsWith("new", StringComparison.Ordinal))
        {
            var type = s switch
            {
                "new-checklist" => NoteType.Checklist,
                "new-list" => NoteType.List,
                "new-code" => NoteType.Code,
                "new-journal" => NoteType.Journal,
                _ => NoteType.Text
            };
            await NewNote(type);
        }
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var notes = await _store.ListAsync<Note>();
            var notebooks = await _store.ListAsync<Notebook>(q => q.OrderBy(n => n.SortOrder));
            _all.Clear();
            _all.AddRange(notes);
            Notebooks.Clear();
            foreach (var nb in notebooks) Notebooks.Add(nb);
            RebuildNotebookFilters();
            _loaded = true;
            ApplyFilter();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void RebuildNotebookFilters()
    {
        var selectedKey = SelectedFilter.Key;
        NotebookFilters.Clear();
        foreach (var nb in Notebooks)
            NotebookFilters.Add(new SideFilter($"nb:{nb.Id}", nb.Name, nb.Icon, nb, nb.Color));
        NotebookChoices = [new Notebook { Id = 0, Name = "No notebook", Icon = Glyphs.Notes }, .. Notebooks];
        OnPropertyChanged(nameof(NotebookChoices));
        if (selectedKey.StartsWith("nb:"))
        {
            var again = NotebookFilters.FirstOrDefault(f => f.Key == selectedKey);
            if (again != null) { _selectedFilter = again; again.IsSelected = true; }
            else SelectedFilter = AllFilter;
        }
        RefreshCounts();
    }

    public void RefreshCounts()
    {
        var active = _all.Where(n => !n.IsTrashed && !n.IsArchived).ToList();
        AllFilter.Count = active.Count;
        JournalFilter.Count = active.Count(n => n.Type == NoteType.Journal);
        ArchiveFilter.Count = _all.Count(n => n.IsArchived && !n.IsTrashed);
        TrashFilter.Count = _all.Count(n => n.IsTrashed);
        foreach (var f in NotebookFilters)
            if (f.Payload is Notebook nb) f.Count = active.Count(n => n.NotebookId == nb.Id);

        var tags = active.SelectMany(n => n.TagList).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t).ToList();
        var selectedTag = SelectedFilter.Key.StartsWith("tag:") ? SelectedFilter.Key : null;
        if (!tags.SequenceEqual(TagFilters.Select(t => t.Title), StringComparer.OrdinalIgnoreCase))
        {
            TagFilters.Clear();
            foreach (var t in tags) TagFilters.Add(new SideFilter($"tag:{t.ToLowerInvariant()}", t, Glyphs.Tag, t));
            if (selectedTag != null)
            {
                var again = TagFilters.FirstOrDefault(f => f.Key == selectedTag);
                if (again != null) { _selectedFilter = again; again.IsSelected = true; }
            }
        }
        foreach (var f in TagFilters) f.Count = active.Count(n => n.TagList.Contains(f.Title, StringComparer.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(HasTrash));
    }

    private IEnumerable<Note> Visible()
    {
        IEnumerable<Note> q = _all;
        var f = SelectedFilter;
        if (f == TrashFilter) q = q.Where(n => n.IsTrashed);
        else if (f == ArchiveFilter) q = q.Where(n => n.IsArchived && !n.IsTrashed);
        else
        {
            q = q.Where(n => !n.IsTrashed && !n.IsArchived);
            if (f == JournalFilter) q = q.Where(n => n.Type == NoteType.Journal);
            else if (f.Payload is Notebook nb) q = q.Where(n => n.NotebookId == nb.Id);
            else if (f.Payload is string tag) q = q.Where(n => n.TagList.Contains(tag, StringComparer.OrdinalIgnoreCase));
        }

        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length > 0)
            q = q.Where(n => terms.All(t => n.Title.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                                            n.PlainText.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                                            n.Tags.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                                            (n.Type == NoteType.Code && n.Content.Contains(t, StringComparison.OrdinalIgnoreCase))));

        if (f == JournalFilter) return q.OrderByDescending(n => n.JournalDate ?? n.CreatedAt);
        return SortMode switch
        {
            "Edited" => q.OrderByDescending(n => n.UpdatedAt),
            "Created" => q.OrderByDescending(n => n.CreatedAt),
            "Title" => q.OrderBy(n => n.DisplayTitle, StringComparer.CurrentCultureIgnoreCase),
            _ => q.OrderBy(n => n.SortOrder).ThenByDescending(n => n.UpdatedAt)
        };
    }

    public void ApplyFilter()
    {
        if (!_loaded) return;
        var list = Visible().ToList();
        bool split = !IsTrashView && !IsArchiveView;
        Replace(PinnedNotes, split ? list.Where(n => n.IsPinned) : []);
        Replace(OtherNotes, split ? list.Where(n => !n.IsPinned) : list);
        RefreshCounts();
        OnPropertyChanged(nameof(HasPinned));
        OnPropertyChanged(nameof(HasOthers));
        OnPropertyChanged(nameof(ShowSectionHeaders));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanReorder));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyText));
    }

    private static void Replace(ObservableCollection<Note> target, IEnumerable<Note> items)
    {
        var list = items.ToList();
        if (target.SequenceEqual(list)) return;
        // Keep existing containers where possible so the animated panel can glide instead of rebuild.
        for (int i = target.Count - 1; i >= 0; i--)
            if (!list.Contains(target[i])) target.RemoveAt(i);
        for (int i = 0; i < list.Count; i++)
        {
            int existing = target.IndexOf(list[i]);
            if (existing == -1) target.Insert(i, list[i]);
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
    private async Task NewNote(NoteType type)
    {
        int? notebookId = SelectedFilter.Payload is Notebook nb ? nb.Id : null;
        var note = new Note
        {
            Type = type,
            NotebookId = notebookId,
            SortOrder = (_all.Count == 0 ? 0 : _all.Min(n => n.SortOrder)) - 1,
            JournalDate = type == NoteType.Journal ? DateTime.Today : null,
            Language = type == NoteType.Code ? "Plain text" : null,
            Tags = SelectedFilter.Payload is string tag ? tag : ""
        };
        if (type == NoteType.Journal && SelectedFilter != JournalFilter && SelectedFilter != AllFilter) SelectedFilter = AllFilter;
        if (IsTrashView || IsArchiveView) SelectedFilter = AllFilter;
        await _store.AddAsync(note);
        _all.Add(note);
        if (!string.IsNullOrWhiteSpace(SearchText)) SearchText = "";
        ApplyFilter();
        await OpenAsync(note);
    }

    public override void OnNewShortcut() => _ = NewNote(NoteType.Text);

    [RelayCommand]
    private Task Open(Note? note) => note == null ? Task.CompletedTask : OpenAsync(note);

    public async Task OpenAsync(Note note)
    {
        if (Editor?.Note == note) return;
        await CloseEditorAsync();
        Editor = new NoteEditorViewModel(note, this, _store);
    }

    [RelayCommand]
    private Task CloseEditor() => CloseEditorAsync();

    private async Task CloseEditorAsync()
    {
        var editor = Editor;
        if (editor == null) return;
        Editor = null;
        try
        {
            await editor.SaveAsync();
            if (editor.IsEmpty && !editor.Note.IsTrashed)
            {
                // Silently discard notes that were created and left empty.
                await _store.DeleteAsync(editor.Note);
                _all.Remove(editor.Note);
                ApplyFilter();
            }
        }
        finally
        {
            editor.Dispose();
        }
    }

    public override async Task FlushAsync()
    {
        if (Editor != null) await Editor.SaveAsync();
    }

    public override Task OnNavigatedFromAsync() => FlushAsync();

    private async Task SaveNote(Note note)
    {
        note.UpdatedAt = DateTime.Now;
        await _store.UpdateAsync(note);
    }

    [RelayCommand]
    private async Task TogglePin(Note? note)
    {
        if (note == null) return;
        note.IsPinned = !note.IsPinned;
        if (Editor?.Note != note) await SaveNote(note);
        ApplyFilter();
    }

    [RelayCommand]
    private async Task SetColor(object? parameter)
    {
        if (parameter is not object[] { Length: 2 } args || args[0] is not Note note || args[1] is not string color) return;
        note.Color = color;
        if (Editor?.Note != note) await SaveNote(note);
    }

    public async Task SetColorAsync(Note note, string color)
    {
        note.Color = color;
        if (Editor?.Note != note) await SaveNote(note);
    }

    [RelayCommand]
    private async Task ToggleArchive(Note? note)
    {
        if (note == null) return;
        note.IsArchived = !note.IsArchived;
        if (note.IsArchived) note.IsPinned = false;
        if (Editor?.Note == note) await CloseEditorAsync();
        await SaveNote(note);
        ApplyFilter();
        if (note.IsArchived)
            _toasts.Show("Note archived", ToastKind.Info, "Undo", async () => { note.IsArchived = false; await SaveNote(note); ApplyFilter(); });
    }

    [RelayCommand]
    private async Task Trash(Note? note)
    {
        if (note == null) return;
        if (Editor?.Note == note)
        {
            Editor.Dispose();
            Editor = null;
        }
        note.IsTrashed = true;
        note.TrashedAt = DateTime.Now;
        await SaveNote(note);
        ApplyFilter();
        _toasts.Show("Moved to trash", ToastKind.Info, "Undo", async () => { note.IsTrashed = false; note.TrashedAt = null; await SaveNote(note); ApplyFilter(); });
    }

    [RelayCommand]
    private async Task Restore(Note? note)
    {
        if (note == null) return;
        note.IsTrashed = false;
        note.TrashedAt = null;
        await SaveNote(note);
        ApplyFilter();
        _toasts.Success("Note restored");
    }

    [RelayCommand]
    private async Task DeleteForever(Note? note)
    {
        if (note == null) return;
        if (!await _dialogs.ConfirmAsync("Delete forever?", $"“{note.DisplayTitle}” will be permanently deleted.", "Delete", danger: true)) return;
        if (Editor?.Note == note) { Editor.Dispose(); Editor = null; }
        await _store.DeleteAsync(note);
        _all.Remove(note);
        ApplyFilter();
    }

    [RelayCommand]
    private async Task EmptyTrash()
    {
        var trashed = _all.Where(n => n.IsTrashed).ToList();
        if (trashed.Count == 0) return;
        if (!await _dialogs.ConfirmAsync("Empty trash?", $"{trashed.Count} note(s) will be permanently deleted.", "Empty trash", danger: true)) return;
        await _store.DeleteRangeAsync(trashed);
        foreach (var n in trashed) _all.Remove(n);
        ApplyFilter();
        _toasts.Success("Trash emptied");
    }

    [RelayCommand]
    private async Task Duplicate(Note? note)
    {
        if (note == null) return;
        if (Editor?.Note == note) await Editor.SaveAsync();
        var copy = new Note
        {
            Type = note.Type,
            NotebookId = note.NotebookId,
            Title = string.IsNullOrWhiteSpace(note.Title) ? "" : note.Title + " (copy)",
            Content = note.Content,
            PlainText = note.PlainText,
            Tags = note.Tags,
            Color = note.Color,
            Language = note.Language,
            JournalDate = note.JournalDate,
            Mood = note.Mood,
            SortOrder = note.SortOrder + 0.5
        };
        await _store.AddAsync(copy);
        _all.Add(copy);
        ApplyFilter();
        _toasts.Success("Note duplicated");
    }

    [RelayCommand]
    private async Task MoveToNotebook(object? parameter)
    {
        if (parameter is not object[] { Length: 2 } args || args[0] is not Note note) return;
        var nb = args[1] as Notebook;
        await MoveAsync(note, nb);
    }

    private async Task MoveAsync(Note note, Notebook? nb)
    {
        int? id = nb == null || nb.Id == 0 ? null : nb.Id;
        if (note.NotebookId == id) return;
        note.NotebookId = id;
        if (Editor?.Note != note) await SaveNote(note);
        ApplyFilter();
        _toasts.Success(nb == null || nb.Id == 0 ? "Removed from notebook" : $"Moved to {nb.Name}");
    }

    [RelayCommand]
    private async Task DropOnNotebook(DropEventArgs? args)
    {
        if (args?.Data is not Note note) return;
        if (args.Target is SideFilter { Payload: Notebook nb }) await MoveAsync(note, nb);
        else if (args.Target is SideFilter f && f == AllFilter) await MoveAsync(note, null);
        else if (args.Target is SideFilter f2 && f2 == TrashFilter) await Trash(note);
        else if (args.Target is SideFilter f3 && f3 == ArchiveFilter && !note.IsArchived) await ToggleArchive(note);
    }

    [RelayCommand]
    private async Task Reorder(ReorderEventArgs? args)
    {
        if (args == null) return;
        if (args.Item is Note moved && args.ChangedList)
        {
            moved.IsPinned = ReferenceEquals(args.TargetList, PinnedNotes);
            await _store.UpdateAsync(moved);
        }
        int i = 0;
        foreach (var n in PinnedNotes) n.SortOrder = i++;
        foreach (var n in OtherNotes) n.SortOrder = i++;
        await _store.SaveOrderAsync(PinnedNotes.Concat(OtherNotes), n => n.SortOrder);
        OnPropertyChanged(nameof(HasPinned));
        OnPropertyChanged(nameof(ShowSectionHeaders));
    }

    public async Task ToggleCardItemAsync(Note note, int index)
    {
        var items = Note.ParseItems(note.Content);
        if (index < 0 || index >= items.Count) return;
        items[index].IsChecked = !items[index].IsChecked;
        note.Content = Note.SerializeItems(items);
        if (Editor?.Note == note)
        {
            Editor.Dispose();
            Editor = new NoteEditorViewModel(note, this, _store);
        }
        await SaveNote(note);
    }

    [RelayCommand]
    private async Task NewNotebook()
    {
        var name = await _dialogs.PromptAsync("New notebook", null, "", "e.g. Recipes, Travel, Ideas", "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        var colors = Palettes.Tags.Select(t => t.Key).ToList();
        var nb = new Notebook
        {
            Name = name,
            Color = colors[Notebooks.Count % colors.Count],
            Icon = Glyphs.Book,
            SortOrder = Notebooks.Count == 0 ? 0 : Notebooks.Max(n => n.SortOrder) + 1
        };
        await _store.AddAsync(nb);
        Notebooks.Add(nb);
        RebuildNotebookFilters();
        SelectedFilter = NotebookFilters.First(f => f.Payload == nb);
    }

    [RelayCommand]
    private async Task RenameNotebook(SideFilter? filter)
    {
        if (filter?.Payload is not Notebook nb) return;
        var name = await _dialogs.PromptAsync("Rename notebook", null, nb.Name, "Notebook name", "Rename");
        if (string.IsNullOrWhiteSpace(name)) return;
        nb.Name = name;
        filter.Title = name;
        await _store.UpdateAsync(nb);
        OnPropertyChanged(nameof(HeaderTitle));
    }

    [RelayCommand]
    private async Task SetNotebookColor(object? parameter)
    {
        if (parameter is not object[] { Length: 2 } args || args[0] is not SideFilter { Payload: Notebook nb } filter || args[1] is not string color) return;
        nb.Color = color;
        filter.ColorKey = color;
        await _store.UpdateAsync(nb);
    }

    [RelayCommand]
    private async Task DeleteNotebook(SideFilter? filter)
    {
        if (filter?.Payload is not Notebook nb) return;
        int count = _all.Count(n => n.NotebookId == nb.Id);
        var message = count == 0 ? $"Delete “{nb.Name}”?" : $"Delete “{nb.Name}”? Its {count} note(s) will be kept without a notebook.";
        if (!await _dialogs.ConfirmAsync("Delete notebook", message, "Delete", danger: true)) return;
        await _store.DeleteAsync(nb);
        foreach (var n in _all.Where(n => n.NotebookId == nb.Id)) n.NotebookId = null;
        Notebooks.Remove(nb);
        if (SelectedFilter == filter) SelectedFilter = AllFilter;
        RebuildNotebookFilters();
        ApplyFilter();
    }

    [RelayCommand]
    private async Task ReorderNotebooks(ReorderEventArgs? args)
    {
        int i = 0;
        foreach (var f in NotebookFilters)
            if (f.Payload is Notebook nb) nb.SortOrder = i++;
        var ordered = NotebookFilters.Select(f => (Notebook)f.Payload!).ToList();
        Notebooks.Clear();
        foreach (var nb in ordered) Notebooks.Add(nb);
        NotebookChoices = [new Notebook { Id = 0, Name = "No notebook", Icon = Glyphs.Notes }, .. Notebooks];
        await _store.SaveOrderAsync(ordered, n => n.SortOrder);
    }

    [RelayCommand]
    private void ToggleView() => IsGridView = !IsGridView;

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    [RelayCommand]
    private async Task Export(Note? note)
    {
        if (note == null) return;
        if (Editor?.Note == note) await Editor.SaveAsync();
        var safeName = string.Concat(note.DisplayTitle.Split(Path.GetInvalidFileNameChars())).Trim();
        var dialog = new SaveFileDialog
        {
            Title = "Export note",
            FileName = string.IsNullOrWhiteSpace(safeName) ? "note" : safeName,
            Filter = "Markdown (*.md)|*.md|Text file (*.txt)|*.txt",
            DefaultExt = ".md"
        };
        if (dialog.ShowDialog() != true) return;
        await File.WriteAllTextAsync(dialog.FileName, ToMarkdown(note), Encoding.UTF8);
        _toasts.Success("Note exported (unencrypted copy)");
    }

    [RelayCommand]
    private async Task ExportAll()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder for the exported notes" };
        if (dialog.ShowDialog() != true) return;
        int count = 0;
        foreach (var note in _all.Where(n => !n.IsTrashed))
        {
            var name = string.Concat(note.DisplayTitle.Split(Path.GetInvalidFileNameChars())).Trim();
            if (string.IsNullOrWhiteSpace(name)) name = $"note-{note.Id}";
            var path = Path.Combine(dialog.FolderName, $"{name}.md");
            if (File.Exists(path)) path = Path.Combine(dialog.FolderName, $"{name}-{note.Id}.md");
            await File.WriteAllTextAsync(path, ToMarkdown(note), Encoding.UTF8);
            count++;
        }
        _toasts.Success($"Exported {count} notes as Markdown (unencrypted)");
    }

    public static string ToMarkdown(Note note)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {note.DisplayTitle}").AppendLine();
        switch (note.Type)
        {
            case NoteType.Checklist:
                foreach (var i in note.Items) sb.AppendLine($"{new string(' ', i.Indent * 2)}- [{(i.IsChecked ? "x" : " ")}] {i.Text}");
                break;
            case NoteType.List:
                foreach (var i in note.Items) sb.AppendLine($"{new string(' ', i.Indent * 2)}- {i.Text}");
                break;
            case NoteType.Code:
                sb.AppendLine($"```{note.Language?.ToLowerInvariant().Replace("plain text", "")}").AppendLine(note.Content).AppendLine("```");
                break;
            default:
                sb.AppendLine(note.PlainText);
                break;
        }
        if (note.TagList.Count > 0) sb.AppendLine().AppendLine(string.Join(' ', note.TagList.Select(t => "#" + t)));
        return sb.ToString();
    }

    public void Dispose() => Editor?.Dispose();
}
