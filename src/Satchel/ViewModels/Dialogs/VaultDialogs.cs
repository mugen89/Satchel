using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Core;
using Satchel.Models;
using Satchel.Security;
using Satchel.Services;

namespace Satchel.ViewModels.Dialogs;

public sealed partial class ProfileEditorViewModel : DialogViewModel
{
    public ProfileEditorViewModel(VaultProfile? profile, IEnumerable<string> categories)
    {
        IsNew = profile == null;
        Categories = categories.ToList();
        _name = profile?.Name ?? "";
        _category = profile?.Category ?? "";
        _website = profile?.Website ?? "";
        _tags = profile?.Tags ?? "";
        _notes = profile?.Notes ?? "";
        _image = profile?.Image;
        _icon = profile?.Icon ?? "";
        _color = profile?.Color ?? Palettes.Tags[Random.Shared.Next(Palettes.Tags.Count)].Key;
        _isFavorite = profile?.IsFavorite ?? false;
    }

    public bool IsNew { get; }
    public string HeaderText => IsNew ? "New profile" : "Edit profile";
    public IReadOnlyList<string> Categories { get; }
    public override double DialogWidth => 560;

    public static IReadOnlyList<string> IconChoices { get; } =
    [
        "", Glyphs.Globe, Glyphs.Mail, Glyphs.People, Glyphs.Bank, Glyphs.Card, Glyphs.Work, Glyphs.Shop, Glyphs.Cart, Glyphs.Video, Glyphs.Music,
        Glyphs.Game, Glyphs.Code, Glyphs.Cloud, Glyphs.Server, Glyphs.Database, Glyphs.Wifi, Glyphs.Phone, Glyphs.Laptop, Glyphs.Home, Glyphs.Car,
        Glyphs.Airplane, Glyphs.Health, Glyphs.Education, Glyphs.Key, Glyphs.Shield, Glyphs.Lightning, Glyphs.Heart, Glyphs.Chat, Glyphs.Puzzle, Glyphs.Photo
    ];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name;

    [ObservableProperty] private string _category;
    [ObservableProperty] private string _website;
    [ObservableProperty] private string _tags;
    [ObservableProperty] private string _notes;
    [ObservableProperty] private byte[]? _image;
    [ObservableProperty] private string _icon;
    [ObservableProperty] private string _color;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private bool _addAccountAfterSave = true;

    public bool HasImage => Image != null;
    partial void OnImageChanged(byte[]? value) => OnPropertyChanged(nameof(HasImage));

    partial void OnWebsiteChanged(string value)
    {
        if (IsNew && string.IsNullOrWhiteSpace(Name))
        {
            var guess = CsvService.HostName(value);
            if (!string.IsNullOrEmpty(guess)) Name = guess;
        }
    }

    [RelayCommand]
    private void PickImage()
    {
        var img = ImageService.PickImage();
        if (img != null) Image = img;
    }

    [RelayCommand]
    private void PasteImage()
    {
        var img = ImageService.FromClipboard();
        if (img != null) Image = img;
    }

    [RelayCommand]
    private void RemoveImage() => Image = null;

    [RelayCommand]
    private void SetIcon(string? glyph)
    {
        if (glyph == null) return;
        Icon = glyph;
        Image = null;
    }

    private bool CanSave => !string.IsNullOrWhiteSpace(Name);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => Close("save");

    [RelayCommand]
    private void Delete() => Close("delete");

    public void ApplyTo(VaultProfile p)
    {
        p.Name = Name.Trim();
        p.Category = Category?.Trim() ?? "";
        p.Website = Website?.Trim() ?? "";
        p.Tags = Tags ?? "";
        p.Notes = Notes ?? "";
        p.Image = Image;
        p.Icon = Icon;
        p.Color = Color;
        p.IsFavorite = IsFavorite;
    }
}

public sealed partial class FieldEditor : ObservableObject
{
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _value = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSecret))]
    [NotifyPropertyChangedFor(nameof(IsMultiline))]
    private FieldType _type;

    public bool IsSecret => CustomField.IsSecretType(Type);
    public bool IsMultiline => Type is FieldType.Multiline or FieldType.SecretNote;

    public static IReadOnlyList<Choice<FieldType>> TypeChoices { get; } =
    [
        new(FieldType.Text, "Text"), new(FieldType.Secret, "Secret"), new(FieldType.Multiline, "Multi-line"), new(FieldType.SecretNote, "Secret note"),
        new(FieldType.Url, "URL"), new(FieldType.Email, "Email"), new(FieldType.Phone, "Phone"), new(FieldType.Date, "Date"), new(FieldType.Number, "Number")
    ];
}

public sealed partial class AccountEditorViewModel : DialogViewModel
{
    private readonly DialogService _dialogs;
    private readonly string _originalPassword;

    public AccountEditorViewModel(VaultAccount a, bool isNew, PasswordOptions generator, DialogService dialogs, string profileName)
    {
        IsNew = isNew;
        Generator = generator;
        _dialogs = dialogs;
        ProfileName = profileName;
        _kind = a.Kind;
        _label = a.Label;
        _username = a.Username;
        _email = a.Email;
        _password = a.Password;
        _originalPassword = a.Password;
        _url = a.Url;
        _totpSecret = a.TotpSecret;
        _notes = a.Notes;
        _tags = a.Tags;
        _signInWith = a.SignInWith;
        _image = a.Image;
        _isFavorite = a.IsFavorite;
        _expiresAt = a.ExpiresAt;
        foreach (var f in CustomField.Parse(a.Fields)) Fields.Add(new FieldEditor { Label = f.Label, Type = f.Type, Value = f.Value });
        if (isNew) ApplyTemplate(a.Kind);
    }

    public bool IsNew { get; }
    public string ProfileName { get; }
    public PasswordOptions Generator { get; }
    public string HeaderText => IsNew ? $"New account · {ProfileName}" : $"Edit account · {ProfileName}";
    public override double DialogWidth => 640;
    public override bool CloseOnBackdrop => false;
    public ObservableCollection<FieldEditor> Fields { get; } = [];
    public IReadOnlyList<string> SignInOptions => AccountTemplates.SignInOptions;

    public static IReadOnlyList<Choice<AccountKind>> KindChoices { get; } =
        AccountTemplates.All.Select(t => new Choice<AccountKind>(t.Kind, VaultAccount.KindName(t.Kind), VaultAccount.KindGlyph(t.Kind))).ToList();

    public static IReadOnlyList<(string Label, FieldType Type)> QuickFields { get; } =
    [
        ("Recovery codes", FieldType.SecretNote), ("Security question", FieldType.Text), ("Security answer", FieldType.Secret), ("PIN", FieldType.Secret),
        ("API key", FieldType.Secret), ("Phone", FieldType.Phone), ("Account number", FieldType.Text), ("Note", FieldType.Multiline)
    ];

    public IReadOnlyList<string> QuickFieldNames => QuickFields.Select(q => q.Label).ToList();

    [ObservableProperty] private AccountKind _kind;
    [ObservableProperty] private string _label;
    [ObservableProperty] private string _username;
    [ObservableProperty] private string _email;
    [ObservableProperty] private string _password;
    [ObservableProperty] private string _url;
    [ObservableProperty] private string _totpSecret;
    [ObservableProperty] private string _notes;
    [ObservableProperty] private string _tags;
    [ObservableProperty] private string _signInWith;
    [ObservableProperty] private byte[]? _image;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private DateTime? _expiresAt;
    [ObservableProperty] private bool _showAll;
    [ObservableProperty] private string? _error;

    private AccountTemplate Template => AccountTemplates.For(Kind);
    public bool ShowUsername => ShowAll || Template.Username || !string.IsNullOrEmpty(Username);
    public bool ShowEmail => ShowAll || Template.Email || !string.IsNullOrEmpty(Email);
    public bool ShowPassword => ShowAll || Template.Password || !string.IsNullOrEmpty(Password);
    public bool ShowUrl => ShowAll || Template.Url || !string.IsNullOrEmpty(Url);
    public bool ShowTotp => ShowAll || Template.Totp || !string.IsNullOrEmpty(TotpSecret);
    public bool HasHiddenFields => !ShowAll && !(ShowUsername && ShowEmail && ShowPassword && ShowUrl && ShowTotp);
    public bool TotpValid => Totp.IsValid(TotpSecret);
    public bool TotpInvalid => !string.IsNullOrWhiteSpace(TotpSecret) && !TotpValid;
    public bool HasImage => Image != null;
    public string KindGlyph => VaultAccount.KindGlyph(Kind);

    partial void OnKindChanged(AccountKind value)
    {
        if (IsNew) ApplyTemplate(value);
        RaiseVisibility();
        OnPropertyChanged(nameof(KindGlyph));
    }

    partial void OnShowAllChanged(bool value) => RaiseVisibility();
    partial void OnTotpSecretChanged(string value) { OnPropertyChanged(nameof(TotpValid)); OnPropertyChanged(nameof(TotpInvalid)); }
    partial void OnImageChanged(byte[]? value) => OnPropertyChanged(nameof(HasImage));

    private void RaiseVisibility()
    {
        foreach (var n in new[] { nameof(ShowUsername), nameof(ShowEmail), nameof(ShowPassword), nameof(ShowUrl), nameof(ShowTotp), nameof(HasHiddenFields) })
            OnPropertyChanged(n);
    }

    /// <summary>On new accounts, swap the template's default custom fields (keeping anything the user typed).</summary>
    private void ApplyTemplate(AccountKind kind)
    {
        for (int i = Fields.Count - 1; i >= 0; i--)
            if (string.IsNullOrWhiteSpace(Fields[i].Value)) Fields.RemoveAt(i);
        foreach (var (label, type) in AccountTemplates.For(kind).Fields)
            if (!Fields.Any(f => string.Equals(f.Label, label, StringComparison.OrdinalIgnoreCase)))
                Fields.Add(new FieldEditor { Label = label, Type = type });
    }

    [RelayCommand]
    private void SetKind(AccountKind kind) => Kind = kind;

    [RelayCommand]
    private void GeneratePassword() => Password = PasswordGenerator.Generate(Generator);

    [RelayCommand]
    private async Task OpenGenerator()
    {
        var result = await _dialogs.ShowAsync(new PasswordGeneratorViewModel(Generator, canUse: true));
        if (result is string pw && pw.Length > 0) Password = pw;
    }

    [RelayCommand]
    private void AddField(string? preset)
    {
        var quick = QuickFields.FirstOrDefault(q => q.Label == preset);
        Fields.Add(new FieldEditor { Label = quick.Label ?? "", Type = quick.Label == null ? FieldType.Text : quick.Type });
    }

    [RelayCommand]
    private void RemoveField(FieldEditor? field)
    {
        if (field != null) Fields.Remove(field);
    }

    [RelayCommand]
    private void PickImage()
    {
        var img = ImageService.PickImage();
        if (img != null) Image = img;
    }

    [RelayCommand]
    private void PasteImage()
    {
        var img = ImageService.FromClipboard();
        if (img != null) Image = img;
    }

    [RelayCommand]
    private void RemoveImage() => Image = null;

    [RelayCommand]
    private void ToggleShowAll() => ShowAll = !ShowAll;

    [RelayCommand]
    private void Save()
    {
        if (!string.IsNullOrWhiteSpace(TotpSecret) && !TotpValid) { Error = "The 2FA secret isn't valid base32 or an otpauth:// link."; return; }
        bool hasContent = new[] { Label, Username, Email, Password, Url, Notes, TotpSecret }.Any(s => !string.IsNullOrWhiteSpace(s)) || Fields.Any(f => !string.IsNullOrWhiteSpace(f.Value));
        if (!hasContent) { Error = "Fill in at least one field."; return; }
        Close("save");
    }

    [RelayCommand]
    private void Delete() => Close("delete");

    public void ApplyTo(VaultAccount a)
    {
        if (!IsNew && a.Password != Password && !string.IsNullOrEmpty(a.Password))
        {
            var history = PasswordHistoryEntry.Parse(a.PasswordHistory);
            history.Insert(0, new PasswordHistoryEntry { Password = a.Password, ChangedAt = DateTime.Now });
            a.PasswordHistory = PasswordHistoryEntry.Serialize(history.Take(20));
        }
        if (a.Password != Password || (IsNew && !string.IsNullOrEmpty(Password))) a.PasswordChangedAt = DateTime.Now;
        a.Kind = Kind;
        a.Label = Label?.Trim() ?? "";
        a.Username = Username?.Trim() ?? "";
        a.Email = Email?.Trim() ?? "";
        a.Password = Password ?? "";
        a.Url = Url?.Trim() ?? "";
        a.TotpSecret = TotpSecret?.Trim() ?? "";
        a.Notes = Notes ?? "";
        a.Tags = Tags ?? "";
        a.SignInWith = SignInWith ?? "";
        a.Image = Image;
        a.IsFavorite = IsFavorite;
        a.ExpiresAt = ExpiresAt;
        a.Fields = CustomField.Serialize(Fields.Where(f => !string.IsNullOrWhiteSpace(f.Value) || !string.IsNullOrWhiteSpace(f.Label))
            .Select(f => new CustomField { Label = f.Label.Trim(), Type = f.Type, Value = f.Value }));
    }
}

public sealed partial class PasswordGeneratorViewModel : DialogViewModel
{
    public PasswordGeneratorViewModel(PasswordOptions options, bool canUse)
    {
        Options = options;
        CanUse = canUse;
        Options.PropertyChanged += OnOptionsChanged;
        Regenerate();
    }

    public PasswordOptions Options { get; }
    public bool CanUse { get; }
    public override double DialogWidth => 520;

    [ObservableProperty] private string _password = "";
    public string EntropyText => $"≈ {Math.Round(PasswordGenerator.Entropy(Options))} bits of entropy";

    private void OnOptionsChanged(object? sender, PropertyChangedEventArgs e) => Regenerate();

    [RelayCommand]
    private void Regenerate()
    {
        Password = PasswordGenerator.Generate(Options);
        OnPropertyChanged(nameof(EntropyText));
    }

    [RelayCommand]
    private void Copy() => SecureClipboard.Copy(Password, sensitive: true, "Password copied — clears automatically");

    [RelayCommand]
    private void Use() => Close(Password);

    protected override void OnClosing(object? result) => Options.PropertyChanged -= OnOptionsChanged;
}
