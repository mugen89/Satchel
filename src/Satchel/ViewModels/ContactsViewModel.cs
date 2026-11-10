using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Services;
using Satchel.ViewModels.Dialogs;

namespace Satchel.ViewModels;

public sealed class ContactGroup
{
    public required string Letter { get; init; }
    public List<Contact> Items { get; } = [];
}

public sealed partial class ContactsViewModel : PageViewModel
{
    private readonly DataStore _store;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly List<Contact> _all = [];
    private bool _loaded;

    public ContactsViewModel(INavigator nav, DataStore store, DialogService dialogs, ToastService toasts)
    {
        _store = store;
        _dialogs = dialogs;
        _toasts = toasts;
    }

    public override string Title => "Contacts";
    public ObservableCollection<ContactGroup> Groups { get; } = [];
    public ObservableCollection<FieldRow> Rows { get; } = [];

    [ObservableProperty] private string _filter = "All";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private Contact? _selected;

    public bool HasContacts => _all.Count > 0;
    public bool HasSelection => Selected != null;
    public bool IsListEmpty => Groups.Count == 0;
    public int TotalCount => _all.Count;
    public IReadOnlyList<string> SelectedTags => Selected == null ? [] : Note.SplitTags(Selected.Tags);
    public bool SelectedHasNotes => !string.IsNullOrWhiteSpace(Selected?.Notes);

    partial void OnFilterChanged(string value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedChanged(Contact? value)
    {
        BuildRows();
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedTags));
        OnPropertyChanged(nameof(SelectedHasNotes));
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_loaded) await LoadAsync();
        if (parameter is int id && _all.FirstOrDefault(c => c.Id == id) is { } c)
        {
            Filter = "All";
            SearchText = "";
            Selected = c;
        }
        else if (parameter is "new") await NewContact();
        else if (parameter is "import") await ImportVcf();
        else if (parameter is "export") await ExportVcf();
    }

    private async Task LoadAsync()
    {
        var list = await _store.ListAsync<Contact>();
        _all.Clear();
        _all.AddRange(list);
        _loaded = true;
        ApplyFilter();
        Selected ??= Groups.FirstOrDefault()?.Items.FirstOrDefault();
    }

    private void ApplyFilter()
    {
        IEnumerable<Contact> q = _all;
        if (Filter == "Favorites") q = q.Where(c => c.IsFavorite);
        else if (Filter == "Birthdays") q = q.Where(c => c.Birthday.HasValue).OrderBy(c => c.NextBirthday);
        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length > 0)
            q = q.Where(c => terms.All(t => $"{c.FullName} {c.Nickname} {c.Company} {c.JobTitle} {c.Emails} {c.Phones} {c.Tags} {c.Address}".Contains(t, StringComparison.OrdinalIgnoreCase)));

        Groups.Clear();
        if (Filter == "Birthdays")
        {
            var g = new ContactGroup { Letter = "Upcoming birthdays" };
            g.Items.AddRange(q);
            if (g.Items.Count > 0) Groups.Add(g);
        }
        else
        {
            foreach (var group in q.OrderBy(c => c.SortKey, StringComparer.CurrentCultureIgnoreCase).GroupBy(c => c.GroupLetter))
            {
                var g = new ContactGroup { Letter = group.Key };
                g.Items.AddRange(group);
                Groups.Add(g);
            }
        }
        OnPropertyChanged(nameof(HasContacts));
        OnPropertyChanged(nameof(IsListEmpty));
        OnPropertyChanged(nameof(TotalCount));
    }

    private void BuildRows()
    {
        Rows.Clear();
        var c = Selected;
        if (c == null) return;
        foreach (var p in ContactEntry.Parse(c.Phones).Where(p => !string.IsNullOrWhiteSpace(p.Value)))
            Rows.Add(new FieldRow(string.IsNullOrWhiteSpace(p.Label) ? "Phone" : p.Label, p.Value, FieldType.Phone, Glyphs.Phone));
        foreach (var e in ContactEntry.Parse(c.Emails).Where(e => !string.IsNullOrWhiteSpace(e.Value)))
            Rows.Add(new FieldRow(string.IsNullOrWhiteSpace(e.Label) ? "Email" : e.Label, e.Value, FieldType.Email, Glyphs.Mail));
        if (!string.IsNullOrWhiteSpace(c.Address)) Rows.Add(new FieldRow("Address", c.Address, FieldType.Multiline, Glyphs.MapPin));
        if (!string.IsNullOrWhiteSpace(c.Website)) Rows.Add(new FieldRow("Website", c.Website, FieldType.Url, Glyphs.Globe));
        if (c.Birthday.HasValue) Rows.Add(new FieldRow("Birthday", c.BirthdayText + (c.AgeNext.HasValue ? $" · turns {c.AgeNext}" : ""), FieldType.Date, Glyphs.Heart));
    }

    [RelayCommand]
    private void Select(Contact? c) => Selected = c;

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    public override void OnNewShortcut() => _ = NewContact();

    [RelayCommand]
    private async Task NewContact()
    {
        var vm = new ContactEditorViewModel(null);
        if (await _dialogs.ShowAsync(vm) is not "save") return;
        var c = new Contact();
        vm.ApplyTo(c);
        await _store.AddAsync(c);
        _all.Add(c);
        Filter = "All";
        ApplyFilter();
        Selected = c;
        _toasts.Success($"{c.FullName} added");
    }

    [RelayCommand]
    private async Task EditContact(Contact? c)
    {
        c ??= Selected;
        if (c == null) return;
        var vm = new ContactEditorViewModel(c);
        var result = await _dialogs.ShowAsync(vm);
        if (result is "save")
        {
            vm.ApplyTo(c);
            c.UpdatedAt = DateTime.Now;
            await _store.UpdateAsync(c);
            ApplyFilter();
            BuildRows();
            OnPropertyChanged(nameof(SelectedTags));
            OnPropertyChanged(nameof(SelectedHasNotes));
        }
        else if (result is "delete") await DeleteContact(c);
    }

    [RelayCommand]
    private async Task DeleteContact(Contact? c)
    {
        c ??= Selected;
        if (c == null) return;
        if (!await _dialogs.ConfirmAsync("Delete contact?", $"{c.FullName} will be permanently deleted.", "Delete", danger: true)) return;
        await _store.DeleteAsync(c);
        _all.Remove(c);
        ApplyFilter();
        if (Selected == c) Selected = Groups.FirstOrDefault()?.Items.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ToggleFavorite(Contact? c)
    {
        c ??= Selected;
        if (c == null) return;
        c.IsFavorite = !c.IsFavorite;
        await _store.UpdateAsync(c);
        if (Filter == "Favorites") ApplyFilter();
    }

    [RelayCommand]
    private void CopyAll()
    {
        if (Selected == null) return;
        var sb = new StringBuilder(Selected.FullName);
        foreach (var r in Rows) sb.Append($"\n{r.Label}: {r.Value}");
        Security.SecureClipboard.Copy(sb.ToString(), false, "Contact copied");
    }

    [RelayCommand]
    private async Task ImportVcf()
    {
        var dialog = new OpenFileDialog { Title = "Import contacts (vCard)", Filter = "vCard (*.vcf)|*.vcf|All files|*.*" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var imported = VCardService.Parse(await File.ReadAllTextAsync(dialog.FileName));
            if (imported.Count == 0) { _toasts.Warning("No contacts found in that file."); return; }
            int i = 0;
            foreach (var c in imported) c.Color = Palettes.Tags[i++ % Palettes.Tags.Count].Key;
            await _store.AddRangeAsync(imported);
            _all.AddRange(imported);
            ApplyFilter();
            _toasts.Success($"Imported {imported.Count} contact(s)");
        }
        catch (Exception ex)
        {
            _toasts.Error("Couldn't read that file: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task ExportVcf()
    {
        if (_all.Count == 0) return;
        var dialog = new SaveFileDialog { Title = "Export contacts", FileName = "Satchel contacts.vcf", Filter = "vCard (*.vcf)|*.vcf" };
        if (dialog.ShowDialog() != true) return;
        await File.WriteAllTextAsync(dialog.FileName, VCardService.Build(_all), new UTF8Encoding(false));
        _toasts.Success($"Exported {_all.Count} contact(s) (unencrypted)");
    }
}
