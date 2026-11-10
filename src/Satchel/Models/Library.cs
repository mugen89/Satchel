using System.ComponentModel.DataAnnotations.Schema;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Satchel.Models;

/// <summary>A Markdown document in the Library (imported .md file or written in the app).</summary>
public partial class LibraryDoc : Entity
{
    [ObservableProperty][property: Encrypted] private string _title = "";
    [ObservableProperty][property: Encrypted] private string _markdown = "";
    [ObservableProperty][property: Encrypted] private string _fileName = "";
    /// <summary>Old flat grouping (before folders); moved into folders the first time the Library loads.</summary>
    [ObservableProperty][property: Encrypted] private string _collection = "";
    /// <summary>The folder the document is in (null = not in a folder).</summary>
    [ObservableProperty] private int? _folderId;
    [ObservableProperty][property: Encrypted] private string _tags = "";
    /// <summary>Reading position, 0..1.</summary>
    [ObservableProperty] private double _progress;
    [ObservableProperty] private DateTime? _lastOpenedAt;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private double _sortOrder;
    [ObservableProperty] private int _wordCount;

    [NotMapped] public int ReadingMinutes => Math.Max(1, (int)Math.Round(WordCount / 230.0));
    [NotMapped] public string Excerpt => MakeExcerpt(Markdown);
    [NotMapped] public bool IsStarted => Progress > 0.02;

    partial void OnMarkdownChanged(string value)
    {
        WordCount = CountWords(value);
        OnPropertyChanged(nameof(Excerpt));
    }

    partial void OnWordCountChanged(int value) => OnPropertyChanged(nameof(ReadingMinutes));
    partial void OnProgressChanged(double value) => OnPropertyChanged(nameof(IsStarted));

    public static int CountWords(string? text) => string.IsNullOrWhiteSpace(text) ? 0 : Regex.Matches(text, @"[\p{L}\p{N}]+").Count;

    /// <summary>First real paragraph of the document, without Markdown syntax.</summary>
    public static string MakeExcerpt(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("```") || line.StartsWith("---") || line.StartsWith('|') || line.StartsWith("<")) continue;
            line = Regex.Replace(line, @"!\[[^\]]*\]\([^)]*\)", "");
            line = Regex.Replace(line, @"\[([^\]]*)\]\([^)]*\)", "$1");
            // Markup goes, escaped characters stay ("5\*3" reads "5*3").
            line = Regex.Replace(line, @"(?<!\\)[*_`>#~]+", "").Trim();
            line = Regex.Replace(line, @"^([-+]|\d+\.)\s+", "");
            line = Regex.Replace(line, @"\\([\\`*_{}\[\]()#+\-.!|~^<>=])", "$1");
            line = line.Replace("  ", " ").Trim();
            if (line.Length < 3) continue;
            return line.Length > 220 ? line[..220] + "…" : line;
        }
        return "";
    }

    /// <summary>Title from the first heading, else the file name.</summary>
    public static string TitleFrom(string markdown, string fallback)
    {
        var m = Regex.Match(markdown ?? "", @"^\s{0,3}#{1,2}\s+(.+?)\s*#*\s*$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value.Trim() : fallback;
    }
}

/// <summary>A folder in the Library. Folders nest (<see cref="ParentId"/>); a private folder hides what's inside.</summary>
public partial class LibraryFolder : Entity
{
    [ObservableProperty][property: Encrypted] private string _name = "";
    [ObservableProperty] private int? _parentId;
}

/// <summary>An image (or other file) referenced by a document, stored so the document stays self-contained.</summary>
public partial class DocAsset : Entity
{
    [ObservableProperty] private int _docId;
    /// <summary>The path exactly as written in the Markdown.</summary>
    [ObservableProperty][property: Encrypted] private string _name = "";
    [ObservableProperty][property: Encrypted] private byte[]? _data;
}
