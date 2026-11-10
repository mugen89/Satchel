using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Satchel.Services.Import;

/// <summary>
/// Rich text (.rtf — and old ".doc" files that are really RTF) → Markdown. WPF reads the RTF into a FlowDocument;
/// headings are recognised by size, plus bold / italic / strike, links, lists, tables and pictures.
/// </summary>
public static class RtfConverter
{
    public static bool IsRtf(ReadOnlySpan<byte> data) => data.Length > 5 && data[0] == '{' && data[1] == '\\' && data[2] == 'r' && data[3] == 't' && data[4] == 'f';

    /// <summary>Safe to call from any thread: the FlowDocument is built on a short-lived STA thread.</summary>
    public static ConvertedDoc Convert(byte[] bytes)
    {
        ConvertedDoc? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var doc = new FlowDocument();
                var range = new TextRange(doc.ContentStart, doc.ContentEnd);
                using (var ms = new MemoryStream(bytes)) range.Load(ms, DataFormats.Rtf);
                result = FlowToMarkdown(doc);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (error != null) throw new InvalidDataException("This rich text file couldn't be read.", error);
        return result!;
    }

    private sealed class State
    {
        public double BaseSize = 12;
        public readonly List<DocImage> Images = [];
    }

    /// <summary>Any FlowDocument (RTF, pasted rich text) as Markdown. Must run on the document's thread.</summary>
    public static ConvertedDoc FlowToMarkdown(FlowDocument doc)
    {
        var state = new State { BaseSize = BodySize(doc) };
        var output = new MarkdownOut();
        Blocks(doc.Blocks, output, state, 0);
        return new ConvertedDoc(output.ToString(), null, state.Images, []);
    }

    /// <summary>The size most of the text is written in: anything clearly bigger is a heading.</summary>
    private static double BodySize(FlowDocument doc)
    {
        var sizes = new Dictionary<double, int>();
        foreach (var run in AllRuns(doc.Blocks))
        {
            var size = Math.Round(run.FontSize, 1);
            sizes[size] = sizes.GetValueOrDefault(size) + run.Text.Length;
        }
        return sizes.Count == 0 ? 12 : sizes.MaxBy(p => p.Value).Key;
    }

    private static IEnumerable<Run> AllRuns(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph p: foreach (var r in InlineRuns(p.Inlines)) yield return r; break;
                case Section s: foreach (var r in AllRuns(s.Blocks)) yield return r; break;
                case List l: foreach (var item in l.ListItems) foreach (var r in AllRuns(item.Blocks)) yield return r; break;
                case Table t:
                    foreach (var row in t.RowGroups.SelectMany(g => g.Rows))
                        foreach (var cell in row.Cells)
                            foreach (var r in AllRuns(cell.Blocks)) yield return r;
                    break;
            }
        }
    }

    private static IEnumerable<Run> InlineRuns(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Run run) yield return run;
            else if (inline is Span span) foreach (var r in InlineRuns(span.Inlines)) yield return r;
        }
    }

    private static void Blocks(IEnumerable<Block> blocks, MarkdownOut output, State state, int listLevel)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph p:
                    Paragraph(p, output, state);
                    break;
                case Section s:
                    Blocks(s.Blocks, output, state, listLevel);
                    break;
                case List list:
                    int n = list.StartIndex;
                    bool numbered = list.MarkerStyle is TextMarkerStyle.Decimal or TextMarkerStyle.LowerLatin or TextMarkerStyle.UpperLatin
                        or TextMarkerStyle.LowerRoman or TextMarkerStyle.UpperRoman;
                    foreach (var item in list.ListItems)
                    {
                        bool first = true;
                        foreach (var child in item.Blocks)
                        {
                            if (child is List nested) { Blocks([nested], output, state, listLevel + 1); continue; }
                            if (child is not Paragraph para) continue;
                            var b = new InlineBuilder();
                            Inlines(para.Inlines, b, state, para.FontWeight >= FontWeights.SemiBold, para.FontStyle == FontStyles.Italic, null);
                            if (!b.HasContent) continue;
                            if (first) output.ListItem(listLevel, numbered ? n : null, b.ToMarkdown());
                            else output.ListItem(listLevel + 1, null, b.ToMarkdown());
                            first = false;
                        }
                        n++;
                    }
                    break;
                case Table table:
                    var rows = new List<IReadOnlyList<string>>();
                    foreach (var row in table.RowGroups.SelectMany(g => g.Rows))
                    {
                        var cells = new List<string>();
                        foreach (var cell in row.Cells)
                        {
                            var parts = new List<string>();
                            foreach (var para in cell.Blocks.OfType<Paragraph>())
                            {
                                var b = new InlineBuilder();
                                Inlines(para.Inlines, b, state, para.FontWeight >= FontWeights.SemiBold, false, null);
                                var text = b.ToMarkdown(inTable: true);
                                if (text.Length > 0) parts.Add(text);
                            }
                            cells.Add(string.Join(" ", parts));
                            for (int i = 1; i < cell.ColumnSpan; i++) cells.Add("");
                        }
                        rows.Add(cells);
                    }
                    if (rows.Count > 0 && rows.Any(r => r.Count > 1)) output.Table(rows);
                    else foreach (var r in rows) foreach (var c in r) output.Paragraph(c);
                    break;
                case BlockUIContainer { Child: Image { Source: BitmapSource bitmap } }:
                    output.Raw(ImageMarkdown(bitmap, state));
                    break;
            }
        }
    }

    private static void Paragraph(Paragraph p, MarkdownOut output, State state)
    {
        var b = new InlineBuilder();
        Inlines(p.Inlines, b, state, p.FontWeight >= FontWeights.SemiBold, p.FontStyle == FontStyles.Italic, null);
        if (!b.HasContent) return;
        var runs = InlineRuns(p.Inlines).Where(r => r.Text.Trim().Length > 0).ToList();
        double size = runs.Count == 0 ? p.FontSize : runs.Max(r => r.FontSize);
        bool allBold = runs.Count > 0 && runs.All(r => r.FontWeight >= FontWeights.SemiBold);
        int textLength = b.PlainText.Length;
        int level = size >= state.BaseSize * 1.6 ? 1 : size >= state.BaseSize * 1.3 ? 2 : size >= state.BaseSize * 1.12 && allBold ? 3 : 0;
        if (level > 0 && textLength <= 160) output.Heading(level, Plain(b, p, state));
        else output.Paragraph(b.ToMarkdown());
    }

    /// <summary>Heading text without the bold markers (a heading is bold anyway).</summary>
    private static string Plain(InlineBuilder fallback, Paragraph p, State state)
    {
        var b = new InlineBuilder();
        Inlines(p.Inlines, b, state, false, false, null, ignoreBold: true);
        var text = b.ToMarkdown();
        return text.Length > 0 ? text : fallback.ToMarkdown();
    }

    private static void Inlines(InlineCollection inlines, InlineBuilder b, State state, bool bold, bool italic, string? link, bool ignoreBold = false)
    {
        foreach (var inline in inlines)
        {
            bool strike = inline.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true;
            switch (inline)
            {
                case Run run:
                    bool isBold = !ignoreBold && (bold || run.FontWeight >= FontWeights.SemiBold);
                    bool isItalic = italic || run.FontStyle == FontStyles.Italic;
                    bool code = run.FontFamily?.Source is { } f && (f.Contains("Consolas") || f.Contains("Courier") || f.Contains("Mono"));
                    b.Add(run.Text, isBold, isItalic, strike, code, link);
                    break;
                case LineBreak:
                    b.LineBreak();
                    break;
                case Hyperlink h:
                    Inlines(h.Inlines, b, state, bold, italic, h.NavigateUri?.ToString() ?? link, ignoreBold);
                    break;
                case Span span:
                    Inlines(span.Inlines, b, state, !ignoreBold && (bold || span.FontWeight >= FontWeights.SemiBold), italic || span.FontStyle == FontStyles.Italic, link, ignoreBold);
                    break;
                case InlineUIContainer { Child: Image { Source: BitmapSource bitmap } }:
                    b.AddRaw(ImageMarkdown(bitmap, state));
                    break;
            }
        }
    }

    private static string ImageMarkdown(BitmapSource bitmap, State state)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            var name = $"media/image{state.Images.Count + 1}.png";
            state.Images.Add(new DocImage(name, ImageService.Resize(ms.ToArray(), 1800) ?? ms.ToArray()));
            return $"![]({name})";
        }
        catch (Exception)
        {
            return "";
        }
    }
}
