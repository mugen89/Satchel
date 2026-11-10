using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Satchel.Services.Import;

/// <summary>
/// Word (.docx) → Markdown: headings, bold / italic / strike / code, bullet and numbered lists (nested), links,
/// tables, footnotes and pictures. Reads the Office Open XML parts directly (no Word needed).
/// </summary>
public static partial class DocxConverter
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    private static readonly XNamespace M = "http://schemas.openxmlformats.org/officeDocument/2006/math";
    private static readonly XNamespace DC = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace MC = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    private static readonly HashSet<string> MonoFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Consolas", "Courier New", "Courier", "Cascadia Code", "Cascadia Mono", "Lucida Console", "Lucida Sans Typewriter",
        "Menlo", "Monaco", "Source Code Pro", "Fira Code", "Fira Mono", "JetBrains Mono", "Roboto Mono", "Ubuntu Mono", "DejaVu Sans Mono"
    };

    private sealed record StyleInfo(string Id, string Name, string? BasedOn, int? Outline, string? NumId, int Ilvl, bool? Bold, bool? Italic, bool Mono, string Type);

    private sealed class Context
    {
        public required ZipArchive Zip;
        public required string Folder;
        public required Dictionary<string, (string Target, bool External)> Rels;
        public required Dictionary<string, StyleInfo> Styles;
        public required Dictionary<string, Dictionary<int, (string Format, int Start)>> Numbering;
        public required Dictionary<string, XElement> Footnotes;
        public readonly List<DocImage> Images = [];
        public readonly List<string> Notes = [];
        public readonly List<string> UsedFootnotes = [];
        public readonly Dictionary<string, int[]> ListCounters = [];
        public int SkippedImages;
    }

    /// <summary>Complex fields (HYPERLINK …) span several runs: begin, instruction, separate, result, end.</summary>
    private sealed class FieldState
    {
        public readonly StringBuilder Instruction = new();
        public bool InResult;
        public int ResultStart;
    }

    public static ConvertedDoc Convert(byte[] bytes)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var main = MainPart(zip) ?? "word/document.xml";
        var doc = Xml(zip, main) ?? throw new InvalidDataException("This file has no Word document inside.");
        int slash = main.LastIndexOf('/');
        var folder = slash >= 0 ? main[..(slash + 1)] : "";
        var ctx = new Context
        {
            Zip = zip,
            Folder = folder,
            Rels = Relationships(zip, folder + "_rels/" + main[(slash + 1)..] + ".rels"),
            Styles = Styles(Xml(zip, folder + "styles.xml")),
            Numbering = Numbering(Xml(zip, folder + "numbering.xml")),
            Footnotes = Footnotes(Xml(zip, folder + "footnotes.xml")),
        };
        var output = new MarkdownOut();
        var body = doc.Root?.Element(W + "body") ?? throw new InvalidDataException("This Word document is empty.");
        Blocks(ctx, body.Elements(), output);

        // Footnotes at the end, in the order they were referenced.
        if (ctx.UsedFootnotes.Count > 0)
        {
            foreach (var id in ctx.UsedFootnotes)
            {
                if (!ctx.Footnotes.TryGetValue(id, out var note)) continue;
                var text = string.Join(" ", Descendants(note, "p").Select(p => Inline(ctx, p).ToMarkdown(inTable: true)).Where(t => t.Length > 0));
                output.Raw($"[^{id}]: {text}");
            }
        }
        if (ctx.SkippedImages > 0)
            ctx.Notes.Add(ctx.SkippedImages == 1 ? "1 picture is in a format that can't be shown (EMF/WMF) and was left out."
                : $"{ctx.SkippedImages} pictures are in a format that can't be shown (EMF/WMF) and were left out.");

        var title = Xml(zip, "docProps/core.xml")?.Root?.Element(DC + "title")?.Value.Trim();
        return new ConvertedDoc(output.ToString(), string.IsNullOrWhiteSpace(title) ? null : title, ctx.Images, ctx.Notes);
    }

    // ----- parts -----

    private static XDocument? Xml(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path) ?? zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, path, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return null;
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    private static byte[]? Bytes(ZipArchive zip, string path)
    {
        var entry = zip.GetEntry(path) ?? zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, path, StringComparison.OrdinalIgnoreCase));
        if (entry == null || entry.Length > 64 * 1024 * 1024) return null;
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static string? MainPart(ZipArchive zip)
    {
        var rels = Xml(zip, "_rels/.rels");
        var target = rels?.Root?.Elements(Rel + "Relationship")
            .FirstOrDefault(r => ((string?)r.Attribute("Type"))?.EndsWith("/officeDocument", StringComparison.Ordinal) == true)?.Attribute("Target")?.Value;
        return target?.TrimStart('/');
    }

    private static Dictionary<string, (string, bool)> Relationships(ZipArchive zip, string path)
    {
        var map = new Dictionary<string, (string, bool)>();
        foreach (var r in Xml(zip, path)?.Root?.Elements(Rel + "Relationship") ?? [])
        {
            var id = (string?)r.Attribute("Id");
            var target = (string?)r.Attribute("Target");
            if (id != null && target != null) map[id] = (target, string.Equals((string?)r.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase));
        }
        return map;
    }

    private static string? Val(XElement? e, string child) => e?.Element(W + child)?.Attribute(W + "val")?.Value;

    private static Dictionary<string, StyleInfo> Styles(XDocument? xml)
    {
        var map = new Dictionary<string, StyleInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in xml?.Root?.Elements(W + "style") ?? [])
        {
            var id = (string?)s.Attribute(W + "styleId");
            if (id == null) continue;
            var pPr = s.Element(W + "pPr");
            var rPr = s.Element(W + "rPr");
            var numPr = pPr?.Element(W + "numPr");
            var font = rPr?.Element(W + "rFonts")?.Attribute(W + "ascii")?.Value;
            map[id] = new StyleInfo(id, Val(s, "name") ?? id, Val(s, "basedOn"),
                int.TryParse(Val(pPr, "outlineLvl"), out var o) ? o : null,
                Val(numPr, "numId"), int.TryParse(Val(numPr, "ilvl"), out var l) ? l : 0,
                Toggle(rPr, "b"), Toggle(rPr, "i"), font != null && MonoFonts.Contains(font), (string?)s.Attribute(W + "type") ?? "paragraph");
        }
        return map;
    }

    /// <summary>numId → level → (number format, start). "bullet" means a bulleted level.</summary>
    private static Dictionary<string, Dictionary<int, (string, int)>> Numbering(XDocument? xml)
    {
        var result = new Dictionary<string, Dictionary<int, (string, int)>>();
        if (xml?.Root == null) return result;
        var abstracts = new Dictionary<string, Dictionary<int, (string, int)>>();
        foreach (var a in xml.Root.Elements(W + "abstractNum"))
        {
            var id = (string?)a.Attribute(W + "abstractNumId");
            if (id == null) continue;
            var levels = new Dictionary<int, (string, int)>();
            foreach (var lvl in a.Elements(W + "lvl"))
                if (int.TryParse((string?)lvl.Attribute(W + "ilvl"), out var i))
                    levels[i] = (Val(lvl, "numFmt") ?? "decimal", int.TryParse(Val(lvl, "start"), out var st) ? st : 1);
            abstracts[id] = levels;
        }
        foreach (var n in xml.Root.Elements(W + "num"))
        {
            var id = (string?)n.Attribute(W + "numId");
            var abs = Val(n, "abstractNumId");
            if (id != null && abs != null && abstracts.TryGetValue(abs, out var levels)) result[id] = levels;
        }
        return result;
    }

    private static Dictionary<string, XElement> Footnotes(XDocument? xml)
    {
        var map = new Dictionary<string, XElement>();
        foreach (var f in xml?.Root?.Elements(W + "footnote") ?? [])
        {
            var type = (string?)f.Attribute(W + "type");
            if (type is "separator" or "continuationSeparator" or "continuationNotice") continue;
            if ((string?)f.Attribute(W + "id") is { } id) map[id] = f;
        }
        return map;
    }

    /// <summary>On/off properties (&lt;w:b/&gt;, &lt;w:b w:val="0"/&gt;); null when not set here.</summary>
    private static bool? Toggle(XElement? rPr, string name)
    {
        var e = rPr?.Element(W + name);
        if (e == null) return null;
        var v = (string?)e.Attribute(W + "val");
        return v is null or "1" or "true" or "on";
    }

    // ----- style lookups (following basedOn) -----

    private static IEnumerable<StyleInfo> Chain(Context ctx, string? styleId)
    {
        for (int depth = 0; styleId != null && depth < 12 && ctx.Styles.TryGetValue(styleId, out var s); depth++)
        {
            yield return s;
            styleId = s.BasedOn;
        }
    }

    private static int HeadingLevel(Context ctx, string? styleId, XElement? pPr)
    {
        if (int.TryParse(Val(pPr, "outlineLvl"), out var direct) && direct < 9) return direct + 1;
        foreach (var s in Chain(ctx, styleId))
        {
            var m = HeadingName().Match(s.Name);
            if (m.Success) return int.Parse(m.Groups[1].Value);
            if (s.Name.Equals("Title", StringComparison.OrdinalIgnoreCase)) return 1;
            if (s.Name.Equals("Subtitle", StringComparison.OrdinalIgnoreCase)) return 2;
            if (s.Outline is { } o) return o < 9 ? o + 1 : 0;
        }
        return 0;
    }

    private static bool StyleNamed(Context ctx, string? styleId, Func<string, bool> test) => Chain(ctx, styleId).Any(s => test(s.Name) || test(s.Id));

    // ----- blocks -----

    private static IEnumerable<XElement> Unwrap(IEnumerable<XElement> nodes)
    {
        foreach (var n in nodes)
        {
            if (n.Name == W + "sdt") { foreach (var c in Unwrap(n.Element(W + "sdtContent")?.Elements() ?? [])) yield return c; }
            else if (n.Name == W + "customXml" || n.Name == W + "smartTag" || n.Name == W + "ins" || n.Name == W + "moveTo") { foreach (var c in Unwrap(n.Elements())) yield return c; }
            else if (n.Name == MC + "AlternateContent") { foreach (var c in Unwrap((n.Element(MC + "Choice") ?? n.Element(MC + "Fallback"))?.Elements() ?? [])) yield return c; }
            else yield return n;
        }
    }

    private static IEnumerable<XElement> Descendants(XElement e, string localName) => e.Descendants(W + localName);

    private static void Blocks(Context ctx, IEnumerable<XElement> nodes, MarkdownOut output)
    {
        foreach (var node in Unwrap(nodes))
        {
            if (node.Name == W + "p") Paragraph(ctx, node, output);
            else if (node.Name == W + "tbl") Table(ctx, node, output);
        }
    }

    private static void Paragraph(Context ctx, XElement p, MarkdownOut output)
    {
        var pPr = p.Element(W + "pPr");
        var styleId = Val(pPr, "pStyle");
        var inline = Inline(ctx, p);
        if (!inline.HasContent) return;

        int heading = HeadingLevel(ctx, styleId, pPr);
        if (heading > 0)
        {
            output.Heading(heading, inline.ToMarkdown());
            return;
        }

        // Lists: direct numbering, or numbering that comes with the style ("List Bullet", "List Number").
        var numPr = pPr?.Element(W + "numPr");
        string? numId = Val(numPr, "numId");
        int ilvl = int.TryParse(Val(numPr, "ilvl"), out var l) ? l : 0;
        if (numId == null && Chain(ctx, styleId).FirstOrDefault(s => s.NumId != null) is { } listStyle)
        {
            numId = listStyle.NumId;
            ilvl = listStyle.Ilvl;
        }
        if (numId is not null and not "0")
        {
            var format = ctx.Numbering.TryGetValue(numId, out var levels) && levels.TryGetValue(ilvl, out var f) ? f : ("bullet", 1);
            int? number = null;
            if (format.Item1 is not ("bullet" or "none"))
            {
                if (!ctx.ListCounters.TryGetValue(numId, out var counters)) ctx.ListCounters[numId] = counters = new int[9];
                int level = Math.Clamp(ilvl, 0, 8);
                counters[level] = counters[level] == 0 ? format.Item2 : counters[level] + 1;
                for (int deeper = level + 1; deeper < 9; deeper++) counters[deeper] = 0;
                number = counters[level];
            }
            output.ListItem(ilvl, number, inline.ToMarkdown(), numId);
            return;
        }
        if (numId == null && StyleNamed(ctx, styleId, n => n.StartsWith("List Bullet", StringComparison.OrdinalIgnoreCase)))
        {
            output.ListItem(0, null, inline.ToMarkdown(), "style-bullets");
            return;
        }

        if (StyleNamed(ctx, styleId, n => n.Contains("Code", StringComparison.OrdinalIgnoreCase) || n.Contains("Preformatted", StringComparison.OrdinalIgnoreCase) || n == "Source Code")
            || (ctx.Styles.TryGetValue(styleId ?? "", out var st) && st.Mono))
        {
            foreach (var line in inline.PlainText.Split('\n')) output.CodeLine(line);
            return;
        }
        if (StyleNamed(ctx, styleId, n => n.Contains("Quote", StringComparison.OrdinalIgnoreCase)))
        {
            output.Quote(inline.ToMarkdown());
            return;
        }
        output.Paragraph(inline.ToMarkdown());
    }

    private static readonly string[] LinkPrefixes = ["http://", "https://", "mailto:", "ftp://", "file:", "#"];

    /// <summary>The runs of a paragraph (or footnote) as formatted inline text.</summary>
    private static InlineBuilder Inline(Context ctx, XElement p)
    {
        var builder = new InlineBuilder();
        var styleId = Val(p.Element(W + "pPr"), "pStyle");
        // Bold or italic paragraph styles (e.g. a "Strong paragraph" style) apply to their runs; headings are bold anyway.
        var style = Chain(ctx, styleId).ToList();
        bool baseBold = style.Select(s => s.Bold).FirstOrDefault(b => b != null) ?? false;
        bool baseItalic = style.Select(s => s.Italic).FirstOrDefault(i => i != null) ?? false;
        if (HeadingLevel(ctx, styleId, p.Element(W + "pPr")) > 0) baseBold = baseItalic = false;
        var fields = new Stack<FieldState>();
        RunsOf(ctx, p.Elements(), builder, baseBold, baseItalic, null, fields);
        return builder;
    }

    private static void RunsOf(Context ctx, IEnumerable<XElement> nodes, InlineBuilder b, bool bold, bool italic, string? link, Stack<FieldState> fields)
    {
        foreach (var node in nodes)
        {
            var name = node.Name;
            if (name == W + "r") Run(ctx, node, b, bold, italic, link, fields);
            else if (name == W + "hyperlink")
            {
                string? url = null;
                if ((string?)node.Attribute(R + "id") is { } rid && ctx.Rels.TryGetValue(rid, out var rel)) url = rel.Target;
                else if ((string?)node.Attribute(W + "anchor") is { } anchor) url = null; // jumps inside the document: keep the text only
                RunsOf(ctx, node.Elements(), b, bold, italic, url ?? link, fields);
            }
            else if (name == W + "fldSimple")
            {
                var url = HyperlinkFrom((string?)node.Attribute(W + "instr"));
                RunsOf(ctx, node.Elements(), b, bold, italic, url ?? link, fields);
            }
            else if (name == W + "ins" || name == W + "moveTo" || name == W + "smartTag" || name == W + "customXml" || name == W + "dir" || name == W + "bdo")
                RunsOf(ctx, node.Elements(), b, bold, italic, link, fields);
            else if (name == W + "sdt") RunsOf(ctx, node.Element(W + "sdtContent")?.Elements() ?? [], b, bold, italic, link, fields);
            else if (name == MC + "AlternateContent") RunsOf(ctx, (node.Element(MC + "Choice") ?? node.Element(MC + "Fallback"))?.Elements() ?? [], b, bold, italic, link, fields);
            else if (name == M + "oMath" || name == M + "oMathPara")
                b.Add(string.Concat(node.Descendants(M + "t").Select(t => t.Value)), italic: true);
            // w:del, w:moveFrom (deleted text), bookmarks, comments and proofing marks are left out.
        }
    }

    private static void Run(Context ctx, XElement r, InlineBuilder b, bool baseBold, bool baseItalic, string? link, Stack<FieldState> fields)
    {
        var rPr = r.Element(W + "rPr");
        if (Toggle(rPr, "vanish") == true || Toggle(rPr, "webHidden") == true) return;
        var charStyle = Val(rPr, "rStyle");
        var chain = Chain(ctx, charStyle).ToList();
        bool bold = Toggle(rPr, "b") ?? chain.Select(s => s.Bold).FirstOrDefault(x => x != null) ?? baseBold;
        bool italic = Toggle(rPr, "i") ?? chain.Select(s => s.Italic).FirstOrDefault(x => x != null) ?? baseItalic;
        bool strike = (Toggle(rPr, "strike") ?? false) || (Toggle(rPr, "dstrike") ?? false);
        var font = rPr?.Element(W + "rFonts")?.Attribute(W + "ascii")?.Value;
        bool code = (font != null && MonoFonts.Contains(font)) || chain.Any(s => s.Mono || s.Name.Contains("Code", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Verbatim", StringComparison.OrdinalIgnoreCase));

        foreach (var c in r.Elements())
        {
            var n = c.Name;
            // Field codes: skip the instruction text, keep the result (and make HYPERLINK results links).
            if (n == W + "fldChar")
            {
                switch ((string?)c.Attribute(W + "fldCharType"))
                {
                    case "begin":
                        fields.Push(new FieldState());
                        break;
                    case "separate" when fields.Count > 0:
                        fields.Peek().InResult = true;
                        fields.Peek().ResultStart = b.Count;
                        break;
                    case "end" when fields.Count > 0:
                        var field = fields.Pop();
                        if (field.InResult && HyperlinkFrom(field.Instruction.ToString()) is { } url) b.LinkFrom(field.ResultStart, url);
                        break;
                }
                continue;
            }
            if (n == W + "instrText")
            {
                if (fields.Count > 0 && !fields.Peek().InResult) fields.Peek().Instruction.Append(c.Value);
                continue;
            }
            if (fields.Any(f => !f.InResult)) continue;

            if (n == W + "t") b.Add(c.Value, bold, italic, strike, code, link);
            else if (n == W + "tab" || n == W + "ptab") b.Add(" ", bold, italic, strike, code, link);
            else if (n == W + "br")
            {
                var type = (string?)c.Attribute(W + "type");
                if (type is null or "textWrapping") b.LineBreak();
            }
            else if (n == W + "cr") b.LineBreak();
            else if (n == W + "noBreakHyphen") b.Add("-", bold, italic, strike, code, link);
            else if (n == W + "sym")
            {
                var fontName = (string?)c.Attribute(W + "font") ?? "";
                if (!fontName.Contains("Symbol", StringComparison.OrdinalIgnoreCase) && !fontName.Contains("Wingdings", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse((string?)c.Attribute(W + "char"), System.Globalization.NumberStyles.HexNumber, null, out var code32) && code32 is > 31 and < 0xE000)
                    b.Add(char.ConvertFromUtf32(code32), bold, italic, strike, false, link);
            }
            else if (n == W + "drawing")
            {
                var alt = c.Descendants(WP + "docPr").Select(d => (string?)d.Attribute("descr") ?? (string?)d.Attribute("title")).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                foreach (var blip in c.Descendants(A + "blip"))
                    Image(ctx, b, (string?)blip.Attribute(R + "embed") ?? (string?)blip.Attribute(R + "link"), alt);
            }
            else if (n == W + "pict" || n == W + "object")
            {
                foreach (var data in c.Descendants(V + "imagedata"))
                    Image(ctx, b, (string?)data.Attribute(R + "id"), (string?)data.Attribute("title"));
            }
            else if (n == W + "footnoteReference")
            {
                if ((string?)c.Attribute(W + "id") is { } id && ctx.Footnotes.ContainsKey(id))
                {
                    if (!ctx.UsedFootnotes.Contains(id)) ctx.UsedFootnotes.Add(id);
                    b.AddRaw($"[^{id}]");
                }
            }
            else if (n == MC + "AlternateContent")
            {
                var inner = new XElement(W + "r", (c.Element(MC + "Choice") ?? c.Element(MC + "Fallback"))?.Elements());
                if (rPr != null) inner.AddFirst(new XElement(rPr));
                Run(ctx, inner, b, baseBold, baseItalic, link, fields);
            }
        }
    }

    private static string? HyperlinkFrom(string? instruction)
    {
        if (instruction == null) return null;
        var m = HyperlinkField().Match(instruction);
        if (!m.Success) return null;
        var target = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        if (instruction.Contains("\\l", StringComparison.Ordinal)) return null; // a jump inside the document
        return LinkPrefixes.Any(p => target.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || target.Contains('.') ? target : null;
    }

    private static void Image(Context ctx, InlineBuilder b, string? relId, string? alt)
    {
        if (relId == null || !ctx.Rels.TryGetValue(relId, out var rel)) return;
        alt = InlineBuilder.Escape((alt ?? "").Replace('\n', ' ').Trim());
        if (rel.External)
        {
            if (rel.Target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) b.AddRaw($"![{alt}]({InlineBuilder.LinkTarget(rel.Target)})");
            return;
        }
        var path = Combine(ctx.Folder, rel.Target);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".emf" or ".wmf" or ".emz" or ".wmz" or ".svg")
        {
            // Word keeps a PNG copy of SVG pictures next to them; EMF/WMF can't be shown.
            if (ext != ".svg") ctx.SkippedImages++;
            return;
        }
        var name = "media/" + Path.GetFileName(path);
        if (ctx.Images.All(i => i.Name != name))
        {
            var bytes = Bytes(ctx.Zip, path);
            if (bytes == null) return;
            var data = ImageService.Resize(bytes, 1800, photo: ext is ".jpg" or ".jpeg") ?? bytes;
            ctx.Images.Add(new DocImage(name, data));
        }
        b.AddRaw($"![{alt}]({name})");
    }

    private static string Combine(string folder, string target)
    {
        if (target.StartsWith('/')) return target.TrimStart('/');
        var parts = (folder + target).Split('/').ToList();
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i] == "..") { if (i > 0) { parts.RemoveAt(i - 1); i--; } parts.RemoveAt(i); i--; }
            else if (parts[i] == ".") { parts.RemoveAt(i); i--; }
        }
        return string.Join('/', parts);
    }

    // ----- tables -----

    private static void Table(Context ctx, XElement tbl, MarkdownOut output)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var tr in Unwrap(tbl.Elements()).Where(e => e.Name == W + "tr"))
        {
            var cells = new List<string>();
            foreach (var tc in Unwrap(tr.Elements()).Where(e => e.Name == W + "tc"))
            {
                var tcPr = tc.Element(W + "tcPr");
                int span = int.TryParse(Val(tcPr, "gridSpan"), out var s) ? Math.Clamp(s, 1, 64) : 1;
                var vMerge = tcPr?.Element(W + "vMerge");
                bool continuation = vMerge != null && (string?)vMerge.Attribute(W + "val") != "restart";
                cells.Add(continuation ? "" : CellText(ctx, tc));
                for (int i = 1; i < span; i++) cells.Add("");
            }
            if (cells.Count > 0) rows.Add(cells);
        }
        if (rows.Count == 0) return;
        // A one-column "table" is usually a box around text (a callout): keep it as paragraphs.
        if (rows.All(r => r.Count == 1))
        {
            foreach (var tc in tbl.Descendants(W + "tc"))
                Blocks(ctx, tc.Elements(), output);
            return;
        }
        output.Table(rows);
    }

    private static string CellText(Context ctx, XElement tc)
    {
        var parts = new List<string>();
        foreach (var node in Unwrap(tc.Elements()))
        {
            if (node.Name == W + "p")
            {
                var text = Inline(ctx, node).ToMarkdown(inTable: true);
                if (text.Length == 0) continue;
                bool listed = node.Element(W + "pPr")?.Element(W + "numPr") != null;
                parts.Add(listed ? "• " + text : text);
            }
            else if (node.Name == W + "tbl")
                parts.Add(string.Join(" · ", node.Descendants(W + "p").Select(p => Inline(ctx, p).ToMarkdown(inTable: true)).Where(t => t.Length > 0)));
        }
        return string.Join(" ", parts);
    }

    [GeneratedRegex(@"^heading\s*([1-9])$", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingName();

    [GeneratedRegex(@"HYPERLINK\s+(?:""([^""]+)""|(\S+))", RegexOptions.IgnoreCase)]
    private static partial Regex HyperlinkField();
}
