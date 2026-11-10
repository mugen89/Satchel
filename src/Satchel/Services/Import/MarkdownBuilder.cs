using System.Text;
using System.Text.RegularExpressions;

namespace Satchel.Services.Import;

/// <summary>A converted document: Markdown, the images it references (stored next to it), and notes about what didn't come across.</summary>
public sealed record ConvertedDoc(string Markdown, string? Title, IReadOnlyList<DocImage> Images, IReadOnlyList<string> Notes);

/// <summary>An image a converted document references by <see cref="Name"/> (e.g. <c>media/image1.png</c>).</summary>
public sealed record DocImage(string Name, byte[] Data);

/// <summary>One stretch of text with the same look, on its way to Markdown.</summary>
internal sealed class InlineRun
{
    public string Text = "";
    public bool Bold, Italic, Strike, Code;
    public string? Link;
    /// <summary>Already Markdown (an image, a footnote reference): written as is.</summary>
    public bool Raw;
    public bool IsBreak;

    public bool SameLook(InlineRun o) => !Raw && !o.Raw && !IsBreak && !o.IsBreak && Bold == o.Bold && Italic == o.Italic && Strike == o.Strike && Code == o.Code && Link == o.Link;
}

/// <summary>Collects formatted runs of one paragraph and writes them as Markdown (bold, italic, strike, code, links).</summary>
internal sealed partial class InlineBuilder
{
    private readonly List<InlineRun> _runs = [];

    public int Count => _runs.Count;

    public void Add(string text, bool bold = false, bool italic = false, bool strike = false, bool code = false, string? link = null)
    {
        if (text.Length == 0) return;
        _runs.Add(new InlineRun { Text = text, Bold = bold, Italic = italic, Strike = strike, Code = code, Link = link });
    }

    public void AddRaw(string markdown)
    {
        if (markdown.Length > 0) _runs.Add(new InlineRun { Text = markdown, Raw = true });
    }

    public void LineBreak() => _runs.Add(new InlineRun { IsBreak = true });

    /// <summary>Turns the runs from <paramref name="from"/> on into a link (a field's result in old Word files).</summary>
    public void LinkFrom(int from, string url)
    {
        for (int i = from; i < _runs.Count; i++)
            if (!_runs[i].Raw && !_runs[i].IsBreak) _runs[i].Link ??= url;
    }

    public bool HasContent => _runs.Any(r => r.Raw || (!r.IsBreak && r.Text.Trim().Length > 0));

    public string PlainText => string.Concat(_runs.Select(r => r.IsBreak ? "\n" : r.Raw ? "" : r.Text));

    /// <summary>The paragraph as Markdown. In a table cell, line breaks become spaces and pipes are escaped.</summary>
    public string ToMarkdown(bool inTable = false)
    {
        // Word splits text into runs all the time (spelling marks, revisions): merge neighbours that look the same first.
        var merged = new List<InlineRun>();
        foreach (var r in _runs)
        {
            if (merged.Count > 0 && merged[^1].SameLook(r)) merged[^1].Text += r.Text;
            else merged.Add(new InlineRun { Text = r.Text, Bold = r.Bold, Italic = r.Italic, Strike = r.Strike, Code = r.Code, Link = r.Link, Raw = r.Raw, IsBreak = r.IsBreak });
        }
        var sb = new StringBuilder();
        for (int i = 0; i < merged.Count; i++)
        {
            var r = merged[i];
            if (r.IsBreak)
            {
                // A hard break — but never at the very end (it would be meaningless there).
                if (i < merged.Count - 1) sb.Append(inTable ? " " : "  \n");
                continue;
            }
            if (r.Raw) { sb.Append(r.Text); continue; }
            var text = r.Text.Replace('\t', ' ').Replace(' ', ' ');
            if (text.Length == 0) continue;
            // Markers must hug the text: "** bold**" isn't bold in Markdown, so edge spaces go outside.
            int lead = text.Length - text.TrimStart().Length, trail = text.Length - text.TrimEnd().Length;
            if (lead == text.Length) { sb.Append(' '); continue; }
            var core = text.Substring(lead, text.Length - lead - trail);
            string body = r.Code ? CodeSpan(core) : Escape(core, inTable);
            if (!r.Code)
            {
                if (r.Strike) body = "~~" + body + "~~";
                if (r.Italic) body = "*" + body + "*";
                if (r.Bold) body = "**" + body + "**";
            }
            if (r.Link != null) body = $"[{body}]({LinkTarget(r.Link)})";
            sb.Append(' ', lead > 0 ? 1 : 0).Append(body).Append(' ', trail > 0 ? 1 : 0);
        }
        var result = MultiSpace().Replace(sb.ToString(), " ");
        return inTable ? result.Replace("\n", " ").Trim() : result.Trim(' ');
    }

    private static string CodeSpan(string text)
    {
        var fence = text.Contains('`') ? "``" : "`";
        return fence + (text.StartsWith('`') || text.EndsWith('`') ? " " + text + " " : text) + fence;
    }

    public static string LinkTarget(string url) => url.Contains(' ') || url.Contains(')') ? "<" + url.Replace(">", "%3E") + ">" : url;

    /// <summary>Text that must read literally: Markdown's special characters get a backslash.</summary>
    public static string Escape(string text, bool inTable = false)
    {
        var sb = new StringBuilder(text.Length + 8);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            switch (ch)
            {
                case '\\' or '`' or '*' or '_' or '[' or ']' or '<' or '~' or '^':
                    sb.Append('\\').Append(ch);
                    break;
                case '|' when inTable:
                    sb.Append("\\|");
                    break;
                // "==mark==" and "++insert++" are Markdown extensions here.
                case '=' or '+' when i + 1 < text.Length && text[i + 1] == ch:
                    sb.Append('\\').Append(ch);
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>A paragraph that would start a heading, list, quote or rule in Markdown gets that first character escaped.</summary>
    public static string EscapeLineStart(string line)
    {
        var m = LineStart().Match(line);
        if (!m.Success) return line;
        for (int g = 1; g < m.Groups.Count; g++)
            if (m.Groups[g].Success) return line.Insert(m.Groups[g].Index, "\\");
        return line;
    }

    [GeneratedRegex(@"(?<! ) {2,}(?!\n)")]
    private static partial Regex MultiSpace();

    // "# ", "> ", "- ", "+ ", "1. ", "1) ", "===", "---" at the start of a paragraph ("*" is always escaped already).
    [GeneratedRegex(@"^ {0,3}(?:(#{1,6})(?= |$)|(>)|([-+])(?= |$)|\d{1,9}([.)])(?= |$)|(=+)\s*$|(-{3,})\s*$)")]
    private static partial Regex LineStart();
}

/// <summary>Writes blocks of Markdown with the right spacing: blank lines between paragraphs, tight lists, merged code lines.</summary>
internal sealed class MarkdownOut
{
    private readonly StringBuilder _sb = new();
    private string _last = "";

    public bool IsEmpty => _sb.Length == 0;

    private void Separate(string kind)
    {
        if (_last == "code" && kind != "code") { _sb.Append("```\n"); }
        if (_sb.Length == 0) { _last = kind; return; }
        if (kind == "code" && _last == "code") return;
        _sb.Append(kind == "li" && _last == "li" ? "\n" : _last == "code" ? "\n" : "\n\n");
        _last = kind;
    }

    public void Paragraph(string markdown)
    {
        if (markdown.Trim().Length == 0) return;
        Separate("p");
        _sb.Append(InlineBuilder.EscapeLineStart(markdown));
    }

    public void Heading(int level, string markdown)
    {
        markdown = markdown.Replace("  \n", " ").Trim();
        if (markdown.Length == 0) return;
        Separate("h");
        _sb.Append(new string('#', Math.Clamp(level, 1, 6))).Append(' ').Append(markdown);
    }

    public void Quote(string markdown)
    {
        if (markdown.Trim().Length == 0) return;
        Separate("p");
        _sb.Append("> ").Append(markdown.Replace("\n", "\n> "));
    }

    private string? _listKey;
    private bool _altMarker;

    /// <summary>
    /// A list item; <paramref name="number"/> null for a bullet. Four spaces per level nest under both kinds.
    /// <paramref name="listKey"/> tells lists apart: when a different list follows straight on, it gets the other
    /// marker ("1)" instead of "1.", "*" instead of "-") so Markdown keeps them separate instead of renumbering.
    /// </summary>
    public void ListItem(int level, int? number, string markdown, string? listKey = null)
    {
        if (level == 0 && listKey != null)
        {
            if (_last == "li" && _listKey != null && _listKey != listKey) _altMarker = !_altMarker;
            else if (_last != "li") _altMarker = false;
            _listKey = listKey;
        }
        else if (_last != "li") { _altMarker = false; _listKey = listKey; }
        Separate("li");
        int indent = Math.Clamp(level, 0, 8) * 4;
        var marker = number is { } n ? n + (_altMarker && level == 0 ? ") " : ". ") : _altMarker && level == 0 ? "* " : "- ";
        _sb.Append(' ', indent).Append(marker).Append(markdown.Replace("  \n", "  \n" + new string(' ', indent + marker.Length)));
    }

    public void CodeLine(string text)
    {
        if (_last != "code")
        {
            Separate("code");
            _sb.Append("```\n");
            _last = "code";
        }
        _sb.Append(text.Replace("```", "`​``")).Append('\n');
    }

    public void Table(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0) return;
        int cols = rows.Max(r => r.Count);
        if (cols == 0) return;
        Separate("table");
        string Row(IReadOnlyList<string> cells) =>
            "| " + string.Join(" | ", Enumerable.Range(0, cols).Select(i => i < cells.Count && cells[i].Length > 0 ? cells[i] : " ")) + " |";
        _sb.Append(Row(rows[0])).Append('\n');
        _sb.Append("|").Append(string.Concat(Enumerable.Repeat(" --- |", cols)));
        foreach (var r in rows.Skip(1)) _sb.Append('\n').Append(Row(r));
    }

    public void Rule()
    {
        Separate("hr");
        _sb.Append("---");
    }

    public void Raw(string markdown)
    {
        if (markdown.Trim().Length == 0) return;
        Separate("p");
        _sb.Append(markdown);
    }

    public override string ToString()
    {
        var text = _sb.ToString();
        if (_last == "code") text += "```\n";
        return text.TrimEnd() + "\n";
    }
}
