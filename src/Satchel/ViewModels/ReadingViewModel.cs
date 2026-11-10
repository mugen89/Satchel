using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Services;

namespace Satchel.ViewModels;

/// <summary>Reading tracker: shelves, progress and sessions, quotes, a yearly goal and online book lookup.</summary>
public sealed partial class ReadingViewModel : PageViewModel, IDisposable
{
    private readonly DataStore _store;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly SettingsService _settings;
    private readonly WebService _web;
    private readonly DispatcherTimer _saveTimer;
    private readonly HashSet<Entity> _dirty = [];
    private List<ReadingSession> _sessions = [];
    private bool _loaded;
    private CancellationTokenSource? _searchCts;

    public ReadingViewModel(INavigator nav, DataStore store, DialogService dialogs, ToastService toasts, SettingsService settings, WebService web)
    {
        _store = store;
        _dialogs = dialogs;
        _toasts = toasts;
        _settings = settings;
        _web = web;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += async (_, _) => await FlushAsync();
    }

    public override string Title => "Reading";
    public ObservableCollection<Book> Books { get; } = [];
    public ObservableCollection<Book> Shelf { get; } = [];
    public ObservableCollection<Book> CurrentlyReading { get; } = [];
    public ObservableCollection<BookQuote> Quotes { get; } = [];
    public ObservableCollection<QuoteRow> AllQuotes { get; } = [];
    public ObservableCollection<ReadingSession> Sessions { get; } = [];
    public ObservableCollection<DayBar> Last14Days { get; } = [];
    public ObservableCollection<BookSearchResult> SearchResults { get; } = [];

    [ObservableProperty] private string _view = "Reading";
    [ObservableProperty] private Book? _selected;
    [ObservableProperty] private bool _isAdding;
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _searchMessage = "";
    [ObservableProperty] private string _newQuote = "";
    [ObservableProperty] private string _newQuotePage = "";
    [ObservableProperty] private string _pageInput = "";

    public int Goal => _settings.Current.ReadingGoal;
    public int FinishedThisYear => Books.Count(b => b.Status == BookStatus.Finished && (b.FinishedAt ?? b.UpdatedAt).Year == DateTime.Today.Year);
    public double GoalProgress => Goal <= 0 ? 0 : Math.Min(1, (double)FinishedThisYear / Goal);
    public int PagesThisMonth => _sessions.Where(s => s.Date.Year == DateTime.Today.Year && s.Date.Month == DateTime.Today.Month).Sum(s => s.Pages);
    public int Streak
    {
        get
        {
            var days = _sessions.Where(s => s.Pages > 0 || s.Minutes > 0).Select(s => s.Date.Date).ToHashSet();
            var d = DateTime.Today;
            if (!days.Contains(d)) d = d.AddDays(-1);
            int n = 0;
            while (days.Contains(d)) { n++; d = d.AddDays(-1); }
            return n;
        }
    }
    public bool IsEmpty => Books.Count == 0;
    public bool HasSelection => Selected != null;
    public bool IsQuotesView => View == "Quotes";
    public bool IsReadingView => View == "Reading";

    public static IReadOnlyList<Choice<BookStatus>> Statuses { get; } =
    [
        new(BookStatus.WantToRead, "Want to read", Glyphs.Bookmark), new(BookStatus.Reading, "Reading", Glyphs.Book),
        new(BookStatus.Finished, "Finished", Glyphs.Completed), new(BookStatus.Abandoned, "Stopped", Glyphs.Pause)
    ];

    public static IReadOnlyList<Choice<BookFormat>> Formats { get; } =
    [
        new(BookFormat.Paper, "Paper"), new(BookFormat.Ebook, "E-book"), new(BookFormat.Audiobook, "Audiobook"),
        new(BookFormat.Comic, "Comic / manga"), new(BookFormat.Article, "Article")
    ];

    partial void OnViewChanged(string value)
    {
        OnPropertyChanged(nameof(IsQuotesView));
        OnPropertyChanged(nameof(IsReadingView));
        ApplyShelf();
        if (value == "Quotes") _ = LoadAllQuotesAsync();
    }

    async partial void OnSelectedChanged(Book? oldValue, Book? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= OnBookChanged;
        if (newValue != null) newValue.PropertyChanged += OnBookChanged;
        OnPropertyChanged(nameof(HasSelection));
        PageInput = newValue?.CurrentPage.ToString() ?? "";
        Quotes.Clear();
        Sessions.Clear();
        if (newValue == null) return;
        var id = newValue.Id;
        var quotes = await _store.ListAsync<BookQuote>(q => q.Where(x => x.BookId == id).OrderBy(x => x.Page));
        if (!ReferenceEquals(Selected, newValue)) return;
        foreach (var q in quotes) Quotes.Add(q);
        foreach (var s in _sessions.Where(s => s.BookId == id).OrderByDescending(s => s.Date).Take(30)) Sessions.Add(s);
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_loaded) await LoadAsync();
        if (parameter is int id && Books.FirstOrDefault(b => b.Id == id) is { } book) Selected = book;
        else if (parameter is "new") OpenAdd();
    }

    private async Task LoadAsync()
    {
        var (books, sessions) = await _store.RunAsync(db => (db.Books.OrderBy(b => b.SortOrder).ToList(), db.ReadingSessions.OrderBy(s => s.Date).ToList()));
        Books.Clear();
        foreach (var b in books) Books.Add(b);
        _sessions = sessions;
        _loaded = true;
        if (!Books.Any(b => b.Status == BookStatus.Reading) && View == "Reading" && Books.Count > 0) View = "WantToRead";
        ApplyShelf();
        RaiseStats();
    }

    private void ApplyShelf()
    {
        CurrentlyReading.Clear();
        foreach (var b in Books.Where(b => b.Status == BookStatus.Reading).OrderByDescending(b => b.UpdatedAt)) CurrentlyReading.Add(b);
        IEnumerable<Book> items = View switch
        {
            "WantToRead" => Books.Where(b => b.Status == BookStatus.WantToRead),
            "Finished" => Books.Where(b => b.Status == BookStatus.Finished).OrderByDescending(b => b.FinishedAt ?? b.UpdatedAt),
            "Stopped" => Books.Where(b => b.Status == BookStatus.Abandoned),
            "All" => Books.OrderBy(b => b.Title),
            _ => Books.Where(b => b.Status == BookStatus.Reading)
        };
        Shelf.Clear();
        foreach (var b in items) Shelf.Add(b);
        BuildChart();
    }

    private void RaiseStats()
    {
        OnPropertyChanged(nameof(Goal));
        OnPropertyChanged(nameof(FinishedThisYear));
        OnPropertyChanged(nameof(GoalProgress));
        OnPropertyChanged(nameof(PagesThisMonth));
        OnPropertyChanged(nameof(Streak));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void BuildChart()
    {
        Last14Days.Clear();
        var start = DateTime.Today.AddDays(-13);
        var byDay = _sessions.Where(s => s.Date.Date >= start).GroupBy(s => s.Date.Date).ToDictionary(g => g.Key, g => g.Sum(s => s.Pages));
        int max = Math.Max(10, byDay.Values.DefaultIfEmpty(0).Max());
        for (int i = 0; i < 14; i++)
        {
            var day = start.AddDays(i);
            int pages = byDay.GetValueOrDefault(day);
            Last14Days.Add(new DayBar(day, pages, (double)pages / max));
        }
    }

    private void OnBookChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not Book b || e.PropertyName is nameof(Book.UpdatedAt) or nameof(Book.Progress) or nameof(Book.ProgressText) or nameof(Book.PagesLeft) or nameof(Book.UnitName)) return;
        if (e.PropertyName == nameof(Book.Status))
        {
            if (b.Status == BookStatus.Reading && b.StartedAt == null) b.StartedAt = DateTime.Today;
            if (b.Status == BookStatus.Finished)
            {
                b.FinishedAt ??= DateTime.Today;
                if (b.TotalPages > 0) b.CurrentPage = b.TotalPages;
            }
            ApplyShelf();
            RaiseStats();
        }
        MarkDirty(b);
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

    // ----- selection & editing -----

    [RelayCommand]
    private void Select(Book? book) => Selected = book;

    [RelayCommand]
    private void CloseDetail() => Selected = null;

    [RelayCommand]
    private void SetView(string? view) => View = view ?? "Reading";

    [RelayCommand]
    private void SetStatus(BookStatus status)
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
    private async Task SetGoal()
    {
        var text = await _dialogs.PromptAsync("Reading goal", $"How many books do you want to finish in {DateTime.Today.Year}?", Goal.ToString(), "e.g. 12", "Save");
        if (int.TryParse(text, out var goal) && goal >= 0)
        {
            _settings.Current.ReadingGoal = Math.Min(goal, 1000);
            RaiseStats();
        }
    }

    /// <summary>Records progress: "page 120", or "+15" for pages read now.</summary>
    [RelayCommand]
    private async Task LogProgress(string? input)
    {
        if (Selected is not { } b) return;
        input ??= PageInput;
        input = input.Trim();
        int target;
        if (input.StartsWith('+') && int.TryParse(input[1..], out var delta)) target = b.CurrentPage + delta;
        else if (!int.TryParse(input, out target)) return;
        if (b.TotalPages > 0) target = Math.Min(target, b.TotalPages);
        target = Math.Max(0, target);
        int read = target - b.CurrentPage;
        if (read == 0) return;
        b.CurrentPage = target;
        if (b.Status is BookStatus.WantToRead or BookStatus.Abandoned) b.Status = BookStatus.Reading;
        if (read > 0)
        {
            var session = new ReadingSession { BookId = b.Id, Date = DateTime.Now, Pages = read, ToPage = target };
            await _store.AddAsync(session);
            _sessions.Add(session);
            Sessions.Insert(0, session);
        }
        PageInput = target.ToString();
        if (b.TotalPages > 0 && target >= b.TotalPages && b.Status != BookStatus.Finished)
        {
            b.Status = BookStatus.Finished;
            _toasts.Success($"Finished “{b.Title}” 🎉");
        }
        BuildChart();
        RaiseStats();
        await FlushAsync();
    }

    [RelayCommand]
    private async Task QuickPages(Book? book)
    {
        if (book == null) return;
        Selected = book;
        await LogProgress("+10");
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
    private async Task FindCover()
    {
        if (Selected is not { } b) return;
        if (!_web.IsAvailable) { _toasts.Info("You're offline (or online features are off in Settings)"); return; }
        var results = await SearchOpenLibraryAsync($"{b.Title} {b.Author}", CancellationToken.None);
        var hit = results.FirstOrDefault(r => r.CoverId > 0);
        if (hit == null) { _toasts.Info("No cover found online"); return; }
        var cover = await _web.GetImageAsync($"https://covers.openlibrary.org/b/id/{hit.CoverId}-L.jpg", ImageService.CoverSize);
        if (cover != null) b.Cover = cover;
    }

    [RelayCommand]
    private async Task DeleteBook()
    {
        if (Selected is not { } b) return;
        if (_settings.Current.ConfirmDeletes && !await _dialogs.ConfirmAsync("Remove book?", $"“{b.Title}”, its quotes and reading sessions will be deleted.", "Remove", danger: true)) return;
        _dirty.Remove(b);
        await _store.DeleteAsync(b);
        Books.Remove(b);
        _sessions.RemoveAll(s => s.BookId == b.Id);
        Selected = null;
        ApplyShelf();
        RaiseStats();
    }

    // ----- quotes -----

    [RelayCommand]
    private async Task AddQuote()
    {
        if (Selected is not { } b || string.IsNullOrWhiteSpace(NewQuote)) return;
        var q = new BookQuote { BookId = b.Id, Text = NewQuote.Trim(), Page = int.TryParse(NewQuotePage, out var p) ? p : b.CurrentPage };
        await _store.AddAsync(q);
        Quotes.Add(q);
        NewQuote = "";
        NewQuotePage = "";
    }

    [RelayCommand]
    private async Task DeleteQuote(BookQuote? q)
    {
        if (q == null) return;
        Quotes.Remove(q);
        if (AllQuotes.FirstOrDefault(r => ReferenceEquals(r.Quote, q) || r.Quote.Id == q.Id) is { } row) AllQuotes.Remove(row);
        await _store.DeleteAsync(q);
    }

    private async Task LoadAllQuotesAsync()
    {
        var quotes = await _store.ListAsync<BookQuote>(q => q.OrderByDescending(x => x.CreatedAt));
        AllQuotes.Clear();
        foreach (var q in quotes)
            if (Books.FirstOrDefault(b => b.Id == q.BookId) is { } book) AllQuotes.Add(new QuoteRow(q, book));
    }

    [RelayCommand]
    private void CopyQuote(BookQuote? q)
    {
        if (q == null) return;
        var book = Books.FirstOrDefault(b => b.Id == q.BookId);
        Security.SecureClipboard.Copy($"“{q.Text}” — {book?.Author}, {book?.Title}", sensitive: false, "Quote copied");
    }

    // ----- adding books -----

    [RelayCommand]
    private void OpenAdd()
    {
        IsAdding = true;
        SearchMessage = _web.IsAvailable ? "Search by title, author or ISBN — or add it by hand." : "Offline: type the title and add it by hand.";
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
        var q = SearchQuery.Trim();
        if (q.Length < 2) return;
        if (!_web.IsAvailable) { SearchMessage = "You're offline — add the book by hand."; return; }
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        IsSearching = true;
        SearchResults.Clear();
        SearchMessage = "Searching Open Library…";
        try
        {
            var results = await SearchOpenLibraryAsync(q, cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var r in results) SearchResults.Add(r);
            SearchMessage = results.Count == 0 ? "Nothing found — try other words, or add it by hand." : "";
            foreach (var r in results.Where(r => r.CoverId > 0))
            {
                var bytes = await _web.GetBytesAsync($"https://covers.openlibrary.org/b/id/{r.CoverId}-M.jpg", ct: cts.Token);
                if (cts.IsCancellationRequested) return;
                if (bytes != null) r.Thumb = ImageService.ToBitmap(bytes, 120);
            }
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts)) IsSearching = false;
        }
    }

    private async Task<List<BookSearchResult>> SearchOpenLibraryAsync(string query, CancellationToken ct)
    {
        var url = $"https://openlibrary.org/search.json?q={Uri.EscapeDataString(query)}&limit=12&fields=title,author_name,first_publish_year,number_of_pages_median,cover_i,isbn";
        var json = await _web.GetStringAsync(url, ct);
        var list = new List<BookSearchResult>();
        if (json == null) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("docs", out var docs)) return list;
            foreach (var d in docs.EnumerateArray())
            {
                string Str(string name) => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                int Int(string name) => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
                string First(string name) => d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0 ? v[0].GetString() ?? "" : "";
                var title = Str("title");
                if (title.Length == 0) continue;
                list.Add(new BookSearchResult(title, First("author_name"), Int("first_publish_year"), Int("number_of_pages_median"), Int("cover_i"), First("isbn")));
            }
        }
        catch (JsonException) { }
        return list;
    }

    [RelayCommand]
    private async Task AddResult(BookSearchResult? r)
    {
        if (r == null) return;
        var book = new Book
        {
            Title = r.Title, Author = r.Author, Year = r.Year, TotalPages = r.Pages, Isbn = r.Isbn,
            Status = BookStatus.WantToRead, Color = RandomColor(), SortOrder = Books.Count
        };
        if (r.CoverId > 0) book.Cover = await _web.GetImageAsync($"https://covers.openlibrary.org/b/id/{r.CoverId}-L.jpg", ImageService.CoverSize);
        await AddBookAsync(book);
    }

    [RelayCommand]
    private async Task AddManual()
    {
        var title = SearchQuery.Trim();
        if (title.Length == 0) title = await _dialogs.PromptAsync("Add a book", null, "", "Title", "Add") ?? "";
        if (string.IsNullOrWhiteSpace(title)) return;
        await AddBookAsync(new Book { Title = title.Trim(), Status = BookStatus.WantToRead, Color = RandomColor(), SortOrder = Books.Count });
    }

    private static string RandomColor() => Palettes.Tags[Random.Shared.Next(Math.Min(10, Palettes.Tags.Count))].Key;

    private async Task AddBookAsync(Book book)
    {
        await _store.AddAsync(book);
        Books.Add(book);
        CloseAdd();
        SearchQuery = "";
        if (View is "Reading" or "Finished" or "Stopped") View = "WantToRead";
        ApplyShelf();
        RaiseStats();
        Selected = book;
        _toasts.Success($"Added “{book.Title}”");
    }

    public void Dispose()
    {
        _saveTimer.Stop();
        _searchCts?.Cancel();
        if (Selected != null) Selected.PropertyChanged -= OnBookChanged;
    }
}

public sealed partial class BookSearchResult(string title, string author, int year, int pages, int coverId, string isbn) : ObservableObject
{
    public string Title { get; } = title;
    public string Author { get; } = author;
    public int Year { get; } = year;
    public int Pages { get; } = pages;
    public int CoverId { get; } = coverId;
    public string Isbn { get; } = isbn;
    public string Meta => string.Join(" · ", new[] { Year > 0 ? Year.ToString() : "", Pages > 0 ? $"{Pages} pages" : "" }.Where(s => s.Length > 0));
    [ObservableProperty] private BitmapSource? _thumb;
}

public sealed record DayBar(DateTime Day, int Pages, double Fraction)
{
    public string Label => Day.ToString("ddd d");
    public double BarHeight => Math.Max(2, Fraction * 56);
}

public sealed record QuoteRow(BookQuote Quote, Book Book);
