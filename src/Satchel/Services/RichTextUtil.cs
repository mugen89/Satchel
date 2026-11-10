using System.IO;
using System.Windows;
using System.Windows.Documents;
using Satchel.Controls;

namespace Satchel.Services;

/// <summary>Small edits to rich-text note content (base64 XamlPackage) without opening an editor.</summary>
public static class RichTextUtil
{
    public static FlowDocument Load(string? content)
    {
        var doc = new FlowDocument();
        if (string.IsNullOrEmpty(content)) return doc;
        try
        {
            using var ms = new MemoryStream(Convert.FromBase64String(content));
            new TextRange(doc.ContentStart, doc.ContentEnd).Load(ms, DataFormats.XamlPackage);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException or IOException)
        {
            // not rich text: start fresh
        }
        return doc;
    }

    /// <summary>Appends paragraphs (an optional bold heading first) and returns the new content and plain text.</summary>
    public static (string Content, string Plain) Append(string? content, IEnumerable<string> lines, string? heading = null)
    {
        var doc = Load(content);
        if (doc.Blocks.Count == 1 && doc.Blocks.FirstBlock is Paragraph { Inlines.Count: 0 }) doc.Blocks.Clear();
        if (heading != null) doc.Blocks.Add(new Paragraph(new Bold(new Run(heading))) { Margin = new Thickness(0, 12, 0, 4) });
        foreach (var line in lines) doc.Blocks.Add(new Paragraph(new Run(line)) { Margin = new Thickness(0, 0, 0, 2) });
        return RichTextEditor.SerializeDocument(doc);
    }
}
