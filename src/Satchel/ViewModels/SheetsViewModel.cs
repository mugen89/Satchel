using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Services;
using Satchel.Services.Sheets;

namespace Satchel.ViewModels;

/// <summary>Open (and import) this file in Sheets — e.g. from the Library's "Open in Sheets".</summary>
public sealed record SheetImportRequest(string Path);

public sealed record SheetTab(int Index, string Name, bool IsActive);

public sealed record FormatChoice(string Label, string? Code, string Sample);

/// <summary>Sheets: spreadsheets with formulas, kept in the encrypted database; import and export Excel and CSV.</summary>
public sealed partial class SheetsViewModel : PageViewModel, IDisposable, Controls.IKeepAlivePage
{
    /// <summary>The grid stays as it was (scroll position, selection) while you visit other tabs.</summary>
    public bool IsDisposed { get; private set; }

    private readonly INavigator _nav;
    private readonly DataStore _store;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _saveTimer;
    private bool _loaded, _dirty;

    public SheetsViewModel(INavigator nav, DataStore store, DialogService dialogs, ToastService toasts, SettingsService settings)
    {
        _nav = nav;
        _store = store;
        _dialogs = dialogs;
        _toasts = toasts;
        _settings = settings;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _saveTimer.Tick += async (_, _) => await FlushAsync();
    }

    public override string Title => "Sheets";
    public ObservableCollection<Spreadsheet> Items { get; } = [];
    public ObservableCollection<Spreadsheet> Filtered { get; } = [];
    public ObservableCollection<SheetTab> Tabs { get; } = [];

    [ObservableProperty] private Spreadsheet? _selected;
    [ObservableProperty] private SheetDocument? _document;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _formulaText = "";

    public bool IsEmpty => Items.Count == 0;
    public bool HasDocument => Document != null;
    public string CountText => Items.Count == 1 ? "1 spreadsheet" : $"{Items.Count} spreadsheets";
    public IReadOnlyList<FormatChoice> Formats => CellFormat.Presets.Select(p => new FormatChoice(p.Label, p.Code, p.Sample)).ToList();
    public bool IsFrozen => Document?.Sheet.FrozenRows > 0;

    /// <summary>The formula bar is being typed into: selection changes shouldn't overwrite it.</summary>
    public bool FormulaBarEditing { get; set; }

    /// <summary>Asks the view to put the keyboard back in the grid.</summary>
    public event Action? FocusGridRequested;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_loaded) await LoadAsync();
        switch (parameter)
        {
            case int id when Items.FirstOrDefault(s => s.Id == id) is { } item:
                Selected = item;
                break;
            case SheetImportRequest request:
                await ImportFilesAsync([request.Path]);
                break;
            case "new":
                await New();
                break;
            case "import":
                await Import();
                break;
        }
        FocusGridRequested?.Invoke();
    }

    private async Task LoadAsync()
    {
        var list = await _store.ListAsync<Spreadsheet>(q => q.OrderByDescending(s => s.LastOpenedAt ?? s.UpdatedAt));
        Items.Clear();
        foreach (var s in list) Items.Add(s);
        _loaded = true;
        ApplyFilter();
        Selected ??= Filtered.FirstOrDefault();
        Counts();
    }

    private void Counts()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountText));
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var q = SearchText.Trim();
        var list = Items.Where(s => q.Length == 0 || s.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || s.FileName.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(s => s.IsFavorite).ThenByDescending(s => s.LastOpenedAt ?? s.UpdatedAt).ToList();
        Filtered.Clear();
        foreach (var s in list) Filtered.Add(s);
    }

    async partial void OnSelectedChanged(Spreadsheet? oldValue, Spreadsheet? newValue)
    {
        if (oldValue != null && _dirty) await SaveAsync(oldValue, Document);
        if (Document != null)
        {
            Document.Changed -= OnDocumentChanged;
            Document.SelectionChanged -= OnDocumentSelection;
            Document.PropertyChanged -= OnDocumentProperty;
        }
        if (newValue == null)
        {
            Document = null;
            Tabs.Clear();
            OnPropertyChanged(nameof(HasDocument));
            return;
        }
        SheetDocument doc;
        try
        {
            var data = Workbook.ParseJson(newValue.Data);
            var book = await Task.Run(() => Workbook.FromData(data));
            doc = new SheetDocument(book, data.Active);
        }
        catch (Exception ex)
        {
            App.Log("Sheets open", ex);
            _toasts.Warning("This spreadsheet couldn't be opened.");
            doc = new SheetDocument(Workbook.New());
        }
        doc.Changed += OnDocumentChanged;
        doc.SelectionChanged += OnDocumentSelection;
        doc.PropertyChanged += OnDocumentProperty;
        Document = doc;
        OnPropertyChanged(nameof(HasDocument));
        RebuildTabs();
        FormulaText = doc.ActiveRaw;
        newValue.LastOpenedAt = DateTime.Now;
        await _store.UpdateAsync(newValue);
        FocusGridRequested?.Invoke();
    }

    private void OnDocumentChanged()
    {
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
        RebuildTabs();
        OnPropertyChanged(nameof(IsFrozen));
        if (!FormulaBarEditing && Document != null) FormulaText = Document.ActiveRaw;
    }

    private void OnDocumentSelection()
    {
        if (!FormulaBarEditing && Document != null) FormulaText = Document.ActiveRaw;
    }

    private void OnDocumentProperty(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SheetDocument.SheetIndex)) { RebuildTabs(); OnPropertyChanged(nameof(IsFrozen)); }
    }

    private void RebuildTabs()
    {
        if (Document is not { } doc) { Tabs.Clear(); return; }
        var wanted = doc.Book.Sheets.Select((s, i) => new SheetTab(i, s.Name, i == doc.SheetIndex)).ToList();
        if (wanted.SequenceEqual(Tabs)) return;
        Tabs.Clear();
        foreach (var t in wanted) Tabs.Add(t);
    }

    public override async Task FlushAsync()
    {
        _saveTimer.Stop();
        if (!_dirty || Selected == null) return;
        await SaveAsync(Selected, Document);
    }

    private async Task SaveAsync(Spreadsheet item, SheetDocument? doc)
    {
        if (doc == null) return;
        _dirty = false;
        item.Data = doc.Book.ToJson(doc.SheetIndex);
        item.SheetCount = doc.Book.Sheets.Count;
        item.CellCount = doc.Book.CellCount;
        item.UpdatedAt = DateTime.Now;
        await _store.UpdateAsync(item);
        item.RefreshSummary();
    }

    public override void OnNewShortcut() => _ = New();

    // ===== workbooks =====

    private string UniqueTitle(string baseName)
    {
        if (Items.All(s => !string.Equals(s.Title, baseName, StringComparison.CurrentCultureIgnoreCase))) return baseName;
        for (int i = 2; ; i++)
            if (Items.All(s => !string.Equals(s.Title, $"{baseName} {i}", StringComparison.CurrentCultureIgnoreCase))) return $"{baseName} {i}";
    }

    [RelayCommand]
    private async Task New()
    {
        var item = new Spreadsheet { Title = UniqueTitle("Spreadsheet"), Data = Workbook.New().ToJson(), SheetCount = 1, LastOpenedAt = DateTime.Now };
        await _store.AddAsync(item);
        Items.Insert(0, item);
        ApplyFilter();
        Counts();
        Selected = item;
    }

    [RelayCommand]
    private async Task Import()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import spreadsheets",
            Filter = "Spreadsheets|*.xlsx;*.xlsm;*.xls;*.csv;*.tsv|All files|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog() != true) return;
        await ImportFilesAsync(dialog.FileNames);
    }

    /// <summary>Brings Excel and CSV files in as spreadsheets (formulas, formats and column widths included for .xlsx).</summary>
    public async Task ImportFilesAsync(IEnumerable<string> paths)
    {
        Spreadsheet? last = null;
        int count = 0;
        var notes = new List<string>();
        foreach (var path in paths)
        {
            if (!SpreadsheetIO.IsSpreadsheet(path)) continue;
            try
            {
                var fileNotes = new List<string>();
                var (json, sheets, cells) = await Task.Run(() =>
                {
                    var bytes = File.ReadAllBytes(path);
                    var wb = SpreadsheetIO.Read(bytes, Path.GetFileName(path), fileNotes);
                    return (wb.ToJson(), wb.Sheets.Count, wb.CellCount);
                });
                var item = new Spreadsheet
                {
                    Title = UniqueTitle(Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Trim()),
                    FileName = Path.GetFileName(path),
                    Data = json,
                    SheetCount = sheets,
                    CellCount = cells,
                    LastOpenedAt = DateTime.Now
                };
                await _store.AddAsync(item);
                Items.Insert(0, item);
                last = item;
                count++;
                notes.AddRange(fileNotes);
            }
            catch (Exception ex)
            {
                App.Log("Sheets import", ex);
                _toasts.Warning($"Couldn't open {Path.GetFileName(path)}: {(ex is InvalidDataException ? ex.Message : "it isn't a spreadsheet Satchel can read.")}");
            }
        }
        if (count == 0) return;
        ApplyFilter();
        Counts();
        Selected = last;
        var message = count == 1 ? $"“{last!.Title}” is ready" : $"{count} spreadsheets imported";
        if (notes.Count > 0) message += " — " + string.Join(" ", notes.Distinct());
        _toasts.Show(message, notes.Count > 0 ? ToastKind.Info : ToastKind.Success, durationMs: notes.Count > 0 ? 7000 : 2600);
    }

    [RelayCommand]
    private void Open(Spreadsheet? item)
    {
        if (item != null) Selected = item;
    }

    [RelayCommand]
    private async Task Rename()
    {
        if (Selected is not { } item) return;
        var name = await _dialogs.PromptAsync("Rename spreadsheet", null, item.Title, "Name", "Rename");
        if (string.IsNullOrWhiteSpace(name)) return;
        item.Title = name.Trim();
        await _store.UpdateAsync(item);
        ApplyFilter();
    }

    [RelayCommand]
    private async Task Duplicate()
    {
        if (Selected is not { } item || Document is not { } doc) return;
        await FlushAsync();
        var copy = new Spreadsheet
        {
            Title = UniqueTitle(item.Title + " copy"),
            Data = doc.Book.ToJson(doc.SheetIndex),
            FileName = item.FileName,
            SheetCount = item.SheetCount,
            CellCount = item.CellCount,
            IsPrivate = item.IsPrivate,
            LastOpenedAt = DateTime.Now
        };
        await _store.AddAsync(copy);
        Items.Insert(0, copy);
        ApplyFilter();
        Counts();
        Selected = copy;
    }

    [RelayCommand]
    private async Task ToggleFavorite()
    {
        if (Selected is not { } item) return;
        item.IsFavorite = !item.IsFavorite;
        await _store.UpdateAsync(item);
        ApplyFilter();
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (Selected is not { } item) return;
        if (_settings.Current.ConfirmDeletes && !await _dialogs.ConfirmAsync("Delete spreadsheet?", $"“{item.Title}” will be removed.", "Delete", danger: true)) return;
        _dirty = false;
        if (Document != null) item.Data = Document.Book.ToJson(Document.SheetIndex);
        await _store.DeleteAsync(item);
        Items.Remove(item);
        ApplyFilter();
        Counts();
        Selected = Filtered.FirstOrDefault();
        _toasts.Show("Spreadsheet deleted", ToastKind.Info, "Undo", async () =>
        {
            item.Id = 0;
            await _store.AddAsync(item);
            Items.Insert(0, item);
            ApplyFilter();
            Counts();
            Selected = item;
        });
    }

    private static string SafeFileName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();

    [RelayCommand]
    private void ExportXlsx()
    {
        if (Selected is not { } item || Document is not { } doc) return;
        var dialog = new SaveFileDialog { Title = "Export as Excel workbook", Filter = "Excel workbook|*.xlsx", FileName = SafeFileName(item.Title) + ".xlsx" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, SpreadsheetIO.WriteXlsx(doc.Book));
            _toasts.Success("Exported — opens in Excel, LibreOffice and Google Sheets");
        }
        catch (Exception ex)
        {
            App.Log("Sheets export", ex);
            _toasts.Error("Export failed: " + ex.Message);
        }
    }

    [RelayCommand]
    private void ExportCsv()
    {
        if (Selected is not { } item || Document is not { } doc) return;
        var name = doc.Book.Sheets.Count > 1 ? $"{item.Title} - {doc.Sheet.Name}" : item.Title;
        var dialog = new SaveFileDialog { Title = "Export this sheet as CSV", Filter = "CSV|*.csv", FileName = SafeFileName(name) + ".csv" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            File.WriteAllBytes(dialog.FileName, SpreadsheetIO.WriteCsv(doc.Sheet));
            _toasts.Success("Sheet exported as CSV");
        }
        catch (Exception ex)
        {
            _toasts.Error("Export failed: " + ex.Message);
        }
    }

    /// <summary>The workbook as Markdown tables in the Library (a snapshot of the values).</summary>
    [RelayCommand]
    private async Task CopyToLibrary()
    {
        if (Selected is not { } item || Document is not { } doc) return;
        var markdown = SpreadsheetIO.ToMarkdown(doc.Book, item.Title);
        var libraryDoc = new LibraryDoc
        {
            Title = item.Title,
            Markdown = markdown,
            FileName = SafeFileName(item.Title) + ".md",
            Collection = "Spreadsheets",
            IsPrivate = item.IsPrivate,
            LastOpenedAt = DateTime.Now
        };
        await _store.AddAsync(libraryDoc);
        _toasts.Show($"“{item.Title}” is in the Library as tables", ToastKind.Success, "Open", () => _nav.NavigateTo("Library", libraryDoc.Id), durationMs: 5000);
    }

    // ===== cells (toolbar and menus) =====

    [RelayCommand] private void Bold() => Document?.ToggleStyle(CellStyle.Bold);
    [RelayCommand] private void Italic() => Document?.ToggleStyle(CellStyle.Italic);
    [RelayCommand] private void Strike() => Document?.ToggleStyle(CellStyle.Strike);
    [RelayCommand] private void AlignLeft() => Document?.SetAlignment(CellStyle.Left);
    [RelayCommand] private void AlignCenter() => Document?.SetAlignment(CellStyle.Center);
    [RelayCommand] private void AlignRight() => Document?.SetAlignment(CellStyle.Right);
    [RelayCommand] private void SetFormat(FormatChoice? choice) { if (choice != null) Document?.SetFormat(choice.Code); FocusGridRequested?.Invoke(); }
    [RelayCommand] private void MoreDecimals() => Document?.ChangeDecimals(+1);
    [RelayCommand] private void FewerDecimals() => Document?.ChangeDecimals(-1);
    [RelayCommand] private void AutoSum() => Document?.AutoSum();
    [RelayCommand] private void SortAscending() => Document?.Sort(true);
    [RelayCommand] private void SortDescending() => Document?.Sort(false);
    [RelayCommand] private void InsertRowAbove() => Document?.InsertRows(below: false);
    [RelayCommand] private void InsertRowBelow() => Document?.InsertRows(below: true);
    [RelayCommand] private void InsertColumnLeft() => Document?.InsertColumns(right: false);
    [RelayCommand] private void InsertColumnRight() => Document?.InsertColumns(right: true);
    [RelayCommand] private void DeleteRows() => Document?.DeleteRows();
    [RelayCommand] private void DeleteColumns() => Document?.DeleteColumns();
    [RelayCommand] private void ClearCells() => Document?.ClearSelection();
    [RelayCommand] private void ClearAll() => Document?.ClearSelection(formatsToo: true);
    [RelayCommand] private void Undo() => Document?.Undo();
    [RelayCommand] private void Redo() => Document?.Redo();
    [RelayCommand] private void Copy() => Document?.Copy(cut: false);
    [RelayCommand] private void Cut() => Document?.Copy(cut: true);
    [RelayCommand] private void Paste() => Document?.Paste();
    [RelayCommand] private void FillDown() => Document?.Fill(down: true);
    [RelayCommand] private void FillRight() => Document?.Fill(down: false);

    [RelayCommand]
    private void ToggleFreeze()
    {
        if (Document is not { } doc) return;
        doc.SetFrozenRows(doc.Sheet.FrozenRows > 0 ? 0 : 1);
        OnPropertyChanged(nameof(IsFrozen));
    }

    /// <summary>Enter in the formula bar (or clicking away from it): the text goes into the active cell.</summary>
    public void CommitFormula(bool move = true)
    {
        FormulaBarEditing = false;
        if (Document is not { } doc) return;
        doc.Commit(doc.ActiveRow, doc.ActiveCol, FormulaText);
        if (move)
        {
            doc.Move(1, 0, withinSelection: true);
            FocusGridRequested?.Invoke();
        }
    }

    public void CancelFormula()
    {
        FormulaBarEditing = false;
        FormulaText = Document?.ActiveRaw ?? "";
        FocusGridRequested?.Invoke();
    }

    /// <summary>The name box: type B12 or A1:C20 to go there.</summary>
    public bool GoTo(string address)
    {
        if (Document is not { } doc) return false;
        var parts = address.Trim().Split(':');
        if (parts.Length == 1 && CellRef.TryParse(parts[0], out var r, out var c)) { doc.Select(r, c); FocusGridRequested?.Invoke(); return true; }
        if (parts.Length == 2 && CellRef.TryParse(parts[0], out var r1, out var c1) && CellRef.TryParse(parts[1], out var r2, out var c2))
        {
            doc.SelectRange(r1, c1, r2, c2);
            FocusGridRequested?.Invoke();
            return true;
        }
        return false;
    }

    // ===== sheets (tabs) =====

    [RelayCommand]
    private void SelectTab(SheetTab? tab)
    {
        if (tab != null && Document != null) Document.SheetIndex = tab.Index;
        FocusGridRequested?.Invoke();
    }

    [RelayCommand] private void AddTab() { Document?.AddSheet(); FocusGridRequested?.Invoke(); }

    [RelayCommand]
    private async Task RenameTab(SheetTab? tab)
    {
        if (Document is not { } doc) return;
        tab ??= Tabs.FirstOrDefault(t => t.IsActive);
        if (tab == null) return;
        var name = await _dialogs.PromptAsync("Rename sheet", "Up to 31 characters, without : \\ / ? * [ ] !", tab.Name, "Sheet name", "Rename");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!doc.RenameSheet(tab.Index, name)) _toasts.Warning("That name is taken or has a character sheets can't use.");
    }

    [RelayCommand]
    private void DuplicateTab(SheetTab? tab)
    {
        tab ??= Tabs.FirstOrDefault(t => t.IsActive);
        if (tab != null) Document?.DuplicateSheet(tab.Index);
    }

    [RelayCommand]
    private async Task DeleteTab(SheetTab? tab)
    {
        if (Document is not { } doc) return;
        tab ??= Tabs.FirstOrDefault(t => t.IsActive);
        if (tab == null) return;
        if (doc.Book.Sheets.Count <= 1) { _toasts.Info("A spreadsheet keeps at least one sheet."); return; }
        if (doc.Book.Sheets[tab.Index].CellCount > 0 && !await _dialogs.ConfirmAsync("Delete this sheet?", $"“{tab.Name}” and everything on it will be removed (Ctrl+Z brings it back).", "Delete", danger: true)) return;
        doc.DeleteSheet(tab.Index);
    }

    [RelayCommand] private void MoveTabLeft(SheetTab? tab) { tab ??= Tabs.FirstOrDefault(t => t.IsActive); if (tab != null) Document?.MoveSheet(tab.Index, -1); }
    [RelayCommand] private void MoveTabRight(SheetTab? tab) { tab ??= Tabs.FirstOrDefault(t => t.IsActive); if (tab != null) Document?.MoveSheet(tab.Index, +1); }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    public void Dispose()
    {
        IsDisposed = true;
        _saveTimer.Stop();
        if (Document != null)
        {
            Document.Changed -= OnDocumentChanged;
            Document.SelectionChanged -= OnDocumentSelection;
            Document.PropertyChanged -= OnDocumentProperty;
        }
    }
}
