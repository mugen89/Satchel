using System.Text.Json;
using System.Text.Json.Serialization;

namespace Satchel.Services.Sheets;

[Flags]
public enum CellStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Strike = 4,
    Left = 8,
    Center = 16,
    Right = 32,
    AlignMask = Left | Center | Right,
}

/// <summary>One cell: what you typed (<see cref="Raw"/>: text, a number or =formula), its look, and the computed value.</summary>
public sealed class Cell
{
    public string Raw { get; internal set; } = "";
    public CellStyle Style { get; internal set; }
    /// <summary>Number format code (Excel style, e.g. "0.00", "#,##0", "0%", "dd.mm.yyyy"); null = General.</summary>
    public string? Format { get; internal set; }
    public XValue Value { get; internal set; }
    public string Display { get; internal set; } = "";

    internal Formula.Node? Parsed;
    internal string? ParsedFrom;
    internal int Pass;
    internal bool Busy;

    public bool IsFormula => Raw.Length > 1 && Raw[0] == '=';
    public bool IsBlank => Raw.Length == 0;

    public CellRecord ToRecord(int row, int col) => new() { Row = row, Col = col, Value = Raw, Style = (int)Style, Format = Format };
}

public sealed class Sheet(string name)
{
    public const double DefaultWidth = 96, MinWidth = 24, MaxWidth = 900;

    public string Name { get; set; } = name;
    internal readonly Dictionary<long, Cell> Cells = new();
    public Dictionary<int, double> Widths { get; } = new();
    public int FrozenRows { get; set; }
    private int _rows = -1, _cols = -1;

    public static long Key(int row, int col) => ((long)row << 16) | (uint)col;
    public static (int Row, int Col) FromKey(long key) => ((int)(key >> 16), (int)(key & 0xFFFF));

    public Cell? Get(int row, int col) => Cells.TryGetValue(Key(row, col), out var c) ? c : null;
    public int CellCount => Cells.Count;
    public double Width(int col) => Widths.TryGetValue(col, out var w) ? w : DefaultWidth;

    public IEnumerable<(int Row, int Col, Cell Cell)> All()
    {
        foreach (var (key, cell) in Cells)
        {
            var (r, c) = FromKey(key);
            yield return (r, c, cell);
        }
    }

    /// <summary>Rows in use (the last row with anything in it, plus one).</summary>
    public int UsedRows { get { if (_rows < 0) Measure(); return _rows; } }
    public int UsedCols { get { if (_cols < 0) Measure(); return _cols; } }

    private void Measure()
    {
        int rows = 0, cols = 0;
        foreach (var key in Cells.Keys)
        {
            var (r, c) = FromKey(key);
            if (r + 1 > rows) rows = r + 1;
            if (c + 1 > cols) cols = c + 1;
        }
        _rows = rows;
        _cols = cols;
    }

    internal void Touch() => _rows = _cols = -1;

    internal Cell GetOrAdd(int row, int col)
    {
        var key = Key(row, col);
        if (!Cells.TryGetValue(key, out var cell))
        {
            Cells[key] = cell = new Cell();
            if (row >= _rows || col >= _cols) Touch();
        }
        return cell;
    }

    internal void Remove(int row, int col)
    {
        if (Cells.Remove(Key(row, col))) Touch();
    }
}

/// <summary>A spreadsheet: sheets of cells, formulas recalculated after every change.</summary>
public sealed class Workbook
{
    public List<Sheet> Sheets { get; } = [];
    private int _pass;

    public static Workbook New(string sheetName = "Sheet1")
    {
        var wb = new Workbook();
        wb.Sheets.Add(new Sheet(sheetName));
        return wb;
    }

    public Sheet? Find(string name) => Sheets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public string UniqueSheetName(string baseName)
    {
        if (Find(baseName) == null) return baseName;
        for (int i = 2; ; i++)
            if (Find($"{baseName} {i}") == null) return $"{baseName} {i}";
    }

    public int CellCount => Sheets.Sum(s => s.CellCount);

    // ----- editing -----

    /// <summary>Sets what a cell holds (text, number or =formula). Empty text with no formatting removes the cell.</summary>
    public void SetRaw(Sheet sheet, int row, int col, string raw)
    {
        raw ??= "";
        var cell = sheet.Get(row, col);
        if (raw.Length == 0 && (cell == null || (cell.Style == CellStyle.None && cell.Format == null)))
        {
            sheet.Remove(row, col);
            return;
        }
        cell ??= sheet.GetOrAdd(row, col);
        if (raw.Length > 1 && raw[0] == '=') raw = Formula.Normalize(raw);
        cell.Raw = raw;
        if (!cell.IsFormula)
        {
            var (value, suggested) = CellInput.Parse(raw, cell.Format);
            cell.Value = value;
            if (cell.Format == null && suggested != null) cell.Format = suggested;
            cell.Display = CellFormat.Display(cell.Value, cell.Format);
        }
    }

    public void SetCell(Sheet sheet, int row, int col, CellRecord? record)
    {
        if (record == null || (record.Value.Length == 0 && record.Style == 0 && record.Format == null))
        {
            sheet.Remove(row, col);
            return;
        }
        var cell = sheet.GetOrAdd(row, col);
        cell.Style = (CellStyle)record.Style;
        cell.Format = record.Format;
        cell.Raw = "";
        SetRaw(sheet, row, col, record.Value);
    }

    public void SetStyle(Sheet sheet, int row, int col, CellStyle style, string? format, bool setFormat)
    {
        var cell = sheet.Get(row, col);
        if (cell == null && style == CellStyle.None && (!setFormat || format == null)) return;
        cell ??= sheet.GetOrAdd(row, col);
        cell.Style = style;
        if (setFormat) cell.Format = format;
        if (cell.Raw.Length == 0 && cell.Style == CellStyle.None && cell.Format == null) { sheet.Remove(row, col); return; }
        if (!cell.IsFormula) cell.Display = CellFormat.Display(cell.Value, cell.Format);
    }

    // ----- structure -----

    /// <summary>
    /// Inserts (<paramref name="count"/> &gt; 0) or deletes (&lt; 0) rows or columns at <paramref name="index"/>.
    /// Cells move, and every formula in the workbook follows (references to deleted cells become #REF!).
    /// </summary>
    public void Resize(Sheet sheet, bool rows, int index, int count)
    {
        if (count == 0 || index < 0) return;
        int limit = rows ? CellRef.MaxRows : CellRef.MaxCols;
        var cells = sheet.All().ToList();
        sheet.Cells.Clear();
        foreach (var (r, c, cell) in cells)
        {
            int pos = rows ? r : c;
            if (count < 0 && pos >= index && pos < index - count) continue;
            int moved = pos >= index ? pos + count : pos;
            if (moved < 0 || moved >= limit) continue;
            sheet.Cells[rows ? Sheet.Key(moved, c) : Sheet.Key(r, moved)] = cell;
        }
        sheet.Touch();
        if (!rows)
        {
            var widths = sheet.Widths.ToList();
            sheet.Widths.Clear();
            foreach (var (c, w) in widths)
            {
                if (count < 0 && c >= index && c < index - count) continue;
                int moved = c >= index ? c + count : c;
                if (moved >= 0 && moved < limit) sheet.Widths[moved] = w;
            }
        }
        foreach (var s in Sheets)
            foreach (var cell in s.Cells.Values)
                if (cell.IsFormula)
                {
                    var adjusted = Formula.AdjustForResize(cell.Raw, s.Name, sheet.Name, rows, index, count);
                    if (adjusted != cell.Raw) { cell.Raw = adjusted; cell.Parsed = null; }
                }
        Recalculate();
    }

    /// <summary>Sorts rows <paramref name="r1"/>…<paramref name="r2"/> (columns c1…c2) by one column; empty cells go last.</summary>
    public void SortRows(Sheet sheet, int r1, int r2, int c1, int c2, int keyCol, bool ascending)
    {
        if (r2 <= r1) return;
        var rows = Enumerable.Range(r1, r2 - r1 + 1).Select(r => (Row: r, Key: sheet.Get(r, keyCol)?.Value ?? XValue.Empty)).ToList();
        var filled = rows.Where(x => !x.Key.IsEmpty && !(x.Key.IsText && x.Key.Text!.Length == 0)).ToList();
        var ordered = (ascending ? filled.OrderBy(x => x.Key, ValueComparer.Instance) : filled.OrderByDescending(x => x.Key, ValueComparer.Instance))
            .Concat(rows.Except(filled)).Select(x => x.Row).ToList();
        var moving = new Dictionary<(int Row, int Col), Cell>();
        for (int r = r1; r <= r2; r++)
            for (int c = c1; c <= c2; c++)
                if (sheet.Get(r, c) is { } cell) { moving[(r, c)] = cell; sheet.Cells.Remove(Sheet.Key(r, c)); }
        for (int i = 0; i < ordered.Count; i++)
        {
            int from = ordered[i], to = r1 + i;
            for (int c = c1; c <= c2; c++)
            {
                if (!moving.TryGetValue((from, c), out var cell)) continue;
                if (cell.IsFormula && from != to) { cell.Raw = Formula.Shift(cell.Raw, to - from, 0); cell.Parsed = null; }
                sheet.Cells[Sheet.Key(to, c)] = cell;
            }
        }
        sheet.Touch();
        Recalculate();
    }

    private sealed class ValueComparer : IComparer<XValue>
    {
        public static readonly ValueComparer Instance = new();
        public int Compare(XValue a, XValue b) => Formula.Compare(a, b);
    }

    // ----- calculation -----

    /// <summary>
    /// Recalculates every formula. Cells go in reading order so the usual chains (running totals pointing up) are
    /// already known; big workbooks run on a thread with a deep stack so long chains can't overflow it.
    /// </summary>
    public void Recalculate()
    {
        _pass++;
        int formulas = 0;
        foreach (var s in Sheets) foreach (var c in s.Cells.Values) if (c.IsFormula) formulas++;
        if (formulas == 0) return;
        if (formulas < 400) { RecalculateCore(); return; }
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { RecalculateCore(); }
            catch (Exception ex) { error = ex; }
        }, 256 * 1024 * 1024);
        thread.Start();
        thread.Join();
        if (error != null) throw new InvalidOperationException("Recalculation failed", error);
    }

    private void RecalculateCore()
    {
        foreach (var sheet in Sheets)
        {
            var keys = sheet.Cells.Where(p => p.Value.IsFormula).Select(p => p.Key).ToList();
            keys.Sort();
            foreach (var key in keys)
            {
                var (r, c) = Sheet.FromKey(key);
                Evaluate(sheet, r, c, 0);
            }
        }
        foreach (var sheet in Sheets)
            foreach (var cell in sheet.Cells.Values)
                if (cell.IsFormula) cell.Display = CellFormat.Display(cell.Value, cell.Format);
    }

    /// <summary>The value of a cell during calculation (formulas evaluate on demand, once per pass).</summary>
    internal XValue Evaluate(Sheet sheet, int row, int col, int depth)
    {
        var cell = sheet.Get(row, col);
        if (cell == null) return XValue.Empty;
        if (!cell.IsFormula) return cell.Value;
        if (cell.Pass == _pass && !cell.Busy) return cell.Value;
        if (cell.Busy || depth > 200_000) return XValue.Err(Errors.Cycle);
        cell.Busy = true;
        cell.Pass = _pass;
        try
        {
            if (cell.Parsed == null || !ReferenceEquals(cell.ParsedFrom, cell.Raw))
            {
                cell.Parsed = Formula.Parse(cell.Raw);
                cell.ParsedFrom = cell.Raw;
            }
            var value = Formula.Evaluate(cell.Parsed, new Formula.Context(this, sheet, row, col, depth + 1));
            // A formula that points at an empty cell shows 0, like in Excel.
            cell.Value = value.IsEmpty ? XValue.Num(0) : value;
        }
        catch (Exception)
        {
            cell.Value = XValue.Err(Errors.Value);
        }
        finally
        {
            cell.Busy = false;
        }
        return cell.Value;
    }

    // ----- storage -----

    public static Workbook FromData(WorkbookData data)
    {
        var wb = new Workbook();
        foreach (var sd in data.Sheets)
        {
            var sheet = new Sheet(string.IsNullOrWhiteSpace(sd.Name) ? wb.UniqueSheetName("Sheet") : sd.Name) { FrozenRows = Math.Clamp(sd.Frozen, 0, 50) };
            if (sd.Widths != null) foreach (var (c, w) in sd.Widths) sheet.Widths[c] = Math.Clamp(w, Sheet.MinWidth, Sheet.MaxWidth);
            wb.Sheets.Add(sheet);
            foreach (var rec in sd.Cells)
                if (rec.Row >= 0 && rec.Row < CellRef.MaxRows && rec.Col >= 0 && rec.Col < CellRef.MaxCols) wb.SetCell(sheet, rec.Row, rec.Col, rec);
        }
        if (wb.Sheets.Count == 0) wb.Sheets.Add(new Sheet("Sheet1"));
        wb.Recalculate();
        return wb;
    }

    public WorkbookData ToData(int active = 0) => new()
    {
        Active = active,
        Sheets = Sheets.Select(s => new SheetData
        {
            Name = s.Name,
            Frozen = s.FrozenRows,
            Widths = s.Widths.Count > 0 ? new Dictionary<int, double>(s.Widths) : null,
            Cells = s.Cells.OrderBy(p => p.Key).Select(p => { var (r, c) = Sheet.FromKey(p.Key); return p.Value.ToRecord(r, c); }).ToList()
        }).ToList()
    };

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public string ToJson(int active = 0) => JsonSerializer.Serialize(ToData(active), Json);

    public static WorkbookData ParseJson(string json) =>
        string.IsNullOrWhiteSpace(json) ? new WorkbookData() : JsonSerializer.Deserialize<WorkbookData>(json, Json) ?? new WorkbookData();
}

/// <summary>A workbook as stored (JSON inside the encrypted database).</summary>
public sealed class WorkbookData
{
    public List<SheetData> Sheets { get; set; } = [];
    public int Active { get; set; }
}

public sealed class SheetData
{
    public string Name { get; set; } = "Sheet1";
    public List<CellRecord> Cells { get; set; } = [];
    public Dictionary<int, double>? Widths { get; set; }
    public int Frozen { get; set; }
}

public sealed class CellRecord
{
    [JsonPropertyName("r")] public int Row { get; set; }
    [JsonPropertyName("c")] public int Col { get; set; }
    [JsonPropertyName("v")] public string Value { get; set; } = "";
    [JsonPropertyName("s"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Style { get; set; }
    [JsonPropertyName("f")] public string? Format { get; set; }

    public CellRecord Clone() => new() { Row = Row, Col = Col, Value = Value, Style = Style, Format = Format };
}
