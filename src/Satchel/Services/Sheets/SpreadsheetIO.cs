using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ExcelDataReader;
using Satchel.Services.Import;

namespace Satchel.Services.Sheets;

/// <summary>Opening and saving spreadsheets: .xlsx (with formulas and formatting), .xls, .csv / .tsv, and Markdown tables.</summary>
public static class SpreadsheetIO
{
    public const int MaxCells = 300_000;

    public static readonly string[] Extensions = [".xlsx", ".xlsm", ".xls", ".csv", ".tsv"];

    public static bool IsSpreadsheet(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Reads any supported spreadsheet (decided by its content first, then its extension).</summary>
    public static Workbook Read(byte[] bytes, string fileName, List<string> notes)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        Workbook wb;
        if (bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K') wb = ReadXlsx(bytes, notes);
        else if (CompoundFile.IsCompoundFile(bytes)) wb = ReadXls(bytes, notes);
        else wb = ReadCsv(bytes, ext == ".tsv" ? '\t' : null, Path.GetFileNameWithoutExtension(fileName));
        var unknown = wb.Sheets.SelectMany(s => s.All()).Count(c => c.Cell.IsFormula && c.Cell.Value is { IsError: true, Text: Errors.Name });
        if (unknown > 0) notes.Add(unknown == 1 ? "1 formula uses a function Satchel doesn't have yet (it shows #NAME?)." : $"{unknown} formulas use functions Satchel doesn't have yet (they show #NAME?).");
        return wb;
    }

    // ===================== .xlsx =====================

    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    private sealed record XfStyle(string? Format, CellStyle Style);

    public static Workbook ReadXlsx(byte[] bytes, List<string> notes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var mainPath = Xml(zip, "_rels/.rels")?.Root?.Elements(Rel + "Relationship")
            .FirstOrDefault(r => ((string?)r.Attribute("Type"))?.EndsWith("/officeDocument", StringComparison.Ordinal) == true)?.Attribute("Target")?.Value.TrimStart('/') ?? "xl/workbook.xml";
        var workbookXml = Xml(zip, mainPath) ?? throw new InvalidDataException("This file has no workbook inside.");
        int slash = mainPath.LastIndexOf('/');
        var folder = slash >= 0 ? mainPath[..(slash + 1)] : "";
        var rels = new Dictionary<string, (string Target, string Type)>();
        foreach (var r in Xml(zip, folder + "_rels/" + mainPath[(slash + 1)..] + ".rels")?.Root?.Elements(Rel + "Relationship") ?? [])
            if ((string?)r.Attribute("Id") is { } id) rels[id] = ((string?)r.Attribute("Target") ?? "", (string?)r.Attribute("Type") ?? "");

        string PartPath(string target) => target.StartsWith('/') ? target.TrimStart('/') : folder + target;
        var sharedPath = rels.Values.FirstOrDefault(r => r.Type.EndsWith("/sharedStrings", StringComparison.Ordinal)).Target;
        var stylesPath = rels.Values.FirstOrDefault(r => r.Type.EndsWith("/styles", StringComparison.Ordinal)).Target;
        var shared = SharedStrings(zip, sharedPath != null ? PartPath(sharedPath) : folder + "sharedStrings.xml");
        var styles = Styles(Xml(zip, stylesPath != null ? PartPath(stylesPath) : folder + "styles.xml"));
        bool date1904 = workbookXml.Root?.Element(S + "workbookPr")?.Attribute("date1904")?.Value is "1" or "true";
        if (date1904) notes.Add("This workbook uses the 1904 date system; dates were moved to the usual one.");

        var wb = new Workbook();
        int total = 0;
        foreach (var sheetEl in workbookXml.Root?.Element(S + "sheets")?.Elements(S + "sheet") ?? [])
        {
            var name = (string?)sheetEl.Attribute("name") ?? "Sheet";
            var rid = (string?)sheetEl.Attribute(R + "id");
            if (rid == null || !rels.TryGetValue(rid, out var rel) || !rel.Type.EndsWith("/worksheet", StringComparison.Ordinal)) continue;
            var sheet = new Sheet(wb.UniqueSheetName(name));
            wb.Sheets.Add(sheet);
            var entry = Entry(zip, PartPath(rel.Target));
            if (entry == null) continue;
            using var stream = entry.Open();
            total += ReadWorksheet(stream, wb, sheet, shared, styles, date1904, MaxCells - total);
            if (total >= MaxCells)
            {
                notes.Add($"Only the first {MaxCells:N0} cells were brought in.");
                break;
            }
        }
        if (wb.Sheets.Count == 0) wb.Sheets.Add(new Sheet("Sheet1"));
        wb.Recalculate();
        return wb;
    }

    private static int ReadWorksheet(Stream stream, Workbook wb, Sheet sheet, List<string> shared, List<XfStyle> styles, bool date1904, int budget)
    {
        int count = 0;
        var masters = new Dictionary<string, (int Row, int Col, string Formula)>();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { IgnoreWhitespace = false, DtdProcessing = DtdProcessing.Prohibit });
        int nextRow = 0;
        int rowIndex = 0, colIndex = 0;
        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            switch (reader.LocalName)
            {
                case "pane":
                    if (reader.GetAttribute("state") is "frozen" or "frozenSplit" && double.TryParse(reader.GetAttribute("ySplit"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ySplit))
                        sheet.FrozenRows = Math.Clamp((int)ySplit, 0, 50);
                    break;
                case "col":
                    if (int.TryParse(reader.GetAttribute("min"), out var min) && int.TryParse(reader.GetAttribute("max"), out var max)
                        && double.TryParse(reader.GetAttribute("width"), NumberStyles.Float, CultureInfo.InvariantCulture, out var width))
                    {
                        for (int c = min - 1; c < Math.Min(max, min + 512); c++)
                            if (c >= 0 && c < CellRef.MaxCols) sheet.Widths[c] = Math.Clamp(Math.Round(width * 7 + 5), Sheet.MinWidth, Sheet.MaxWidth);
                    }
                    break;
                case "row":
                    rowIndex = int.TryParse(reader.GetAttribute("r"), out var rr) ? rr - 1 : nextRow;
                    nextRow = rowIndex + 1;
                    colIndex = 0;
                    break;
                case "c":
                {
                    if (count >= budget) return count;
                    var refText = reader.GetAttribute("r");
                    int row = rowIndex, col = colIndex;
                    if (refText != null && CellRef.TryParse(refText, out var pr, out var pc)) { row = pr; col = pc; }
                    colIndex = col + 1;
                    var type = reader.GetAttribute("t") ?? "n";
                    var styleIndex = int.TryParse(reader.GetAttribute("s"), out var si) && si >= 0 && si < styles.Count ? styles[si] : null;
                    string? value = null, formula = null, inline = null;
                    string? fType = null, fRef = null, fSi = null;
                    if (!reader.IsEmptyElement)
                    {
                        using var cell = reader.ReadSubtree();
                        cell.Read(); // <c>
                        cell.Read();
                        // ReadElementContentAsString moves on by itself, so only call Read() when nothing was consumed.
                        while (!cell.EOF)
                        {
                            if (cell.NodeType == XmlNodeType.Element && cell.LocalName == "v") { value = cell.ReadElementContentAsString(); continue; }
                            if (cell.NodeType == XmlNodeType.Element && cell.LocalName == "f")
                            {
                                fType = cell.GetAttribute("t");
                                fRef = cell.GetAttribute("ref");
                                fSi = cell.GetAttribute("si");
                                if (cell.IsEmptyElement) { formula = ""; cell.Read(); }
                                else formula = cell.ReadElementContentAsString();
                                continue;
                            }
                            if (cell.NodeType == XmlNodeType.Element && cell.LocalName == "is") { inline = RichText(cell); continue; }
                            cell.Read();
                        }
                    }
                    string raw;
                    if (formula != null && (formula.Length > 0 || (fType == "shared" && fSi != null)))
                    {
                        if (fType == "shared" && fSi != null)
                        {
                            if (formula.Length > 0) masters[fSi] = (row, col, formula);
                            else if (masters.TryGetValue(fSi, out var m)) formula = Formula.Shift("=" + m.Formula, row - m.Row, col - m.Col)[1..];
                        }
                        raw = formula.Length > 0 ? "=" + formula : value ?? "";
                    }
                    else
                    {
                        raw = type switch
                        {
                            "s" => int.TryParse(value, out var idx) && idx >= 0 && idx < shared.Count ? AsText(shared[idx]) : "",
                            "inlineStr" => AsText(inline ?? ""),
                            "str" => AsText(value ?? ""),
                            "b" => value == "1" ? "TRUE" : "FALSE",
                            "e" => value ?? "",
                            "d" => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
                                ? CellFormat.DateToSerial(d).ToString("R", CultureInfo.InvariantCulture) : AsText(value ?? ""),
                            _ => value != null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                                ? (date1904 && CellFormat.IsDateFormat(styleIndex?.Format) ? n + 1462 : n).ToString("R", CultureInfo.InvariantCulture) : ""
                        };
                    }
                    if (raw.Length == 0 && (styleIndex == null || (styleIndex.Style == CellStyle.None && styleIndex.Format == null))) break;
                    wb.SetCell(sheet, row, col, new CellRecord { Row = row, Col = col, Value = raw, Style = (int)(styleIndex?.Style ?? CellStyle.None), Format = styleIndex?.Format });
                    count++;
                    break;
                }
            }
        }
        return count;
    }

    /// <summary>
    /// The text of a rich string (&lt;si&gt; or &lt;is&gt;): its &lt;t&gt; parts, without the phonetic guides (&lt;rPh&gt;).
    /// Leaves the reader just after the element.
    /// </summary>
    private static string RichText(XmlReader reader)
    {
        var sb = new StringBuilder();
        if (reader.IsEmptyElement) { reader.Read(); return ""; }
        using (var sub = reader.ReadSubtree())
        {
            sub.Read();
            sub.Read();
            while (!sub.EOF)
            {
                if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "rPh") { sub.Skip(); continue; }
                if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "t") { sb.Append(sub.ReadElementContentAsString()); continue; }
                sub.Read();
            }
        }
        reader.Read();
        return sb.ToString();
    }

    /// <summary>Text that would read as a number or formula keeps its leading apostrophe, like in Excel.</summary>
    private static string AsText(string text)
    {
        if (text.Length == 0) return text;
        if (text[0] is '=' or '\'') return "'" + text;
        var (value, _) = CellInput.Parse(text, null);
        return value.IsText ? text : "'" + text;
    }

    private static List<string> SharedStrings(ZipArchive zip, string path)
    {
        var list = new List<string>();
        var entry = Entry(zip, path);
        if (entry == null) return list;
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        reader.Read();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si") { list.Add(RichText(reader)); continue; }
            reader.Read();
        }
        return list;
    }

    private static readonly Dictionary<int, string> BuiltInFormats = new()
    {
        [1] = "0", [2] = "0.00", [3] = "#,##0", [4] = "#,##0.00", [9] = "0%", [10] = "0.00%", [11] = "0.00E+00",
        [15] = "d-mmm-yy", [16] = "d-mmm", [17] = "mmm-yy", [18] = "h:mm AM/PM", [19] = "h:mm:ss AM/PM", [20] = "h:mm", [21] = "h:mm:ss",
        [37] = "#,##0 ;(#,##0)", [38] = "#,##0 ;(#,##0)", [39] = "#,##0.00;(#,##0.00)", [40] = "#,##0.00;(#,##0.00)",
        [45] = "mm:ss", [46] = "[h]:mm:ss", [47] = "mm:ss.0", [48] = "##0.0E+0", [49] = "@",
    };

    private static List<XfStyle> Styles(XDocument? xml)
    {
        var result = new List<XfStyle>();
        var root = xml?.Root;
        if (root == null) return result;
        var custom = new Dictionary<int, string>();
        foreach (var f in root.Element(S + "numFmts")?.Elements(S + "numFmt") ?? [])
            if (int.TryParse((string?)f.Attribute("numFmtId"), out var id) && (string?)f.Attribute("formatCode") is { } code) custom[id] = code;
        var fonts = (root.Element(S + "fonts")?.Elements(S + "font") ?? []).Select(f =>
        {
            var style = CellStyle.None;
            if (On(f.Element(S + "b"))) style |= CellStyle.Bold;
            if (On(f.Element(S + "i"))) style |= CellStyle.Italic;
            if (On(f.Element(S + "strike"))) style |= CellStyle.Strike;
            return style;
        }).ToList();
        foreach (var xf in root.Element(S + "cellXfs")?.Elements(S + "xf") ?? [])
        {
            int numFmt = int.TryParse((string?)xf.Attribute("numFmtId"), out var n) ? n : 0;
            int font = int.TryParse((string?)xf.Attribute("fontId"), out var fi) ? fi : 0;
            string? format = numFmt switch
            {
                0 => null,
                14 or (>= 27 and <= 36) or (>= 50 and <= 58) => CellFormat.DateCode(),
                22 => CellFormat.DateCode() + " hh:mm",
                _ => custom.TryGetValue(numFmt, out var c) ? c : BuiltInFormats.GetValueOrDefault(numFmt)
            };
            if (format == "General") format = null;
            var style = font >= 0 && font < fonts.Count ? fonts[font] : CellStyle.None;
            style |= (string?)xf.Element(S + "alignment")?.Attribute("horizontal") switch
            {
                "left" => CellStyle.Left,
                "center" or "centerContinuous" => CellStyle.Center,
                "right" => CellStyle.Right,
                _ => CellStyle.None
            };
            result.Add(new XfStyle(format, style));
        }
        return result;
    }

    private static bool On(XElement? e) => e != null && (string?)e.Attribute("val") is null or "1" or "true";

    private static ZipArchiveEntry? Entry(ZipArchive zip, string path) =>
        zip.GetEntry(path) ?? zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, path, StringComparison.OrdinalIgnoreCase));

    private static XDocument? Xml(ZipArchive zip, string path)
    {
        var entry = Entry(zip, path);
        if (entry == null) return null;
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    // ===================== .xls (Excel 97–2003) =====================

    public static Workbook ReadXls(byte[] bytes, List<string> notes)
    {
        TextFiles.EnsureCodePages();
        var wb = new Workbook();
        using var stream = new MemoryStream(bytes);
        using var reader = ExcelReaderFactory.CreateReader(stream, new ExcelReaderConfiguration { FallbackEncoding = Encoding.GetEncoding(1252) });
        int total = 0;
        do
        {
            var sheet = new Sheet(wb.UniqueSheetName(string.IsNullOrWhiteSpace(reader.Name) ? "Sheet" : reader.Name));
            wb.Sheets.Add(sheet);
            int row = 0;
            while (reader.Read() && total < MaxCells)
            {
                for (int c = 0; c < reader.FieldCount; c++)
                {
                    object? value;
                    try { value = reader.GetValue(c); } catch { continue; }
                    if (value == null) continue;
                    string? format = null;
                    try { format = reader.GetNumberFormatString(c); } catch { /* no format */ }
                    if (format is "General" or "") format = null;
                    string raw = value switch
                    {
                        double d => d.ToString("R", CultureInfo.InvariantCulture),
                        float f => ((double)f).ToString("R", CultureInfo.InvariantCulture),
                        int or long or short or decimal => System.Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
                        bool b => b ? "TRUE" : "FALSE",
                        DateTime dt => CellFormat.DateToSerial(dt).ToString("R", CultureInfo.InvariantCulture),
                        TimeSpan ts => ts.TotalDays.ToString("R", CultureInfo.InvariantCulture),
                        string s => AsText(s),
                        _ => AsText(value.ToString() ?? "")
                    };
                    if (value is DateTime && !CellFormat.IsDateFormat(format)) format = CellFormat.DateCode();
                    wb.SetCell(sheet, row, c, new CellRecord { Row = row, Col = c, Value = raw, Format = format });
                    total++;
                }
                row++;
            }
            for (int c = 0; c < reader.FieldCount && c < 512; c++)
            {
                try
                {
                    var w = reader.GetColumnWidth(c);
                    if (w > 0 && Math.Abs(w - 8.43) > 0.01) sheet.Widths[c] = Math.Clamp(Math.Round(w * 7 + 5), Sheet.MinWidth, Sheet.MaxWidth);
                }
                catch { /* widths are optional */ }
            }
            if (total >= MaxCells) { notes.Add($"Only the first {MaxCells:N0} cells were brought in."); break; }
        } while (reader.NextResult());
        if (wb.Sheets.Count == 0) wb.Sheets.Add(new Sheet("Sheet1"));
        notes.Add("Formulas in old .xls files come in as their results.");
        wb.Recalculate();
        return wb;
    }

    // ===================== .csv / .tsv =====================

    public static Workbook ReadCsv(byte[] bytes, char? separator, string sheetName)
    {
        var text = TextFiles.Decode(bytes);
        char sep = separator ?? DetectSeparator(text);
        var wb = new Workbook();
        var sheet = new Sheet(string.IsNullOrWhiteSpace(sheetName) ? "Sheet1" : sheetName.Length > 31 ? sheetName[..31] : sheetName);
        wb.Sheets.Add(sheet);
        int row = 0, total = 0;
        foreach (var fields in ParseDelimited(text, sep))
        {
            for (int c = 0; c < fields.Count && c < CellRef.MaxCols; c++)
            {
                var v = fields[c];
                if (v.Length == 0) continue;
                // A leading "=" in data is text here (CSV files don't carry formulas you meant to run).
                wb.SetRaw(sheet, row, c, v[0] is '=' ? "'" + v : v);
                if (++total >= MaxCells) break;
            }
            if (total >= MaxCells) break;
            row++;
        }
        return wb;
    }

    private static char DetectSeparator(string text)
    {
        var lines = text.Split('\n').Take(30).ToList();
        char best = ',';
        int bestScore = -1;
        foreach (var candidate in (char[])['\t', ';', ',', '|'])
        {
            var counts = lines.Where(l => l.Trim().Length > 0).Select(l => CountOutsideQuotes(l, candidate)).ToList();
            if (counts.Count == 0) continue;
            int min = counts.Min();
            // The separator appears on every line, and about as often on each.
            int score = min > 0 ? min * 100 - (counts.Max() - min) : 0;
            if (score > bestScore) { bestScore = score; best = candidate; }
        }
        return bestScore > 0 ? best : ',';
    }

    private static int CountOutsideQuotes(string line, char c)
    {
        int n = 0;
        bool quote = false;
        foreach (var ch in line)
        {
            if (ch == '"') quote = !quote;
            else if (ch == c && !quote) n++;
        }
        return n;
    }

    public static IEnumerable<List<string>> ParseDelimited(string text, char sep)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(ch);
                continue;
            }
            if (ch == '"' && field.Length == 0) { quoted = true; any = true; continue; }
            if (ch == sep) { row.Add(field.ToString()); field.Clear(); any = true; continue; }
            if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                yield return row;
                row = [];
                any = false;
                continue;
            }
            field.Append(ch);
            any = true;
        }
        if (any || field.Length > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }

    /// <summary>
    /// One sheet as CSV for Excel on this PC: the Windows region's list separator and decimal mark (Excel reads CSV
    /// that way, whatever language Satchel shows dates in), UTF-8 with a BOM.
    /// </summary>
    public static byte[] WriteCsv(Sheet sheet, char? separator = null)
    {
        var culture = App.SystemCulture;
        char sep = separator ?? (culture.TextInfo.ListSeparator is { Length: 1 } ls ? ls[0] : ',');
        var sb = new StringBuilder();
        for (int r = 0; r < sheet.UsedRows; r++)
        {
            for (int c = 0; c < sheet.UsedCols; c++)
            {
                if (c > 0) sb.Append(sep);
                var cell = sheet.Get(r, c);
                if (cell == null) continue;
                string text = cell.Value.Kind switch
                {
                    XKind.Number when CellFormat.IsDateFormat(cell.Format) => cell.Display,
                    XKind.Number => cell.Value.Number.ToString("0.###############", culture),
                    _ => Formula.ToText(cell.Value)
                };
                if (text.IndexOfAny([sep, '"', '\n', '\r']) >= 0) text = "\"" + text.Replace("\"", "\"\"") + "\"";
                sb.Append(text);
            }
            sb.Append("\r\n");
        }
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    // ===================== writing .xlsx =====================

    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private static readonly HashSet<string> NewerFunctions = ["IFS", "IFNA", "XLOOKUP", "CONCAT", "TEXTJOIN", "DAYS", "XOR"];

    public static byte[] WriteXlsx(Workbook wb)
    {
        // Every distinct look (font, alignment, number format) becomes one cell style.
        var looks = new List<(CellStyle Style, string? Format)> { (CellStyle.None, null) };
        var lookIndex = new Dictionary<(CellStyle, string?), int> { [(CellStyle.None, null)] = 0 };
        foreach (var sheet in wb.Sheets)
            foreach (var (_, _, cell) in sheet.All())
                if (!lookIndex.ContainsKey((cell.Style, cell.Format)))
                {
                    lookIndex[(cell.Style, cell.Format)] = looks.Count;
                    looks.Add((cell.Style, cell.Format));
                }
        var formats = looks.Select(l => l.Format).Where(f => f != null).Distinct().ToList();
        var fonts = looks.Select(l => l.Style & (CellStyle.Bold | CellStyle.Italic | CellStyle.Strike)).Distinct().ToList();
        if (fonts[0] != CellStyle.None) fonts.Insert(0, CellStyle.None);
        if (!fonts.Contains(CellStyle.None)) fonts.Insert(0, CellStyle.None);

        var names = new List<string>();
        foreach (var s in wb.Sheets)
        {
            var name = new string(s.Name.Select(ch => ch is ':' or '\\' or '/' or '?' or '*' or '[' or ']' ? ' ' : ch).ToArray()).Trim();
            if (name.Length == 0) name = "Sheet";
            if (name.Length > 31) name = name[..31];
            var unique = name;
            for (int i = 2; names.Contains(unique, StringComparer.OrdinalIgnoreCase); i++) unique = (name.Length > 28 ? name[..28] : name) + " " + i;
            names.Add(unique);
        }

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "[Content_Types].xml", w =>
            {
                w.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types");
                Default(w, "rels", "application/vnd.openxmlformats-package.relationships+xml");
                Default(w, "xml", "application/xml");
                Override(w, "/xl/workbook.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
                Override(w, "/xl/styles.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
                for (int i = 0; i < wb.Sheets.Count; i++) Override(w, $"/xl/worksheets/sheet{i + 1}.xml", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
                w.WriteEndElement();
            });
            Write(zip, "_rels/.rels", w =>
            {
                w.WriteStartElement("Relationships", Rel.NamespaceName);
                Relationship(w, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml");
                w.WriteEndElement();
            });
            Write(zip, "xl/workbook.xml", w =>
            {
                w.WriteStartElement("workbook", MainNs);
                w.WriteAttributeString("xmlns", "r", null, RelNs);
                w.WriteStartElement("sheets", MainNs);
                for (int i = 0; i < wb.Sheets.Count; i++)
                {
                    w.WriteStartElement("sheet", MainNs);
                    w.WriteAttributeString("name", names[i]);
                    w.WriteAttributeString("sheetId", (i + 1).ToString(CultureInfo.InvariantCulture));
                    w.WriteAttributeString("id", RelNs, $"rId{i + 1}");
                    w.WriteEndElement();
                }
                w.WriteEndElement();
                // Excel recalculates everything when it opens the file.
                w.WriteStartElement("calcPr", MainNs);
                w.WriteAttributeString("fullCalcOnLoad", "1");
                w.WriteEndElement();
                w.WriteEndElement();
            });
            Write(zip, "xl/_rels/workbook.xml.rels", w =>
            {
                w.WriteStartElement("Relationships", Rel.NamespaceName);
                for (int i = 0; i < wb.Sheets.Count; i++)
                    Relationship(w, $"rId{i + 1}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", $"worksheets/sheet{i + 1}.xml");
                Relationship(w, $"rId{wb.Sheets.Count + 1}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml");
                w.WriteEndElement();
            });
            Write(zip, "xl/styles.xml", w => WriteStyles(w, looks, formats, fonts));
            for (int i = 0; i < wb.Sheets.Count; i++)
            {
                var sheet = wb.Sheets[i];
                Write(zip, $"xl/worksheets/sheet{i + 1}.xml", w => WriteSheet(w, sheet, lookIndex));
            }
        }
        return ms.ToArray();
    }

    private static void WriteStyles(XmlWriter w, List<(CellStyle Style, string? Format)> looks, List<string?> formats, List<CellStyle> fonts)
    {
        int FormatId(string? code) => code switch
        {
            null => 0,
            "0" => 1, "0.00" => 2, "#,##0" => 3, "#,##0.00" => 4, "0%" => 9, "0.00%" => 10, "@" => 49,
            _ => 164 + formats.IndexOf(code)
        };
        w.WriteStartElement("styleSheet", MainNs);
        var custom = formats.Where(f => FormatId(f) >= 164).ToList();
        if (custom.Count > 0)
        {
            w.WriteStartElement("numFmts", MainNs);
            w.WriteAttributeString("count", custom.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var f in custom)
            {
                w.WriteStartElement("numFmt", MainNs);
                w.WriteAttributeString("numFmtId", FormatId(f).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("formatCode", f!);
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteStartElement("fonts", MainNs);
        w.WriteAttributeString("count", fonts.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var f in fonts)
        {
            w.WriteStartElement("font", MainNs);
            if (f.HasFlag(CellStyle.Bold)) w.WriteElementString("b", MainNs, null);
            if (f.HasFlag(CellStyle.Italic)) w.WriteElementString("i", MainNs, null);
            if (f.HasFlag(CellStyle.Strike)) w.WriteElementString("strike", MainNs, null);
            w.WriteStartElement("sz", MainNs); w.WriteAttributeString("val", "11"); w.WriteEndElement();
            w.WriteStartElement("name", MainNs); w.WriteAttributeString("val", "Calibri"); w.WriteEndElement();
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteRaw("<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>");
        w.WriteRaw("<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>");
        w.WriteRaw("<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>");
        w.WriteStartElement("cellXfs", MainNs);
        w.WriteAttributeString("count", looks.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var (style, format) in looks)
        {
            w.WriteStartElement("xf", MainNs);
            w.WriteAttributeString("numFmtId", FormatId(format).ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("fontId", fonts.IndexOf(style & (CellStyle.Bold | CellStyle.Italic | CellStyle.Strike)).ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("fillId", "0");
            w.WriteAttributeString("borderId", "0");
            w.WriteAttributeString("xfId", "0");
            if (format != null) w.WriteAttributeString("applyNumberFormat", "1");
            var align = style & CellStyle.AlignMask;
            if (align != CellStyle.None)
            {
                w.WriteAttributeString("applyAlignment", "1");
                w.WriteStartElement("alignment", MainNs);
                w.WriteAttributeString("horizontal", align == CellStyle.Left ? "left" : align == CellStyle.Center ? "center" : "right");
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteRaw("<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>");
        w.WriteEndElement();
    }

    private static void WriteSheet(XmlWriter w, Sheet sheet, Dictionary<(CellStyle, string?), int> lookIndex)
    {
        w.WriteStartElement("worksheet", MainNs);
        if (sheet.FrozenRows > 0)
        {
            w.WriteStartElement("sheetViews", MainNs);
            w.WriteStartElement("sheetView", MainNs);
            w.WriteAttributeString("workbookViewId", "0");
            w.WriteStartElement("pane", MainNs);
            w.WriteAttributeString("ySplit", sheet.FrozenRows.ToString(CultureInfo.InvariantCulture));
            w.WriteAttributeString("topLeftCell", CellRef.Name(sheet.FrozenRows, 0));
            w.WriteAttributeString("activePane", "bottomLeft");
            w.WriteAttributeString("state", "frozen");
            w.WriteEndElement();
            w.WriteEndElement();
            w.WriteEndElement();
        }
        if (sheet.Widths.Count > 0)
        {
            w.WriteStartElement("cols", MainNs);
            foreach (var (col, px) in sheet.Widths.OrderBy(p => p.Key))
            {
                w.WriteStartElement("col", MainNs);
                var n = (col + 1).ToString(CultureInfo.InvariantCulture);
                w.WriteAttributeString("min", n);
                w.WriteAttributeString("max", n);
                w.WriteAttributeString("width", Math.Round(Math.Max(1, (px - 5) / 7), 2).ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("customWidth", "1");
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteStartElement("sheetData", MainNs);
        foreach (var row in sheet.All().GroupBy(c => c.Row).OrderBy(g => g.Key))
        {
            w.WriteStartElement("row", MainNs);
            w.WriteAttributeString("r", (row.Key + 1).ToString(CultureInfo.InvariantCulture));
            foreach (var (r, c, cell) in row.OrderBy(x => x.Col))
            {
                w.WriteStartElement("c", MainNs);
                w.WriteAttributeString("r", CellRef.Name(r, c));
                int s = lookIndex.GetValueOrDefault((cell.Style, cell.Format));
                if (s > 0) w.WriteAttributeString("s", s.ToString(CultureInfo.InvariantCulture));
                if (cell.IsFormula)
                {
                    var v = cell.Value;
                    if (v.IsText) w.WriteAttributeString("t", "str");
                    else if (v.Kind == XKind.Bool) w.WriteAttributeString("t", "b");
                    else if (v.IsError) w.WriteAttributeString("t", "e");
                    w.WriteElementString("f", MainNs, CleanXml(ForExcel(cell.Raw)));
                    w.WriteElementString("v", MainNs, CleanXml(v.Kind switch
                    {
                        XKind.Number => v.Number.ToString("R", CultureInfo.InvariantCulture),
                        XKind.Bool => v.Number != 0 ? "1" : "0",
                        XKind.Error => Errors.All.Contains(v.ErrorCode) && v.ErrorCode != Errors.Cycle ? v.ErrorCode : Errors.Value,
                        _ => v.Text ?? ""
                    }));
                }
                else switch (cell.Value.Kind)
                {
                    case XKind.Number:
                        w.WriteElementString("v", MainNs, cell.Value.Number.ToString("R", CultureInfo.InvariantCulture));
                        break;
                    case XKind.Bool:
                        w.WriteAttributeString("t", "b");
                        w.WriteElementString("v", MainNs, cell.Value.Number != 0 ? "1" : "0");
                        break;
                    case XKind.Error:
                        w.WriteAttributeString("t", "e");
                        w.WriteElementString("v", MainNs, cell.Value.ErrorCode);
                        break;
                    case XKind.Text:
                        w.WriteAttributeString("t", "inlineStr");
                        w.WriteStartElement("is", MainNs);
                        w.WriteStartElement("t", MainNs);
                        w.WriteAttributeString("xml", "space", null, "preserve");
                        w.WriteString(CleanXml(cell.Value.Text ?? ""));
                        w.WriteEndElement();
                        w.WriteEndElement();
                        break;
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndElement();
        w.WriteEndElement();
    }

    /// <summary>A formula as Excel stores it: no "=", "," between arguments, newer functions with their _xlfn. prefix.</summary>
    private static string ForExcel(string raw)
    {
        var body = Formula.Normalize(raw)[1..];
        List<Formula.Token> tokens;
        try { tokens = Formula.Tokenize(body); }
        catch { return body; }
        var sb = new StringBuilder();
        int last = 0;
        foreach (var t in tokens)
        {
            if (t.Kind != Formula.T.Func) continue;
            var name = body.Substring(t.Start, t.Length).ToUpperInvariant();
            if (!NewerFunctions.Contains(name)) continue;
            sb.Append(body, last, t.Start - last).Append("_xlfn.").Append(name);
            last = t.End;
        }
        sb.Append(body, last, body.Length - last);
        return sb.ToString();
    }

    private static string CleanXml(string text) =>
        text.Any(ch => ch < 0x20 && ch is not ('\t' or '\n' or '\r')) ? new string(text.Where(ch => ch >= 0x20 || ch is '\t' or '\n' or '\r').ToArray()) : text;

    private static void Write(ZipArchive zip, string path, Action<XmlWriter> body)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var w = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        w.WriteStartDocument(true);
        body(w);
        w.WriteEndDocument();
    }

    private static void Default(XmlWriter w, string ext, string type)
    {
        w.WriteStartElement("Default", "http://schemas.openxmlformats.org/package/2006/content-types");
        w.WriteAttributeString("Extension", ext);
        w.WriteAttributeString("ContentType", type);
        w.WriteEndElement();
    }

    private static void Override(XmlWriter w, string part, string type)
    {
        w.WriteStartElement("Override", "http://schemas.openxmlformats.org/package/2006/content-types");
        w.WriteAttributeString("PartName", part);
        w.WriteAttributeString("ContentType", type);
        w.WriteEndElement();
    }

    private static void Relationship(XmlWriter w, string id, string type, string target)
    {
        w.WriteStartElement("Relationship", Rel.NamespaceName);
        w.WriteAttributeString("Id", id);
        w.WriteAttributeString("Type", type);
        w.WriteAttributeString("Target", target);
        w.WriteEndElement();
    }

    // ===================== Markdown =====================

    /// <summary>
    /// The workbook as Markdown tables (one per sheet, under its name). Number columns are right-aligned; very long
    /// sheets stop after <paramref name="maxRows"/> rows with a note.
    /// </summary>
    public static string ToMarkdown(Workbook wb, string? title, int maxRows = 1000, string? source = null)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title)) sb.Append("# ").Append(InlineBuilder.Escape(title)).Append("\n\n");
        bool many = wb.Sheets.Count(s => s.CellCount > 0) > 1;
        // A line that says what this is (it's also the document's excerpt in the Library list).
        var names = wb.Sheets.Where(s => s.CellCount > 0).Select(s => s.Name).ToList();
        var intro = string.Join(" — ", new[]
        {
            source != null ? "From " + source : null,
            many ? $"{names.Count} sheets: {string.Join(", ", names)}" : null
        }.Where(s => s != null));
        if (intro.Length > 0) sb.Append('*').Append(InlineBuilder.Escape(intro)).Append(".*\n\n");
        foreach (var sheet in wb.Sheets)
        {
            var cells = sheet.All().Where(c => c.Cell.Display.Trim().Length > 0).ToList();
            if (cells.Count == 0) continue;
            int r1 = cells.Min(c => c.Row), r2 = cells.Max(c => c.Row), c1 = cells.Min(c => c.Col), c2 = cells.Max(c => c.Col);
            if (many) sb.Append("## ").Append(InlineBuilder.Escape(sheet.Name)).Append("\n\n");
            string Text(int r, int c) => sheet.Get(r, c) is { } cell ? InlineBuilder.Escape(cell.Display.Replace("\r", "").Replace("\n", " "), inTable: true) : "";
            int cols = c2 - c1 + 1;
            if (cols == 1)
            {
                // One column: a plain list reads better than a one-column table.
                for (int r = r1; r <= Math.Min(r2, r1 + maxRows); r++) { var t = Text(r, c1); if (t.Length > 0) sb.Append("- ").Append(t).Append('\n'); }
                sb.Append('\n');
                continue;
            }
            int last = Math.Min(r2, r1 + maxRows);
            sb.Append('|');
            for (int c = c1; c <= c2; c++) { var h = Text(r1, c); sb.Append(' ').Append(h.Length > 0 ? h : CellRef.ColumnName(c)).Append(" |"); }
            sb.Append("\n|");
            for (int c = c1; c <= c2; c++)
            {
                int numbers = 0, filled = 0;
                for (int r = r1 + 1; r <= last; r++) if (sheet.Get(r, c) is { } cell && !cell.IsBlank) { filled++; if (cell.Value.IsNumber) numbers++; }
                sb.Append(filled > 0 && numbers * 2 > filled ? " ---: |" : " --- |");
            }
            sb.Append('\n');
            for (int r = r1 + 1; r <= last; r++)
            {
                sb.Append('|');
                for (int c = c1; c <= c2; c++) { var t = Text(r, c); sb.Append(' ').Append(t.Length > 0 ? t : " ").Append(" |"); }
                sb.Append('\n');
            }
            if (r2 > last) sb.Append('\n').Append($"*… and {r2 - last:N0} more rows — open the file in Sheets to see them all.*\n");
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd() + "\n";
    }
}
