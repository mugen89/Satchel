using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Satchel.Behaviors;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Security;
using Satchel.Services;
using Satchel.ViewModels.Dialogs;

#pragma warning disable MVVMTK0034

namespace Satchel.ViewModels;

public sealed partial class VaultViewModel : PageViewModel
{
    private readonly INavigator _nav;
    private readonly DataStore _store;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly SettingsService _settings;
    private readonly List<VaultProfile> _profiles = [];
    private readonly List<VaultAccount> _accounts = [];
    private readonly Dictionary<int, AccountHealth> _health = new();
    private bool _loaded;

    private sealed record AccountHealth(bool Weak, int Reused, bool Old, bool Expiring, bool Expired)
    {
        public bool Any => Weak || Reused > 0 || Old || Expiring || Expired;
    }

    public VaultViewModel(INavigator nav, DataStore store, DialogService dialogs, ToastService toasts, SettingsService settings)
    {
        _nav = nav;
        _store = store;
        _dialogs = dialogs;
        _toasts = toasts;
        _settings = settings;
        AllFilter = new SideFilter("all", "All items", Glyphs.Shield);
        FavoritesFilter = new SideFilter("fav", "Favorites", Glyphs.Star);
        WeakFilter = new SideFilter("weak", "Weak passwords", Glyphs.Warning, null, "red");
        ReusedFilter = new SideFilter("reused", "Reused passwords", Glyphs.Sync, null, "orange");
        OldFilter = new SideFilter("old", "Older than a year", Glyphs.History, null, "amber");
        ExpiringFilter = new SideFilter("expiring", "Expiring soon", Glyphs.Clock, null, "rose");
        MainFilters = [AllFilter, FavoritesFilter];
        HealthFilters = [WeakFilter, ReusedFilter, OldFilter, ExpiringFilter];
        _selectedFilter = AllFilter;
        AllFilter.IsSelected = true;
    }

    public override string Title => "Vault";

    public SideFilter AllFilter { get; }
    public SideFilter FavoritesFilter { get; }
    public SideFilter WeakFilter { get; }
    public SideFilter ReusedFilter { get; }
    public SideFilter OldFilter { get; }
    public SideFilter ExpiringFilter { get; }
    public ObservableCollection<SideFilter> MainFilters { get; }
    public ObservableCollection<SideFilter> CategoryFilters { get; } = [];
    public ObservableCollection<SideFilter> HealthFilters { get; }

    public ObservableCollection<VaultProfile> Profiles { get; } = [];
    public ObservableCollection<AccountItemViewModel> Accounts { get; } = [];

    [ObservableProperty] private SideFilter _selectedFilter;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private VaultProfile? _selectedProfile;
    [ObservableProperty] private int _score = 100;
    [ObservableProperty] private int _totalAccounts;

    public bool HasProfiles => _profiles.Count > 0;
    public bool HasSelection => SelectedProfile != null;
    public bool CanReorder => SelectedFilter == AllFilter && string.IsNullOrWhiteSpace(SearchText);
    public bool IsListEmpty => Profiles.Count == 0;
    public string ScoreText => TotalAccounts == 0 ? "No passwords yet" : Score >= 90 ? "Excellent" : Score >= 70 ? "Good" : Score >= 50 ? "Needs attention" : "At risk";
    public double ScoreFraction => Score / 100.0;
    public int IssueCount => WeakFilter.Count + ReusedFilter.Count + OldFilter.Count + ExpiringFilter.Count;
    public string SelectedSubtitle => SelectedProfile == null ? "" :
        string.Join(" · ", new[] { SelectedProfile.Category, $"{Accounts.Count} account{(Accounts.Count == 1 ? "" : "s")}" }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public static IReadOnlyList<string> CategoryPresets { get; } =
    [
        "Email", "Social", "Finance", "Work", "Shopping", "Entertainment", "Gaming", "Developer", "Cloud", "Utilities", "Travel", "Health", "Education", "Government", "Home", "Other"
    ];

    partial void OnSelectedFilterChanged(SideFilter value)
    {
        foreach (var f in MainFilters.Concat(CategoryFilters).Concat(HealthFilters)) f.IsSelected = ReferenceEquals(f, value);
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedProfileChanged(VaultProfile? value)
    {
        LoadAccounts();
        OnPropertyChanged(nameof(HasSelection));
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_loaded) await LoadAsync();
        if (parameter is int profileId && _profiles.FirstOrDefault(p => p.Id == profileId) is { } profile)
        {
            if (!Profiles.Contains(profile)) { SearchText = ""; SelectedFilter = AllFilter; }
            SelectedProfile = profile;
        }
        else if (parameter is "new") await NewProfile();
        else if (parameter is "generator") await OpenGenerator();
        else if (parameter is "import") await ImportCsv();
        else if (parameter is "export") await ExportCsv();
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var profiles = await _store.ListAsync<VaultProfile>(q => q.OrderBy(p => p.SortOrder));
            var accounts = await _store.ListAsync<VaultAccount>(q => q.OrderBy(a => a.SortOrder));
            _profiles.Clear();
            _profiles.AddRange(profiles);
            _accounts.Clear();
            _accounts.AddRange(accounts);
            _loaded = true;
            Recompute();
            ApplyFilter();
            if (SelectedProfile == null || !_profiles.Contains(SelectedProfile)) SelectedProfile = Profiles.FirstOrDefault();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Recomputes password health, counts and categories.</summary>
    private void Recompute()
    {
        _health.Clear();
        var withPassword = _accounts.Where(a => !string.IsNullOrEmpty(a.Password)).ToList();
        var reuse = withPassword.GroupBy(a => a.Password).ToDictionary(g => g.Key, g => g.Count());
        var yearAgo = DateTime.Now.AddDays(-365);
        foreach (var a in _accounts)
        {
            bool hasPw = !string.IsNullOrEmpty(a.Password);
            var changed = a.PasswordChangedAt ?? a.CreatedAt;
            _health[a.Id] = new AccountHealth(
                Weak: hasPw && PasswordStrength.Evaluate(a.Password).Score < 2,
                Reused: hasPw ? reuse[a.Password] - 1 : 0,
                Old: hasPw && changed < yearAgo,
                Expiring: a.ExpiresAt.HasValue && a.ExpiresAt.Value >= DateTime.Today && a.ExpiresAt.Value <= DateTime.Today.AddDays(30),
                Expired: a.ExpiresAt.HasValue && a.ExpiresAt.Value < DateTime.Today);
        }

        foreach (var p in _profiles)
        {
            var mine = _accounts.Where(a => a.ProfileId == p.Id).ToList();
            p.AccountCount = mine.Count;
            p.IssueCount = mine.Count(a => _health.TryGetValue(a.Id, out var h) && h.Any);
        }

        AllFilter.Count = _profiles.Count;
        FavoritesFilter.Count = _profiles.Count(p => p.IsFavorite) + _accounts.Count(a => a.IsFavorite && !_profiles.First(p => p.Id == a.ProfileId).IsFavorite);
        WeakFilter.Count = _health.Values.Count(h => h.Weak);
        ReusedFilter.Count = _health.Values.Count(h => h.Reused > 0);
        OldFilter.Count = _health.Values.Count(h => h.Old);
        ExpiringFilter.Count = _health.Values.Count(h => h.Expiring || h.Expired);
        TotalAccounts = withPassword.Count;
        var flagged = withPassword.Count(a => _health[a.Id].Weak || _health[a.Id].Reused > 0 || _health[a.Id].Old);
        Score = TotalAccounts == 0 ? 100 : (int)Math.Round(100.0 * (TotalAccounts - flagged) / TotalAccounts);

        var selectedKey = SelectedFilter.Key;
        var categories = _profiles.Select(p => string.IsNullOrWhiteSpace(p.Category) ? "Uncategorized" : p.Category.Trim())
            .GroupBy(c => c, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key).ToList();
        CategoryFilters.Clear();
        foreach (var g in categories)
            CategoryFilters.Add(new SideFilter($"cat:{g.Key.ToLowerInvariant()}", g.Key, CategoryGlyph(g.Key), g.Key) { Count = g.Count() });
        if (selectedKey.StartsWith("cat:"))
        {
            var again = CategoryFilters.FirstOrDefault(f => f.Key == selectedKey);
            if (again != null) { _selectedFilter = again; again.IsSelected = true; }
            else { _selectedFilter = AllFilter; AllFilter.IsSelected = true; }
        }

        foreach (var name in new[] { nameof(HasProfiles), nameof(ScoreText), nameof(ScoreFraction), nameof(IssueCount) }) OnPropertyChanged(name);
    }

    public static string CategoryGlyph(string category) => category.ToLowerInvariant() switch
    {
        "email" => Glyphs.Mail,
        "social" => Glyphs.People,
        "finance" or "banking" or "bank" => Glyphs.Bank,
        "work" => Glyphs.Work,
        "shopping" => Glyphs.Shop,
        "entertainment" or "streaming" => Glyphs.Video,
        "gaming" or "games" => Glyphs.Game,
        "developer" or "dev" or "development" => Glyphs.Code,
        "cloud" => Glyphs.Cloud,
        "utilities" => Glyphs.Lightbulb,
        "travel" => Glyphs.Airplane,
        "health" => Glyphs.Health,
        "education" or "school" => Glyphs.Education,
        "government" => Glyphs.Bank,
        "home" => Glyphs.Home,
        "uncategorized" => Glyphs.Folder,
        _ => Glyphs.Tag
    };

    private bool ProfileMatches(VaultProfile p)
    {
        var f = SelectedFilter;
        var mine = _accounts.Where(a => a.ProfileId == p.Id);
        bool pass = f switch
        {
            _ when f == AllFilter => true,
            _ when f == FavoritesFilter => p.IsFavorite || mine.Any(a => a.IsFavorite),
            _ when f == WeakFilter => mine.Any(a => _health.TryGetValue(a.Id, out var h) && h.Weak),
            _ when f == ReusedFilter => mine.Any(a => _health.TryGetValue(a.Id, out var h) && h.Reused > 0),
            _ when f == OldFilter => mine.Any(a => _health.TryGetValue(a.Id, out var h) && h.Old),
            _ when f == ExpiringFilter => mine.Any(a => _health.TryGetValue(a.Id, out var h) && (h.Expiring || h.Expired)),
            _ when f.Payload is string cat => string.Equals(string.IsNullOrWhiteSpace(p.Category) ? "Uncategorized" : p.Category.Trim(), cat, StringComparison.OrdinalIgnoreCase),
            _ => true
        };
        if (!pass) return false;

        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return true;
        string hay = $"{p.Name} {p.Website} {p.Category} {p.Tags} {p.Notes} " + string.Join(' ', mine.Select(a =>
            $"{a.Label} {a.Username} {a.Email} {a.Url} {a.Tags} {a.SignInWith} " +
            string.Join(' ', CustomField.Parse(a.Fields).Where(cf => !CustomField.IsSecretType(cf.Type)).Select(cf => $"{cf.Label} {cf.Value}"))));
        return terms.All(t => hay.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    public void ApplyFilter()
    {
        if (!_loaded) return;
        var list = _profiles.Where(ProfileMatches).ToList();
        if (SelectedFilter != AllFilter || !string.IsNullOrWhiteSpace(SearchText))
            list = list.OrderByDescending(p => p.IsFavorite).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        for (int i = Profiles.Count - 1; i >= 0; i--)
            if (!list.Contains(Profiles[i])) Profiles.RemoveAt(i);
        for (int i = 0; i < list.Count; i++)
        {
            int existing = Profiles.IndexOf(list[i]);
            if (existing == -1) Profiles.Insert(i, list[i]);
            else if (existing != i) Profiles.Move(existing, i);
        }
        if (SelectedProfile != null && !Profiles.Contains(SelectedProfile)) SelectedProfile = Profiles.FirstOrDefault();
        else LoadAccounts();
        OnPropertyChanged(nameof(CanReorder));
        OnPropertyChanged(nameof(IsListEmpty));
    }

    private void LoadAccounts()
    {
        Accounts.Clear();
        if (SelectedProfile == null) { OnPropertyChanged(nameof(SelectedSubtitle)); return; }
        foreach (var a in _accounts.Where(a => a.ProfileId == SelectedProfile.Id).OrderBy(a => a.SortOrder))
        {
            var item = new AccountItemViewModel(a, OnPasswordCopied);
            ApplyHealth(item);
            Accounts.Add(item);
        }
        OnPropertyChanged(nameof(SelectedSubtitle));
    }

    private void ApplyHealth(AccountItemViewModel item)
    {
        if (!_health.TryGetValue(item.Account.Id, out var h)) return;
        item.IsWeak = h.Weak;
        item.ReuseCount = h.Reused;
        item.IsOld = h.Old;
        item.IsExpiring = h.Expiring;
        item.IsExpired = h.Expired;
        var f = SelectedFilter;
        item.IsHighlighted = (f == WeakFilter && h.Weak) || (f == ReusedFilter && h.Reused > 0) || (f == OldFilter && h.Old) || (f == ExpiringFilter && (h.Expiring || h.Expired));
    }

    private async void OnPasswordCopied(VaultAccount account)
    {
        account.LastUsedAt = DateTime.Now;
        try { await _store.UpdateAsync(account); } catch { }
    }

    // ----- commands -----

    [RelayCommand]
    private void SelectFilter(SideFilter? filter)
    {
        if (filter != null) SelectedFilter = filter;
    }

    [RelayCommand]
    private void SelectProfile(VaultProfile? profile) => SelectedProfile = profile;

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    public override void OnNewShortcut() => _ = NewProfile();

    [RelayCommand]
    private async Task NewProfile()
    {
        var vm = new ProfileEditorViewModel(null, ExistingCategories());
        if (await _dialogs.ShowAsync(vm) is not "save") return;
        var p = new VaultProfile { SortOrder = (_profiles.Count == 0 ? 0 : _profiles.Min(x => x.SortOrder)) - 1 };
        vm.ApplyTo(p);
        await _store.AddAsync(p);
        _profiles.Add(p);
        Recompute();
        SearchText = "";
        if (SelectedFilter != AllFilter && SelectedFilter.Payload is not string) SelectedFilter = AllFilter;
        ApplyFilter();
        SelectedProfile = p;
        _toasts.Success($"“{p.Name}” added — now add an account to it");
        if (vm.AddAccountAfterSave) await NewAccount();
    }

    private IEnumerable<string> ExistingCategories() =>
        CategoryPresets.Concat(_profiles.Select(p => p.Category)).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c);

    [RelayCommand]
    private async Task EditProfile(VaultProfile? profile)
    {
        profile ??= SelectedProfile;
        if (profile == null) return;
        var vm = new ProfileEditorViewModel(profile, ExistingCategories());
        var result = await _dialogs.ShowAsync(vm);
        if (result is "save")
        {
            vm.ApplyTo(profile);
            profile.UpdatedAt = DateTime.Now;
            await _store.UpdateAsync(profile);
            Recompute();
            ApplyFilter();
            OnPropertyChanged(nameof(SelectedSubtitle));
        }
        else if (result is "delete") await DeleteProfile(profile);
    }

    [RelayCommand]
    private async Task DeleteProfile(VaultProfile? profile)
    {
        profile ??= SelectedProfile;
        if (profile == null) return;
        int count = _accounts.Count(a => a.ProfileId == profile.Id);
        if (!await _dialogs.ConfirmAsync("Delete profile?", count == 0
                ? $"“{profile.Name}” will be deleted."
                : $"“{profile.Name}” and its {count} account(s) will be permanently deleted.", "Delete", danger: true)) return;
        await _store.DeleteAsync(profile);
        _profiles.Remove(profile);
        _accounts.RemoveAll(a => a.ProfileId == profile.Id);
        if (SelectedProfile == profile) SelectedProfile = null;
        Recompute();
        ApplyFilter();
        SelectedProfile ??= Profiles.FirstOrDefault();
        _toasts.Info($"Deleted “{profile.Name}”");
    }

    [RelayCommand]
    private async Task ToggleProfileFavorite(VaultProfile? profile)
    {
        profile ??= SelectedProfile;
        if (profile == null) return;
        profile.IsFavorite = !profile.IsFavorite;
        await _store.UpdateAsync(profile);
        Recompute();
    }

    [RelayCommand]
    private async Task NewAccount()
    {
        if (SelectedProfile == null) { await NewProfile(); return; }
        var account = new VaultAccount
        {
            ProfileId = SelectedProfile.Id,
            Kind = AccountKind.Login,
            Url = SelectedProfile.Website,
            SortOrder = _accounts.Where(a => a.ProfileId == SelectedProfile.Id).Select(a => a.SortOrder).DefaultIfEmpty(0).Max() + 1
        };
        var vm = new AccountEditorViewModel(account, true, _settings.Current.Generator, _dialogs, SelectedProfile.Name);
        if (await _dialogs.ShowAsync(vm) is not "save") return;
        vm.ApplyTo(account);
        await _store.AddAsync(account);
        _accounts.Add(account);
        Recompute();
        LoadAccounts();
        _toasts.Success("Account saved (encrypted)");
    }

    [RelayCommand]
    private async Task EditAccount(AccountItemViewModel? item)
    {
        if (item == null) return;
        var parent = _profiles.FirstOrDefault(p => p.Id == item.Account.ProfileId);
        var vm = new AccountEditorViewModel(item.Account, false, _settings.Current.Generator, _dialogs, parent?.Name ?? "");
        var result = await _dialogs.ShowAsync(vm);
        if (result is "save")
        {
            vm.ApplyTo(item.Account);
            item.Account.UpdatedAt = DateTime.Now;
            await _store.UpdateAsync(item.Account);
            Recompute();
            item.Rebuild();
            ApplyHealth(item);
        }
        else if (result is "delete") await DeleteAccount(item);
    }

    [RelayCommand]
    private async Task DeleteAccount(AccountItemViewModel? item)
    {
        if (item == null) return;
        if (!await _dialogs.ConfirmAsync("Delete account?", $"“{item.Account.DisplayName}” will be permanently deleted.", "Delete", danger: true)) return;
        await _store.DeleteAsync(item.Account);
        _accounts.Remove(item.Account);
        Accounts.Remove(item);
        Recompute();
        OnPropertyChanged(nameof(SelectedSubtitle));
    }

    [RelayCommand]
    private async Task ToggleAccountFavorite(AccountItemViewModel? item)
    {
        if (item == null) return;
        item.Account.IsFavorite = !item.Account.IsFavorite;
        await _store.UpdateAsync(item.Account);
        Recompute();
    }

    [RelayCommand]
    private async Task DuplicateAccount(AccountItemViewModel? item)
    {
        if (item == null) return;
        var a = item.Account;
        var copy = new VaultAccount
        {
            ProfileId = a.ProfileId, Kind = a.Kind, Label = string.IsNullOrWhiteSpace(a.Label) ? "Copy" : a.Label + " (copy)", Username = a.Username, Email = a.Email,
            Password = a.Password, Url = a.Url, TotpSecret = a.TotpSecret, Notes = a.Notes, Fields = a.Fields, Tags = a.Tags, SignInWith = a.SignInWith,
            Image = a.Image, ExpiresAt = a.ExpiresAt, PasswordChangedAt = a.PasswordChangedAt, SortOrder = a.SortOrder + 0.5
        };
        await _store.AddAsync(copy);
        _accounts.Add(copy);
        Recompute();
        LoadAccounts();
    }

    public async Task MoveAccountAsync(AccountItemViewModel item, VaultProfile target)
    {
        if (item.Account.ProfileId == target.Id) return;
        item.Account.ProfileId = target.Id;
        await _store.UpdateAsync(item.Account);
        Recompute();
        LoadAccounts();
        _toasts.Success($"Moved to {target.Name}");
    }

    [RelayCommand]
    private async Task DropOnProfile(DropEventArgs? args)
    {
        if (args?.Data is AccountItemViewModel item && args.Target is VaultProfile target) await MoveAccountAsync(item, target);
    }

    [RelayCommand]
    private async Task ReorderProfiles(ReorderEventArgs? args)
    {
        for (int i = 0; i < Profiles.Count; i++) Profiles[i].SortOrder = i;
        await _store.SaveOrderAsync(Profiles, p => p.SortOrder);
    }

    [RelayCommand]
    private async Task ReorderAccounts(ReorderEventArgs? args)
    {
        for (int i = 0; i < Accounts.Count; i++) Accounts[i].Account.SortOrder = i;
        await _store.SaveOrderAsync(Accounts.Select(a => a.Account), a => a.SortOrder);
    }

    [RelayCommand]
    private async Task OpenGenerator()
    {
        await _dialogs.ShowAsync(new PasswordGeneratorViewModel(_settings.Current.Generator, canUse: false));
    }

    [RelayCommand]
    private async Task ImportCsv()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import passwords (CSV from Chrome, Edge, Firefox, Bitwarden, 1Password…)",
            Filter = "CSV files (*.csv)|*.csv|All files|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        List<ImportedLogin> logins;
        try { logins = CsvService.ParseLogins(await File.ReadAllTextAsync(dialog.FileName)); }
        catch (Exception ex) { _toasts.Error("Couldn't read the file: " + ex.Message); return; }
        if (logins.Count == 0) { _toasts.Warning("No logins found in that file."); return; }

        var groups = logins.GroupBy(l => l.Name.Trim(), StringComparer.OrdinalIgnoreCase).ToList();
        if (!await _dialogs.ConfirmAsync("Import passwords",
                $"Found {logins.Count} login(s) for {groups.Count} site(s). They'll be added as profiles with accounts. " +
                "Delete the CSV file afterwards — it isn't encrypted.", "Import")) return;

        int order = _profiles.Count;
        var newAccounts = new List<VaultAccount>();
        foreach (var g in groups)
        {
            var profile = _profiles.FirstOrDefault(p => string.Equals(p.Name, g.Key, StringComparison.OrdinalIgnoreCase));
            if (profile == null)
            {
                var first = g.First();
                profile = new VaultProfile
                {
                    Name = g.Key,
                    Website = first.Url,
                    Category = string.IsNullOrWhiteSpace(first.Folder) ? "" : first.Folder,
                    Color = Palettes.Tags[order % Palettes.Tags.Count].Key,
                    Icon = Glyphs.Globe,
                    SortOrder = order++
                };
                await _store.AddAsync(profile);
                _profiles.Add(profile);
            }
            int i = 0;
            foreach (var l in g)
            {
                var isEmail = l.Username.Contains('@');
                newAccounts.Add(new VaultAccount
                {
                    ProfileId = profile.Id,
                    Kind = AccountKind.Login,
                    Username = isEmail ? "" : l.Username,
                    Email = isEmail ? l.Username : "",
                    Password = l.Password,
                    Url = l.Url,
                    Notes = l.Notes,
                    TotpSecret = l.Totp,
                    PasswordChangedAt = DateTime.Now,
                    SortOrder = i++
                });
            }
        }
        await _store.AddRangeAsync(newAccounts);
        _accounts.AddRange(newAccounts);
        Recompute();
        ApplyFilter();
        _toasts.Success($"Imported {newAccounts.Count} account(s)");
    }

    [RelayCommand]
    private async Task ExportCsv()
    {
        if (!await _dialogs.ConfirmAsync("Export unencrypted?",
                "The CSV file will contain all your passwords in plain text. Store it safely and delete it when you're done.", "Export anyway", danger: true)) return;
        var dialog = new SaveFileDialog { Title = "Export vault", FileName = $"satchel-vault-{DateTime.Now:yyyyMMdd}.csv", Filter = "CSV files (*.csv)|*.csv" };
        if (dialog.ShowDialog() != true) return;
        await File.WriteAllTextAsync(dialog.FileName, CsvService.BuildExport(_profiles, _accounts), new UTF8Encoding(true));
        _toasts.Warning("Exported — remember this file is NOT encrypted.");
    }
}
