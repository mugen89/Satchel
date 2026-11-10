using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Behaviors;
using Satchel.Core;
using Satchel.Models;
using Satchel.Security;

namespace Satchel.ViewModels;

public sealed record AccountTemplate(AccountKind Kind, bool Username, bool Email, bool Password, bool Url, bool Totp, (string Label, FieldType Type)[] Fields);

public static class AccountTemplates
{
    public static readonly IReadOnlyList<AccountTemplate> All =
    [
        new(AccountKind.Login, true, true, true, true, true, []),
        new(AccountKind.Email, false, true, true, true, true, [("Recovery email", FieldType.Email), ("Phone", FieldType.Phone)]),
        new(AccountKind.ApiToken, false, false, false, true, false, [("Token", FieldType.Secret), ("Key ID", FieldType.Text), ("Secret key", FieldType.Secret), ("Scopes", FieldType.Text)]),
        new(AccountKind.Server, true, false, true, false, false, [("Host", FieldType.Text), ("Port", FieldType.Number), ("Private key", FieldType.SecretNote), ("Passphrase", FieldType.Secret)]),
        new(AccountKind.Database, true, false, true, false, false, [("Host", FieldType.Text), ("Port", FieldType.Number), ("Database", FieldType.Text), ("Connection string", FieldType.Secret)]),
        new(AccountKind.WiFi, false, false, true, false, false, [("Network name (SSID)", FieldType.Text), ("Security", FieldType.Text)]),
        new(AccountKind.Card, false, false, false, false, false, [("Cardholder", FieldType.Text), ("Card number", FieldType.Secret), ("Expiry (MM/YY)", FieldType.Text), ("CVV", FieldType.Secret), ("PIN", FieldType.Secret)]),
        new(AccountKind.BankAccount, true, false, true, true, true, [("Account holder", FieldType.Text), ("IBAN", FieldType.Secret), ("SWIFT / BIC", FieldType.Text), ("PIN", FieldType.Secret)]),
        new(AccountKind.License, false, true, false, true, false, [("License key", FieldType.Secret), ("Version", FieldType.Text), ("Purchase date", FieldType.Date), ("Seats", FieldType.Number)]),
        new(AccountKind.Identity, false, true, false, false, false, [("Full name", FieldType.Text), ("Phone", FieldType.Phone), ("Address", FieldType.Multiline), ("Date of birth", FieldType.Date), ("ID / passport no.", FieldType.Secret)]),
        new(AccountKind.SecureNote, false, false, false, false, false, []),
        new(AccountKind.Link, false, false, false, true, false, []),
        new(AccountKind.Other, true, true, true, true, true, []),
    ];

    public static AccountTemplate For(AccountKind kind) => All.FirstOrDefault(t => t.Kind == kind) ?? All[0];

    public static readonly IReadOnlyList<string> SignInOptions =
        ["", "Password", "Google", "Apple", "Microsoft", "Facebook", "GitHub", "Single sign-on", "Passkey", "Magic link", "Other"];
}

/// <summary>A single field shown on an account card (copy, reveal, open).</summary>
public sealed partial class FieldRow : ObservableObject
{
    private const string Mask = "••••••••••";
    private readonly Action? _onCopied;

    public FieldRow(string label, string value, FieldType type, string glyph, Action? onCopied = null, bool isPassword = false)
    {
        Label = label;
        Value = value;
        Type = type;
        Glyph = glyph;
        IsPassword = isPassword;
        _onCopied = onCopied;
        if (isPassword) Strength = PasswordStrength.Evaluate(value);
    }

    public string Label { get; }
    public string Value { get; }
    public FieldType Type { get; }
    public string Glyph { get; }
    public bool IsPassword { get; }
    public StrengthResult Strength { get; }
    public bool IsSecret => IsPassword || CustomField.IsSecretType(Type);
    public bool IsUrl => Type == FieldType.Url;
    public bool IsMultiline => Type is FieldType.Multiline or FieldType.SecretNote;
    public bool CanOpen => IsUrl || Type == FieldType.Email;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display))]
    private bool _isRevealed;

    public string Display => IsSecret && !IsRevealed ? (IsMultiline ? Mask + "  (hidden)" : Mask) : Value;

    [RelayCommand]
    private void Copy()
    {
        SecureClipboard.Copy(Value, IsSecret, IsSecret ? $"{Label} copied — clears in {SecureClipboard.ClearAfterSeconds}s" : $"{Label} copied");
        _onCopied?.Invoke();
    }

    [RelayCommand]
    private void ToggleReveal() => IsRevealed = !IsRevealed;

    [RelayCommand]
    private void Open() => UiCommands.OpenLink(Type == FieldType.Email ? "mailto:" + Value : Value);
}

/// <summary>Display wrapper for an account on the profile detail.</summary>
public sealed partial class AccountItemViewModel : ObservableObject
{
    public AccountItemViewModel(VaultAccount account, Action<VaultAccount> onPasswordCopied)
    {
        Account = account;
        _onPasswordCopied = onPasswordCopied;
        Rebuild();
    }

    private readonly Action<VaultAccount> _onPasswordCopied;

    public VaultAccount Account { get; }
    public ObservableCollection<FieldRow> Rows { get; } = [];
    public string KindName => VaultAccount.KindName(Account.Kind);
    public string KindGlyph => VaultAccount.KindGlyph(Account.Kind);
    public bool HasSignIn => !string.IsNullOrWhiteSpace(Account.SignInWith) && Account.SignInWith != "Password";
    public string SignInText => $"Sign in with {Account.SignInWith}";
    public bool HasTotp => Totp.IsValid(Account.TotpSecret);
    public bool HasNotes => !string.IsNullOrWhiteSpace(Account.Notes);
    public IReadOnlyList<string> TagList => Note.SplitTags(Account.Tags);
    public bool HasTags => TagList.Count > 0;
    public IReadOnlyList<PasswordHistoryEntry> History { get; private set; } = [];
    public bool HasHistory => History.Count > 0;
    public string HistoryHeader => $"Password history ({History.Count})";

    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _showHistory;

    // Health flags (set by the page)
    [ObservableProperty] private bool _isWeak;
    [ObservableProperty] private int _reuseCount;
    [ObservableProperty] private bool _isOld;
    [ObservableProperty] private bool _isExpiring;
    [ObservableProperty] private bool _isExpired;
    [ObservableProperty] private bool _isHighlighted;

    public bool IsReused => ReuseCount > 0;
    public bool HasIssues => IsWeak || IsReused || IsOld || IsExpiring || IsExpired;

    public IReadOnlyList<string> Issues
    {
        get
        {
            var list = new List<string>();
            if (IsExpired) list.Add("Expired " + Format.Relative(Account.ExpiresAt ?? DateTime.Now));
            else if (IsExpiring && Account.ExpiresAt.HasValue) list.Add($"Expires {Format.FriendlyDay(Account.ExpiresAt.Value)}");
            if (IsWeak) list.Add("Weak password");
            if (IsReused) list.Add(ReuseCount == 1 ? "Reused in 1 other account" : $"Reused in {ReuseCount} other accounts");
            if (IsOld) list.Add("Not changed in over a year");
            return list;
        }
    }

    public string FooterText
    {
        get
        {
            var parts = new List<string>();
            var changed = Account.PasswordChangedAt ?? (string.IsNullOrEmpty(Account.Password) ? null : Account.CreatedAt);
            if (changed.HasValue) parts.Add($"Password changed {Format.Relative(changed.Value)}");
            if (Account.LastUsedAt.HasValue) parts.Add($"last copied {Format.Relative(Account.LastUsedAt.Value)}");
            if (parts.Count == 0) parts.Add($"Added {Format.Relative(Account.CreatedAt)}");
            return string.Join(" · ", parts);
        }
    }

    partial void OnIsWeakChanged(bool value) => RaiseHealth();
    partial void OnReuseCountChanged(int value) => RaiseHealth();
    partial void OnIsOldChanged(bool value) => RaiseHealth();
    partial void OnIsExpiringChanged(bool value) => RaiseHealth();
    partial void OnIsExpiredChanged(bool value) => RaiseHealth();

    private void RaiseHealth()
    {
        OnPropertyChanged(nameof(IsReused));
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(Issues));
    }

    public void Rebuild()
    {
        Rows.Clear();
        var a = Account;
        void Copied() => _onPasswordCopied(a);
        if (!string.IsNullOrWhiteSpace(a.Username)) Rows.Add(new FieldRow("Username", a.Username, FieldType.Text, Glyphs.Contact));
        if (!string.IsNullOrWhiteSpace(a.Email)) Rows.Add(new FieldRow("Email", a.Email, FieldType.Email, Glyphs.Mail));
        if (!string.IsNullOrWhiteSpace(a.Password)) Rows.Add(new FieldRow("Password", a.Password, FieldType.Secret, Glyphs.Key, Copied, isPassword: true));
        if (!string.IsNullOrWhiteSpace(a.Url)) Rows.Add(new FieldRow(a.Kind == AccountKind.Link ? "Link" : "Website", a.Url, FieldType.Url, Glyphs.Globe));
        foreach (var f in CustomField.Parse(a.Fields).Where(f => !string.IsNullOrWhiteSpace(f.Value)))
            Rows.Add(new FieldRow(string.IsNullOrWhiteSpace(f.Label) ? "Field" : f.Label, f.Value, f.Type, GlyphFor(f.Type), CustomField.IsSecretType(f.Type) ? Copied : null));
        if (a.ExpiresAt.HasValue) Rows.Add(new FieldRow("Expires", Format.Date(a.ExpiresAt.Value), FieldType.Date, Glyphs.Clock));
        History = PasswordHistoryEntry.Parse(a.PasswordHistory).OrderByDescending(h => h.ChangedAt).ToList();
        foreach (var name in new[] { nameof(KindName), nameof(KindGlyph), nameof(HasSignIn), nameof(SignInText), nameof(HasTotp), nameof(HasNotes),
                     nameof(TagList), nameof(HasTags), nameof(History), nameof(HasHistory), nameof(HistoryHeader), nameof(FooterText) })
            OnPropertyChanged(name);
    }

    private static string GlyphFor(FieldType t) => t switch
    {
        FieldType.Secret or FieldType.SecretNote => Glyphs.Lock,
        FieldType.Url => Glyphs.Link,
        FieldType.Email => Glyphs.Mail,
        FieldType.Phone => Glyphs.Phone,
        FieldType.Date => Glyphs.Calendar,
        FieldType.Number => Glyphs.Calculator,
        FieldType.Multiline => Glyphs.Document,
        _ => Glyphs.Tag
    };

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void ToggleHistory() => ShowHistory = !ShowHistory;

    [RelayCommand]
    private void CopyHistory(PasswordHistoryEntry? entry)
    {
        if (entry != null) SecureClipboard.Copy(entry.Password, sensitive: true, "Old password copied");
    }
}
