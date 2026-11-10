using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Satchel.Core;

namespace Satchel.Models;

public enum AccountKind
{
    Login = 0,
    Email = 1,
    ApiToken = 2,
    Server = 3,
    Database = 4,
    WiFi = 5,
    Card = 6,
    BankAccount = 7,
    License = 8,
    Identity = 9,
    SecureNote = 10,
    Link = 11,
    Other = 12
}

public enum FieldType
{
    Text = 0,
    Secret = 1,
    Multiline = 2,
    SecretNote = 3,
    Url = 4,
    Email = 5,
    Phone = 6,
    Date = 7,
    Number = 8
}

/// <summary>A "profile": a service, site, company or app — e.g. Google, My Bank, Home Wi-Fi.</summary>
public partial class VaultProfile : Entity
{
    [ObservableProperty][property: Encrypted] private string _name = "";
    [ObservableProperty][property: Encrypted] private string _category = "";
    [ObservableProperty][property: Encrypted] private string _website = "";
    [ObservableProperty][property: Encrypted] private string _notes = "";
    [ObservableProperty][property: Encrypted] private string _tags = "";
    [ObservableProperty][property: Encrypted] private byte[]? _image;
    [ObservableProperty] private string _color = "teal";
    [ObservableProperty] private string _icon = Glyphs.Globe;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private double _sortOrder;

    [ObservableProperty][property: NotMapped] private int _accountCount;
    [ObservableProperty][property: NotMapped] private int _issueCount;

    [NotMapped] public IReadOnlyList<string> TagList => Note.SplitTags(Tags);
    [NotMapped] public bool HasWebsite => !string.IsNullOrWhiteSpace(Website);
    [NotMapped] public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    [NotMapped] public string ListSubtitle =>
        $"{AccountCount} account{(AccountCount == 1 ? "" : "s")}" + (string.IsNullOrWhiteSpace(Category) ? "" : $" · {Category}");

    partial void OnAccountCountChanged(int value) => OnPropertyChanged(nameof(ListSubtitle));
    partial void OnCategoryChanged(string value) => OnPropertyChanged(nameof(ListSubtitle));

    partial void OnTagsChanged(string value) => OnPropertyChanged(nameof(TagList));
    partial void OnWebsiteChanged(string value) => OnPropertyChanged(nameof(HasWebsite));
    partial void OnNotesChanged(string value) => OnPropertyChanged(nameof(HasNotes));
}

public sealed class CustomField
{
    public string Label { get; set; } = "";
    public FieldType Type { get; set; }
    public string Value { get; set; } = "";

    public static List<CustomField> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<CustomField>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string Serialize(IEnumerable<CustomField> fields) => JsonSerializer.Serialize(fields);

    public static bool IsSecretType(FieldType t) => t is FieldType.Secret or FieldType.SecretNote;
}

public sealed class PasswordHistoryEntry
{
    public string Password { get; set; } = "";
    public DateTime ChangedAt { get; set; }

    public static List<PasswordHistoryEntry> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<PasswordHistoryEntry>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    public static string Serialize(IEnumerable<PasswordHistoryEntry> items) => JsonSerializer.Serialize(items);
}

/// <summary>An account ("sub-profile") under a profile — e.g. Personal and Work accounts for Google.</summary>
public partial class VaultAccount : Entity
{
    [ObservableProperty] private int _profileId;
    [ObservableProperty] private AccountKind _kind;
    [ObservableProperty][property: Encrypted] private string _label = "";
    [ObservableProperty][property: Encrypted] private string _username = "";
    [ObservableProperty][property: Encrypted] private string _email = "";
    [ObservableProperty][property: Encrypted] private string _password = "";
    [ObservableProperty][property: Encrypted] private string _url = "";
    [ObservableProperty][property: Encrypted] private string _totpSecret = "";
    [ObservableProperty][property: Encrypted] private string _notes = "";
    [ObservableProperty][property: Encrypted] private string _fields = "";
    [ObservableProperty][property: Encrypted] private string _tags = "";
    [ObservableProperty][property: Encrypted] private string _signInWith = "";
    [ObservableProperty][property: Encrypted] private string _passwordHistory = "";
    [ObservableProperty][property: Encrypted] private byte[]? _image;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private DateTime? _expiresAt;
    [ObservableProperty] private DateTime? _passwordChangedAt;
    [ObservableProperty] private DateTime? _lastUsedAt;
    [ObservableProperty] private double _sortOrder;

    [NotMapped] public string DisplayName => !string.IsNullOrWhiteSpace(Label) ? Label
        : !string.IsNullOrWhiteSpace(Username) ? Username
        : !string.IsNullOrWhiteSpace(Email) ? Email
        : KindName(Kind);

    [NotMapped] public string Subtitle
    {
        get
        {
            var who = !string.IsNullOrWhiteSpace(Username) ? Username : Email;
            if (!string.IsNullOrWhiteSpace(Label) && !string.IsNullOrWhiteSpace(who)) return who;
            return KindName(Kind);
        }
    }

    partial void OnLabelChanged(string value) => RaiseNames();
    partial void OnUsernameChanged(string value) => RaiseNames();
    partial void OnEmailChanged(string value) => RaiseNames();
    partial void OnKindChanged(AccountKind value) => RaiseNames();

    private void RaiseNames()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Subtitle));
    }

    public static string KindName(AccountKind k) => k switch
    {
        AccountKind.Login => "Login",
        AccountKind.Email => "Email account",
        AccountKind.ApiToken => "API token / key",
        AccountKind.Server => "Server / SSH",
        AccountKind.Database => "Database",
        AccountKind.WiFi => "Wi-Fi network",
        AccountKind.Card => "Payment card",
        AccountKind.BankAccount => "Bank account",
        AccountKind.License => "Software license",
        AccountKind.Identity => "Identity",
        AccountKind.SecureNote => "Secure note",
        AccountKind.Link => "Link / bookmark",
        _ => "Other"
    };

    public static string KindGlyph(AccountKind k) => k switch
    {
        AccountKind.Login => Glyphs.Key,
        AccountKind.Email => Glyphs.Mail,
        AccountKind.ApiToken => Glyphs.Code,
        AccountKind.Server => Glyphs.Server,
        AccountKind.Database => Glyphs.Database,
        AccountKind.WiFi => Glyphs.Wifi,
        AccountKind.Card => Glyphs.Card,
        AccountKind.BankAccount => Glyphs.Bank,
        AccountKind.License => Glyphs.Lightning,
        AccountKind.Identity => Glyphs.ContactCard,
        AccountKind.SecureNote => Glyphs.Lock,
        AccountKind.Link => Glyphs.Link,
        _ => Glyphs.Puzzle
    };
}

public enum AttachmentOwner
{
    Note = 0,
    Account = 1,
    Profile = 2,
    Task = 3,
    Event = 4,
    Contact = 5
}

public partial class Attachment : Entity
{
    [ObservableProperty] private AttachmentOwner _ownerType;
    [ObservableProperty] private int _ownerId;
    [ObservableProperty][property: Encrypted] private string _fileName = "";
    [ObservableProperty] private long _size;
    [ObservableProperty][property: Encrypted] private byte[] _data = [];

    [NotMapped] public string SizeText => Format.Bytes(Size);
}
