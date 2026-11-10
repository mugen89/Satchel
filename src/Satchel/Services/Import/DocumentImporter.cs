using System.IO;
using System.Text;
using Satchel.Services.Sheets;

namespace Satchel.Services.Import;

/// <summary>A file turned into a Library document.</summary>
public sealed record ImportedDocument(string Title, string Markdown, IReadOnlyList<DocImage> Images, string FileName, string Kind, IReadOnlyList<string> Notes)
{
    public bool IsSpreadsheet => Kind == "spreadsheet";
}

/// <summary>
/// Reads the files the Library accepts: Markdown and text (any encoding), Word (.docx, .doc), rich text (.rtf) and
/// spreadsheets (.xlsx, .xls, .csv), all as Markdown. Files are recognised by their content first, so a ".doc" that
/// is really RTF or a .docx still opens.
/// </summary>
public static class DocumentImporter
{
    public static readonly string[] MarkdownExtensions = [".md", ".markdown", ".mdown", ".mkd"];
    public static readonly string[] TextExtensions = [".txt", ".text", ".log"];
    public static readonly string[] WordExtensions = [".docx", ".docm", ".dotx", ".doc", ".dot", ".rtf"];

    public static IEnumerable<string> Extensions => MarkdownExtensions.Concat(TextExtensions).Concat(WordExtensions).Concat(SpreadsheetIO.Extensions);

    /// <summary>For open-file dialogs.</summary>
    public const string DialogFilter =
        "Documents and spreadsheets|*.md;*.markdown;*.mdown;*.mkd;*.txt;*.text;*.log;*.docx;*.docm;*.dotx;*.doc;*.dot;*.rtf;*.xlsx;*.xlsm;*.xls;*.csv;*.tsv"
        + "|Markdown and text|*.md;*.markdown;*.mdown;*.mkd;*.txt;*.text;*.log|Word and rich text|*.docx;*.docm;*.dotx;*.doc;*.dot;*.rtf"
        + "|Spreadsheets|*.xlsx;*.xlsm;*.xls;*.csv;*.tsv|All files|*.*";

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public const long MaxFileSize = 200L * 1024 * 1024;

    /// <summary>Reads and converts a file. Throws <see cref="InvalidDataException"/> with a readable message when it can't.</summary>
    public static ImportedDocument Import(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new InvalidDataException("The file isn't there any more.");
        if (info.Length > MaxFileSize) throw new InvalidDataException("This file is too big to bring into the Library (over 200 MB).");
        var bytes = File.ReadAllBytes(path);
        var ext = info.Extension.ToLowerInvariant();
        var fileName = info.Name;
        var fallbackTitle = Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Replace('-', ' ').Trim();
        if (fallbackTitle.Length == 0) fallbackTitle = "Untitled document";

        // Spreadsheets first: .xlsx and .docx are both zip files, so the extension decides between them.
        if (SpreadsheetIO.IsSpreadsheet(path))
        {
            var notes = new List<string>();
            var wb = SpreadsheetIO.Read(bytes, fileName, notes);
            var md = SpreadsheetIO.ToMarkdown(wb, fallbackTitle, maxRows: 500, source: fileName);
            return new ImportedDocument(fallbackTitle, md, [], fileName, "spreadsheet", notes);
        }

        ConvertedDoc converted;
        string kind;
        if (bytes.Length >= 4 && bytes[0] == 'P' && bytes[1] == 'K' && WordExtensions.Contains(ext))
        {
            converted = DocxConverter.Convert(bytes);
            kind = "word";
        }
        else if (CompoundFile.IsCompoundFile(bytes))
        {
            converted = DocConverter.Convert(bytes);
            kind = "word";
        }
        else if (RtfConverter.IsRtf(bytes))
        {
            converted = RtfConverter.Convert(bytes);
            kind = "rtf";
        }
        else if (WordExtensions.Contains(ext) && LooksLikeHtml(bytes))
            throw new InvalidDataException("This is a web page saved with a Word extension — open it in Word and save it as .docx.");
        else if (MarkdownExtensions.Contains(ext))
        {
            var text = TextFiles.Decode(bytes);
            return new ImportedDocument(LibraryTitle(text, fallbackTitle), text.Replace("\r\n", "\n"), LocalImages(text, Path.GetDirectoryName(path) ?? ""), fileName, "markdown", []);
        }
        else if (TextExtensions.Contains(ext) || IsMostlyText(bytes))
        {
            var text = TextFiles.Decode(bytes);
            return new ImportedDocument(fallbackTitle, TextFiles.PlainTextToMarkdown(text), [], fileName, "text", []);
        }
        else throw new InvalidDataException("This file isn't a format the Library can read.");

        var markdown = converted.Markdown;
        var title = converted.Title ?? LibraryTitle(markdown, fallbackTitle);
        return new ImportedDocument(title, markdown, converted.Images, fileName, kind, converted.Notes);
    }

    private static string LibraryTitle(string markdown, string fallback) => Models.LibraryDoc.TitleFrom(markdown, fallback);

    private static bool LooksLikeHtml(byte[] bytes)
    {
        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 512)).TrimStart().ToLowerInvariant();
        return head.StartsWith("<html") || head.StartsWith("<!doctype html") || head.Contains("xmlns:w=\"urn:schemas-microsoft-com:office:word\"");
    }

    private static bool IsMostlyText(byte[] bytes)
    {
        int n = Math.Min(bytes.Length, 8192), control = 0;
        for (int i = 0; i < n; i++)
            if (bytes[i] < 0x09 || (bytes[i] > 0x0D && bytes[i] < 0x20)) control++;
        return n > 0 && control < n / 100 + 1;
    }

    /// <summary>Pictures a Markdown file points at next to it on disk, kept with the document.</summary>
    private static List<DocImage> LocalImages(string markdown, string folder)
    {
        var images = new List<DocImage>();
        foreach (var reference in MarkdownRenderer.ImagePaths(markdown).Distinct())
        {
            if (reference.Contains("://") || reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var clean = Uri.UnescapeDataString(reference.Split('?', '#')[0]).Replace('/', Path.DirectorySeparatorChar);
                var full = Path.GetFullPath(Path.Combine(folder, clean));
                if (!File.Exists(full) || !ImageService.IsImageFile(full)) continue;
                var bytes = ImageService.FromFile(full, 1800, photo: !full.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
                if (bytes != null) images.Add(new DocImage(reference, bytes));
            }
            catch
            {
                // an unreadable picture: the text still comes in
            }
        }
        return images;
    }
}
