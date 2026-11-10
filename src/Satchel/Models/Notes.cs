using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Satchel.Core;

namespace Satchel.Models;

public enum NoteType
{
    Text = 0,
    Checklist = 1,
    List = 2,
    Code = 3,
    Journal = 4
}

public partial class Notebook : Entity
{
    [ObservableProperty][property: Encrypted] private string _name = "";
    [ObservableProperty] private string _color = "teal";
    [ObservableProperty] private string _icon = Glyphs.Book;
    [ObservableProperty] private double _sortOrder;

    [ObservableProperty][property: NotMapped] private int _noteCount;
}

public partial class ChecklistItem : ObservableObject
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private int _indent;
}

public partial class Note : Entity
{
    [ObservableProperty] private NoteType _type;
    [ObservableProperty] private int? _notebookId;
    [ObservableProperty][property: Encrypted] private string _title = "";
    /// <summary>Text notes: base64 XamlPackage (rich text). Checklist/List: JSON items. Code: raw text.</summary>
    [ObservableProperty][property: Encrypted] private string _content = "";
    /// <summary>Plain-text copy of the content used for previews and search.</summary>
    [ObservableProperty][property: Encrypted] private string _plainText = "";
    [ObservableProperty][property: Encrypted] private string _tags = "";
    [ObservableProperty] private string _color = "default";
    [ObservableProperty] private bool _isPinned;
    [ObservableProperty] private bool _isArchived;
    [ObservableProperty] private bool _isTrashed;
    [ObservableProperty] private DateTime? _trashedAt;
    [ObservableProperty] private double _sortOrder;
    [ObservableProperty] private string? _language;
    [ObservableProperty] private DateTime? _journalDate;
    [ObservableProperty] private int _mood;

    private List<ChecklistItem>? _parsedItems;

    [NotMapped] public string TypeGlyph => GlyphFor(Type);

    [NotMapped] public string TypeName => Type switch
    {
        NoteType.Checklist => "Checklist",
        NoteType.List => "List",
        NoteType.Code => "Code",
        NoteType.Journal => "Journal",
        _ => "Note"
    };

    [NotMapped] public bool IsItemList => Type is NoteType.Checklist or NoteType.List;
    [NotMapped] public bool IsCode => Type == NoteType.Code;

    [NotMapped] public IReadOnlyList<string> TagList => SplitTags(Tags);
    [NotMapped] public bool HasTags => TagList.Count > 0;

    [NotMapped] public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? (Type == NoteType.Journal && JournalDate.HasValue ? Format.FriendlyDay(JournalDate.Value) : "Untitled") : Title;

    [NotMapped] public string Preview
    {
        get
        {
            if (IsItemList) return "";
            var text = PlainText.Trim();
            if (Type == NoteType.Code) text = Content;
            return text.Length > 420 ? text[..420] + "…" : text;
        }
    }

    [NotMapped] public bool HasPreview => !string.IsNullOrWhiteSpace(Preview);

    [NotMapped] public IReadOnlyList<ChecklistItem> Items => _parsedItems ??= ParseItems(Content);

    [NotMapped] public IReadOnlyList<ChecklistItem> PreviewItems => Items.Take(8).ToList();
    [NotMapped] public int MoreItemsCount => Math.Max(0, Items.Count - 8);
    [NotMapped] public int CheckedCount => Items.Count(i => i.IsChecked);
    [NotMapped] public int ItemCount => Items.Count;
    [NotMapped] public double Progress => Items.Count == 0 ? 0 : (double)CheckedCount / Items.Count;
    [NotMapped] public string ProgressText => $"{CheckedCount}/{ItemCount}";

    [NotMapped] public string UpdatedText => Format.Relative(UpdatedAt);

    [NotMapped] public string MoodEmoji => Mood switch { 1 => "😞", 2 => "😕", 3 => "😐", 4 => "🙂", 5 => "😄", _ => "" };

    partial void OnContentChanged(string value)
    {
        _parsedItems = null;
        OnPropertyChanged(nameof(Items));
        OnPropertyChanged(nameof(PreviewItems));
        OnPropertyChanged(nameof(MoreItemsCount));
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(ItemCount));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(HasPreview));
    }

    partial void OnPlainTextChanged(string value)
    {
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(HasPreview));
    }

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));
    partial void OnJournalDateChanged(DateTime? value) => OnPropertyChanged(nameof(DisplayTitle));
    partial void OnMoodChanged(int value) => OnPropertyChanged(nameof(MoodEmoji));
    protected override void OnUpdatedAtChangedCore() => OnPropertyChanged(nameof(UpdatedText));

    partial void OnTagsChanged(string value)
    {
        OnPropertyChanged(nameof(TagList));
        OnPropertyChanged(nameof(HasTags));
    }

    partial void OnTypeChanged(NoteType value)
    {
        OnPropertyChanged(nameof(TypeGlyph));
        OnPropertyChanged(nameof(TypeName));
        OnPropertyChanged(nameof(IsItemList));
        OnPropertyChanged(nameof(IsCode));
        OnContentChanged(Content);
    }

    public void RefreshComputed() => OnContentChanged(Content);

    public static string GlyphFor(NoteType type) => type switch
    {
        NoteType.Checklist => Glyphs.Checklist,
        NoteType.List => Glyphs.BulletedList,
        NoteType.Code => Glyphs.Code,
        NoteType.Journal => Glyphs.Journal,
        _ => Glyphs.Document
    };

    public static List<ChecklistItem> ParseItems(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith('[')) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ChecklistItem>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string SerializeItems(IEnumerable<ChecklistItem> items) => JsonSerializer.Serialize(items);

    public static IReadOnlyList<string> SplitTags(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
