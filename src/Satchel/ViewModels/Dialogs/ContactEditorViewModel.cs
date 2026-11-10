using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Core;
using Satchel.Models;
using Satchel.Services;

namespace Satchel.ViewModels.Dialogs;

public sealed partial class EntryEditor : ObservableObject
{
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _value = "";
}

public sealed partial class ContactEditorViewModel : DialogViewModel
{
    public ContactEditorViewModel(Contact? c)
    {
        IsNew = c == null;
        _firstName = c?.FirstName ?? "";
        _lastName = c?.LastName ?? "";
        _nickname = c?.Nickname ?? "";
        _company = c?.Company ?? "";
        _jobTitle = c?.JobTitle ?? "";
        _address = c?.Address ?? "";
        _website = c?.Website ?? "";
        _notes = c?.Notes ?? "";
        _tags = c?.Tags ?? "";
        _image = c?.Image;
        _birthday = c?.Birthday;
        _isFavorite = c?.IsFavorite ?? false;
        _color = c?.Color ?? Palettes.Tags[Random.Shared.Next(Palettes.Tags.Count)].Key;
        foreach (var e in ContactEntry.Parse(c?.Emails)) Emails.Add(new EntryEditor { Label = e.Label, Value = e.Value });
        foreach (var p in ContactEntry.Parse(c?.Phones)) Phones.Add(new EntryEditor { Label = p.Label, Value = p.Value });
        if (Emails.Count == 0) Emails.Add(new EntryEditor { Label = "Personal" });
        if (Phones.Count == 0) Phones.Add(new EntryEditor { Label = "Mobile" });
    }

    public bool IsNew { get; }
    public string HeaderText => IsNew ? "New contact" : "Edit contact";
    public override double DialogWidth => 600;
    public override bool CloseOnBackdrop => false;
    public ObservableCollection<EntryEditor> Emails { get; } = [];
    public ObservableCollection<EntryEditor> Phones { get; } = [];
    public static IReadOnlyList<string> PhoneLabels { get; } = ["Mobile", "Home", "Work", "Other"];
    public static IReadOnlyList<string> EmailLabels { get; } = ["Personal", "Work", "Other"];

    [ObservableProperty] private string _firstName;
    [ObservableProperty] private string _lastName;
    [ObservableProperty] private string _nickname;
    [ObservableProperty] private string _company;
    [ObservableProperty] private string _jobTitle;
    [ObservableProperty] private string _address;
    [ObservableProperty] private string _website;
    [ObservableProperty] private string _notes;
    [ObservableProperty] private string _tags;
    [ObservableProperty] private byte[]? _image;
    [ObservableProperty] private DateTime? _birthday;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private string _color;
    [ObservableProperty] private string? _error;

    public string PreviewName => $"{FirstName} {LastName}".Trim() is { Length: > 0 } n ? n : (string.IsNullOrWhiteSpace(Company) ? "?" : Company);
    public bool HasImage => Image != null;

    partial void OnFirstNameChanged(string value) => OnPropertyChanged(nameof(PreviewName));
    partial void OnLastNameChanged(string value) => OnPropertyChanged(nameof(PreviewName));
    partial void OnCompanyChanged(string value) => OnPropertyChanged(nameof(PreviewName));
    partial void OnImageChanged(byte[]? value) => OnPropertyChanged(nameof(HasImage));

    [RelayCommand] private void AddEmail() => Emails.Add(new EntryEditor { Label = Emails.Count == 0 ? "Personal" : "Work" });
    [RelayCommand] private void AddPhone() => Phones.Add(new EntryEditor { Label = Phones.Count == 0 ? "Mobile" : "Work" });
    [RelayCommand] private void RemoveEmail(EntryEditor? e) { if (e != null) Emails.Remove(e); }
    [RelayCommand] private void RemovePhone(EntryEditor? e) { if (e != null) Phones.Remove(e); }

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

    [RelayCommand] private void RemoveImage() => Image = null;

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(FirstName) && string.IsNullOrWhiteSpace(LastName) && string.IsNullOrWhiteSpace(Company) && string.IsNullOrWhiteSpace(Nickname))
        {
            Error = "Add at least a name or a company.";
            return;
        }
        Close("save");
    }

    [RelayCommand] private void Delete() => Close("delete");

    public void ApplyTo(Contact c)
    {
        c.FirstName = FirstName.Trim();
        c.LastName = LastName.Trim();
        c.Nickname = Nickname.Trim();
        c.Company = Company.Trim();
        c.JobTitle = JobTitle.Trim();
        c.Address = Address.Trim();
        c.Website = Website.Trim();
        c.Notes = Notes;
        c.Tags = Tags;
        c.Image = Image;
        c.Birthday = Birthday;
        c.IsFavorite = IsFavorite;
        c.Color = Color;
        c.Emails = ContactEntry.Serialize(Emails.Where(e => !string.IsNullOrWhiteSpace(e.Value)).Select(e => new ContactEntry { Label = e.Label, Value = e.Value.Trim() }));
        c.Phones = ContactEntry.Serialize(Phones.Where(p => !string.IsNullOrWhiteSpace(p.Value)).Select(p => new ContactEntry { Label = p.Label, Value = p.Value.Trim() }));
    }
}
