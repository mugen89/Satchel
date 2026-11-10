using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Satchel.Models;

public enum FieldKind { Text = 0, LongText = 1, Number = 2, Date = 3, Rating = 4, Check = 5, Choice = 6, Url = 7 }

/// <summary>One column of a custom collection.</summary>
public sealed class FieldDef
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public FieldKind Kind { get; set; }
    /// <summary>Choice fields: options separated by commas.</summary>
    public string Options { get; set; } = "";
    public bool ShowOnCard { get; set; } = true;

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> OptionList => Options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>A user-defined collection ("Collections" in the app): wines, plants, records, recipes…</summary>
public partial class Tracker : Entity
{
    [ObservableProperty][property: Encrypted] private string _name = "";
    [ObservableProperty][property: Encrypted] private string _description = "";
    [ObservableProperty][property: Encrypted] private string _schemaJson = "";
    [ObservableProperty] private string _icon = Core.Glyphs.Tiles;
    [ObservableProperty] private string _color = "teal";
    /// <summary>"grid" (cards) or "table".</summary>
    [ObservableProperty] private string _view = "grid";
    [ObservableProperty] private double _sortOrder;

    [ObservableProperty][property: NotMapped] private int _itemCount;

    private List<FieldDef>? _fields;

    [NotMapped] public List<FieldDef> Fields => _fields ??= ParseFields(SchemaJson);

    partial void OnSchemaJsonChanged(string value)
    {
        _fields = null;
        OnPropertyChanged(nameof(Fields));
    }

    public void SaveFields(IEnumerable<FieldDef> fields) => SchemaJson = JsonSerializer.Serialize(fields.ToList());

    public static List<FieldDef> ParseFields(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<FieldDef>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}

public partial class TrackerItem : Entity
{
    [ObservableProperty] private int _trackerId;
    [ObservableProperty][property: Encrypted] private string _title = "";
    /// <summary>Field values as JSON: { fieldId: text }.</summary>
    [ObservableProperty][property: Encrypted] private string _valuesJson = "";
    [ObservableProperty][property: Encrypted] private string _notes = "";
    [ObservableProperty][property: Encrypted] private string _tags = "";
    [ObservableProperty][property: Encrypted] private byte[]? _image;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private double _sortOrder;

    /// <summary>Short field summary for cards, filled in by the page.</summary>
    [ObservableProperty][property: NotMapped] private string _cardText = "";

    private Dictionary<string, string>? _values;

    [NotMapped] public Dictionary<string, string> Values => _values ??= ParseValues(ValuesJson);

    public string Get(string fieldId) => Values.TryGetValue(fieldId, out var v) ? v : "";

    public void Set(string fieldId, string value)
    {
        var dict = Values;
        if (string.IsNullOrEmpty(value)) dict.Remove(fieldId);
        else dict[fieldId] = value;
        ValuesJson = JsonSerializer.Serialize(dict);
        _values = dict;
    }

    partial void OnValuesJsonChanged(string value) => _values = null;

    public static Dictionary<string, string> ParseValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
