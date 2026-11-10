using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
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

/// <summary>Game backlog and play tracker: status board, library grid, play sessions with a timer, Steam lookup.</summary>
public sealed partial class GamesViewModel : PageViewModel, IDisposable
{
    private readonly DataStore _store;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly SettingsService _settings;
    private readonly WebService _web;
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _clock;
    private readonly HashSet<Entity> _dirty = [];
    private List<PlaySession> _sessions = [];
    private bool _loaded;
    private CancellationTokenSource? _searchCts;

    public GamesViewModel(INavigator nav, DataStore store, DialogService dialogs, ToastService toasts, SettingsService settings, WebService web)
    {
        _store = store;
        _dialogs = dialogs;
        _toasts = toasts;
        _settings = settings;
        _web = web;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += async (_, _) => await FlushAsync();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => OnPropertyChanged(nameof(TimerText));
    }

    public override string Title => "Games";
    public ObservableCollection<Game> Games { get; } = [];
    public ObservableCollection<Game> Playing { get; } = [];
    public ObservableCollection<Game> Backlog { get; } = [];
    public ObservableCollection<Game> Wishlist { get; } = [];
    public ObservableCollection<Game> Completed { get; } = [];
    public ObservableCollection<Game> Dropped { get; } = [];
    public ObservableCollection<Game> LibraryItems { get; } = [];
    public ObservableCollection<PlaySession> Sessions { get; } = [];
    public ObservableCollection<GameSearchResult> SearchResults { get; } = [];

    [ObservableProperty] private Game? _selected;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _libraryFilter = "All";
    [ObservableProperty] private bool _isAdding;
    [ObservableProperty] private string _addQuery = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _searchMessage = "";
    [ObservableProperty] private string _sessionMinutes = "";
    [ObservableProperty] private string _sessionNote = "";
    [ObservableProperty] private Game? _timerGame;
    [ObservableProperty] private DateTime _timerStart;

    public bool IsBoardView
    {
        get => _settings.Current.GamesView != "Library";
        set { _settings.Current.GamesView = value ? "Board" : "Library"; OnPropertyChanged(); OnPropertyChanged(nameof(IsLibraryView)); }
    }
    public bool IsLibraryView => !IsBoardView;
    public bool HasSelection => Selected != null;
    public bool IsEmpty => Games.Count == 0;
    public bool IsTimerRunning => TimerGame != null;
    public string TimerText => TimerGame == null ? "" : (DateTime.Now - TimerStart).ToString(@"h\:mm\:ss");
    public int CompletedThisYear => Games.Count(g => g.Status == GameStatus.Completed && (g.FinishedAt ?? g.UpdatedAt).Year == DateTime.Today.Year);
    public double HoursThisYear => Math.Round(_sessions.Where(s => s.Date.Year == DateTime.Today.Year).Sum(s => s.Minutes) / 60.0, 1);

    public static IReadOnlyList<Choice<GameStatus>> Statuses { get; } =
    [
        new(GameStatus.Playing, "Playing", Glyphs.Play), new(GameStatus.Backlog, "Backlog", Glyphs.List), new(GameStatus.Wishlist, "Wishlist", Glyphs.Heart),
        new(GameStatus.Completed, "Completed", Glyphs.Completed), new(GameStatus.Dropped, "Dropped", Glyphs.Close)
    ];

    public static IReadOnlyList<string> Platforms { get; } = ["PC", "Steam Deck", "PlayStation 5", "PlayStation 4", "Xbox Series", "Nintendo Switch", "Mobile", "VR", "Retro"];

    partial void OnSearchTextChanged(string value) => ApplyLibrary();
    partial void OnLibraryFilterChanged(string value) => ApplyLibrary();
    partial void OnTimerGameChanged(Game? value) => OnPropertyChanged(nameof(IsTimerRunning));

    partial void OnSelectedChanged(Game? oldValue, Game? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= OnGameChanged;
        if (newValue != null) newValue.PropertyChanged += OnGameChanged;
        OnPropertyChanged(nameof(HasSelection));
        Sessions.Clear();
        if (newValue != null)
            foreach (var s in _sessions.Where(s => s.GameId == newValue.Id).OrderByDescending(s => s.Date).Take(40)) Sessions.Add(s);
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_loaded) await LoadAsync();
        if (parameter is int id && Games.FirstOrDefault(g => g.Id == id) is { } game) Selected = game;
        else if (parameter is "new") OpenAdd();
    }

    private async Task LoadAsync()
    {
        var (games, sessions) = await _store.RunAsync(db => (db.Games.OrderBy(g => g.SortOrder).ToList(), db.PlaySessions.OrderBy(s => s.Date).ToList()));
        Games.Clear();
        foreach (var g in games) Games.Add(g);
        _sessions = sessions;
        _loaded = true;
        Distribute();
    }

    private void Distribute()
    {
        void Fill(ObservableCollection<Game> target, GameStatus status)
        {
            target.Clear();
            foreach (var g in Games.Where(g => g.Status == status).OrderBy(g => g.SortOrder)) target.Add(g);
        }
        Fill(Playing, GameStatus.Playing);
        Fill(Backlog, GameStatus.Backlog);
        Fill(Wishlist, GameStatus.Wishlist);
        Fill(Completed, GameStatus.Completed);
        Fill(Dropped, GameStatus.Dropped);
        ApplyLibrary();
        RaiseStats();
    }

    private void ApplyLibrary()
    {
        var q = SearchText.Trim();
        IEnumerable<Game> items = Games;
        if (Enum.TryParse<GameStatus>(LibraryFilter, out var status)) items = items.Where(g => g.Status == status);
        if (q.Length > 0) items = items.Where(g => g.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || g.Platform.Contains(q, StringComparison.CurrentCultureIgnoreCase) || g.Genre.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        LibraryItems.Clear();
        foreach (var g in items.OrderByDescending(g => g.LastPlayedAt ?? g.CreatedAt)) LibraryItems.Add(g);
    }

    private void RaiseStats()
    {
        OnPropertyChanged(nameof(CompletedThisYear));
        OnPropertyChanged(nameof(HoursThisYear));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void OnGameChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not Game g || e.PropertyName is nameof(Game.UpdatedAt) or nameof(Game.HoursText) or nameof(Game.StatusText)) return;
        if (e.PropertyName == nameof(Game.Status)) StatusChanged(g);
        MarkDirty(g);
    }

    private void StatusChanged(Game g)
    {
        if (g.Status == GameStatus.Playing) g.StartedAt ??= DateTime.Today;
        if (g.Status == GameStatus.Completed) { g.FinishedAt ??= DateTime.Today; if (g.Completion < 100) g.Completion = Math.Max(g.Completion, 100); }
        Distribute();
    }

    private void MarkDirty(Entity e)
    {
        _dirty.Add(e);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public override async Task FlushAsync()
    {
        _saveTimer.Stop();
        if (_dirty.Count == 0) return;
        var batch = _dirty.ToList();
        _dirty.Clear();
        foreach (var e in batch) e.UpdatedAt = DateTime.Now;
        await _store.RunAsync(db => { db.UpdateRange(batch); db.SaveChanges(); });
    }

    public override void OnNewShortcut() => OpenAdd();

    // ----- board / selection -----

    [RelayCommand]
    private void Select(Game? game) => Selected = game;

    [RelayCommand]
    private void CloseDetail() => Selected = null;

    [RelayCommand]
    private void SetLibraryFilter(string? filter) => LibraryFilter = filter ?? "All";

    [RelayCommand]
    private void ShowBoard() => IsBoardView = true;

    [RelayCommand]
    private void ShowLibrary() => IsBoardView = false;

    [RelayCommand]
    private async Task Reorder(ReorderEventArgs? args)
    {
        if (args == null) return;
        if (args.Item is Game moved && args.ChangedList && args.TargetTag is string tag && Enum.TryParse<GameStatus>(tag, out var status))
        {
            moved.PropertyChanged -= OnGameChanged;
            moved.Status = status;
            if (status == GameStatus.Playing) moved.StartedAt ??= DateTime.Today;
            if (status == GameStatus.Completed) moved.FinishedAt ??= DateTime.Today;
            if (ReferenceEquals(moved, Selected)) moved.PropertyChanged += OnGameChanged;
            await _store.UpdateAsync(moved);
            if (status == GameStatus.Completed) _toasts.Success($"Completed {moved.Title} 🏆");
        }
        var changed = new List<Game>();
        foreach (var column in new[] { Playing, Backlog, Wishlist, Completed, Dropped })
        {
            int i = 0;
            foreach (var g in column) { g.SortOrder = i++; changed.Add(g); }
        }
        await _store.SaveOrderAsync(changed, g => g.SortOrder);
        ApplyLibrary();
        RaiseStats();
    }

    [RelayCommand]
    private void SetStatus(GameStatus status)
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
    private void SetPlatform(string? platform)
    {
        if (Selected != null && platform != null) Selected.Platform = platform;
    }

    [RelayCommand]
    private void PickCover()
    {
        if (Selected == null) return;
        var img = ImageService.PickImage(ImageService.CoverSize, photo: true);
        if (img != null) Selected.Cover = img;
    }

    [RelayCommand]
    private void PasteCover()
    {
        if (Selected == null) return;
        var img = ImageService.FromClipboard(ImageService.CoverSize, photo: true);
        if (img != null) Selected.Cover = img;
        else _toasts.Info("Copy an image first");
    }

    [RelayCommand]
    private void OpenStore()
    {
        if (Selected is { StoreUrl.Length: > 0 } g) UiCommands.OpenUrl.Execute(g.StoreUrl);
    }

    [RelayCommand]
    private async Task DeleteGame()
    {
        if (Selected is not { } g) return;
        if (_settings.Current.ConfirmDeletes && !await _dialogs.ConfirmAsync("Remove game?", $"“{g.Title}” and its play sessions will be deleted.", "Remove", danger: true)) return;
        if (ReferenceEquals(TimerGame, g)) StopTimerCore(save: false);
        _dirty.Remove(g);
        await _store.DeleteAsync(g);
        Games.Remove(g);
        _sessions.RemoveAll(s => s.GameId == g.Id);
        Selected = null;
        Distribute();
    }

    // ----- sessions & timer -----

    [RelayCommand]
    private async Task LogSession()
    {
        if (Selected is not { } g) return;
        if (!int.TryParse(SessionMinutes.Trim(), out var minutes) || minutes <= 0)
        {
            _toasts.Info("Enter the minutes you played");
            return;
        }
        await AddSessionAsync(g, minutes, SessionNote.Trim());
        SessionMinutes = "";
        SessionNote = "";
    }

    private async Task AddSessionAsync(Game g, int minutes, string note)
    {
        var session = new PlaySession { GameId = g.Id, Date = DateTime.Now, Minutes = minutes, Note = note };
        await _store.AddAsync(session);
        _sessions.Add(session);
        if (ReferenceEquals(Selected, g)) Sessions.Insert(0, session);
        g.Hours = Math.Round(g.Hours + minutes / 60.0, 2);
        g.LastPlayedAt = DateTime.Now;
        if (g.Status is GameStatus.Backlog or GameStatus.Wishlist) g.Status = GameStatus.Playing;
        MarkDirty(g);
        await FlushAsync();
        RaiseStats();
    }

    [RelayCommand]
    private void StartTimer(Game? game)
    {
        game ??= Selected;
        if (game == null) return;
        if (TimerGame != null) StopTimerCore(save: true);
        TimerGame = game;
        TimerStart = DateTime.Now;
        _clock.Start();
        OnPropertyChanged(nameof(TimerText));
        _toasts.Info($"Session started — {game.Title}");
    }

    [RelayCommand]
    private void StopTimer() => StopTimerCore(save: true);

    private async void StopTimerCore(bool save)
    {
        if (TimerGame is not { } g) return;
        var minutes = (int)Math.Round((DateTime.Now - TimerStart).TotalMinutes);
        TimerGame = null;
        _clock.Stop();
        OnPropertyChanged(nameof(TimerText));
        if (!save) return;
        if (minutes < 1) { _toasts.Info("Session too short to log"); return; }
        await AddSessionAsync(g, minutes, "");
        _toasts.Success($"Logged {minutes} min of {g.Title}");
    }

    // ----- adding games -----

    [RelayCommand]
    private void OpenAdd()
    {
        IsAdding = true;
        SearchMessage = _web.IsAvailable ? "Search the Steam store for cover art — or add any game by hand." : "Offline: type the title and add it by hand.";
    }

    [RelayCommand]
    private void CloseAdd()
    {
        IsAdding = false;
        _searchCts?.Cancel();
        SearchResults.Clear();
    }

    [RelayCommand]
    private async Task Search()
    {
        var q = AddQuery.Trim();
        if (q.Length < 2) return;
        if (!_web.IsAvailable) { SearchMessage = "You're offline — add the game by hand."; return; }
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        IsSearching = true;
        SearchResults.Clear();
        SearchMessage = "Searching…";
        try
        {
            var json = await _web.GetStringAsync($"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(q)}&l=english&cc=US", cts.Token);
            if (cts.IsCancellationRequested) return;
            var results = new List<GameSearchResult>();
            if (json != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("items", out var items))
                        foreach (var it in items.EnumerateArray())
                        {
                            var name = it.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                            var id = it.TryGetProperty("id", out var i) && i.TryGetInt32(out var v) ? v : 0;
                            var thumb = it.TryGetProperty("tiny_image", out var t) ? t.GetString() ?? "" : "";
                            if (name.Length > 0 && id > 0) results.Add(new GameSearchResult(name, id, thumb));
                        }
                }
                catch (JsonException) { }
            }
            foreach (var r in results) SearchResults.Add(r);
            SearchMessage = results.Count == 0 ? "Nothing found on Steam — add it by hand (any platform works)." : "";
            foreach (var r in results)
            {
                if (r.ThumbUrl.Length == 0) continue;
                var bytes = await _web.GetBytesAsync(r.ThumbUrl, ct: cts.Token);
                if (cts.IsCancellationRequested) return;
                if (bytes != null) r.Thumb = ImageService.ToBitmap(bytes, 160);
            }
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts)) IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task AddResult(GameSearchResult? r)
    {
        if (r == null) return;
        var game = new Game
        {
            Title = r.Name, SteamAppId = r.AppId, Platform = "PC", StoreUrl = $"https://store.steampowered.com/app/{r.AppId}",
            Status = GameStatus.Backlog, Color = RandomColor(), SortOrder = Backlog.Count
        };
        game.Cover = await _web.GetImageAsync($"https://cdn.akamai.steamstatic.com/steam/apps/{r.AppId}/library_600x900.jpg", ImageService.CoverSize)
                     ?? await _web.GetImageAsync($"https://cdn.akamai.steamstatic.com/steam/apps/{r.AppId}/header.jpg", ImageService.CoverSize);
        var details = await _web.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={r.AppId}&l=english");
        if (details != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(details);
                if (doc.RootElement.TryGetProperty(r.AppId.ToString(), out var app) && app.TryGetProperty("data", out var data))
                {
                    if (data.TryGetProperty("genres", out var genres))
                        game.Genre = string.Join(", ", genres.EnumerateArray().Select(gn => gn.TryGetProperty("description", out var d) ? d.GetString() : null).Where(s => s != null).Take(3));
                }
            }
            catch (JsonException) { }
        }
        await AddGameAsync(game);
    }

    [RelayCommand]
    private async Task AddManual()
    {
        var title = AddQuery.Trim();
        if (title.Length == 0) title = await _dialogs.PromptAsync("Add a game", null, "", "Title", "Add") ?? "";
        if (string.IsNullOrWhiteSpace(title)) return;
        await AddGameAsync(new Game { Title = title.Trim(), Status = GameStatus.Backlog, Color = RandomColor(), SortOrder = Backlog.Count });
    }

    private static string RandomColor() => Palettes.Tags[Random.Shared.Next(Math.Min(10, Palettes.Tags.Count))].Key;

    private async Task AddGameAsync(Game game)
    {
        await _store.AddAsync(game);
        Games.Add(game);
        CloseAdd();
        AddQuery = "";
        Distribute();
        Selected = game;
        _toasts.Success($"Added {game.Title}");
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    public void Dispose()
    {
        _saveTimer.Stop();
        _clock.Stop();
        _searchCts?.Cancel();
        if (Selected != null) Selected.PropertyChanged -= OnGameChanged;
    }
}

public sealed partial class GameSearchResult(string name, int appId, string thumbUrl) : ObservableObject
{
    public string Name { get; } = name;
    public int AppId { get; } = appId;
    public string ThumbUrl { get; } = thumbUrl;
    [ObservableProperty] private BitmapSource? _thumb;
}
