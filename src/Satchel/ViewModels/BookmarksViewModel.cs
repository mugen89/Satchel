using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Satchel.Behaviors;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Services;

namespace Satchel.ViewModels;

/// <summary>Bookmarks: saved links with previews, folders, favorites, read-later; import from browsers, export as HTML.</summary>
public sealed partial class BookmarksViewModel : PageViewModel, IDisposable
{
    private readonly DataStore _store;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly SettingsService _settings;
    private readonly WebService _web;
    private readonly DispatcherTimer _saveTimer;
    private readonly HashSet<Bookmark> _dirty = [];
    private List<Bookmark> _all = [];
    private bool _loaded;

    public BookmarksViewModel(INavigator nav, DataStore store, DialogService dialogs, ToastService toasts, SettingsService settings, WebService web)
    {
        _store = store;
        _dialogs = dialogs;
        _toasts = toasts;
        _settings = settings;
        _web = web;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += async (_, _) => await FlushAsync();
        AllFilter = new SideFilter("all", "All bookmarks", Glyphs.Bookmark);
        FavoritesFilter = new SideFilter("fav", "Favorites", Glyphs.Star);
        LaterFilter = new SideFilter("later", "Read later", Glyphs.Clock);
        RecentFilter = new SideFilter("recent", "Recently opened", Glyphs.History);
        _selectedFilter = AllFilter;
        AllFilter.IsSelected = true;
    }

    public override string Title => "Bookmarks";
    public SideFilter AllFilter { get; }
    public SideFilter FavoritesFilter { get; }
    public SideFilter LaterFilter { get; }
    public SideFilter RecentFilter { get; }
    public IReadOnlyList<SideFilter> MainFilters => [AllFilter, FavoritesFilter, LaterFilter, RecentFilter];
    public ObservableCollection<SideFilter> Folders { get; } = [];
    public ObservableCollection<Bookmark> Visible { get; } = [];
    public ObservableCollection<string> FolderNames { get; } = [];

    [ObservableProperty] private SideFilter _selectedFilter;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private Bookmark? _selected;
    [ObservableProperty] private bool _isBusy;

    public bool HasSelection => Selected != null;
    public bool IsEmpty => Visible.Count == 0;
    public bool HasAny => _all.Count > 0;
    public bool IsCards
    {
        get => _settings.Current.BookmarksView != "List";
        set { _settings.Current.BookmarksView = value ? "Cards" : "List"; OnPropertyChanged(); OnPropertyChanged(nameof(IsList)); }
    }
    public bool IsList => !IsCards;

    partial void OnSelectedFilterChanged(SideFilter? oldValue, SideFilter newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        newValue.IsSelected = true;
        Apply();
    }

    partial void OnSearchChanged(string value) => Apply();

    partial void OnSelectedChanged(Bookmark? oldValue, Bookmark? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= OnBookmarkChanged;
        if (newValue != null) newValue.PropertyChanged += OnBookmarkChanged;
        OnPropertyChanged(nameof(HasSelection));
    }

    private void OnBookmarkChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not Bookmark b || e.PropertyName is nameof(Bookmark.UpdatedAt) or nameof(Bookmark.Domain) or nameof(Bookmark.DisplayTitle)
                or nameof(Bookmark.TagList) or nameof(Bookmark.HasDescription) or nameof(Bookmark.Initial) or nameof(Entity.IsPrivate)) return;
        _dirty.Add(b);
        _saveTimer.Stop();
        _saveTimer.Start();
        if (e.PropertyName is nameof(Bookmark.Folder) or nameof(Bookmark.IsFavorite) or nameof(Bookmark.IsReadLater)) RefreshSide();
    }

    public override async Task FlushAsync()
    {
        _saveTimer.Stop();
        if (_dirty.Count == 0) return;
        var batch = _dirty.ToList();
        _dirty.Clear();
        foreach (var b in batch) b.UpdatedAt = DateTime.Now;
        await _store.UpdateRangeAsync(batch);
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_loaded) await LoadAsync();
        if (parameter is "add") await Add();
        else if (parameter is int id && _all.FirstOrDefault(b => b.Id == id) is { } bookmark) Selected = bookmark;
        else if (parameter is string url && url.Contains("://")) await AddUrlAsync(url);
    }

    public override void OnNewShortcut() => _ = Add();

    private async Task LoadAsync()
    {
        _all = await _store.ListAsync<Bookmark>();
        _loaded = true;
        RefreshSide();
        Apply();
    }

    private void RefreshSide()
    {
        AllFilter.Count = _all.Count;
        FavoritesFilter.Count = _all.Count(b => b.IsFavorite);
        LaterFilter.Count = _all.Count(b => b.IsReadLater);
        RecentFilter.Count = 0;
        var names = _all.Select(b => b.Folder.Trim()).Where(f => f.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
        var selectedKey = SelectedFilter.Key;
        Folders.Clear();
        foreach (var f in names)
        {
            var filter = new SideFilter("folder:" + f, f, Glyphs.Folder, f) { Count = _all.Count(b => b.Folder.Trim().Equals(f, StringComparison.CurrentCultureIgnoreCase)) };
            Folders.Add(filter);
#pragma warning disable MVVMTK0034 // same filter after a rebuild: swap the instance without re-filtering
            if (filter.Key == selectedKey && !ReferenceEquals(filter, SelectedFilter)) { _selectedFilter = filter; filter.IsSelected = true; }
#pragma warning restore MVVMTK0034
        }
        FolderNames.Clear();
        foreach (var f in names) FolderNames.Add(f);
        OnPropertyChanged(nameof(HasAny));
    }

    private void Apply()
    {
        IEnumerable<Bookmark> items = _all;
        var f = SelectedFilter;
        if (f == FavoritesFilter) items = items.Where(b => b.IsFavorite);
        else if (f == LaterFilter) items = items.Where(b => b.IsReadLater);
        else if (f == RecentFilter) items = items.Where(b => b.LastVisitedAt != null).OrderByDescending(b => b.LastVisitedAt).Take(60);
        else if (f.Payload is string folder) items = items.Where(b => b.Folder.Trim().Equals(folder, StringComparison.CurrentCultureIgnoreCase));
        var q = Search.Trim();
        if (q.Length > 0)
            items = items.Where(b => b.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || b.Url.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                     b.Description.Contains(q, StringComparison.CurrentCultureIgnoreCase) || b.Tags.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                                     b.Notes.Contains(q, StringComparison.CurrentCultureIgnoreCase) || b.Folder.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        if (f != RecentFilter) items = items.OrderByDescending(b => b.IsFavorite).ThenByDescending(b => b.CreatedAt);
        Visible.Clear();
        foreach (var b in items.Take(1000)) Visible.Add(b);
        OnPropertyChanged(nameof(IsEmpty));
    }

    // ----- commands -----

    [RelayCommand] private void SelectFilter(SideFilter? filter) { if (filter != null) SelectedFilter = filter; }
    [RelayCommand] private void ShowCards() => IsCards = true;
    [RelayCommand] private void ShowList() => IsCards = false;
    [RelayCommand] private void ClearSearch() => Search = "";
    [RelayCommand] private void Select(Bookmark? b) => Selected = b;
    [RelayCommand] private void CloseDetail() => Selected = null;

    [RelayCommand]
    private async Task Open(Bookmark? b)
    {
        if (b == null) return;
        UiCommands.OpenUrl.Execute(b.Url);
        b.Visits++;
        b.LastVisitedAt = DateTime.Now;
        if (b.IsReadLater) b.IsReadLater = false;
        _dirty.Add(b);
        await FlushAsync();
        RefreshSide();
    }

    [RelayCommand]
    private void CopyLink(Bookmark? b)
    {
        if (b != null) Security.SecureClipboard.Copy(b.Url, sensitive: false, "Link copied");
    }

    [RelayCommand]
    private async Task Add()
    {
        var url = await _dialogs.PromptAsync("Add a bookmark", "Paste a web address. The title, description, icon and a preview image are fetched for you.", "", "https://…", "Add");
        if (!string.IsNullOrWhiteSpace(url)) await AddUrlAsync(url);
    }

    public async Task<Bookmark?> AddUrlAsync(string url, string folder = "")
    {
        url = url.Trim();
        if (!url.Contains("://")) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out _)) { _toasts.Warning("That doesn't look like a web address"); return null; }
        if (_all.FirstOrDefault(b => b.Url.Equals(url, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            _toasts.Info("You already have that bookmark");
            Selected = existing;
            return existing;
        }
        var b = new Bookmark
        {
            Url = url, Title = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : url,
            Folder = folder.Length > 0 ? folder : SelectedFilter.Payload as string ?? "",
            IsFavorite = SelectedFilter == FavoritesFilter, IsReadLater = SelectedFilter == LaterFilter
        };
        await _store.AddAsync(b);
        _all.Add(b);
        RefreshSide();
        Apply();
        Selected = b;
        await FetchPreviewAsync(b);
        return b;
    }

    private async Task FetchPreviewAsync(Bookmark b)
    {
        if (!_web.IsAvailable) return;
        IsBusy = true;
        try
        {
            if (YouTube.VideoId(b.Url) is { } videoId)
            {
                var json = await _web.GetStringAsync($"https://www.youtube.com/oembed?url={Uri.EscapeDataString(YouTube.WatchUrl(videoId))}&format=json");
                if (json != null)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("title", out var t)) b.Title = t.GetString() ?? b.Title;
                        if (doc.RootElement.TryGetProperty("author_name", out var a)) b.Description = "YouTube · " + a.GetString();
                    }
                    catch (JsonException) { }
                }
                b.Image = await _web.GetImageAsync(YouTube.ThumbnailUrl(videoId), 480);
                b.Icon = await _web.GetImageAsync("https://www.youtube.com/favicon.ico", 64);
            }
            else
            {
                var preview = await _web.GetPreviewAsync(b.Url);
                if (preview != null)
                {
                    if (preview.Title.Length > 0) b.Title = preview.Title.Length > 160 ? preview.Title[..160] : preview.Title;
                    if (preview.Description.Length > 0) b.Description = preview.Description.Length > 300 ? preview.Description[..300] + "…" : preview.Description;
                    if (preview.ImageUrl != null) b.Image = await _web.GetImageAsync(preview.ImageUrl, 480);
                    if (preview.IconUrl != null) b.Icon = await _web.GetImageAsync(preview.IconUrl, 64);
                }
            }
            await _store.UpdateAsync(b);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshPreview(Bookmark? b)
    {
        b ??= Selected;
        if (b == null) return;
        if (!_web.IsAvailable) { _toasts.Info("You're offline (or online features are off in Settings)"); return; }
        await FetchPreviewAsync(b);
    }

    [RelayCommand]
    private async Task Delete(Bookmark? b)
    {
        b ??= Selected;
        if (b == null) return;
        _dirty.Remove(b);
        await _store.DeleteAsync(b);
        _all.Remove(b);
        if (ReferenceEquals(Selected, b)) Selected = null;
        RefreshSide();
        Apply();
        _toasts.Show("Bookmark deleted", ToastKind.Info, "Undo", async () =>
        {
            b.Id = 0;
            await _store.AddAsync(b);
            _all.Add(b);
            RefreshSide();
            Apply();
        });
    }

    [RelayCommand] private void ToggleFavorite(Bookmark? b) { b ??= Selected; if (b != null) { b.IsFavorite = !b.IsFavorite; _dirty.Add(b); _ = FlushAsync(); RefreshSide(); } }
    [RelayCommand] private void ToggleLater(Bookmark? b) { b ??= Selected; if (b != null) { b.IsReadLater = !b.IsReadLater; _dirty.Add(b); _ = FlushAsync(); RefreshSide(); } }

    [RelayCommand]
    private async Task RenameFolder(SideFilter? filter)
    {
        if (filter?.Payload is not string folder) return;
        var name = await _dialogs.PromptAsync("Rename folder", null, folder, "Folder name", "Rename");
        if (string.IsNullOrWhiteSpace(name)) return;
        var changed = _all.Where(b => b.Folder.Trim().Equals(folder, StringComparison.CurrentCultureIgnoreCase)).ToList();
        foreach (var b in changed) b.Folder = name.Trim();
        await _store.UpdateRangeAsync(changed);
        RefreshSide();
        Apply();
    }

    [RelayCommand]
    private async Task MoveToFolder(Bookmark? b)
    {
        b ??= Selected;
        if (b == null) return;
        var name = await _dialogs.PromptAsync("Move to folder", "Type a folder name (new or existing). Leave empty for no folder.", b.Folder, "e.g. Work/Tools", "Move");
        if (name == null) return;
        b.Folder = name.Trim();
        _dirty.Add(b);
        await FlushAsync();
        RefreshSide();
        Apply();
    }

    // ----- import / export -----

    [RelayCommand]
    private async Task ImportFile()
    {
        var dialog = new OpenFileDialog { Title = "Import bookmarks", Filter = "Bookmark exports (*.html;*.htm;*.json)|*.html;*.htm;*.json|All files|*.*" };
        if (dialog.ShowDialog() != true) return;
        var text = await File.ReadAllTextAsync(dialog.FileName);
        var items = dialog.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? ParseChromium(text) : ParseNetscape(text);
        await ImportAsync(items, Path.GetFileNameWithoutExtension(dialog.FileName));
    }

    /// <summary>Reads Chrome's or Edge's bookmarks straight from this PC's browser profile (read-only).</summary>
    [RelayCommand]
    private async Task ImportBrowser(string? browser)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var path = browser switch
        {
            "edge" => Path.Combine(local, "Microsoft", "Edge", "User Data", "Default", "Bookmarks"),
            "brave" => Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data", "Default", "Bookmarks"),
            _ => Path.Combine(local, "Google", "Chrome", "User Data", "Default", "Bookmarks")
        };
        if (!File.Exists(path)) { _toasts.Info($"No {BrowserName(browser)} bookmarks found on this PC"); return; }
        string text;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            text = await reader.ReadToEndAsync();
        }
        catch (IOException ex)
        {
            _toasts.Warning("Couldn't read the browser's bookmarks: " + ex.Message);
            return;
        }
        await ImportAsync(ParseChromium(text), BrowserName(browser));
    }

    private static string BrowserName(string? key) => key switch { "edge" => "Edge", "brave" => "Brave", _ => "Chrome" };

    private async Task ImportAsync(List<(string Url, string Title, string Folder, DateTime? Added)> items, string source)
    {
        var known = _all.Select(b => b.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = items.Where(i => i.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && known.Add(i.Url))
            .Select(i => new Bookmark { Url = i.Url, Title = i.Title, Folder = i.Folder, CreatedAt = i.Added ?? DateTime.Now }).ToList();
        if (fresh.Count == 0) { _toasts.Info(items.Count == 0 ? "No bookmarks found in that file" : "Everything there is already in your bookmarks"); return; }
        if (!await _dialogs.ConfirmAsync($"Import {fresh.Count} bookmarks?", $"From {source}. Folders are kept. Previews are fetched when you open or refresh a bookmark.", "Import")) return;
        await _store.AddRangeAsync(fresh);
        _all.AddRange(fresh);
        RefreshSide();
        Apply();
        _toasts.Success($"Imported {fresh.Count} bookmarks");
    }

    [RelayCommand]
    private async Task Export()
    {
        var dialog = new SaveFileDialog { Title = "Export bookmarks", Filter = "Bookmarks file (*.html)|*.html", FileName = "satchel-bookmarks.html" };
        if (dialog.ShowDialog() != true) return;
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE NETSCAPE-Bookmark-file-1>");
        sb.AppendLine("<META HTTP-EQUIV=\"Content-Type\" CONTENT=\"text/html; charset=UTF-8\">");
        sb.AppendLine("<TITLE>Bookmarks</TITLE><H1>Bookmarks</H1><DL><p>");
        foreach (var group in _all.GroupBy(b => b.Folder.Trim()).OrderBy(g => g.Key))
        {
            bool folder = group.Key.Length > 0;
            if (folder) sb.AppendLine($"<DT><H3>{WebUtility.HtmlEncode(group.Key)}</H3><DL><p>");
            foreach (var b in group)
                sb.AppendLine($"<DT><A HREF=\"{WebUtility.HtmlEncode(b.Url)}\" ADD_DATE=\"{new DateTimeOffset(b.CreatedAt).ToUnixTimeSeconds()}\">{WebUtility.HtmlEncode(b.DisplayTitle)}</A>");
            if (folder) sb.AppendLine("</DL><p>");
        }
        sb.AppendLine("</DL><p>");
        await File.WriteAllTextAsync(dialog.FileName, sb.ToString(), Encoding.UTF8);
        _toasts.Success($"Exported {_all.Count} bookmarks (not encrypted)");
    }

    /// <summary>The bookmarks HTML every browser exports (Netscape format): folders from &lt;H3&gt;, links from &lt;A HREF&gt;.</summary>
    public static List<(string Url, string Title, string Folder, DateTime? Added)> ParseNetscape(string html)
    {
        var result = new List<(string, string, string, DateTime?)>();
        var folders = new Stack<string>();
        string? pendingFolder = null;
        foreach (Match m in NetscapeToken().Matches(html))
        {
            if (m.Groups["h3"].Success) pendingFolder = WebUtility.HtmlDecode(StripTags(m.Groups["h3"].Value)).Trim();
            else if (m.Value.StartsWith("<DL", StringComparison.OrdinalIgnoreCase)) { folders.Push(pendingFolder ?? ""); pendingFolder = null; }
            else if (m.Value.StartsWith("</DL", StringComparison.OrdinalIgnoreCase)) { if (folders.Count > 0) folders.Pop(); }
            else if (m.Groups["href"].Success)
            {
                var path = string.Join("/", folders.Reverse().Where(f => f.Length > 0 && !IsRootFolder(f)));
                DateTime? added = long.TryParse(m.Groups["add"].Value, out var unix) && unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime : null;
                result.Add((WebUtility.HtmlDecode(m.Groups["href"].Value), WebUtility.HtmlDecode(StripTags(m.Groups["title"].Value)).Trim(), path, added));
            }
        }
        return result;
    }

    /// <summary>Chrome / Edge / Brave "Bookmarks" JSON.</summary>
    public static List<(string Url, string Title, string Folder, DateTime? Added)> ParseChromium(string json)
    {
        var result = new List<(string, string, string, DateTime?)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("roots", out var roots)) return result;
            void Walk(JsonElement node, string path)
            {
                var type = node.TryGetProperty("type", out var t) ? t.GetString() : null;
                var name = node.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (type == "url" && node.TryGetProperty("url", out var u))
                {
                    DateTime? added = node.TryGetProperty("date_added", out var d) && long.TryParse(d.GetString(), out var micro) && micro > 0
                        ? new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(micro * 10).ToLocalTime() : null;
                    result.Add((u.GetString() ?? "", name, path, added));
                }
                else if (node.TryGetProperty("children", out var children))
                {
                    var sub = IsRootFolder(name) || name.Length == 0 ? path : (path.Length == 0 ? name : path + "/" + name);
                    foreach (var c in children.EnumerateArray()) Walk(c, sub);
                }
            }
            foreach (var root in roots.EnumerateObject())
                if (root.Value.ValueKind == JsonValueKind.Object) Walk(root.Value, "");
        }
        catch (JsonException) { }
        return result;
    }

    private static bool IsRootFolder(string name) => name is "Bookmarks bar" or "Bookmarks Bar" or "Favorites bar" or "Other bookmarks" or "Other favorites" or "Mobile bookmarks" or "Bookmarks Toolbar" or "Bookmarks Menu";

    private static string StripTags(string s) => Regex.Replace(s, "<[^>]+>", "");

    [GeneratedRegex(@"<H3[^>]*>(?<h3>.*?)</H3>|<DL[^>]*>|</DL>|<A\s+[^>]*?HREF=""(?<href>[^""]*)""(?:[^>]*?ADD_DATE=""(?<add>\d+)"")?[^>]*>(?<title>.*?)</A>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NetscapeToken();

    public void Dispose()
    {
        _saveTimer.Stop();
        if (Selected != null) Selected.PropertyChanged -= OnBookmarkChanged;
    }
}
