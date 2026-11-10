using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfTable = System.Windows.Documents.Table;

namespace Satchel.Services;

/// <summary>Visual settings for the reader (theme, typography, measure).</summary>
public sealed record ReaderStyle(
    Brush Background, Brush Foreground, Brush Muted, Brush Accent, Brush CodeBackground, Brush Rule, Brush QuoteBar,
    FontFamily Font, FontFamily HeadingFont, FontFamily MonoFont, double FontSize, double LineHeight, bool Justify, bool IsDark);

/// <summary>A heading in the rendered document (for the table of contents).</summary>
public sealed record TocEntry(int Level, string Text, Paragraph Target)
{
    public Thickness Indent => new(Math.Max(0, Level - 1) * 12, 0, 0, 0);
}

/// <summary>
/// Renders Markdown (CommonMark + tables, task lists, strikethrough, autolinks, footnotes) into a themed WPF FlowDocument.
/// Images are resolved through a callback so documents can use stored assets, data URIs or (online) web images.
/// </summary>
public static partial class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseListExtras()
        .UseFootnotes()
        .UseAutoIdentifiers()
        .Build();

    public static FlowDocument Render(string markdown, ReaderStyle style, Func<string, BitmapSource?> resolveImage, Action<string> openLink,
        out List<TocEntry> toc)
    {
        var doc = new FlowDocument
        {
            Background = Brushes.Transparent,
            Foreground = style.Foreground,
            FontFamily = style.Font,
            FontSize = style.FontSize,
            LineHeight = style.FontSize * style.LineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            PagePadding = new Thickness(0),
            TextAlignment = style.Justify ? TextAlignment.Justify : TextAlignment.Left,
            IsHyphenationEnabled = style.Justify,
            IsOptimalParagraphEnabled = style.Justify,
            ColumnWidth = double.PositiveInfinity
        };
        var ctx = new Context(style, resolveImage, openLink);
        var parsed = Markdig.Markdown.Parse(markdown ?? "", Pipeline);
        foreach (var block in parsed)
            if (ctx.RenderBlock(block) is { } wpf) doc.Blocks.Add(wpf);
        toc = ctx.Toc;
        return doc;
    }

    private sealed class Context(ReaderStyle style, Func<string, BitmapSource?> resolveImage, Action<string> openLink)
    {
        public List<TocEntry> Toc { get; } = [];
        private readonly Dictionary<string, Paragraph> _anchors = new(StringComparer.OrdinalIgnoreCase);

        public WpfBlock? RenderBlock(MdBlock block)
        {
            switch (block)
            {
                case HeadingBlock h: return Heading(h);
                case ParagraphBlock p:
                {
                    var para = new Paragraph { Margin = new Thickness(0, 0, 0, style.FontSize * 0.85) };
                    AddInlines(para.Inlines, p.Inline);
                    // A paragraph that is only an image becomes a centred figure.
                    if (para.Inlines.Count == 1 && para.Inlines.FirstInline is InlineUIContainer) para.TextAlignment = TextAlignment.Center;
                    return para;
                }
                case QuoteBlock q:
                {
                    var section = new Section
                    {
                        BorderBrush = style.QuoteBar,
                        BorderThickness = new Thickness(3, 0, 0, 0),
                        Padding = new Thickness(18, 2, 0, 2),
                        Margin = new Thickness(0, 4, 0, style.FontSize),
                        Foreground = style.Muted,
                        FontStyle = FontStyles.Italic
                    };
                    foreach (var child in q) if (RenderBlock(child) is { } b) section.Blocks.Add(b);
                    TrimLastMargin(section.Blocks);
                    return section;
                }
                case ListBlock l: return List(l);
                case FencedCodeBlock f: return Code(f.Lines.ToString(), f.Info);
                case CodeBlock c: return Code(c.Lines.ToString(), null);
                case ThematicBreakBlock:
                    return new BlockUIContainer(new Border { Height = 1, Background = style.Rule, Margin = new Thickness(0, style.FontSize * 0.6, 0, style.FontSize * 0.6) });
                case MdTable t: return Table(t);
                case HtmlBlock html:
                {
                    var text = StripTags(html.Lines.ToString()).Trim();
                    if (text.Length == 0) return null;
                    return new Paragraph(new Run(text)) { Foreground = style.Muted, Margin = new Thickness(0, 0, 0, style.FontSize * 0.85) };
                }
                case Markdig.Extensions.Footnotes.FootnoteGroup group:
                {
                    var section = new Section { FontSize = style.FontSize * 0.86, Foreground = style.Muted, Margin = new Thickness(0, style.FontSize, 0, 0) };
                    section.Blocks.Add(new BlockUIContainer(new Border { Height = 1, Background = style.Rule, Width = 120, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) }));
                    int n = 1;
                    foreach (var child in group)
                    {
                        if (child is not ContainerBlock note) continue;
                        var list = new Section();
                        foreach (var inner in note) if (RenderBlock(inner) is { } b) list.Blocks.Add(b);
                        if (list.Blocks.FirstBlock is Paragraph first) first.Inlines.InsertBefore(first.Inlines.FirstInline, new Run($"{n}. ") { FontWeight = FontWeights.SemiBold });
                        section.Blocks.Add(list);
                        n++;
                    }
                    return section;
                }
                case LinkReferenceDefinitionGroup:
                    return null;
                case ContainerBlock container:
                {
                    var section = new Section();
                    foreach (var child in container) if (RenderBlock(child) is { } b) section.Blocks.Add(b);
                    return section.Blocks.Count > 0 ? section : null;
                }
                case LeafBlock leaf when leaf.Inline != null:
                {
                    var para = new Paragraph();
                    AddInlines(para.Inlines, leaf.Inline);
                    return para;
                }
                default:
                    return null;
            }
        }

        private Paragraph Heading(HeadingBlock h)
        {
            double[] scale = [2.0, 1.55, 1.28, 1.12, 1.0, 0.92];
            int level = Math.Clamp(h.Level, 1, 6);
            double size = style.FontSize * scale[level - 1];
            var para = new Paragraph
            {
                FontFamily = style.HeadingFont,
                FontSize = size,
                FontWeight = level <= 2 ? FontWeights.Bold : FontWeights.SemiBold,
                LineHeight = size * 1.25,
                Margin = new Thickness(0, level == 1 ? style.FontSize * 0.4 : style.FontSize * 1.3, 0, style.FontSize * (level <= 2 ? 0.6 : 0.4)),
                TextAlignment = TextAlignment.Left,
                KeepWithNext = true
            };
            if (level == 6) para.Foreground = style.Muted;
            AddInlines(para.Inlines, h.Inline);
            var text = new TextRange(para.ContentStart, para.ContentEnd).Text.Trim();
            Toc.Add(new TocEntry(level, text, para));
            var id = h.GetAttributes()?.Id;
            if (!string.IsNullOrEmpty(id)) _anchors[id] = para;
            if (level <= 2)
            {
                // Hairline under the top-level headings.
                para.BorderBrush = style.Rule;
                para.BorderThickness = new Thickness(0, 0, 0, level == 1 ? 0 : 1);
                para.Padding = new Thickness(0, 0, 0, level == 1 ? 0 : 6);
            }
            return para;
        }

        private System.Windows.Documents.List List(ListBlock l)
        {
            var list = new System.Windows.Documents.List
            {
                MarkerStyle = l.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                Margin = new Thickness(0, 0, 0, style.FontSize * 0.85),
                Padding = new Thickness(style.FontSize * 1.6, 0, 0, 0)
            };
            if (l.IsOrdered && int.TryParse(l.OrderedStart, out var start) && start > 1) list.StartIndex = start;
            bool isTaskList = false;
            foreach (var item in l)
            {
                if (item is not ListItemBlock li) continue;
                var listItem = new ListItem();
                foreach (var child in li) if (RenderBlock(child) is { } b) listItem.Blocks.Add(b);
                foreach (var block in listItem.Blocks.OfType<Paragraph>()) block.Margin = new Thickness(0, 0, 0, style.FontSize * 0.25);
                if (listItem.Blocks.FirstBlock is Paragraph p && p.Inlines.FirstInline is Run { Tag: "task" }) isTaskList = true;
                list.ListItems.Add(listItem);
            }
            if (isTaskList) list.MarkerStyle = TextMarkerStyle.None;
            return list;
        }

        private WpfBlock Code(string code, string? language)
        {
            code = code.TrimEnd('\n', '\r');
            var para = new Paragraph
            {
                FontFamily = style.MonoFont,
                FontSize = style.FontSize * 0.84,
                LineHeight = style.FontSize * 0.84 * 1.5,
                Background = style.CodeBackground,
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 2, 0, style.FontSize),
                TextAlignment = TextAlignment.Left,
                BorderBrush = style.Rule,
                BorderThickness = new Thickness(1)
            };
            foreach (var inline in SyntaxHighlighter.Highlight(code, language, style.IsDark)) para.Inlines.Add(inline);
            return para;
        }

        private WpfBlock Table(MdTable t)
        {
            var table = new WpfTable
            {
                CellSpacing = 0,
                BorderBrush = style.Rule,
                BorderThickness = new Thickness(1, 1, 0, 0),
                Margin = new Thickness(0, 4, 0, style.FontSize),
                FontSize = style.FontSize * 0.92
            };
            int columns = t.ColumnDefinitions.Count > 0 ? t.ColumnDefinitions.Count : t.OfType<MdTableRow>().Select(r => r.Count).DefaultIfEmpty(1).Max();
            // Columns share the width by how much they hold, so short values (dates, amounts) don't wrap mid-word.
            var longest = new int[columns];
            foreach (var row in t.OfType<MdTableRow>())
            {
                int c = 0;
                foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
                {
                    if (c >= columns) break;
                    int length = cell.Descendants<LiteralInline>().Sum(l => l.Content.Length) + cell.Descendants<CodeInline>().Sum(x => x.Content.Length);
                    longest[c] = Math.Max(longest[c], length);
                    c++;
                }
            }
            for (int i = 0; i < columns; i++) table.Columns.Add(new TableColumn { Width = new GridLength(Math.Clamp(longest[i], 3, 40) + 3, GridUnitType.Star) });
            var group = new TableRowGroup();
            int rowIndex = 0;
            foreach (var row in t.OfType<MdTableRow>())
            {
                var tr = new System.Windows.Documents.TableRow();
                if (row.IsHeader) { tr.FontWeight = FontWeights.SemiBold; tr.Background = style.CodeBackground; }
                else if (rowIndex % 2 == 0) tr.Background = new SolidColorBrush(Color.FromArgb(style.IsDark ? (byte)14 : (byte)10, 128, 128, 128));
                int col = 0;
                foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
                {
                    var tc = new System.Windows.Documents.TableCell
                    {
                        BorderBrush = style.Rule,
                        BorderThickness = new Thickness(0, 0, 1, 1),
                        Padding = new Thickness(10, 6, 10, 6)
                    };
                    var align = col < t.ColumnDefinitions.Count ? t.ColumnDefinitions[col].Alignment : null;
                    foreach (var child in cell)
                    {
                        if (RenderBlock(child) is not { } b) continue;
                        if (b is Paragraph p)
                        {
                            p.Margin = new Thickness(0);
                            p.TextAlignment = align switch
                            {
                                TableColumnAlign.Center => TextAlignment.Center,
                                TableColumnAlign.Right => TextAlignment.Right,
                                _ => TextAlignment.Left
                            };
                        }
                        tc.Blocks.Add(b);
                    }
                    tr.Cells.Add(tc);
                    col++;
                }
                group.Rows.Add(tr);
                if (!row.IsHeader) rowIndex++;
            }
            table.RowGroups.Add(group);
            return table;
        }

        private void AddInlines(InlineCollection target, ContainerInline? container)
        {
            if (container == null) return;
            foreach (var inline in container) Add(target, inline);
        }

        private void Add(InlineCollection target, MdInline inline)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    target.Add(new Run(lit.Content.ToString()));
                    break;
                case TaskList task:
                    target.Add(new Run(task.Checked ? "☑ " : "☐ ") { Tag = "task", Foreground = task.Checked ? style.Accent : style.Muted, FontFamily = new FontFamily("Segoe UI Symbol") });
                    break;
                case EmphasisInline em:
                {
                    var span = new Span();
                    switch (em.DelimiterChar)
                    {
                        case '*' or '_' when em.DelimiterCount >= 2: span.FontWeight = FontWeights.Bold; break;
                        case '*' or '_': span.FontStyle = FontStyles.Italic; break;
                        case '~' when em.DelimiterCount >= 2: span.TextDecorations = TextDecorations.Strikethrough; break;
                        case '~': span.BaselineAlignment = BaselineAlignment.Subscript; span.FontSize = style.FontSize * 0.75; break;
                        case '^': span.BaselineAlignment = BaselineAlignment.Superscript; span.FontSize = style.FontSize * 0.75; break;
                        case '+': span.TextDecorations = TextDecorations.Underline; break;
                        case '=': span.Background = new SolidColorBrush(Color.FromArgb(style.IsDark ? (byte)90 : (byte)120, 255, 214, 64)); break;
                    }
                    foreach (var child in em) Add(span.Inlines, child);
                    target.Add(span);
                    break;
                }
                case CodeInline code:
                    target.Add(new Run(code.Content)
                    {
                        FontFamily = style.MonoFont,
                        FontSize = style.FontSize * 0.88,
                        Background = style.CodeBackground
                    });
                    break;
                case LinkInline link when link.IsImage:
                {
                    var url = link.Url ?? "";
                    var bitmap = resolveImage(url);
                    if (bitmap == null)
                    {
                        target.Add(new Run($"[image: {LinkText(link)}]") { Foreground = style.Muted, FontStyle = FontStyles.Italic });
                        break;
                    }
                    var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxWidth = Math.Min(bitmap.Width, 900), ToolTip = LinkText(link) };
                    RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                    target.Add(new InlineUIContainer(new Border { Child = image, CornerRadius = new CornerRadius(6), ClipToBounds = true, Margin = new Thickness(0, 6, 0, 6) }));
                    break;
                }
                case LinkInline link:
                {
                    var url = link.Url ?? "";
                    var hyperlink = new Hyperlink { Foreground = style.Accent, TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = url };
                    hyperlink.MouseEnter += (_, _) => hyperlink.TextDecorations = TextDecorations.Underline;
                    hyperlink.MouseLeave += (_, _) => hyperlink.TextDecorations = null;
                    hyperlink.Click += (_, _) => Navigate(url);
                    foreach (var child in link) Add(hyperlink.Inlines, child);
                    if (hyperlink.Inlines.Count == 0) hyperlink.Inlines.Add(new Run(url));
                    target.Add(hyperlink);
                    break;
                }
                case AutolinkInline auto:
                {
                    var url = auto.IsEmail ? "mailto:" + auto.Url : auto.Url;
                    var hyperlink = new Hyperlink(new Run(auto.Url)) { Foreground = style.Accent, TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand };
                    hyperlink.Click += (_, _) => Navigate(url);
                    target.Add(hyperlink);
                    break;
                }
                case LineBreakInline br:
                    target.Add(br.IsHard ? new LineBreak() : new Run(" "));
                    break;
                case HtmlEntityInline entity:
                    target.Add(new Run(entity.Transcoded.ToString()));
                    break;
                case HtmlInline html:
                    if (Regex.IsMatch(html.Tag, @"^<br\s*/?>$", RegexOptions.IgnoreCase)) target.Add(new LineBreak());
                    break;
                case Markdig.Extensions.Footnotes.FootnoteLink footnote when !footnote.IsBackLink:
                    target.Add(new Run(footnote.Index.ToString(CultureInfo.InvariantCulture)) { BaselineAlignment = BaselineAlignment.Superscript, FontSize = style.FontSize * 0.7, Foreground = style.Accent });
                    break;
                case ContainerInline container:
                    foreach (var child in container) Add(target, child);
                    break;
            }
        }

        private void Navigate(string url)
        {
            if (url.StartsWith('#'))
            {
                if (_anchors.TryGetValue(url[1..], out var target)) target.BringIntoView();
                return;
            }
            openLink(url);
        }

        private static string LinkText(LinkInline link)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var child in link) if (child is LiteralInline l) sb.Append(l.Content.ToString());
            return sb.ToString();
        }
    }

    private static void TrimLastMargin(BlockCollection blocks)
    {
        if (blocks.LastBlock is { } last) last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
    }

    private static string StripTags(string html) => TagRegex().Replace(html, "");

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    /// <summary>All image paths referenced by a document (for importing them alongside an .md file).</summary>
    public static IEnumerable<string> ImagePaths(string markdown)
    {
        var parsed = Markdig.Markdown.Parse(markdown ?? "", Pipeline);
        foreach (var link in parsed.Descendants<LinkInline>())
            if (link.IsImage && !string.IsNullOrWhiteSpace(link.Url)) yield return link.Url!;
    }
}

/// <summary>A small, fast, language-agnostic highlighter: comments, strings, numbers and common keywords.</summary>
public static partial class SyntaxHighlighter
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract","as","async","await","base","bool","break","case","catch","class","const","continue","def","default","del","delete","do","double",
        "elif","else","enum","except","export","extends","false","finally","fn","for","foreach","from","func","function","get","if","impl","implements",
        "import","in","int","interface","is","lambda","let","match","mut","namespace","new","nil","none","None","not","null","object","or","and","override",
        "package","pass","private","protected","pub","public","raise","readonly","record","ref","return","sealed","select","self","set","static","string",
        "struct","super","switch","this","throw","true","True","False","try","type","typeof","use","using","var","virtual","void","where","while","with","yield",
        "SELECT","FROM","WHERE","INSERT","UPDATE","DELETE","JOIN","LEFT","RIGHT","INNER","ORDER","BY","GROUP","AND","OR","NOT","NULL","AS","ON","INTO","VALUES","CREATE","TABLE"
    };

    [GeneratedRegex(@"(?<comment>//[^\n]*|#(?![a-fA-F0-9]{3,8}\b)[^\n]*|/\*[\s\S]*?\*/|--[^\n]*)|(?<string>""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|`[^`]*`)|(?<number>\b\d+(?:\.\d+)?\b)|(?<word>\b[A-Za-z_][A-Za-z0-9_]*\b)")]
    private static partial Regex Tokens();

    public static IEnumerable<WpfInline> Highlight(string code, string? language, bool dark)
    {
        var comment = new SolidColorBrush(dark ? Color.FromRgb(0x7F, 0x8C, 0x8D) : Color.FromRgb(0x6A, 0x73, 0x7D));
        var str = new SolidColorBrush(dark ? Color.FromRgb(0x98, 0xC3, 0x79) : Color.FromRgb(0x0A, 0x7F, 0x46));
        var num = new SolidColorBrush(dark ? Color.FromRgb(0xE5, 0xA7, 0x5C) : Color.FromRgb(0xB3, 0x5C, 0x00));
        var kw = new SolidColorBrush(dark ? Color.FromRgb(0x61, 0xAF, 0xEF) : Color.FromRgb(0x00, 0x5C, 0xC5));
        bool hashComments = language is null or "" or "python" or "py" or "bash" or "sh" or "shell" or "powershell" or "ps1" or "yaml" or "yml" or "ruby" or "toml" or "ini";
        bool dashComments = language is "sql" or "lua" or "haskell";
        int last = 0;
        foreach (Match m in Tokens().Matches(code))
        {
            if (m.Index > last) yield return new Run(code[last..m.Index]);
            Brush? brush = null;
            if (m.Groups["comment"].Success)
            {
                var v = m.Value;
                bool isComment = v.StartsWith("//") || v.StartsWith("/*") || (v.StartsWith('#') && hashComments) || (v.StartsWith("--") && dashComments);
                if (isComment) brush = comment;
                else
                {
                    // Not a comment in this language: emit the first character and rescan the rest.
                    yield return new Run(v[..1]);
                    last = m.Index + 1;
                    foreach (var inner in Highlight(v[1..], language, dark)) yield return inner;
                    last = m.Index + m.Length;
                    continue;
                }
            }
            else if (m.Groups["string"].Success) brush = str;
            else if (m.Groups["number"].Success) brush = num;
            else if (m.Groups["word"].Success && Keywords.Contains(m.Value)) brush = kw;
            yield return brush == null ? new Run(m.Value) : new Run(m.Value) { Foreground = brush };
            last = m.Index + m.Length;
        }
        if (last < code.Length) yield return new Run(code[last..]);
    }
}
