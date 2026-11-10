using System.Globalization;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Satchel.Services.Sheets;

namespace Satchel.ViewModels;

/// <summary>
/// An open workbook with its selection, edits, undo / redo and clipboard. The grid draws it and sends what you do here;
/// <see cref="Changed"/> tells the page to save and the grid to redraw.
/// </summary>
public sealed partial class SheetDocument : ObservableObject
{
    private const int MaxUndo = 200;

    public Workbook Book { get; }

    public SheetDocument(Workbook book, int activeSheet = 0)
    {
        Book = book;
        _sheetIndex = Math.Clamp(activeSheet, 0, Math.Max(0, book.Sheets.Count - 1));
    }

    /// <summary>Contents changed (an edit, undo, a new sheet…).</summary>
    public event Action? Changed;
    /// <summary>The selection moved.</summary>
    public event Action? SelectionChanged;
    /// <summary>Asks the grid to scroll the active cell into view.</summary>
    public event Action? RevealRequested;

    private int _sheetIndex;

    /// <summary>The sheet on screen. Switching sheets starts at A1.</summary>
    public int SheetIndex
    {
        get => _sheetIndex;
        set
        {
            value = Math.Clamp(value, 0, Math.Max(0, Book.Sheets.Count - 1));
            if (value == _sheetIndex) return;
            _sheetIndex = value;
            OnPropertyChanged();
            _anchorRow = _anchorCol = _activeRow = _activeCol = 0;
            CopyRange = null;
            OnSelection();
            Changed?.Invoke();
        }
    }

    public Sheet Sheet => Book.Sheets[Math.Clamp(SheetIndex, 0, Book.Sheets.Count - 1)];

    // ===== selection =====

    private int _anchorRow, _anchorCol, _activeRow, _activeCol;
    public int ActiveRow => _activeRow;
    public int ActiveCol => _activeCol;
    public int AnchorRow => _anchorRow;
    public int AnchorCol => _anchorCol;

    public (int R1, int C1, int R2, int C2) Selection =>
        (Math.Min(_anchorRow, _activeRow), Math.Min(_anchorCol, _activeCol), Math.Max(_anchorRow, _activeRow), Math.Max(_anchorCol, _activeCol));

    public bool IsMultiple => _anchorRow != _activeRow || _anchorCol != _activeCol;

    /// <summary>The copied (or cut) range on this sheet, drawn with a dashed border until the next edit.</summary>
    public (int R1, int C1, int R2, int C2)? CopyRange { get; private set; }

    public void Select(int row, int col, bool extend = false)
    {
        row = Math.Clamp(row, 0, CellRef.MaxRows - 1);
        col = Math.Clamp(col, 0, CellRef.MaxCols - 1);
        _activeRow = row;
        _activeCol = col;
        if (!extend) { _anchorRow = row; _anchorCol = col; }
        OnSelection();
    }

    public void SelectRange(int r1, int c1, int r2, int c2)
    {
        _anchorRow = Math.Clamp(r1, 0, CellRef.MaxRows - 1);
        _anchorCol = Math.Clamp(c1, 0, CellRef.MaxCols - 1);
        _activeRow = Math.Clamp(r2, 0, CellRef.MaxRows - 1);
        _activeCol = Math.Clamp(c2, 0, CellRef.MaxCols - 1);
        OnSelection();
    }

    /// <summary>Ctrl+A: the cells in use (or everything up to them).</summary>
    public void SelectAll() => SelectRange(0, 0, Math.Max(0, Sheet.UsedRows - 1), Math.Max(0, Sheet.UsedCols - 1));

    /// <summary>Moves the active cell; when a range is selected, Enter and Tab walk inside it.</summary>
    public void Move(int dRow, int dCol, bool extend = false, bool withinSelection = false)
    {
        if (withinSelection && IsMultiple && !extend)
        {
            var (r1, c1, r2, c2) = Selection;
            int r = _activeRow, c = _activeCol;
            if (dCol != 0)
            {
                c += dCol;
                if (c > c2) { c = c1; r = r + 1 > r2 ? r1 : r + 1; }
                if (c < c1) { c = c2; r = r - 1 < r1 ? r2 : r - 1; }
            }
            else
            {
                r += dRow;
                if (r > r2) { r = r1; c = c + 1 > c2 ? c1 : c + 1; }
                if (r < r1) { r = r2; c = c - 1 < c1 ? c2 : c - 1; }
            }
            _activeRow = r;
            _activeCol = c;
            OnSelection();
            return;
        }
        Select(_activeRow + dRow, _activeCol + dCol, extend);
    }

    /// <summary>Ctrl+arrow: to the edge of the data block (or the next filled cell), like Excel.</summary>
    public void Jump(int dRow, int dCol, bool extend)
    {
        int r = _activeRow, c = _activeCol;
        bool Filled(int row, int col) => row >= 0 && col >= 0 && Sheet.Get(row, col) is { IsBlank: false };
        int maxR = Math.Max(Sheet.UsedRows, r + 1), maxC = Math.Max(Sheet.UsedCols, c + 1);
        bool startFilled = Filled(r, c), nextFilled = Filled(r + dRow, c + dCol);
        if (startFilled && nextFilled)
        {
            while (Filled(r + dRow, c + dCol)) { r += dRow; c += dCol; }
        }
        else
        {
            r += dRow;
            c += dCol;
            while (r >= 0 && c >= 0 && r < maxR && c < maxC && !Filled(r, c)) { r += dRow; c += dCol; }
            if (r < 0 || c < 0) { r = Math.Max(0, r); c = Math.Max(0, c); }
            if (r >= maxR || c >= maxC) { r = dRow > 0 ? Math.Max(_activeRow, maxR - 1) : r; c = dCol > 0 ? Math.Max(_activeCol, maxC - 1) : c; }
        }
        Select(r, c, extend);
    }

    /// <summary>Whole columns (or rows) — down to the cells in use, and at least the first thousand rows / 26 columns.</summary>
    public void SelectColumns(int c1, int c2) => SelectRange(0, c1, Math.Max(Sheet.UsedRows - 1, 999), c2);
    public void SelectRows(int r1, int r2) => SelectRange(r1, 0, r2, Math.Max(Sheet.UsedCols - 1, 25));

    private void OnSelection()
    {
        OnPropertyChanged(nameof(ActiveAddress));
        OnPropertyChanged(nameof(ActiveRaw));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ActiveStyle));
        OnPropertyChanged(nameof(ActiveFormat));
        foreach (var name in (string[])[nameof(IsBold), nameof(IsItalic), nameof(IsStrike), nameof(IsLeft), nameof(IsCenter), nameof(IsRight)]) OnPropertyChanged(name);
        SelectionChanged?.Invoke();
        RevealRequested?.Invoke();
    }

    /// <summary>"B4", or "A1:C9" for a range.</summary>
    public string ActiveAddress => IsMultiple ? CellRef.RangeName(_anchorRow, _anchorCol, _activeRow, _activeCol) : CellRef.Name(_activeRow, _activeCol);

    /// <summary>What the active cell holds as typed (its formula, not its result).</summary>
    public string ActiveRaw => RawAt(_activeRow, _activeCol);

    public CellStyle ActiveStyle => Sheet.Get(_activeRow, _activeCol)?.Style ?? CellStyle.None;
    public string? ActiveFormat => Sheet.Get(_activeRow, _activeCol)?.Format;

    // For the toolbar's toggle buttons.
    public bool IsBold => ActiveStyle.HasFlag(CellStyle.Bold);
    public bool IsItalic => ActiveStyle.HasFlag(CellStyle.Italic);
    public bool IsStrike => ActiveStyle.HasFlag(CellStyle.Strike);
    public bool IsLeft => (ActiveStyle & CellStyle.AlignMask) == CellStyle.Left;
    public bool IsCenter => (ActiveStyle & CellStyle.AlignMask) == CellStyle.Center;
    public bool IsRight => (ActiveStyle & CellStyle.AlignMask) == CellStyle.Right;

    public string RawAt(int row, int col) => Sheet.Get(row, col)?.Raw ?? "";

    /// <summary>Sum, average and count of the selection, as in Excel's status bar.</summary>
    public string StatusText
    {
        get
        {
            if (!IsMultiple) return "";
            var (r1, c1, r2, c2) = Selection;
            r2 = Math.Min(r2, Sheet.UsedRows - 1);
            c2 = Math.Min(c2, Sheet.UsedCols - 1);
            double sum = 0;
            int numbers = 0, filled = 0;
            for (int r = r1; r <= r2; r++)
                for (int c = c1; c <= c2; c++)
                {
                    if (Sheet.Get(r, c) is not { } cell || cell.Value.IsEmpty) continue;
                    filled++;
                    if (cell.Value.IsNumber) { sum += cell.Value.Number; numbers++; }
                }
            if (filled == 0) return "";
            if (numbers == 0) return $"Count {filled}";
            return $"Sum {CellFormat.General(sum)}   ·   Average {CellFormat.General(sum / numbers)}   ·   Count {filled}";
        }
    }

    // ===== editing (every change goes through Apply, which records undo) =====

    private sealed record CellChange(int Sheet, int Row, int Col, CellRecord? Before, CellRecord? After);

    private sealed class UndoStep
    {
        public List<CellChange> Cells = [];
        public WorkbookData? BookBefore, BookAfter;
        public (int Sheet, int Col, double? Before, double? After)? Width;
        public int SheetIndex;
        public (int R1, int C1, int R2, int C2) Selection;
    }

    private readonly List<UndoStep> _undo = [];
    private readonly List<UndoStep> _redo = [];

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    private void Push(UndoStep step)
    {
        _undo.Add(step);
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    private CellRecord? Record(int row, int col) => Sheet.Get(row, col)?.ToRecord(row, col);

    /// <summary>Applies new contents to cells (null removes a cell) as one undoable step.</summary>
    private void Apply(IEnumerable<(int Row, int Col, CellRecord? After)> changes, bool recalc = true)
    {
        var step = new UndoStep { SheetIndex = SheetIndex, Selection = Selection };
        foreach (var (row, col, after) in changes)
        {
            var before = Record(row, col);
            if (Same(before, after)) continue;
            step.Cells.Add(new CellChange(SheetIndex, row, col, before, after?.Clone()));
            Book.SetCell(Sheet, row, col, after);
        }
        if (step.Cells.Count == 0) return;
        if (recalc) Book.Recalculate();
        CopyRange = CopyRange is { } cr && step.Cells.All(c => c.Row < cr.R1 || c.Row > cr.R2 || c.Col < cr.C1 || c.Col > cr.C2) ? cr : null;
        Push(step);
        Done();
    }

    private static bool Same(CellRecord? a, CellRecord? b)
    {
        bool emptyA = a == null || (a.Value.Length == 0 && a.Style == 0 && a.Format == null);
        bool emptyB = b == null || (b.Value.Length == 0 && b.Style == 0 && b.Format == null);
        if (emptyA || emptyB) return emptyA == emptyB;
        return a!.Value == b!.Value && a.Style == b.Style && a.Format == b.Format;
    }

    private void Done()
    {
        OnSelection();
        Changed?.Invoke();
    }

    /// <summary>Whole-workbook changes (insert / delete rows, sort, sheets): the before and after are kept for undo.</summary>
    private void Structural(Action change)
    {
        var step = new UndoStep { SheetIndex = SheetIndex, Selection = Selection, BookBefore = Book.ToData(SheetIndex) };
        change();
        step.BookAfter = Book.ToData(SheetIndex);
        Push(step);
        CopyRange = null;
        Done();
    }

    /// <summary>What you typed into a cell (text, a number or =formula), keeping its formatting.</summary>
    public void Commit(int row, int col, string raw)
    {
        var before = Record(row, col);
        var after = before?.Clone() ?? new CellRecord { Row = row, Col = col };
        after.Value = raw;
        // A typed percentage, date or amount picks its format only if the cell had none.
        Apply([(row, col, after)]);
    }

    /// <summary>Delete: empties the selected cells (their formatting stays).</summary>
    public void ClearSelection(bool formatsToo = false)
    {
        var (r1, c1, r2, c2) = Clamp(Selection);
        var changes = new List<(int, int, CellRecord?)>();
        for (int r = r1; r <= r2; r++)
            for (int c = c1; c <= c2; c++)
            {
                if (Record(r, c) is not { } rec) continue;
                if (formatsToo) changes.Add((r, c, null));
                else { rec.Value = ""; changes.Add((r, c, rec)); }
            }
        Apply(changes);
    }

    /// <summary>A selection clamped to the cells in use, so "whole column" selections stay cheap.</summary>
    private (int R1, int C1, int R2, int C2) Clamp((int R1, int C1, int R2, int C2) s) =>
        (s.R1, s.C1, Math.Min(s.R2, Math.Max(s.R1, Sheet.UsedRows - 1)), Math.Min(s.C2, Math.Max(s.C1, Sheet.UsedCols - 1)));

    /// <summary>Toggles or sets a look on every selected cell (bold, italic, alignment…).</summary>
    public void ChangeStyle(Func<CellStyle, CellStyle> change)
    {
        var (r1, c1, r2, c2) = Selection;
        r2 = Math.Min(r2, r1 + 5000);
        c2 = Math.Min(c2, c1 + 500);
        var changes = new List<(int, int, CellRecord?)>();
        for (int r = r1; r <= r2; r++)
            for (int c = c1; c <= c2; c++)
            {
                var rec = Record(r, c) ?? new CellRecord { Row = r, Col = c };
                rec.Style = (int)change((CellStyle)rec.Style);
                changes.Add((r, c, rec));
            }
        Apply(changes, recalc: false);
    }

    public void ToggleStyle(CellStyle flag)
    {
        bool on = !ActiveStyle.HasFlag(flag);
        ChangeStyle(s => on ? s | flag : s & ~flag);
    }

    public void SetAlignment(CellStyle align)
    {
        bool same = (ActiveStyle & CellStyle.AlignMask) == align;
        ChangeStyle(s => (s & ~CellStyle.AlignMask) | (same ? CellStyle.None : align));
    }

    /// <summary>Gives the selection a number format (null = General).</summary>
    public void SetFormat(string? code)
    {
        var (r1, c1, r2, c2) = Clamp(Selection);
        if (!IsMultiple) (r2, c2) = (r1, c1);
        var changes = new List<(int, int, CellRecord?)>();
        for (int r = r1; r <= r2; r++)
            for (int c = c1; c <= c2; c++)
            {
                var rec = Record(r, c) ?? new CellRecord { Row = r, Col = c };
                rec.Format = code;
                changes.Add((r, c, rec));
            }
        Apply(changes);
    }

    /// <summary>One more (or one less) decimal place on the selection's numbers.</summary>
    public void ChangeDecimals(int delta)
    {
        var current = ActiveFormat;
        var value = Sheet.Get(_activeRow, _activeCol)?.Value ?? XValue.Empty;
        string code;
        if (current == null || current == "General")
        {
            int shown = value.IsNumber ? Decimals(CellFormat.General(value.Number)) : 0;
            code = Decimals("0", Math.Max(0, shown + delta));
        }
        else if (CellFormat.IsDateFormat(current)) return;
        else
        {
            var m = System.Text.RegularExpressions.Regex.Match(current, @"0(\.0+)?");
            if (!m.Success) return;
            int now = m.Groups[1].Success ? m.Groups[1].Value.Length - 1 : 0;
            int next = Math.Clamp(now + delta, 0, 10);
            code = current[..m.Index] + Decimals("0", next) + current[(m.Index + m.Length)..];
        }
        SetFormat(code);
    }

    private static int Decimals(string text)
    {
        var sep = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        int at = text.IndexOf(sep, StringComparison.Ordinal);
        return at < 0 ? 0 : text.Length - at - sep.Length;
    }

    private static string Decimals(string integer, int places) => places == 0 ? integer : integer + "." + new string('0', places);

    /// <summary>Ctrl+D / Ctrl+R: copies the first row (or column) of the selection over the rest, formulas adjusted.</summary>
    public void Fill(bool down)
    {
        var (r1, c1, r2, c2) = Selection;
        if (!IsMultiple)
        {
            // One cell: copy from the cell above (or to the left).
            if (down && r1 == 0 || !down && c1 == 0) return;
            if (down) r1--; else c1--;
        }
        var changes = new List<(int, int, CellRecord?)>();
        if (down)
        {
            for (int c = c1; c <= c2; c++)
            {
                var source = Record(r1, c);
                for (int r = r1 + 1; r <= Math.Min(r2, r1 + 10000); r++) changes.Add((r, c, Shifted(source, r - r1, 0, r, c)));
            }
        }
        else
        {
            for (int r = r1; r <= r2; r++)
            {
                var source = Record(r, c1);
                for (int c = c1 + 1; c <= Math.Min(c2, c1 + 1000); c++) changes.Add((r, c, Shifted(source, 0, c - c1, r, c)));
            }
        }
        Apply(changes);
    }

    private static CellRecord? Shifted(CellRecord? source, int dRow, int dCol, int row, int col)
    {
        if (source == null) return null;
        var copy = source.Clone();
        copy.Row = row;
        copy.Col = col;
        if (copy.Value.StartsWith('=')) copy.Value = Formula.Shift(copy.Value, dRow, dCol);
        return copy;
    }

    /// <summary>Σ: sums the numbers above (or to the left of) the active cell, or under each selected column.</summary>
    public void AutoSum()
    {
        var (r1, c1, r2, c2) = Selection;
        if (IsMultiple)
        {
            var changes = new List<(int, int, CellRecord?)>();
            for (int c = c1; c <= c2; c++)
                changes.Add((r2 + 1, c, new CellRecord { Row = r2 + 1, Col = c, Value = $"=SUM({CellRef.RangeName(r1, c, r2, c)})", Style = (int)CellStyle.Bold }));
            Apply(changes);
            Select(r2 + 1, c1);
            return;
        }
        int r = _activeRow, col = _activeCol;
        bool Num(int row, int cc) => row >= 0 && cc >= 0 && Sheet.Get(row, cc) is { Value.IsNumber: true };
        string? range = null;
        if (Num(r - 1, col))
        {
            int top = r - 1;
            while (Num(top - 1, col)) top--;
            range = CellRef.RangeName(top, col, r - 1, col);
        }
        else if (Num(r, col - 1))
        {
            int left = col - 1;
            while (Num(r, left - 1)) left--;
            range = CellRef.RangeName(r, left, r, col - 1);
        }
        if (range == null) return;
        Commit(r, col, $"=SUM({range})");
    }

    // ----- clipboard -----

    private sealed record Clip(int Sheet, int R1, int C1, int R2, int C2, List<CellRecord> Cells, string Text, bool Cut);
    private Clip? _clip;

    /// <summary>Copies (or cuts) the selection: as text for other apps, and with formulas for pasting here.</summary>
    public void Copy(bool cut)
    {
        var (r1, c1, r2, c2) = Clamp(Selection);
        var cells = new List<CellRecord>();
        var sb = new StringBuilder();
        for (int r = r1; r <= r2; r++)
        {
            for (int c = c1; c <= c2; c++)
            {
                if (c > c1) sb.Append('\t');
                var cell = Sheet.Get(r, c);
                if (cell == null) continue;
                cells.Add(cell.ToRecord(r - r1, c - c1));
                var text = cell.Display;
                if (text.IndexOfAny(['\t', '\n', '"']) >= 0) text = "\"" + text.Replace("\"", "\"\"") + "\"";
                sb.Append(text);
            }
            if (r < r2) sb.Append("\r\n");
        }
        var clipText = sb.ToString();
        _clip = new Clip(SheetIndex, r1, c1, r2, c2, cells, clipText, cut);
        CopyRange = (r1, c1, r2, c2);
        SetClipboard(clipText);
        Changed?.Invoke();
    }

    public void Paste()
    {
        var text = GetClipboard();
        var (r1, c1, r2, c2) = Selection;
        var changes = new List<(int, int, CellRecord?)>();
        if (_clip != null && text != null && text == _clip.Text)
        {
            int rows = _clip.R2 - _clip.R1 + 1, cols = _clip.C2 - _clip.C1 + 1;
            // One copied cell into a bigger selection fills it.
            int tileRows = IsMultiple && rows == 1 && cols == 1 ? r2 - r1 + 1 : rows;
            int tileCols = IsMultiple && rows == 1 && cols == 1 ? c2 - c1 + 1 : cols;
            var byPos = _clip.Cells.ToDictionary(c => (c.Row, c.Col));
            for (int dr = 0; dr < tileRows; dr++)
                for (int dc = 0; dc < tileCols; dc++)
                {
                    int tr = r1 + dr, tc = c1 + dc;
                    if (tr >= CellRef.MaxRows || tc >= CellRef.MaxCols) continue;
                    var src = byPos.GetValueOrDefault((dr % rows, dc % cols));
                    if (src == null) { changes.Add((tr, tc, null)); continue; }
                    var copy = src.Clone();
                    copy.Row = tr;
                    copy.Col = tc;
                    if (!_clip.Cut && copy.Value.StartsWith('=')) copy.Value = Formula.Shift(copy.Value, tr - (_clip.R1 + dr % rows), tc - (_clip.C1 + dc % cols));
                    changes.Add((tr, tc, copy));
                }
            if (_clip.Cut && _clip.Sheet == SheetIndex)
            {
                for (int r = _clip.R1; r <= _clip.R2; r++)
                    for (int c = _clip.C1; c <= _clip.C2; c++)
                        if (r < r1 || r >= r1 + rows || c < c1 || c >= c1 + cols) changes.Add((r, c, null));
                _clip = null;
            }
            Apply(changes);
            SelectRange(r1, c1, r1 + tileRows - 1, c1 + tileCols - 1);
            return;
        }
        if (string.IsNullOrEmpty(text)) return;
        // Text from another app (Excel, a web page, a text editor): tab-separated rows.
        var rowsText = SpreadsheetIO.ParseDelimited(text.TrimEnd('\r', '\n'), '\t').Take(20000).ToList();
        if (rowsText.Count == 1 && rowsText[0].Count == 1 && IsMultiple)
        {
            for (int r = r1; r <= Math.Min(r2, r1 + 5000); r++)
                for (int c = c1; c <= Math.Min(c2, c1 + 500); c++)
                    changes.Add((r, c, WithValue(r, c, rowsText[0][0])));
        }
        else
        {
            for (int dr = 0; dr < rowsText.Count; dr++)
                for (int dc = 0; dc < rowsText[dr].Count && dc < 1000; dc++)
                    changes.Add((r1 + dr, c1 + dc, WithValue(r1 + dr, c1 + dc, rowsText[dr][dc])));
        }
        Apply(changes);
        if (changes.Count > 1) SelectRange(r1, c1, r1 + rowsText.Count - 1, c1 + Math.Max(0, rowsText.Max(r => r.Count) - 1));
    }

    private CellRecord? WithValue(int row, int col, string raw)
    {
        var rec = Record(row, col) ?? new CellRecord { Row = row, Col = col };
        rec.Value = raw;
        return rec;
    }

    public void ClearCopy()
    {
        if (CopyRange == null) return;
        CopyRange = null;
        Changed?.Invoke();
    }

    private static void SetClipboard(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text.Length == 0 ? " " : text); return; }
            catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(30); }
        }
    }

    private static string? GetClipboard()
    {
        for (int i = 0; i < 5; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(30); }
        }
        return null;
    }

    // ----- structure -----

    public void InsertRows(bool below)
    {
        var (r1, _, r2, _) = Selection;
        int count = Math.Min(r2 - r1 + 1, 1000);
        Structural(() => Book.Resize(Sheet, rows: true, below ? r2 + 1 : r1, count));
        if (!below) SelectRange(r1 + count, _anchorCol, r2 + count, _activeCol);
    }

    public void InsertColumns(bool right)
    {
        var (_, c1, _, c2) = Selection;
        int count = Math.Min(c2 - c1 + 1, 200);
        Structural(() => Book.Resize(Sheet, rows: false, right ? c2 + 1 : c1, count));
        if (!right) SelectRange(_anchorRow, c1 + count, _activeRow, c2 + count);
    }

    public void DeleteRows()
    {
        var (r1, _, r2, _) = Selection;
        Structural(() => Book.Resize(Sheet, rows: true, r1, -(r2 - r1 + 1)));
        Select(r1, _activeCol);
    }

    public void DeleteColumns()
    {
        var (_, c1, _, c2) = Selection;
        Structural(() => Book.Resize(Sheet, rows: false, c1, -(c2 - c1 + 1)));
        Select(_activeRow, c1);
    }

    /// <summary>Sorts by the active column: the selected rows, or the whole table (keeping a header row on top).</summary>
    public void Sort(bool ascending)
    {
        var (r1, c1, r2, c2) = Selection;
        if (!IsMultiple || r1 == r2)
        {
            r1 = 0;
            r2 = Sheet.UsedRows - 1;
            c1 = 0;
            c2 = Math.Max(0, Sheet.UsedCols - 1);
            if (HasHeader(c1, c2)) r1 = 1;
        }
        else r2 = Math.Min(r2, Sheet.UsedRows - 1);
        if (r2 <= r1) return;
        int key = _activeCol;
        Structural(() => Book.SortRows(Sheet, r1, r2, c1, c2, key, ascending));
    }

    /// <summary>A first row of text (often bold) above columns that hold numbers is a header.</summary>
    private bool HasHeader(int c1, int c2)
    {
        bool allText = true, bold = false, below = false;
        for (int c = c1; c <= c2; c++)
        {
            var top = Sheet.Get(0, c);
            if (top == null) continue;
            if (!top.Value.IsText) allText = false;
            if (top.Style.HasFlag(CellStyle.Bold)) bold = true;
            if (Sheet.Get(1, c) is { Value.IsNumber: true }) below = true;
        }
        return allText && (bold || below);
    }

    public void SetFrozenRows(int rows) => Structural(() => Sheet.FrozenRows = Math.Clamp(rows, 0, 20));

    public void SetWidth(int col, double width, bool record = true)
    {
        width = Math.Round(Math.Clamp(width, Sheet.MinWidth, Sheet.MaxWidth));
        double? before = Sheet.Widths.TryGetValue(col, out var w) ? w : null;
        if (before == width) return;
        Sheet.Widths[col] = width;
        if (record)
        {
            Push(new UndoStep { SheetIndex = SheetIndex, Selection = Selection, Width = (SheetIndex, col, before, width) });
            Changed?.Invoke();
        }
    }

    /// <summary>Records a column-width drag once it ends (the live drag doesn't fill the undo list).</summary>
    public void CommitWidth(int col, double? startWidth)
    {
        double? now = Sheet.Widths.TryGetValue(col, out var w) ? w : null;
        if (now == startWidth) return;
        Push(new UndoStep { SheetIndex = SheetIndex, Selection = Selection, Width = (SheetIndex, col, startWidth, now) });
        Changed?.Invoke();
    }

    // ----- sheets -----

    public void AddSheet()
    {
        var name = Book.UniqueSheetName("Sheet" + (Book.Sheets.Count + 1));
        Structural(() => Book.Sheets.Add(new Sheet(name)));
        SheetIndex = Book.Sheets.Count - 1;
    }

    public void DuplicateSheet(int index)
    {
        var data = Book.ToData();
        var source = data.Sheets[index];
        var name = Book.UniqueSheetName(source.Name + " copy");
        Structural(() =>
        {
            var copy = new Sheet(name) { FrozenRows = source.Frozen };
            if (source.Widths != null) foreach (var (c, w) in source.Widths) copy.Widths[c] = w;
            Book.Sheets.Insert(index + 1, copy);
            foreach (var rec in source.Cells) Book.SetCell(copy, rec.Row, rec.Col, rec);
            Book.Recalculate();
        });
        SheetIndex = index + 1;
    }

    public bool RenameSheet(int index, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.IndexOfAny([':', '\\', '/', '?', '*', '[', ']', '!']) >= 0) return false;
        if (name.Length > 31) name = name[..31];
        var sheet = Book.Sheets[index];
        if (Book.Find(name) is { } other && other != sheet) return false;
        var old = sheet.Name;
        if (old == name) return true;
        Structural(() =>
        {
            sheet.Name = name;
            foreach (var s in Book.Sheets)
                foreach (var (row, col, cell) in s.All().Where(x => x.Cell.IsFormula).ToList())
                {
                    var renamed = Formula.RenameSheet(cell.Raw, old, name);
                    if (renamed != cell.Raw) Book.SetRaw(s, row, col, renamed);
                }
            Book.Recalculate();
        });
        return true;
    }

    public void DeleteSheet(int index)
    {
        if (Book.Sheets.Count <= 1) return;
        Structural(() =>
        {
            Book.Sheets.RemoveAt(index);
            Book.Recalculate();
        });
        SheetIndex = Math.Min(index, Book.Sheets.Count - 1);
    }

    public void MoveSheet(int index, int delta)
    {
        int target = index + delta;
        if (target < 0 || target >= Book.Sheets.Count) return;
        Structural(() =>
        {
            var s = Book.Sheets[index];
            Book.Sheets.RemoveAt(index);
            Book.Sheets.Insert(target, s);
        });
        SheetIndex = target;
    }

    // ----- undo -----

    public void Undo() => Step(_undo, _redo, undo: true);
    public void Redo() => Step(_redo, _undo, undo: false);

    private void Step(List<UndoStep> from, List<UndoStep> to, bool undo)
    {
        if (from.Count == 0) return;
        var step = from[^1];
        from.RemoveAt(from.Count - 1);
        if (step.BookBefore != null)
        {
            var data = undo ? step.BookBefore : step.BookAfter!;
            var restored = Workbook.FromData(data);
            Book.Sheets.Clear();
            Book.Sheets.AddRange(restored.Sheets);
            Book.Recalculate();
        }
        else if (step.Width is { } w)
        {
            var sheet = Book.Sheets[Math.Min(w.Sheet, Book.Sheets.Count - 1)];
            var value = undo ? w.Before : w.After;
            if (value is { } v) sheet.Widths[w.Col] = v; else sheet.Widths.Remove(w.Col);
        }
        else
        {
            foreach (var change in undo ? Enumerable.Reverse(step.Cells) : step.Cells)
            {
                var sheet = Book.Sheets[Math.Min(change.Sheet, Book.Sheets.Count - 1)];
                Book.SetCell(sheet, change.Row, change.Col, (undo ? change.Before : change.After)?.Clone());
            }
            Book.Recalculate();
        }
        to.Add(step);
        // Back to the sheet the change was on (without resetting the selection, which is restored below).
        int stepSheet = Math.Clamp(step.SheetIndex, 0, Book.Sheets.Count - 1);
        if (_sheetIndex != stepSheet || _sheetIndex >= Book.Sheets.Count)
        {
            _sheetIndex = stepSheet;
            OnPropertyChanged(nameof(SheetIndex));
        }
        var (r1, c1, r2, c2) = step.Selection;
        _anchorRow = r1; _anchorCol = c1; _activeRow = r2; _activeCol = c2;
        CopyRange = null;
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        Done();
    }

    /// <summary>Tells the grid to redraw after outside changes (e.g. the theme).</summary>
    public void Refresh() => Changed?.Invoke();
}
