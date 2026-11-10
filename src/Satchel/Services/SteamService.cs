using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using Satchel.Data;
using Satchel.Models;

namespace Satchel.Services;

public sealed record SteamGame(int AppId, string Name, int Minutes, DateTime? LastPlayed, bool Installed);

/// <summary>
/// Reads a Steam library two ways: from the local Steam install (installed games and playtime, no key needed) or with the
/// Steam Web API (every owned game; needs an API key — kept in the Vault — and a public game list).
/// </summary>
public sealed partial class SteamService
{
    private const string IdKey = "steam.id";
    private const string HiddenKey = "steam.hidden";
    private readonly WebService _web;
    private readonly DataStore _store;

    public SteamService(WebService web, DataStore store)
    {
        _web = web;
        _store = store;
    }

    // ----- local install -----

    public static string? FindSteamFolder()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string path && Directory.Exists(path)) return Path.GetFullPath(path.Replace('/', '\\'));
        }
        catch { /* no registry access */ }
        foreach (var candidate in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                 }.Where(p => p.Length > 0).Select(p => Path.Combine(p, "Steam")))
            if (Directory.Exists(Path.Combine(candidate, "steamapps"))) return candidate;
        return null;
    }

    /// <summary>Installed games (with names) plus playtime from the local Steam config. Runs on a worker thread.</summary>
    public static (List<SteamGame> Games, string? Error) ReadLocal()
    {
        var steam = FindSteamFolder();
        if (steam == null) return ([], "Steam isn't installed on this PC (or it's somewhere unusual).");
        var libraries = new List<string> { steam };
        try
        {
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf) && Vdf.Parse(File.ReadAllText(vdf)).Get("libraryfolders") is Dictionary<string, object> folders)
                foreach (var folder in folders.Values.OfType<Dictionary<string, object>>())
                    if (folder.GetString("path") is { } p && Directory.Exists(p)) libraries.Add(Path.GetFullPath(p));
        }
        catch { /* fall back to the main library */ }

        // Playtime and last-played per app, from every Steam account used on this PC.
        var played = new Dictionary<int, (int Minutes, DateTime? Last)>();
        try
        {
            var userdata = Path.Combine(steam, "userdata");
            if (Directory.Exists(userdata))
                foreach (var config in Directory.GetDirectories(userdata).Select(d => Path.Combine(d, "config", "localconfig.vdf")).Where(File.Exists))
                {
                    var root = Vdf.Parse(File.ReadAllText(config));
                    if (root.Get("UserLocalConfigStore") is Dictionary<string, object> store &&
                        store.Get("Software") is Dictionary<string, object> software && software.Get("Valve") is Dictionary<string, object> valve &&
                        valve.Get("Steam") is Dictionary<string, object> steamNode && steamNode.Get("apps") is Dictionary<string, object> apps)
                        foreach (var (id, value) in apps)
                        {
                            if (!int.TryParse(id, out var appId) || value is not Dictionary<string, object> app) continue;
                            int minutes = int.TryParse(app.GetString("Playtime"), out var m) ? m : 0;
                            DateTime? last = long.TryParse(app.GetString("LastPlayed"), out var unix) && unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime : null;
                            var prev = played.GetValueOrDefault(appId);
                            played[appId] = (Math.Max(prev.Minutes, minutes), last > prev.Last || prev.Last == null ? last : prev.Last);
                        }
                }
        }
        catch { /* playtime is optional */ }

        var games = new Dictionary<int, SteamGame>();
        foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var manifest in Directory.GetFiles(apps, "appmanifest_*.acf"))
            {
                try
                {
                    if (Vdf.Parse(File.ReadAllText(manifest)).Get("AppState") is not Dictionary<string, object> state) continue;
                    if (!int.TryParse(state.GetString("appid"), out var appId) || state.GetString("name") is not { Length: > 0 } name) continue;
                    if (IsTool(appId, name)) continue;
                    var p = played.GetValueOrDefault(appId);
                    games[appId] = new SteamGame(appId, name, p.Minutes, p.Last, true);
                }
                catch { /* skip a broken manifest */ }
            }
        }
        return (games.Values.OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).ThenBy(g => g.Name).ToList(), null);
    }

    private static bool IsTool(int appId, string name) =>
        appId is 228980 or 250820 or 1070560 or 1391110 or 1628350 or 1826330 ||
        name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Steamworks", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("SteamVR", StringComparison.OrdinalIgnoreCase);

    // ----- Web API -----

    /// <summary>Every game the account owns (needs the game list to be public in Steam's privacy settings).</summary>
    public async Task<(List<SteamGame> Games, string? SteamId, string? Error)> ReadAccountAsync(string apiKey, string account)
    {
        if (!_web.IsAvailable) return ([], null, _web.Enabled ? "You're offline." : "Online features are turned off in Settings.");
        var steamId = await ResolveSteamIdAsync(apiKey, account);
        if (steamId == null) return ([], null, "Couldn't find that Steam account — use your profile link or your 17-digit SteamID.");
        var json = await _web.GetStringAsync($"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={Uri.EscapeDataString(apiKey)}&steamid={steamId}&include_appinfo=1&include_played_free_games=1&format=json");
        if (json == null) return ([], steamId, "Steam didn't answer. Check the API key (steamcommunity.com/dev/apikey).");
        var games = new List<SteamGame>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var response) || !response.TryGetProperty("games", out var list))
                return ([], steamId, "No games came back — is your game list public? (Steam → Profile → Edit profile → Privacy settings → Game details: Public)");
            foreach (var g in list.EnumerateArray())
            {
                var id = g.TryGetProperty("appid", out var a) ? a.GetInt32() : 0;
                var name = g.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var minutes = g.TryGetProperty("playtime_forever", out var p) ? p.GetInt32() : 0;
                DateTime? last = g.TryGetProperty("rtime_last_played", out var r) && r.GetInt64() > 0 ? DateTimeOffset.FromUnixTimeSeconds(r.GetInt64()).LocalDateTime : null;
                if (id > 0 && name.Length > 0) games.Add(new SteamGame(id, name, minutes, last, false));
            }
        }
        catch (JsonException)
        {
            return ([], steamId, "Steam sent something unexpected. Try again later.");
        }
        return (games.OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).ThenByDescending(g => g.Minutes).ToList(), steamId, null);
    }

    private async Task<string?> ResolveSteamIdAsync(string apiKey, string account)
    {
        account = account.Trim().TrimEnd('/');
        if (SteamIdRegex().Match(account) is { Success: true } id) return id.Value;
        var vanity = VanityRegex().Match(account) is { Success: true } v ? v.Groups[1].Value : account;
        if (vanity.Length == 0 || vanity.Contains(' ')) return null;
        var json = await _web.GetStringAsync($"https://api.steampowered.com/ISteamUser/ResolveVanityURL/v1/?key={Uri.EscapeDataString(apiKey)}&vanityurl={Uri.EscapeDataString(vanity)}");
        if (json == null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement.GetProperty("response");
            return r.TryGetProperty("success", out var s) && s.GetInt32() == 1 ? r.GetProperty("steamid").GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\b7656\d{13}\b")]
    private static partial Regex SteamIdRegex();

    [GeneratedRegex(@"steamcommunity\.com/id/([^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex VanityRegex();

    // ----- the key in the Vault, remembered account, hidden games -----

    /// <summary>The Steam Web API key stored in the Vault (an "API token" account under a profile named Steam), if any.</summary>
    public Task<string?> FindApiKeyAsync() => _store.RunAsync(db =>
    {
        var profiles = db.Profiles.IgnoreQueryFilters().ToList().Where(p => p.Name.Contains("Steam", StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).ToHashSet();
        return db.Accounts.IgnoreQueryFilters().Where(a => a.Kind == AccountKind.ApiToken).ToList()
            .FirstOrDefault(a => profiles.Contains(a.ProfileId) || a.Label.Contains("Steam", StringComparison.OrdinalIgnoreCase))?.Password is { Length: > 0 } key ? key : null;
    });

    /// <summary>Saves the key in the Vault: a "Steam" profile with a "Web API key" account (created if missing).</summary>
    public Task SaveApiKeyAsync(string apiKey, string? steamId) => _store.RunAsync(db =>
    {
        var profile = db.Profiles.IgnoreQueryFilters().ToList().FirstOrDefault(p => p.Name.Equals("Steam", StringComparison.OrdinalIgnoreCase));
        if (profile == null)
        {
            profile = new VaultProfile { Name = "Steam", Website = "https://store.steampowered.com", Category = "Games", Icon = Core.Glyphs.Game, Color = "slate" };
            db.Profiles.Add(profile);
            db.SaveChanges();
        }
        var account = db.Accounts.IgnoreQueryFilters().AsTracking().ToList().FirstOrDefault(a => a.ProfileId == profile.Id && a.Kind == AccountKind.ApiToken);
        if (account == null)
        {
            account = new VaultAccount { ProfileId = profile.Id, Kind = AccountKind.ApiToken, Label = "Steam Web API key", Url = "https://steamcommunity.com/dev/apikey" };
            db.Accounts.Add(account);
        }
        account.Password = apiKey;
        if (steamId != null) account.Username = steamId;
        account.PasswordChangedAt = DateTime.Now;
        db.SaveChanges();
    });

    public Task<string?> GetSavedAccountAsync() => _store.GetSecureAsync(IdKey);
    public Task SaveAccountAsync(string account) => _store.SetSecureAsync(IdKey, account);

    public async Task<HashSet<int>> GetHiddenAsync()
    {
        var text = await _store.GetSecureAsync(HiddenKey) ?? "";
        return text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out var v) ? v : 0).Where(v => v > 0).ToHashSet();
    }

    public Task SaveHiddenAsync(IEnumerable<int> ids) => _store.SetSecureAsync(HiddenKey, string.Join(",", ids.Distinct()));
}

/// <summary>Minimal reader for Valve's text KeyValues format (.vdf / .acf).</summary>
public static class Vdf
{
    public static Dictionary<string, object> Parse(string text)
    {
        int i = 0;
        return ReadObject(text, ref i);
    }

    private static Dictionary<string, object> ReadObject(string s, ref int i)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var key = ReadToken(s, ref i);
            if (key == null || key == "}") return result;
            var value = ReadToken(s, ref i);
            if (value == null) return result;
            result[key] = value == "{" ? ReadObject(s, ref i) : value;
        }
    }

    private static string? ReadToken(string s, ref int i)
    {
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c is '{' or '}') { i++; return c.ToString(); }
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length) { i++; sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', _ => s[i] }); }
                    else sb.Append(s[i]);
                    i++;
                }
                i++;
                return sb.ToString();
            }
            int start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not ('{' or '}' or '"')) i++;
            return s[start..i];
        }
        return null;
    }

    public static object? Get(this Dictionary<string, object> d, string key) => d.TryGetValue(key, out var v) ? v : null;
    public static string? GetString(this Dictionary<string, object> d, string key) => d.TryGetValue(key, out var v) ? v as string : null;
}
