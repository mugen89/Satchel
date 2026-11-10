using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Satchel.Behaviors;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Services;

namespace Satchel.ViewModels;

/// <summary>Markdown library: import .md files (with their images), read them beautifully, or write new ones.</summary>
public sealed partial class LibraryViewModel : PageViewModel, IDisposable, Controls.IKeepAlivePage
{
    /// <summary>The rendered document stays on screen while you visit other tabs (it can take a while to lay out).</summary>
    public bool IsDisposed { get; private set; }
    private bool _switching;

    private readonly DataStore _store;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly SettingsService _settings;
    private readonly WebService _web;
    private readonly DispatcherTimer _saveTimer;
    private readonly Dictionary<string, BitmapSource?> _imageCache = new();
    private readonly HashSet<string> _downloading = new();
    private List<DocAsset> _assets = [];
    private bool _loaded;
    private bool _dirty;

    private readonly INavigator _nav;
    private readonly PrivacyService _privacy;

    public LibraryViewModel(INavigator nav, DataStore store, DialogService dialogs, ToastService toasts, SettingsService settings, WebService web, PrivacyService privacy)
    {
        _nav = nav;
        _store = store;
        _dialogs = dialogs;
        _toasts = toasts;
        _settings = settings;
        _web = web;
        _privacy = privacy;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += async (_, _) => await FlushAsync();
        _settings.Changed += OnSettingChanged;
        _privacy.ItemChanged += OnPrivacyChanged;
    }

    /// <summary>A folder or document marked private while private items are hidden disappears right away.</summary>
    private async void OnPrivacyChanged(Entity item)
    {
        if (item is not (LibraryFolder or LibraryDoc) || !_loaded) return;
        if (item.IsPrivate && !_privacy.ShowPrivate)
        {
            if (SelectedDoc != null && (ReferenceEquals(item, SelectedDoc) || (item is LibraryFolder f && IsInside(SelectedDoc.FolderId, f.Id)))) SelectedDoc = null;
            await LoadAsync();
        }
        else if (item is LibraryFolder)
        {
            // Its folders changed along with it (they're other copies of them): reload the folders.
            _folders = await _store.ListAsync<LibraryFolder>(q => q.OrderBy(x => x.Id));
            RebuildFolders();
        }
    }

    /// <summary>Is the folder (or a folder inside it) the given one?</summary>
    private bool IsInside(int? folderId, int ancestor)
    {
        var seen = new HashSet<int>();
        for (var id = folderId; id is int at && seen.Add(at); id = FindFolder(at)?.ParentId)
            if (at == ancestor) return true;
        return false;
    }

    public override string Title => "Library";
    public ObservableCollection<LibraryDoc> Docs { get; } = [];
    public ObservableCollection<LibraryDoc> Filtered { get; } = [];
    public ObservableCollection<TocEntry> Toc { get; } = [];

    /// <summary>The folder pane: All documents, Favorites, then the folder tree.</summary>
    public ObservableCollection<LibraryFolderRow> FolderRows { get; } = [];
    /// <summary>Folders to pick from in "Move to folder" (No folder first).</summary>
    public ObservableCollection<FolderChoice> FolderChoices { get; } = [];
    private List<LibraryFolder> _folders = [];

    [ObservableProperty] private LibraryDoc? _selectedDoc;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private string _editText = "";
    [ObservableProperty] private string _findText = "";
    [ObservableProperty] private bool _isFindOpen;

    public bool IsEmpty => Docs.Count == 0;
    public string CountText => Docs.Count == 1 ? "1 document" : $"{Docs.Count} documents";
    public bool HasSelection => SelectedDoc != null;
    public AppSettings Settings => _settings.Current;

    public static IReadOnlyList<Choice<string>> Themes { get; } =
        [new("app", "Match app"), new("paper", "Paper"), new("sepia", "Sepia"), new("night", "Night")];
    public static IReadOnlyList<Choice<string>> Fonts { get; } =
        [new("serif", "Serif"), new("book", "Book"), new("sans", "Sans"), new("mono", "Mono")];

    /// <summary>Raised when the document or reading style changed and the view must re-render.</summary>
    public event Action? RenderRequested;
    /// <summary>Raised to ask the view to search the rendered document.</summary>
    public event Action<string, bool>? FindRequested;

    private void OnSettingChanged(string name)
    {
        if (name.StartsWith("Reader", StringComparison.Ordinal)) RenderRequested?.Invoke();
        if (name == nameof(AppSettings.ReaderShowToc)) OnPropertyChanged(nameof(Settings));
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_loaded) await LoadAsync();
        // A document added elsewhere (e.g. Sheets → Copy to the Library) since the list was loaded.
        if (parameter is int newId && Docs.All(d => d.Id != newId)) await LoadAsync();
        if (parameter is int id && Docs.FirstOrDefault(d => d.Id == id) is { } doc)
        {
            // Opened from elsewhere (search, the dashboard, Sheets): show its folder so it's in the list too.
            if (!Filtered.Contains(doc))
            {
                SearchText = "";
                CurrentRow = FolderRows.FirstOrDefault(r => r.Kind == FolderRowKind.Folder && r.Folder!.Id == doc.FolderId) ?? FolderRows.FirstOrDefault();
            }
            SelectedDoc = doc;
        }
        else if (parameter is "import") await Import();
        else if (parameter is "new") await NewDoc();
    }

    /// <summary>Reads the documents and folders again (used by the debug tests after adding documents directly).</summary>
    public Task ReloadAsync() => LoadAsync();

    private async Task LoadAsync()
    {
        var docs = await _store.ListAsync<LibraryDoc>(q => q.OrderByDescending(d => d.LastOpenedAt ?? d.CreatedAt));
        _folders = await _store.ListAsync<LibraryFolder>(q => q.OrderBy(f => f.Id));
        await MoveCollectionsIntoFolders(docs);
        var current = SelectedDoc?.Id;
        Docs.Clear();
        foreach (var d in docs) Docs.Add(d);
        _loaded = true;
        RebuildFolders();
        ApplyFilter();
        // A reload brings new copies of the documents: keep the open one open (a hidden one closes).
        var keep = current is int id ? Docs.FirstOrDefault(d => d.Id == id) : null;
        if (keep == null || !ReferenceEquals(keep, SelectedDoc)) SelectedDoc = keep ?? Filtered.FirstOrDefault() ?? Docs.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(CountText));
    }

    /// <summary>
    /// Before folders, documents had a "collection" name. Each becomes a folder (once) — also for documents added
    /// that way later, e.g. by Sheets' "Copy to the Library" or the Markdown guide.
    /// </summary>
    private async Task MoveCollectionsIntoFolders(List<LibraryDoc> docs)
    {
        var legacy = docs.Where(d => d.FolderId == null && !string.IsNullOrWhiteSpace(d.Collection)).ToList();
        if (legacy.Count == 0) return;
        foreach (var group in legacy.GroupBy(d => d.Collection.Trim(), StringComparer.CurrentCultureIgnoreCase))
        {
            var folder = _folders.FirstOrDefault(f => f.ParentId == null && string.Equals(f.Name, group.Key, StringComparison.CurrentCultureIgnoreCase));
            if (folder == null)
            {
                folder = new LibraryFolder { Name = group.Key };
                await _store.AddAsync(folder);
                _folders.Add(folder);
            }
            foreach (var d in group) { d.FolderId = folder.Id; d.Collection = ""; }
        }
        await SaveFoldersOf(legacy);
    }

    /// <summary>Saves only where documents are (not their text, which can be large).</summary>
    private Task SaveFoldersOf(IEnumerable<LibraryDoc> docs) => _store.RunAsync(db =>
    {
        foreach (var d in docs)
        {
            db.Attach(d);
            db.Entry(d).Property(nameof(LibraryDoc.FolderId)).IsModified = true;
            db.Entry(d).Property(nameof(LibraryDoc.Collection)).IsModified = true;
        }
        db.SaveChanges();
        foreach (var d in docs) db.Entry(d).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
    });

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private LibraryFolderRow? _currentRow;

    /// <summary>Which row of the folder pane is chosen (All documents by default).</summary>
    public LibraryFolderRow? CurrentRow
    {
        get => _currentRow;
        set
        {
            if (ReferenceEquals(_currentRow, value)) return;
            if (_currentRow != null) _currentRow.IsSelected = false;
            _currentRow = value;
            if (value != null) value.IsSelected = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DropTitle));
            ApplyFilter();
        }
    }

    /// <summary>The folder chosen in the pane (null for All documents / Favorites).</summary>
    public LibraryFolder? CurrentFolder => CurrentRow?.Folder;

    /// <summary>Above the document list: which folder (or search) it shows.</summary>
    public string ListTitle => SearchText.Trim().Length > 0 ? "Search results" : CurrentRow?.Name ?? "All documents";

    private void ApplyFilter()
    {
        var q = SearchText.Trim();
        IEnumerable<LibraryDoc> items = Docs;
        // A search looks in every folder; otherwise the chosen folder (or favorites) shows.
        if (q.Length > 0)
            items = items.Where(d => d.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || d.Markdown.Contains(q, StringComparison.OrdinalIgnoreCase));
        else if (CurrentRow?.Kind == FolderRowKind.Favorites) items = items.Where(d => d.IsFavorite);
        else if (CurrentFolder is { } folder) items = items.Where(d => d.FolderId == folder.Id);
        var list = items.OrderByDescending(d => d.IsFavorite).ThenByDescending(d => d.LastOpenedAt ?? d.CreatedAt).ToList();
        Filtered.Clear();
        foreach (var d in list) Filtered.Add(d);
        OnPropertyChanged(nameof(ListTitle));
        OnPropertyChanged(nameof(IsListEmpty));
        OnPropertyChanged(nameof(EmptyListText));
    }

    public bool IsListEmpty => Filtered.Count == 0 && Docs.Count > 0;

    public string EmptyListText =>
        SearchText.Trim().Length > 0 ? $"Nothing matches “{SearchText.Trim()}”."
        : CurrentRow?.Kind == FolderRowKind.Favorites ? "No favorites yet — star a document from its ⋯ menu, or drag one onto Favorites."
        : "This folder is empty. Drag documents onto it, or use ⋯ → Move to folder while reading one.";

    // ----- folders -----

    /// <summary>Rebuilds the folder pane (keeps which row is chosen and which folders are folded).</summary>
    private void RebuildFolders()
    {
        var collapsed = Settings.LibraryCollapsedFolders.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out var i) ? i : -1).ToHashSet();
        var currentKind = CurrentRow?.Kind ?? FolderRowKind.All;
        var currentId = CurrentRow?.Folder?.Id;
        var rows = new List<LibraryFolderRow>
        {
            new(FolderRowKind.All, null, "All documents", 0, false) { Count = Docs.Count },
            new(FolderRowKind.Favorites, null, "Favorites", 0, false) { Count = Docs.Count(d => d.IsFavorite) },
        };
        var known = _folders.Select(f => f.Id).ToHashSet();
        var byParent = _folders.GroupBy(f => f.ParentId is int p && known.Contains(p) ? p : (int?)null).ToDictionary(g => g.Key ?? 0, g => g.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
        var choices = new List<FolderChoice> { new(null, "No folder") };
        void Add(int parentKey, int depth, string path)
        {
            if (!byParent.TryGetValue(parentKey, out var children)) return;
            foreach (var f in children)
            {
                bool hasChildren = byParent.ContainsKey(f.Id);
                bool expanded = !collapsed.Contains(f.Id);
                var row = new LibraryFolderRow(FolderRowKind.Folder, f, f.Name, depth, hasChildren) { IsExpanded = expanded, Count = Docs.Count(d => d.FolderId == f.Id) };
                rows.Add(row);
                var label = path.Length == 0 ? f.Name : path + " › " + f.Name;
                choices.Add(new FolderChoice(f.Id, label));
                if (expanded) Add(f.Id, depth + 1, label);
                else AddChoices(f.Id, label);
            }
        }
        void AddChoices(int parentKey, string path)
        {
            if (!byParent.TryGetValue(parentKey, out var children)) return;
            foreach (var f in children)
            {
                var label = path + " › " + f.Name;
                choices.Add(new FolderChoice(f.Id, label));
                AddChoices(f.Id, label);
            }
        }
        Add(0, 0, "");
        choices.Add(new FolderChoice(-1, "New folder…"));
        FolderRows.Clear();
        foreach (var r in rows) FolderRows.Add(r);
        FolderChoices.Clear();
        foreach (var c in choices) FolderChoices.Add(c);
        // The same row stays chosen. Inside a folded folder: that folder. Hidden or deleted: All documents.
        var restore = rows.FirstOrDefault(r => r.Kind == currentKind && r.Folder?.Id == currentId);
        var seen = new HashSet<int>();
        for (var p = currentId is int cid ? FindFolder(cid)?.ParentId : null; restore == null && p is int pid && seen.Add(pid); p = FindFolder(pid)?.ParentId)
            restore = rows.FirstOrDefault(r => r.Folder?.Id == pid);
        CurrentRow = restore ?? rows[0];
    }

    private void RefreshCounts()
    {
        foreach (var row in FolderRows)
            row.Count = row.Kind switch
            {
                FolderRowKind.All => Docs.Count,
                FolderRowKind.Favorites => Docs.Count(d => d.IsFavorite),
                _ => Docs.Count(d => d.FolderId == row.Folder!.Id)
            };
    }

    [RelayCommand]
    private void SelectFolder(LibraryFolderRow? row)
    {
        if (row == null) return;
        SearchText = "";
        CurrentRow = row;
        // The first document of the folder opens (if the open one isn't in it).
        if (SelectedDoc == null || !Filtered.Contains(SelectedDoc)) SelectedDoc = Filtered.FirstOrDefault() ?? SelectedDoc;
    }

    [RelayCommand]
    private void ToggleFolder(LibraryFolderRow? row)
    {
        if (row?.Folder is not { } folder || !row.HasChildren) return;
        var collapsed = Settings.LibraryCollapsedFolders.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (!collapsed.Remove(folder.Id.ToString())) collapsed.Add(folder.Id.ToString());
        Settings.LibraryCollapsedFolders = string.Join(",", collapsed);
        RebuildFolders();
    }

    /// <summary>A new folder — inside the chosen folder when there is one, else at the top.</summary>
    [RelayCommand]
    private async Task NewFolder(LibraryFolderRow? inside)
    {
        var parent = inside?.Folder ?? (inside == null ? CurrentFolder : null);
        var name = await _dialogs.PromptAsync("New folder", parent != null ? $"Inside “{parent.Name}”." : null, "", "Folder name", "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        var folder = await CreateFolderAsync(name, parent);
        CurrentRow = FolderRows.FirstOrDefault(r => r.Folder?.Id == folder.Id) ?? CurrentRow;
    }

    /// <summary>Makes a folder (a folder inside a private one is private too).</summary>
    public async Task<LibraryFolder> CreateFolderAsync(string name, LibraryFolder? parent)
    {
        var folder = new LibraryFolder { Name = name.Trim(), ParentId = parent?.Id, IsPrivate = parent?.IsPrivate ?? false };
        await _store.AddAsync(folder);
        _folders.Add(folder);
        if (parent != null) Expand(parent.Id);
        RebuildFolders();
        return folder;
    }

    private void Expand(int folderId)
    {
        var collapsed = Settings.LibraryCollapsedFolders.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        if (collapsed.Remove(folderId.ToString())) Settings.LibraryCollapsedFolders = string.Join(",", collapsed);
    }

    [RelayCommand]
    private async Task RenameFolder(LibraryFolderRow? row)
    {
        if (row?.Folder is not { } folder) return;
        var name = await _dialogs.PromptAsync("Rename folder", null, folder.Name, "Folder name", "Rename");
        if (string.IsNullOrWhiteSpace(name)) return;
        folder.Name = name.Trim();
        await _store.UpdateAsync(folder);
        RebuildFolders();
    }

    /// <summary>Deletes a folder; its documents and subfolders move up to where the folder was.</summary>
    [RelayCommand]
    private async Task DeleteFolder(LibraryFolderRow? row)
    {
        if (row?.Folder is not { } folder) return;
        var docs = Docs.Where(d => d.FolderId == folder.Id).ToList();
        var subfolders = _folders.Where(f => f.ParentId == folder.Id).ToList();
        var parent = _folders.FirstOrDefault(f => f.Id == folder.ParentId);
        if ((docs.Count > 0 || subfolders.Count > 0) && _settings.Current.ConfirmDeletes)
        {
            var where = parent != null ? $"“{parent.Name}”" : "the top of the Library";
            var what = string.Join(" and ", new[] { docs.Count > 0 ? (docs.Count == 1 ? "its document" : $"its {docs.Count} documents") : null, subfolders.Count > 0 ? (subfolders.Count == 1 ? "its folder" : $"its {subfolders.Count} folders") : null }.Where(s => s != null));
            if (!await _dialogs.ConfirmAsync($"Delete the folder “{folder.Name}”?", $"Nothing is deleted with it: {what} move to {where}.", "Delete folder")) return;
        }
        foreach (var d in docs) d.FolderId = folder.ParentId;
        await SaveFoldersOf(docs);
        foreach (var f in subfolders) { f.ParentId = folder.ParentId; await _store.UpdateAsync(f); }
        await _store.DeleteAsync(folder);
        _folders.Remove(folder);
        RebuildFolders();
        ApplyFilter();
        _toasts.Show($"Folder “{folder.Name}” deleted", ToastKind.Info, "Undo", async () =>
        {
            folder.Id = 0;
            await _store.AddAsync(folder);
            _folders.Add(folder);
            foreach (var d in docs) d.FolderId = folder.Id;
            await SaveFoldersOf(docs);
            foreach (var f in subfolders) { f.ParentId = folder.Id; await _store.UpdateAsync(f); }
            RebuildFolders();
            ApplyFilter();
        });
    }

    /// <summary>Moves a document into a folder (null = out of any folder). Used by drag and drop and the menu.</summary>
    public async Task MoveToFolderAsync(LibraryDoc doc, int? folderId)
    {
        if (doc.FolderId == folderId) return;
        doc.FolderId = folderId;
        doc.Collection = "";
        // A document moved into a private folder is hidden with it — say so instead of letting it vanish silently.
        await SaveFoldersOf([doc]);
        RefreshCounts();
        ApplyFilter();
        var name = folderId is int id ? _folders.FirstOrDefault(f => f.Id == id)?.Name : null;
        _toasts.Show(name != null ? $"Moved to “{name}”" : "Moved out of its folder", ToastKind.Success, durationMs: 1800);
    }

    [RelayCommand]
    private Task MoveSelectedToFolder(FolderChoice? choice) =>
        SelectedDoc != null && choice != null ? MoveToFolderAsync(SelectedDoc, choice.FolderId) : Task.CompletedTask;

    /// <summary>Moves a folder into another (or to the top). A folder can't go inside itself or its own subfolders.</summary>
    public async Task MoveFolderAsync(LibraryFolder folder, int? parentId)
    {
        if (folder.ParentId == parentId || folder.Id == parentId) return;
        for (var p = parentId; p is int id; p = _folders.FirstOrDefault(f => f.Id == id)?.ParentId)
            if (id == folder.Id) { _toasts.Info("A folder can't go inside one of its own folders."); return; }
        folder.ParentId = parentId;
        await _store.UpdateAsync(folder);
        // Moved into a private folder: it's private too, like a folder made there.
        if (parentId is int target && _folders.FirstOrDefault(f => f.Id == target) is { IsPrivate: true } && !folder.IsPrivate)
            await _privacy.SetAsync(folder, true, quiet: true);
        if (parentId is int expand) Expand(expand);
        RebuildFolders();
    }

    public LibraryDoc? FindDoc(int id) => Docs.FirstOrDefault(d => d.Id == id);
    public LibraryFolder? FindFolder(int id) => _folders.FirstOrDefault(f => f.Id == id);

    async partial void OnSelectedDocChanged(LibraryDoc? oldValue, LibraryDoc? newValue)
    {
        // The document you leave keeps its last edits (SelectedDoc is already the new one here).
        if (oldValue != null && _dirty) await SaveAsync(oldValue);
        // Leaving edit mode here mustn't render: the new document renders once its pictures are loaded.
        _switching = true;
        IsEditing = false;
        _switching = false;
        IsFindOpen = false;
        _imageCache.Clear();
        _assets = [];
        OnPropertyChanged(nameof(HasSelection));
        if (newValue == null)
        {
            Toc.Clear();
            RenderRequested?.Invoke();
            return;
        }
        var id = newValue.Id;
        _assets = await _store.ListAsync<DocAsset>(q => q.Where(a => a.DocId == id));
        // Another document was picked while the pictures loaded: that one renders instead.
        if (!ReferenceEquals(SelectedDoc, newValue)) return;
        newValue.LastOpenedAt = DateTime.Now;
        _dirty = true;
        ScheduleSave();
        // The editor only gets the text when you start editing: a text box holding a whole book (with spell checking)
        // made opening the tab take many seconds. (A new document is already in the editor — leave it.)
        if (!IsEditing) EditText = "";
        RenderRequested?.Invoke();
    }

    partial void OnEditTextChanged(string value)
    {
        if (SelectedDoc == null || !IsEditing || SelectedDoc.Markdown == value) return;
        SelectedDoc.Markdown = value;
        var title = LibraryDoc.TitleFrom(value, SelectedDoc.Title);
        if (title.Length > 0) SelectedDoc.Title = title;
        _dirty = true;
        ScheduleSave();
    }

    partial void OnIsEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(SpellCheckInEditor));
        if (value) return;
        EditText = "";
        if (!_switching) RenderRequested?.Invoke();
    }

    /// <summary>Spell checking runs over the whole text on every change: only for documents of a sensible size.</summary>
    public bool SpellCheckInEditor => IsEditing && EditText.Length < 150_000;

    /// <summary>How long the live preview waits after you stop typing — longer for big documents, which take longer to lay out.</summary>
    public TimeSpan PreviewDelay => EditText.Length switch
    {
        < 30_000 => TimeSpan.FromMilliseconds(280),
        < 200_000 => TimeSpan.FromMilliseconds(700),
        _ => TimeSpan.FromMilliseconds(1400)
    };

    /// <summary>The view reports the reading position (0..1) as the reader scrolls.</summary>
    public void ReportProgress(double progress)
    {
        if (SelectedDoc == null || IsEditing) return;
        progress = Math.Round(Math.Clamp(progress, 0, 1), 3);
        if (Math.Abs(SelectedDoc.Progress - progress) < 0.004) return;
        SelectedDoc.Progress = progress;
        _dirty = true;
        ScheduleSave();
    }

    public void SetToc(IEnumerable<TocEntry> entries)
    {
        Toc.Clear();
        foreach (var e in entries) Toc.Add(e);
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public override Task FlushAsync() => SaveAsync(SelectedDoc);

    /// <summary>Saves pending changes (edits, reading position) of the given document.</summary>
    private async Task SaveAsync(LibraryDoc? doc)
    {
        _saveTimer.Stop();
        if (!_dirty || doc == null) return;
        _dirty = false;
        doc.UpdatedAt = DateTime.Now;
        await _store.UpdateAsync(doc);
    }

    public override void OnNewShortcut() => _ = NewDoc();

    // ----- images -----

    /// <summary>Resolves an image reference: stored asset, data URI, or (online, async) web image.</summary>
    public BitmapSource? ResolveImage(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (_imageCache.TryGetValue(url, out var cached)) return cached;
        BitmapSource? result = null;
        if (url.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
        {
            var comma = url.IndexOf(',');
            if (comma > 0)
            {
                try { result = ImageService.ToBitmap(Convert.FromBase64String(url[(comma + 1)..])); }
                catch (FormatException) { }
            }
        }
        else if (_assets.FirstOrDefault(a => string.Equals(a.Name, url, StringComparison.OrdinalIgnoreCase)) is { Data: { } data })
        {
            result = ImageService.ToBitmap(data);
        }
        else if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (_web.IsAvailable && _downloading.Add(url)) _ = DownloadImageAsync(url);
            return null;
        }
        _imageCache[url] = result;
        return result;
    }

    private async Task DownloadImageAsync(string url)
    {
        var bytes = await _web.GetBytesAsync(url);
        var bitmap = bytes == null ? null : await Task.Run(() => ImageService.ToBitmap(bytes));
        _imageCache[url] = bitmap;
        if (bitmap != null) RenderRequested?.Invoke();
    }

    // ----- commands -----

    [RelayCommand]
    private void Open(LibraryDoc? doc)
    {
        if (doc != null) SelectedDoc = doc;
    }

    [RelayCommand]
    private void ToggleEdit()
    {
        if (SelectedDoc == null) return;
        if (!IsEditing) EditText = SelectedDoc.Markdown;
        IsEditing = !IsEditing;
    }

    [RelayCommand]
    private void ToggleToc() => Settings.ReaderShowToc = !Settings.ReaderShowToc;

    /// <summary>Page view: the document laid out on pages like a PDF.</summary>
    public bool IsPageView
    {
        get => Settings.ReaderLayout == "pages";
        set
        {
            Settings.ReaderLayout = value ? "pages" : "scroll";
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void ToggleLayout() => IsPageView = !IsPageView;

    /// <summary>The drop overlay says where dropped files go.</summary>
    public string DropTitle => CurrentFolder is { } f ? $"Drop to add to “{f.Name}”" : "Drop to add to your Library";

    [RelayCommand]
    private async Task NewDoc()
    {
        var doc = new LibraryDoc
        {
            Title = "Untitled document",
            Markdown = "# Untitled document\n\nStart writing here. Use **bold**, *italic*, `code`, lists and tables — it's Markdown.\n",
            FolderId = CurrentFolder?.Id,
            LastOpenedAt = DateTime.Now
        };
        await _store.AddAsync(doc);
        Docs.Insert(0, doc);
        RefreshCounts();
        ApplyFilter();
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(CountText));
        SelectedDoc = doc;
        EditText = doc.Markdown;
        IsEditing = true;
    }

    [RelayCommand]
    private async Task Import()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import into the Library",
            Filter = Services.Import.DocumentImporter.DialogFilter,
            Multiselect = true
        };
        if (dialog.ShowDialog() != true) return;
        await ImportFilesAsync(dialog.FileNames);
    }

    /// <summary>
    /// Brings files into the Library as Markdown: .md and .txt (any encoding), Word (.docx, .doc), rich text and
    /// spreadsheets (as tables). Pictures in Word files and next to Markdown files come along.
    /// </summary>
    public async Task ImportFilesAsync(IEnumerable<string> paths)
    {
        LibraryDoc? last = null;
        int count = 0;
        var notes = new List<string>();
        string? spreadsheet = null;
        foreach (var path in paths)
        {
            if (!Services.Import.DocumentImporter.IsSupported(path)) continue;
            try
            {
                var imported = await Task.Run(() => Services.Import.DocumentImporter.Import(path));
                var doc = new LibraryDoc
                {
                    Title = imported.Title,
                    Markdown = imported.Markdown,
                    FileName = imported.FileName,
                    FolderId = CurrentFolder?.Id,
                    LastOpenedAt = DateTime.Now
                };
                await _store.AddAsync(doc);
                var assets = imported.Images.Select(i => new DocAsset { DocId = doc.Id, Name = i.Name, Data = i.Data }).ToList();
                if (assets.Count > 0) await _store.AddRangeAsync(assets);
                Docs.Insert(0, doc);
                last = doc;
                count++;
                notes.AddRange(imported.Notes);
                if (imported.IsSpreadsheet) spreadsheet = path;
            }
            catch (Exception ex)
            {
                App.Log("Library import", ex);
                _toasts.Warning($"Couldn't import {Path.GetFileName(path)}: {(ex is InvalidDataException ? ex.Message : "the file couldn't be read.")}");
            }
        }
        if (count == 0) return;
        RefreshCounts();
        ApplyFilter();
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(CountText));
        SelectedDoc = last;
        var message = count == 1 ? $"“{last!.Title}” is in your Library" : $"{count} documents imported";
        if (notes.Count > 0) message += " — " + string.Join(" ", notes.Distinct().Take(3));
        if (spreadsheet != null && count == 1)
        {
            // A spreadsheet came in as tables; it can also open as a real spreadsheet.
            var path = spreadsheet;
            _toasts.Show(message + (notes.Count > 0 ? "" : " (as tables)"), ToastKind.Success, "Open in Sheets", () => _nav.NavigateTo("Sheets", new SheetImportRequest(path)), durationMs: 8000);
        }
        else _toasts.Show(message, notes.Count > 0 ? ToastKind.Info : ToastKind.Success, durationMs: notes.Count > 0 ? 7000 : 2600);
    }

    [RelayCommand]
    private async Task LoadGuide()
    {
        var guides = await FolderNamedAsync("Guides");
        var doc = new LibraryDoc { Title = "Markdown guide", Markdown = SampleDocs.Guide, FolderId = guides.Id, FileName = "markdown-guide.md", LastOpenedAt = DateTime.Now };
        await _store.AddAsync(doc);
        Docs.Insert(0, doc);
        RebuildFolders();
        ApplyFilter();
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(CountText));
        SelectedDoc = doc;
    }

    [RelayCommand]
    private async Task ToggleFavorite(LibraryDoc? doc)
    {
        doc ??= SelectedDoc;
        if (doc == null) return;
        doc.IsFavorite = !doc.IsFavorite;
        await _store.UpdateAsync(doc);
        RefreshCounts();
        ApplyFilter();
    }

    [RelayCommand]
    private async Task Rename()
    {
        if (SelectedDoc == null) return;
        var name = await _dialogs.PromptAsync("Rename document", null, SelectedDoc.Title, "Title", "Rename");
        if (string.IsNullOrWhiteSpace(name)) return;
        SelectedDoc.Title = name.Trim();
        await _store.UpdateAsync(SelectedDoc);
    }

    /// <summary>"Move to folder → New folder…": makes a folder (inside the document's own folder) and moves the document into it.</summary>
    public async Task MoveToNewFolderAsync(LibraryDoc doc)
    {
        var parent = doc.FolderId is int id ? FindFolder(id) : null;
        var name = await _dialogs.PromptAsync("New folder", parent != null ? $"Inside “{parent.Name}”." : "Group documents into folders, e.g. “Recipes”, “Work”, “Guides”.", "", "Folder name", "Create and move");
        if (string.IsNullOrWhiteSpace(name)) return;
        var folder = await CreateFolderAsync(name, parent);
        await MoveToFolderAsync(doc, folder.Id);
    }

    /// <summary>A top-level folder with this name (made when it doesn't exist yet).</summary>
    private async Task<LibraryFolder> FolderNamedAsync(string name)
    {
        if (_folders.FirstOrDefault(f => f.ParentId == null && string.Equals(f.Name, name, StringComparison.CurrentCultureIgnoreCase)) is { } existing) return existing;
        var folder = new LibraryFolder { Name = name };
        await _store.AddAsync(folder);
        _folders.Add(folder);
        return folder;
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (SelectedDoc is not { } doc) return;
        if (_settings.Current.ConfirmDeletes && !await _dialogs.ConfirmAsync("Delete document?", $"“{doc.Title}” will be removed from your library.", "Delete", danger: true)) return;
        var assets = _assets.ToList();
        await _store.DeleteAsync(doc);
        Docs.Remove(doc);
        ApplyFilter();
        RefreshCounts();
        OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(CountText));
        SelectedDoc = Filtered.FirstOrDefault();
        _toasts.Show("Document deleted", ToastKind.Info, "Undo", async () =>
        {
            doc.Id = 0;
            await _store.AddAsync(doc);
            foreach (var a in assets) { a.Id = 0; a.DocId = doc.Id; }
            if (assets.Count > 0) await _store.AddRangeAsync(assets);
            Docs.Insert(0, doc);
            ApplyFilter();
            RefreshCounts();
            OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(CountText));
            SelectedDoc = doc;
        });
    }

    [RelayCommand]
    private void Export()
    {
        if (SelectedDoc == null) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export Markdown",
            Filter = "Markdown|*.md|Text|*.txt",
            FileName = SafeFileName(string.IsNullOrWhiteSpace(SelectedDoc.FileName) ? SelectedDoc.Title + ".md" : SelectedDoc.FileName)
        };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, SelectedDoc.Markdown, new UTF8Encoding(false));
        _toasts.Success("Exported");
    }

    private static string SafeFileName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    [RelayCommand]
    private void OpenFind()
    {
        IsFindOpen = true;
        FindRequested?.Invoke("", false);
    }

    [RelayCommand]
    private void FindNext() => FindRequested?.Invoke(FindText, false);

    [RelayCommand]
    private void FindPrevious() => FindRequested?.Invoke(FindText, true);

    [RelayCommand]
    private void CloseFind()
    {
        IsFindOpen = false;
        FindText = "";
    }

    public override void OnSearchShortcut()
    {
        if (SelectedDoc != null && !IsEditing) OpenFind();
        else base.OnSearchShortcut();
    }

    [RelayCommand]
    private void AdjustFont(string? delta)
    {
        var d = double.TryParse(delta, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
        Settings.ReaderFontSize = Math.Clamp(Settings.ReaderFontSize + d, 12, 30);
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    public void Dispose()
    {
        IsDisposed = true;
        _saveTimer.Stop();
        _settings.Changed -= OnSettingChanged;
        _privacy.ItemChanged -= OnPrivacyChanged;
    }
}

public enum FolderRowKind { All, Favorites, Folder }

/// <summary>A row of the Library's folder pane: All documents, Favorites or a folder (indented by its depth).</summary>
public sealed partial class LibraryFolderRow(FolderRowKind kind, LibraryFolder? folder, string name, int depth, bool hasChildren) : ObservableObject
{
    public FolderRowKind Kind { get; } = kind;
    public LibraryFolder? Folder { get; } = folder;
    public string Name { get; } = name;
    public int Depth { get; } = depth;
    public bool HasChildren { get; } = hasChildren;
    public bool IsFolder => Kind == FolderRowKind.Folder;
    public bool IsPrivate => Folder?.IsPrivate == true;
    /// <summary>Left margin of the row's content: one step per level.</summary>
    public System.Windows.Thickness Indent => new(Depth * 14, 0, 0, 0);
    public string Glyph => Kind switch
    {
        FolderRowKind.All => Glyphs.Library,
        FolderRowKind.Favorites => Glyphs.Star,
        _ => HasChildren && IsExpanded ? Glyphs.FolderOpen : Glyphs.Folder
    };
    public string ChevronGlyph => IsExpanded ? Glyphs.ChevronDown : Glyphs.ChevronRight;
    public string PrivacyHeader => IsPrivate ? "Remove private flag" : "Mark private";

    [ObservableProperty][NotifyPropertyChangedFor(nameof(Glyph), nameof(ChevronGlyph))] private bool _isExpanded = true;
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isSelected;
    /// <summary>Something is being dragged over the row and can be dropped into it.</summary>
    [ObservableProperty] private bool _isDropTarget;
}

/// <summary>An entry of the "Move to folder" menu (<see cref="FolderId"/> null = out of any folder, -1 = a new folder).</summary>
public sealed record FolderChoice(int? FolderId, string Label);

public static class SampleDocs
{
    public const string Guide = """
# Welcome to your Library

Drop files anywhere on this page, or use **Import**: **Markdown** and **text**, **Word** documents (`.docx`, `.doc`) and rich text
are turned into Markdown, and **Excel** or **CSV** files into tables. Everything is stored *inside* Satchel's encrypted database —
together with the pictures — so your whole library travels with the drive.

> Tip: open the **Aa** menu to switch between the *Paper*, *Sepia* and *Night* themes, pick a font and set the line width.

## What Markdown can do

You can write **bold**, *italic*, ~~strikethrough~~, ==highlighted== and `inline code`. Links look like [this](https://commonmark.org/help/).

### Lists

1. Numbered lists
2. With as many items as you need
   - and nested bullets
   - inside them

### Task lists

- [x] Import my notes
- [x] Pick a reading theme
- [ ] Write a document from scratch

### Tables

| Feature        | Shortcut     | Notes                      |
|:---------------|:------------:|---------------------------:|
| Find in text   | Ctrl+F       | Enter jumps to the next hit |
| New document   | Ctrl+N       | Opens straight in the editor |
| Edit / read    | the pencil   | Live preview while you type |

### Code

```csharp
// Syntax colouring works for most languages
var greeting = "Hello from Satchel";
for (int i = 0; i < 3; i++) Console.WriteLine(greeting);
```

```python
def fibonacci(n):
    # the classic
    a, b = 0, 1
    for _ in range(n):
        a, b = b, a + b
    return a
```

---

## Reading comfort

Your position in every document is remembered. The table of contents on the right follows the headings — click one to jump there.
Footnotes are supported too.[^1]

[^1]: Like this one, collected at the end of the document.
""";
}
