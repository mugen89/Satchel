using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Behaviors;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Services;

namespace Satchel.ViewModels;

public sealed partial class WatchSearchResult : ObservableObject
{
    public required string Title { get; init; }
    public required WatchKind Kind { get; init; }
    public int Year { get; init; }
    public string Overview { get; init; } = "";
    public string Genres { get; init; } = "";
    public string Network { get; init; } = "";
    public string PosterUrl { get; init; } = "";
    public string ExternalId { get; init; } = "";
    public string Url { get; init; } = "";
    public int Runtime { get; init; }
    public string Subtitle => string.Join(" · ", new[] { Year > 0 ? Year.ToString(CultureInfo.InvariantCulture) : "", Kind == WatchKind.Show ? "TV show" : "Movie", Network }.Where(s => s.Length > 0));
    [ObservableProperty] private BitmapSource? _poster;
}

public sealed record SeasonGroup(int Season, IReadOnlyList<Episode> Episodes)
{
    public string Title => $"Season {Season}";
    public string Progress => $"{Episodes.Count(e => e.Watched)} / {Episodes.Count}";
}

/// <summary>Watching: movies and shows — plan, watch, finish; episodes with "next up"; posters and details from TVmaze (shows) and iTunes (movies).</summary>
public sealed partial class WatchingViewModel : PageViewModel, IDisposable
{
    private readonly DataStore _store;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly SettingsService _settings;
    private readonly WebService _web;
    private readonly DispatcherTimer _saveTimer;
    private readonly HashSet<WatchItem> _dirty = [];
    private bool _loaded;
    private CancellationTokenSource? _searchCts;

    public WatchingViewModel(INavigator nav, DataStore store, DialogService dialogs, ToastService toasts, SettingsService settings, WebService web)
    {
        _store = store;
        _dialogs = dialogs;
        _toasts = toasts;
        _settings = settings;
        _web = web;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += async (_, _) => await FlushAsync();
    }

    public override string Title => "Watching";
    public ObservableCollection<WatchItem> All { get; } = [];
    public ObservableCollection<WatchItem> Watching { get; } = [];
    public ObservableCollection<WatchItem> Planned { get; } = [];
    public ObservableCollection<WatchItem> Completed { get; } = [];
    public ObservableCollection<WatchItem> OnHold { get; } = [];
    public ObservableCollection<WatchItem> LibraryItems { get; } = [];
    public ObservableCollection<SeasonGroup> Seasons { get; } = [];
    public ObservableCollection<WatchSearchResult> SearchResults { get; } = [];

    [ObservableProperty] private WatchItem? _selected;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _libraryFilter = "All";
    [ObservableProperty] private bool _isAdding;
    [ObservableProperty] private string _addQuery = "";
    [ObservableProperty] private string _addKind = "show";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _searchMessage = "";

    public bool IsBoardView
    {
        get => _settings.Current.WatchingView != "Library";
        set { _settings.Current.WatchingView = value ? "Board" : "Library"; OnPropertyChanged(); OnPropertyChanged(nameof(IsLibraryView)); }
    }
    public bool IsLibraryView => !IsBoardView;
    public bool HasSelection => Selected != null;
    public bool IsEmpty => All.Count == 0;
    public bool AddShows => AddKind == "show";
    public bool AddMovies => AddKind == "movie";
    public int EpisodesThisMonth => All.SelectMany(w => w.Episodes).Count(e => e.WatchedAt is { } d && d.Year == DateTime.Today.Year && d.Month == DateTime.Today.Month);
    public int FinishedThisYear => All.Count(w => w.Status == WatchStatus.Completed && (w.FinishedAt ?? w.UpdatedAt).Year == DateTime.Today.Year);

    public static IReadOnlyList<Choice<WatchStatus>> Statuses { get; } =
    [
        new(WatchStatus.Watching, "Watching", Glyphs.Play), new(WatchStatus.Planned, "Plan to watch", Glyphs.Bookmark), new(WatchStatus.Completed, "Completed", Glyphs.Completed),
        new(WatchStatus.OnHold, "On hold", Glyphs.Pause), new(WatchStatus.Dropped, "Dropped", Glyphs.Close)
    ];

    partial void OnSearchChanged(string value) => ApplyLibrary();
    partial void OnLibraryFilterChanged(string value) => ApplyLibrary();
    partial void OnAddKindChanged(string value)
    {
        OnPropertyChanged(nameof(AddShows));
        OnPropertyChanged(nameof(AddMovies));
        SearchResults.Clear();
        SearchMessage = "";
    }

    partial void OnSelectedChanged(WatchItem? oldValue, WatchItem? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= OnItemChanged;
        if (newValue != null) newValue.PropertyChanged += OnItemChanged;
        OnPropertyChanged(nameof(HasSelection));
        BuildSeasons();
    }

    private void BuildSeasons()
    {
        Seasons.Clear();
        if (Selected is not { IsShow: true } w) return;
        foreach (var g in w.Episodes.GroupBy(e => e.Season).OrderBy(g => g.Key)) Seasons.Add(new SeasonGroup(g.Key, g.OrderBy(e => e.Number).ToList()));
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not WatchItem w || e.PropertyName is nameof(Entity.UpdatedAt) or nameof(WatchItem.StatusText) or nameof(WatchItem.Progress) or nameof(WatchItem.ProgressText)
                or nameof(WatchItem.NextEpisode) or nameof(WatchItem.HasNextEpisode) or nameof(WatchItem.Episodes) or nameof(WatchItem.EpisodeCount) or nameof(WatchItem.WatchedCount)
                or nameof(WatchItem.Subtitle) or nameof(WatchItem.IsShow) or nameof(WatchItem.IsMovie) or nameof(Entity.IsPrivate)) return;
        if (e.PropertyName == nameof(WatchItem.Status)) OnStatusChanged(w);
        MarkDirty(w);
    }

    private void OnStatusChanged(WatchItem w)
    {
        if (w.Status == WatchStatus.Watching) w.StartedAt ??= DateTime.Today;
        if (w.Status == WatchStatus.Completed) w.FinishedAt ??= DateTime.Today;
        Distribute();
    }

    private void MarkDirty(WatchItem w)
    {
        _dirty.Add(w);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public override async Task FlushAsync()
    {
        _saveTimer.Stop();
        if (_dirty.Count == 0) return;
        var batch = _dirty.ToList();
        _dirty.Clear();
        foreach (var w in batch) w.UpdatedAt = DateTime.Now;
        await _store.UpdateRangeAsync(batch);
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_loaded) await LoadAsync();
        if (parameter is int id && All.FirstOrDefault(w => w.Id == id) is { } item) Selected = item;
        else if (parameter is "new") OpenAdd();
    }

    public override void OnNewShortcut() => OpenAdd();

    private async Task LoadAsync()
    {
        var items = await _store.ListAsync<WatchItem>(q => q.OrderBy(w => w.SortOrder));
        All.Clear();
        foreach (var w in items) All.Add(w);
        _loaded = true;
        Distribute();
    }

    private void Distribute()
    {
        void Fill(ObservableCollection<WatchItem> target, Func<WatchItem, bool> predicate, Func<WatchItem, object>? order = null)
        {
            target.Clear();
            foreach (var w in All.Where(predicate).OrderBy(order ?? (w => w.SortOrder))) target.Add(w);
        }
        Fill(Watching, w => w.Status == WatchStatus.Watching);
        Fill(Planned, w => w.Status == WatchStatus.Planned);
        Fill(Completed, w => w.Status == WatchStatus.Completed);
        Fill(OnHold, w => w.Status is WatchStatus.OnHold or WatchStatus.Dropped);
        ApplyLibrary();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EpisodesThisMonth));
        OnPropertyChanged(nameof(FinishedThisYear));
    }

    private void ApplyLibrary()
    {
        var q = Search.Trim();
        IEnumerable<WatchItem> items = All;
        if (LibraryFilter == "Shows") items = items.Where(w => w.IsShow);
        else if (LibraryFilter == "Movies") items = items.Where(w => w.IsMovie);
        else if (Enum.TryParse<WatchStatus>(LibraryFilter, out var status)) items = items.Where(w => w.Status == status);
        if (q.Length > 0) items = items.Where(w => w.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || w.Genres.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        LibraryItems.Clear();
        foreach (var w in items.OrderByDescending(w => w.LastWatchedAt ?? w.CreatedAt)) LibraryItems.Add(w);
    }

    // ----- board -----

    [RelayCommand] private void Select(WatchItem? w) => Selected = w;
    [RelayCommand] private void CloseDetail() => Selected = null;
    [RelayCommand] private void ShowBoard() => IsBoardView = true;
    [RelayCommand] private void ShowLibrary() => IsBoardView = false;
    [RelayCommand] private void SetLibraryFilter(string? f) => LibraryFilter = f ?? "All";
    [RelayCommand] private void ClearSearch() => Search = "";

    [RelayCommand]
    private void SetStatus(WatchStatus status)
    {
        if (Selected != null) Selected.Status = status;
    }

    [RelayCommand]
    private void SetRating(string? value)
    {
        if (Selected == null || !int.TryParse(value, out var r)) return;
        Selected.Rating = Selected.Rating == r ? 0 : r;
    }

    [RelayCommand]
    private async Task Reorder(ReorderEventArgs? args)
    {
        if (args == null) return;
        if (args.Item is WatchItem moved && args.ChangedList && args.TargetTag is string tag && Enum.TryParse<WatchStatus>(tag, out var status))
        {
            moved.PropertyChanged -= OnItemChanged;
            moved.Status = status;
            if (status == WatchStatus.Watching) moved.StartedAt ??= DateTime.Today;
            if (status == WatchStatus.Completed) moved.FinishedAt ??= DateTime.Today;
            if (ReferenceEquals(moved, Selected)) moved.PropertyChanged += OnItemChanged;
            await _store.UpdateAsync(moved);
            if (status == WatchStatus.Completed) _toasts.Success($"Finished {moved.Title} 🎬");
        }
        var changed = new List<WatchItem>();
        foreach (var column in new[] { Watching, Planned, Completed, OnHold })
        {
            int i = 0;
            foreach (var w in column) { w.SortOrder = i++; changed.Add(w); }
        }
        await _store.SaveOrderAsync(changed, w => w.SortOrder);
        ApplyLibrary();
    }

    // ----- episodes -----

    /// <summary>"Watched": the next episode of a show (or the movie itself).</summary>
    [RelayCommand]
    private async Task WatchedNext(WatchItem? w)
    {
        w ??= Selected;
        if (w == null) return;
        if (w.IsMovie)
        {
            w.Status = WatchStatus.Completed;
            w.FinishedAt = DateTime.Today;
            w.LastWatchedAt = DateTime.Now;
            MarkDirty(w);
            await FlushAsync();
            Distribute();
            _toasts.Success($"Watched {w.Title} 🎬");
            return;
        }
        if (w.NextEpisode is not { } ep) { _toasts.Info("No more aired episodes"); return; }
        await SetEpisodeAsync(w, ep, true);
        _toasts.Show($"{ep.Code} watched" + (w.NextEpisode is { } next ? $" — next: {next.Label}" : " — all caught up"), ToastKind.Success, durationMs: 2400);
    }

    [RelayCommand]
    private async Task ToggleEpisode(Episode? ep)
    {
        if (ep == null || Selected is not { } w) return;
        await SetEpisodeAsync(w, ep, !ep.Watched);
    }

    private async Task SetEpisodeAsync(WatchItem w, Episode ep, bool watched)
    {
        ep.Watched = watched;
        ep.WatchedAt = watched ? DateTime.Now : null;
        if (watched)
        {
            w.LastWatchedAt = DateTime.Now;
            if (w.Status is WatchStatus.Planned or WatchStatus.OnHold) w.Status = WatchStatus.Watching;
            if (w.Episodes.All(e => e.Watched || !e.HasAired) && w.Episodes.Count > 0 && w.Episodes.All(e => e.HasAired)) w.Status = WatchStatus.Completed;
        }
        w.SaveEpisodes();
        MarkDirty(w);
        await FlushAsync();
        if (ReferenceEquals(w, Selected)) BuildSeasons();
        Distribute();
    }

    [RelayCommand]
    private async Task WatchSeason(SeasonGroup? season)
    {
        if (season == null || Selected is not { } w) return;
        bool all = season.Episodes.All(e => e.Watched);
        foreach (var e in season.Episodes.Where(e => e.HasAired || all))
        {
            e.Watched = !all;
            e.WatchedAt = !all ? (e.WatchedAt ?? DateTime.Now) : null;
        }
        if (!all) w.LastWatchedAt = DateTime.Now;
        w.SaveEpisodes();
        MarkDirty(w);
        await FlushAsync();
        BuildSeasons();
        Distribute();
    }

    [RelayCommand]
    private async Task RefreshEpisodes()
    {
        if (Selected is not { IsShow: true } w || !w.ExternalId.StartsWith("tvmaze:")) { _toasts.Info("Episodes come from TVmaze for shows added from search"); return; }
        if (!_web.IsAvailable) { _toasts.Info("You're offline"); return; }
        var fresh = await FetchEpisodesAsync(w.ExternalId[7..]);
        if (fresh.Count == 0) { _toasts.Warning("Couldn't load the episodes"); return; }
        var watched = w.Episodes.Where(e => e.Watched).ToDictionary(e => (e.Season, e.Number), e => e.WatchedAt);
        foreach (var e in fresh)
            if (watched.TryGetValue((e.Season, e.Number), out var when)) { e.Watched = true; e.WatchedAt = when; }
        w.EpisodesJson = JsonSerializer.Serialize(fresh);
        MarkDirty(w);
        await FlushAsync();
        BuildSeasons();
        _toasts.Success($"{fresh.Count} episodes · {fresh.Count(e => !e.HasAired)} still to air");
    }

    // ----- adding -----

    [RelayCommand]
    private void OpenAdd()
    {
        IsAdding = true;
        SearchMessage = _web.IsAvailable ? "Search TVmaze (shows) or iTunes (movies) for posters and details — or add anything by hand." : "Offline: type the title and add it by hand.";
    }

    [RelayCommand]
    private void CloseAdd()
    {
        IsAdding = false;
        _searchCts?.Cancel();
        SearchResults.Clear();
    }

    [RelayCommand] private void SetAddKind(string? kind) => AddKind = kind ?? "show";

    [RelayCommand]
    private async Task SearchOnline()
    {
        var q = AddQuery.Trim();
        if (q.Length < 2) return;
        if (!_web.IsAvailable) { SearchMessage = "You're offline — add it by hand."; return; }
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        IsSearching = true;
        SearchResults.Clear();
        SearchMessage = "Searching…";
        try
        {
            var results = AddShows ? await SearchShowsAsync(q, cts.Token) : await SearchMoviesAsync(q, cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var r in results) SearchResults.Add(r);
            SearchMessage = results.Count == 0 ? "Nothing found — add it by hand." : "";
            foreach (var r in results.Where(r => r.PosterUrl.Length > 0))
            {
                var bytes = await _web.GetBytesAsync(r.PosterUrl, ct: cts.Token);
                if (cts.IsCancellationRequested) return;
                if (bytes != null) r.Poster = ImageService.ToBitmap(bytes, 120);
            }
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts)) IsSearching = false;
        }
    }

    private async Task<List<WatchSearchResult>> SearchShowsAsync(string q, CancellationToken ct)
    {
        var list = new List<WatchSearchResult>();
        var json = await _web.GetStringAsync($"https://api.tvmaze.com/search/shows?q={Uri.EscapeDataString(q)}", ct);
        if (json == null) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var hit in doc.RootElement.EnumerateArray().Take(12))
            {
                if (!hit.TryGetProperty("show", out var s)) continue;
                string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                int year = DateTime.TryParse(Str(s, "premiered"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var p) ? p.Year : 0;
                var poster = s.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.Object ? Str(img, "original").Length > 0 ? Str(img, "original") : Str(img, "medium") : "";
                var network = s.TryGetProperty("network", out var n) && n.ValueKind == JsonValueKind.Object ? Str(n, "name")
                            : s.TryGetProperty("webChannel", out var wc) && wc.ValueKind == JsonValueKind.Object ? Str(wc, "name") : "";
                var genres = s.TryGetProperty("genres", out var g) ? string.Join(", ", g.EnumerateArray().Select(x => x.GetString()).Take(3)) : "";
                int runtime = s.TryGetProperty("averageRuntime", out var rt) && rt.ValueKind == JsonValueKind.Number ? rt.GetInt32() : 0;
                list.Add(new WatchSearchResult
                {
                    Title = Str(s, "name"), Kind = WatchKind.Show, Year = year, Overview = FeedService.StripHtml(Str(s, "summary")), Genres = genres, Network = network,
                    PosterUrl = poster, ExternalId = "tvmaze:" + (s.TryGetProperty("id", out var id) ? id.GetInt32().ToString(CultureInfo.InvariantCulture) : ""),
                    Url = Str(s, "url"), Runtime = runtime
                });
            }
        }
        catch (JsonException) { }
        return list;
    }

    private async Task<List<WatchSearchResult>> SearchMoviesAsync(string q, CancellationToken ct)
    {
        var list = new List<WatchSearchResult>();
        var json = await _web.GetStringAsync($"https://itunes.apple.com/search?term={Uri.EscapeDataString(q)}&media=movie&entity=movie&limit=12", ct);
        if (json == null) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var m in doc.RootElement.GetProperty("results").EnumerateArray())
            {
                string Str(string n) => m.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                int year = DateTime.TryParse(Str("releaseDate"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d.Year : 0;
                int runtime = m.TryGetProperty("trackTimeMillis", out var t) && t.ValueKind == JsonValueKind.Number ? (int)(t.GetInt64() / 60000) : 0;
                var art = Str("artworkUrl100");
                list.Add(new WatchSearchResult
                {
                    Title = Str("trackName"), Kind = WatchKind.Movie, Year = year, Overview = Str("longDescription"), Genres = Str("primaryGenreName"),
                    PosterUrl = art.Length > 0 ? Regex.Replace(art, @"\d+x\d+bb", "600x900bb") : "",
                    ExternalId = "itunes:" + (m.TryGetProperty("trackId", out var id) ? id.GetInt64().ToString(CultureInfo.InvariantCulture) : ""), Url = Str("trackViewUrl"), Runtime = runtime
                });
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException) { }
        return list;
    }

    private async Task<List<Episode>> FetchEpisodesAsync(string showId)
    {
        var list = new List<Episode>();
        var json = await _web.GetStringAsync($"https://api.tvmaze.com/shows/{showId}/episodes");
        if (json == null) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                int I(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
                string S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                list.Add(new Episode { Season = I("season"), Number = I("number"), Title = WebUtility.HtmlDecode(S("name")), Airdate = S("airdate"), Runtime = I("runtime") });
            }
        }
        catch (JsonException) { }
        return list.Where(e => e.Number > 0).ToList();
    }

    [RelayCommand]
    private async Task AddResult(WatchSearchResult? r)
    {
        if (r == null) return;
        if (All.FirstOrDefault(w => w.ExternalId == r.ExternalId && r.ExternalId.Length > 6) is { } existing)
        {
            _toasts.Info($"{existing.Title} is already on your list");
            CloseAdd();
            Selected = existing;
            return;
        }
        IsSearching = true;
        SearchMessage = "Adding…";
        try
        {
            var item = new WatchItem
            {
                Title = r.Title, Kind = r.Kind, Year = r.Year, Overview = r.Overview.Length > 1500 ? r.Overview[..1500] + "…" : r.Overview, Genres = r.Genres, Network = r.Network,
                ExternalId = r.ExternalId, Url = r.Url, Runtime = r.Runtime, Status = WatchStatus.Planned, Color = RandomColor(), SortOrder = Planned.Count
            };
            if (r.PosterUrl.Length > 0) item.Poster = await _web.GetImageAsync(r.PosterUrl, ImageService.CoverSize);
            if (r.Kind == WatchKind.Show && r.ExternalId.StartsWith("tvmaze:"))
            {
                var episodes = await FetchEpisodesAsync(r.ExternalId[7..]);
                if (episodes.Count > 0) item.EpisodesJson = JsonSerializer.Serialize(episodes);
            }
            await AddItemAsync(item);
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task AddManual()
    {
        var title = AddQuery.Trim();
        if (title.Length == 0) title = await _dialogs.PromptAsync(AddShows ? "Add a show" : "Add a movie", null, "", "Title", "Add") ?? "";
        if (string.IsNullOrWhiteSpace(title)) return;
        await AddItemAsync(new WatchItem { Title = title.Trim(), Kind = AddShows ? WatchKind.Show : WatchKind.Movie, Status = WatchStatus.Planned, Color = RandomColor(), SortOrder = Planned.Count });
    }

    private static string RandomColor() => Palettes.Tags[Random.Shared.Next(Math.Min(10, Palettes.Tags.Count))].Key;

    private async Task AddItemAsync(WatchItem item)
    {
        await _store.AddAsync(item);
        All.Add(item);
        CloseAdd();
        AddQuery = "";
        Distribute();
        Selected = item;
        _toasts.Success($"Added {item.Title}");
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (Selected is not { } w) return;
        if (_settings.Current.ConfirmDeletes && !await _dialogs.ConfirmAsync("Remove from your list?", $"“{w.Title}” and its episode progress will be deleted.", "Remove", danger: true)) return;
        _dirty.Remove(w);
        await _store.DeleteAsync(w);
        All.Remove(w);
        Selected = null;
        Distribute();
    }

    [RelayCommand]
    private void PickPoster()
    {
        if (Selected == null) return;
        var img = ImageService.PickImage(ImageService.CoverSize, photo: true);
        if (img != null) Selected.Poster = img;
    }

    [RelayCommand]
    private void OpenPage()
    {
        if (Selected is { Url.Length: > 0 } w) UiCommands.OpenUrl.Execute(w.Url);
    }

    public void Dispose()
    {
        _saveTimer.Stop();
        _searchCts?.Cancel();
        if (Selected != null) Selected.PropertyChanged -= OnItemChanged;
    }
}
