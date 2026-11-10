using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Behaviors;
using Satchel.Core;
using Satchel.Models;
using Satchel.Services;

namespace Satchel.ViewModels;

/// <summary>One game in the Steam import list.</summary>
public sealed partial class SteamImportRow : ObservableObject
{
    public SteamImportRow(SteamGame game, bool imported)
    {
        Game = game;
        AlreadyImported = imported;
        _isSelected = false;
    }

    public SteamGame Game { get; }
    public bool AlreadyImported { get; }
    public string Name => Game.Name;
    public string HoursText => Game.Minutes <= 0 ? "Not played" : Game.Minutes < 60 ? $"{Game.Minutes} min" : $"{Game.Minutes / 60.0:0.#} h";
    public string LastText => Game.LastPlayed is { } d ? $"Played {Format.Relative(d)}" : Game.Installed ? "Installed" : "";
    public string ThumbUrl => $"https://cdn.akamai.steamstatic.com/steam/apps/{Game.AppId}/capsule_184x69.jpg";

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isPrivate;
}

public sealed partial class GamesViewModel
{
    private SteamService? _steamService;
    private SteamService Steam => _steamService ??= new SteamService(_web, _store);
    private List<SteamImportRow> _steamAll = [];
    private HashSet<int> _steamHidden = [];

    public ObservableCollection<SteamImportRow> SteamRows { get; } = [];

    [ObservableProperty] private bool _isSteamOpen;
    [ObservableProperty] private string _steamSource = "local";
    [ObservableProperty] private string _steamAccount = "";
    [ObservableProperty] private string _steamKey = "";
    [ObservableProperty] private bool _steamKeyFromVault;
    [ObservableProperty] private bool _saveSteamKey = true;
    [ObservableProperty] private string _steamFilter = "";
    [ObservableProperty] private string _steamMessage = "";
    [ObservableProperty] private bool _isSteamBusy;
    [ObservableProperty] private bool _steamShowImported;

    public string SteamFolderText => SteamService.FindSteamFolder() is { } f ? $"Steam found in {f}" : "Steam isn't installed on this PC.";
    public int SteamSelectedCount => _steamAll.Count(r => r.IsSelected);
    public bool HasSteamRows => SteamRows.Count > 0;
    public bool IsSteamLocal => SteamSource == "local";
    public bool IsSteamAccount => SteamSource == "account";

    partial void OnSteamSourceChanged(string value)
    {
        OnPropertyChanged(nameof(IsSteamLocal));
        OnPropertyChanged(nameof(IsSteamAccount));
        _steamAll.Clear();
        ApplySteamFilter();
        SteamMessage = "";
    }

    partial void OnSteamFilterChanged(string value) => ApplySteamFilter();
    partial void OnSteamShowImportedChanged(bool value) => ApplySteamFilter();

    [RelayCommand]
    private async Task OpenSteam()
    {
        IsSteamOpen = true;
        SteamMessage = "";
        _steamHidden = await Steam.GetHiddenAsync();
        SteamAccount = await Steam.GetSavedAccountAsync() ?? "";
        var key = await Steam.FindApiKeyAsync();
        SteamKeyFromVault = key != null;
        SteamKey = key ?? "";
        OnPropertyChanged(nameof(SteamFolderText));
        if (IsSteamLocal) await LoadSteam();
    }

    [RelayCommand]
    private void CloseSteam()
    {
        if (IsSteamBusy) return;
        IsSteamOpen = false;
    }

    [RelayCommand]
    private void SetSteamSource(string? source) => SteamSource = source ?? "local";

    [RelayCommand]
    private async Task LoadSteam()
    {
        IsSteamBusy = true;
        SteamMessage = IsSteamLocal ? "Reading your Steam install…" : "Asking Steam for your library…";
        try
        {
            List<SteamGame> games;
            string? error;
            if (IsSteamLocal)
            {
                (games, error) = await Task.Run(SteamService.ReadLocal);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(SteamKey)) { SteamMessage = "Paste your Steam Web API key (get one at steamcommunity.com/dev/apikey)."; return; }
                if (string.IsNullOrWhiteSpace(SteamAccount)) { SteamMessage = "Paste your Steam profile link or SteamID."; return; }
                string? steamId;
                (games, steamId, error) = await Steam.ReadAccountAsync(SteamKey.Trim(), SteamAccount.Trim());
                if (error == null)
                {
                    await Steam.SaveAccountAsync(SteamAccount.Trim());
                    if (SaveSteamKey && !SteamKeyFromVault)
                    {
                        await Steam.SaveApiKeyAsync(SteamKey.Trim(), steamId);
                        SteamKeyFromVault = true;
                        _toasts.Success("API key saved in the Vault (profile “Steam”)");
                    }
                }
            }
            if (error != null)
            {
                SteamMessage = error;
                _steamAll.Clear();
                ApplySteamFilter();
                return;
            }
            var imported = Games.Where(g => g.SteamAppId > 0).Select(g => g.SteamAppId).ToHashSet();
            _steamAll = games.Where(g => !_steamHidden.Contains(g.AppId)).Select(g => new SteamImportRow(g, imported.Contains(g.AppId))).ToList();
            foreach (var r in _steamAll) r.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SteamImportRow.IsSelected)) OnPropertyChanged(nameof(SteamSelectedCount)); };
            int hidden = games.Count(g => _steamHidden.Contains(g.AppId));
            SteamMessage = games.Count == 0 ? "No games found." :
                $"{games.Count} games · {_steamAll.Count(r => r.AlreadyImported)} already in Satchel" + (hidden > 0 ? $" · {hidden} hidden" : "") +
                (IsSteamLocal ? " · this PC only lists installed games" : "");
            ApplySteamFilter();
        }
        finally
        {
            IsSteamBusy = false;
        }
    }

    private void ApplySteamFilter()
    {
        var q = SteamFilter.Trim();
        SteamRows.Clear();
        foreach (var r in _steamAll.Where(r => (SteamShowImported || !r.AlreadyImported) && (q.Length == 0 || r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))))
            SteamRows.Add(r);
        OnPropertyChanged(nameof(HasSteamRows));
        OnPropertyChanged(nameof(SteamSelectedCount));
    }

    [RelayCommand]
    private void SelectAllSteam()
    {
        bool select = SteamRows.Any(r => !r.IsSelected && !r.AlreadyImported);
        foreach (var r in SteamRows.Where(r => !r.AlreadyImported)) r.IsSelected = select;
    }

    /// <summary>"Don't show again": the game is left out of future imports (kept encrypted).</summary>
    [RelayCommand]
    private async Task HideSteamGame(SteamImportRow? row)
    {
        if (row == null) return;
        _steamHidden.Add(row.Game.AppId);
        await Steam.SaveHiddenAsync(_steamHidden);
        _steamAll.Remove(row);
        SteamRows.Remove(row);
        OnPropertyChanged(nameof(SteamSelectedCount));
    }

    [RelayCommand]
    private void OpenSteamKeyPage() => UiCommands.OpenUrl.Execute("https://steamcommunity.com/dev/apikey");

    [RelayCommand]
    private async Task ImportSteam()
    {
        var picked = _steamAll.Where(r => r.IsSelected && !r.AlreadyImported).ToList();
        if (picked.Count == 0) { _toasts.Info("Tick the games you want to bring in"); return; }
        IsSteamBusy = true;
        try
        {
            var games = picked.Select(r => new Game
            {
                Title = r.Game.Name, SteamAppId = r.Game.AppId, Platform = "PC", StoreUrl = $"https://store.steampowered.com/app/{r.Game.AppId}",
                Hours = Math.Round(r.Game.Minutes / 60.0, 1), LastPlayedAt = r.Game.LastPlayed, IsPrivate = r.IsPrivate,
                Status = r.Game.LastPlayed is { } last && last > DateTime.Now.AddDays(-21) ? GameStatus.Playing : GameStatus.Backlog,
                Color = RandomColor(), SortOrder = Backlog.Count
            }).ToList();
            await _store.AddRangeAsync(games);
            foreach (var g in games) if (!g.IsPrivate || PrivacyStateShown) Games.Add(g);
            Distribute();
            IsSteamOpen = false;
            _toasts.Success(games.Count == 1 ? $"Imported {games[0].Title}" : $"Imported {games.Count} games — covers are on their way");
            _ = FetchCoversAsync(games);
        }
        finally
        {
            IsSteamBusy = false;
        }
    }

    private static bool PrivacyStateShown => Core.PrivacyState.ShowPrivate;

    private async Task FetchCoversAsync(List<Game> games)
    {
        foreach (var g in games)
        {
            if (!_web.IsAvailable) return;
            g.Cover = await _web.GetImageAsync($"https://cdn.akamai.steamstatic.com/steam/apps/{g.SteamAppId}/library_600x900.jpg", ImageService.CoverSize)
                      ?? await _web.GetImageAsync($"https://cdn.akamai.steamstatic.com/steam/apps/{g.SteamAppId}/header.jpg", ImageService.CoverSize);
            if (g.Cover != null) await _store.UpdateAsync(g);
        }
    }

    /// <summary>Brings the hours of already-imported games up to date with Steam.</summary>
    [RelayCommand]
    private async Task SyncSteamHours()
    {
        var byApp = _steamAll.ToDictionary(r => r.Game.AppId, r => r.Game);
        var changed = new List<Game>();
        foreach (var g in Games.Where(g => g.SteamAppId > 0 && byApp.ContainsKey(g.SteamAppId)))
        {
            var s = byApp[g.SteamAppId];
            var hours = Math.Round(s.Minutes / 60.0, 1);
            bool update = false;
            if (hours > g.Hours) { g.Hours = hours; update = true; }
            if (s.LastPlayed is { } last && (g.LastPlayedAt == null || last > g.LastPlayedAt)) { g.LastPlayedAt = last; update = true; }
            if (update) changed.Add(g);
        }
        if (changed.Count == 0) { _toasts.Info(_steamAll.Count == 0 ? "Load your Steam library first" : "Everything is up to date"); return; }
        await _store.UpdateRangeAsync(changed);
        Distribute();
        _toasts.Success($"Updated playtime for {changed.Count} game{(changed.Count == 1 ? "" : "s")}");
    }
}
