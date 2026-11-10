using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Services;

namespace Satchel.ViewModels.Dialogs;

/// <summary>Where "Add from Satchel" can take things from.</summary>
public sealed partial class InsertSource(string key, string title, string one, string glyph, string hint) : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    /// <summary>The name for a single item ("note", "show or movie"…).</summary>
    public string One { get; } = one;
    public string Glyph { get; } = glyph;
    /// <summary>What the item becomes on the board.</summary>
    public string Hint { get; } = hint;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    private int _count = -1;

    public string? CountText => Count < 0 ? null : Count.ToString();
}

/// <summary>Something that can go on a board: a note, photo, video, game…</summary>
public sealed partial class InsertItem : ObservableObject
{
    public required string Source { get; init; }
    /// <summary>The item's id (0 = "Today" for task lists).</summary>
    public required int Id { get; init; }
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    public string Glyph { get; init; } = Glyphs.Document;
    public string ColorKey { get; init; } = "teal";
    /// <summary>Notes: the sticky colour it will get on the board.</summary>
    public string StickyColor { get; init; } = "yellow";
    public string Preview { get; init; } = "";
    public byte[]? Thumb { get; init; }
    /// <summary>The source is private (or inside something private), so its board copy is private too.</summary>
    public bool IsPrivate { get; init; }
    public string Haystack { get; init; } = "";

    [ObservableProperty] private bool _isSelected;

    public bool HasThumb => Thumb != null;
    public bool IsNote => Source == "note";
}

/// <summary>
/// "Add from Satchel": pick notes, photos, videos, games, books, shows, bookmarks, projects or task lists — from several
/// tabs at once — and put them on the board. Returns the picked <see cref="InsertItem"/>s.
/// </summary>
public sealed partial class BoardInsertDialogViewModel : DialogViewModel
{
    private const int MaxShown = 60;
    private readonly DataStore _store;
    private readonly List<InsertItem> _picked = [];
    private List<InsertItem> _loaded = [];
    private int _loadVersion;

    public BoardInsertDialogViewModel(DataStore store, string? startSource = null)
    {
        _store = store;
        Sources =
        [
            new("note", "Notes", "note", Glyphs.Notes, "Copied as board notes you can edit — checklists keep their boxes."),
            new("photo", "Photos", "photo", Glyphs.Photo, "Added as photos, with their captions."),
            new("video", "Videos", "video", Glyphs.Video, "Added as video cards — they play next to the board in the study panel."),
            new("game", "Games", "game", Glyphs.Game, "Added as live cards that keep up with status and hours played."),
            new("book", "Books", "book", Glyphs.Book, "Added as live cards that show how far you've read."),
            new("show", "Shows & movies", "show or movie", Glyphs.Monitor, "Added as live cards that show the next episode."),
            new("bookmark", "Bookmarks", "bookmark", Glyphs.Bookmark, "Added as link cards."),
            new("project", "Projects", "project", Glyphs.Projects, "Added as live cards with progress and the next milestone."),
            new("tasks", "Task lists", "task list", Glyphs.Tasks, "Added as live cards — tick tasks off right on the board."),
        ];
        _source = Sources.FirstOrDefault(s => s.Key == startSource) ?? Sources[0];
        _ = InitAsync();
    }

    public IReadOnlyList<InsertSource> Sources { get; }
    public ObservableCollection<InsertItem> Items { get; } = [];
    public IReadOnlyList<InsertItem> Picked => _picked;
    public override double DialogWidth => 920;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hint), nameof(EmptyText))]
    private InsertSource _source;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    private string _query = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isLoading = true;

    [ObservableProperty] private string _moreText = "";

    public string Hint => Source.Hint;
    public bool IsEmpty => !IsLoading && Items.Count == 0;
    public string EmptyText => Query.Trim().Length > 0 ? "Nothing matches that." : $"No {Source.Title.ToLowerInvariant()} yet.";
    public string AddText => _picked.Count switch { 0 => "Add to the board", 1 => "Add 1 item", var n => $"Add {n} items" };
    public string PickedText => _picked.Count == 0 ? "" : string.Join(" · ", _picked.GroupBy(p => p.Source)
        .Select(g => Sources.First(s => s.Key == g.Key) is var src && g.Count() == 1 ? $"1 {src.One}" : $"{g.Count()} {src.Title.ToLowerInvariant()}"));

    partial void OnSourceChanged(InsertSource value)
    {
        if (Query.Length > 0) Query = "";
        _ = LoadAsync();
    }

    partial void OnQueryChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void SelectSource(InsertSource? source)
    {
        if (source != null && !ReferenceEquals(source, Source)) Source = source;
    }

    [RelayCommand]
    private void Toggle(InsertItem? item)
    {
        if (item == null) return;
        item.IsSelected = !item.IsSelected;
        _picked.RemoveAll(p => p.Source == item.Source && p.Id == item.Id);
        if (item.IsSelected) _picked.Add(item);
        OnPropertyChanged(nameof(AddText));
        OnPropertyChanged(nameof(PickedText));
        AddCommand.NotifyCanExecuteChanged();
    }

    private bool CanAdd => _picked.Count > 0;

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add() => Close(_picked.ToList());

    private async Task InitAsync()
    {
        try
        {
            var counts = await _store.RunAsync(db => new Dictionary<string, int>
            {
                ["note"] = db.Notes.Count(n => !n.IsTrashed),
                ["photo"] = db.Photos.Count(),
                ["video"] = db.Videos.Count(v => !v.IsShort),
                ["game"] = db.Games.Count(),
                ["book"] = db.Books.Count(),
                ["show"] = db.WatchItems.Count(),
                ["bookmark"] = db.Bookmarks.Count(),
                ["project"] = db.Projects.Count(),
                ["tasks"] = db.TaskLists.Count() + 1,
            });
            foreach (var s in Sources) s.Count = counts.GetValueOrDefault(s.Key);
        }
        catch
        {
            // counts are a nicety
        }
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        int version = ++_loadVersion;
        var key = Source.Key;
        IsLoading = true;
        Items.Clear();
        List<InsertItem> items;
        try
        {
            items = await _store.RunAsync(db => Load(db, key));
        }
        catch
        {
            items = [];
        }
        if (version != _loadVersion) return;
        foreach (var it in items) it.IsSelected = _picked.Any(p => p.Source == it.Source && p.Id == it.Id);
        _loaded = items;
        IsLoading = false;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var words = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = _loaded.Where(i => words.All(w => i.Haystack.Contains(w, StringComparison.CurrentCultureIgnoreCase))).ToList();
        Items.Clear();
        foreach (var i in matches.Take(MaxShown)) Items.Add(i);
        MoreText = matches.Count > MaxShown ? $"Showing {MaxShown} of {matches.Count} — search to find the rest" : "";
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static string Clip(string text, int max)
    {
        text = text.Replace("\r", "").Replace('\n', ' ').Trim();
        return text.Length > max ? text[..max] + "…" : text;
    }

    /// <summary>Everything a card needs, without loading full pictures or documents (those load when added).</summary>
    private static List<InsertItem> Load(AppDbContext db, string key)
    {
        switch (key)
        {
            case "note":
            {
                // Inside a private notebook counts as private.
                var privateBooks = db.Notebooks.IgnoreQueryFilters().Where(b => b.IsPrivate).Select(b => b.Id).ToHashSet();
                return db.Notes.Where(n => !n.IsTrashed).OrderByDescending(n => n.UpdatedAt).Take(400)
                    .Select(n => new { n.Id, n.Title, n.Type, n.PlainText, n.Color, n.JournalDate, n.UpdatedAt, n.IsPrivate, n.NotebookId })
                    .AsEnumerable()
                    .Select(n =>
                    {
                        var title = n.Title.Trim().Length > 0 ? n.Title.Trim()
                            : n.Type == NoteType.Journal && n.JournalDate is { } day ? Format.FriendlyDay(day) : "Untitled";
                        var kind = n.Type switch { NoteType.Checklist => "Checklist", NoteType.List => "List", NoteType.Code => "Code", NoteType.Journal => "Journal", _ => "Note" };
                        return new InsertItem
                        {
                            Source = "note", Id = n.Id, Title = title, Subtitle = $"{kind} · {Format.Relative(n.UpdatedAt)}", Glyph = Note.GlyphFor(n.Type),
                            StickyColor = BoardViewModel.StickyColorFor(n.Color), Preview = Clip(n.PlainText, 160),
                            IsPrivate = n.IsPrivate || (n.NotebookId is int nb && privateBooks.Contains(nb)),
                            Haystack = $"{title} {n.PlainText} {kind}"
                        };
                    }).ToList();
            }
            case "photo":
            {
                var privateAlbums = db.Albums.IgnoreQueryFilters().Where(a => a.IsPrivate).Select(a => a.Id).ToHashSet();
                return db.Photos.OrderByDescending(p => p.TakenAt).Take(400)
                    .Select(p => new { p.Id, p.Caption, p.FileName, p.Thumb, p.TakenAt, p.IsPrivate, p.AlbumId })
                    .AsEnumerable()
                    .Select(p =>
                    {
                        var title = p.Caption.Trim().Length > 0 ? p.Caption.Trim() : p.FileName.Trim().Length > 0 ? p.FileName : "Photo";
                        return new InsertItem
                        {
                            Source = "photo", Id = p.Id, Title = title, Subtitle = p.TakenAt.ToString("d MMM yyyy"), Glyph = Glyphs.Photo, ColorKey = "sky",
                            Thumb = p.Thumb, IsPrivate = p.IsPrivate || (p.AlbumId is int a && privateAlbums.Contains(a)), Haystack = $"{p.Caption} {p.FileName}"
                        };
                    }).ToList();
            }
            case "video":
            {
                var privateChannels = db.VideoChannels.IgnoreQueryFilters().Where(c => c.IsPrivate).Select(c => c.Id).ToHashSet();
                return db.Videos.Where(v => !v.IsShort).OrderByDescending(v => v.PublishedAt).Take(400)
                    .Select(v => new { v.Id, v.Title, v.Author, v.Thumbnail, v.PublishedAt, v.IsPrivate, v.ChannelId })
                    .AsEnumerable()
                    .Select(v => new InsertItem
                    {
                        Source = "video", Id = v.Id, Title = v.Title.Length > 0 ? v.Title : "YouTube video",
                        Subtitle = v.Author + (v.PublishedAt is { } d ? $" · {d:d MMM yyyy}" : ""), Glyph = Glyphs.Video, ColorKey = "red", Thumb = v.Thumbnail,
                        IsPrivate = v.IsPrivate || (v.ChannelId is int c && privateChannels.Contains(c)), Haystack = $"{v.Title} {v.Author}"
                    }).ToList();
            }
            case "game":
                return db.Games.OrderBy(g => g.SortOrder).Take(400)
                    .Select(g => new { g.Id, g.Title, g.Platform, g.Cover, g.Status, g.Hours, g.Color, g.IsPrivate })
                    .AsEnumerable()
                    .Select(g => new InsertItem
                    {
                        Source = "game", Id = g.Id, Title = g.Title, Glyph = Glyphs.Game, ColorKey = g.Color, Thumb = g.Cover, IsPrivate = g.IsPrivate,
                        Subtitle = string.Join(" · ", new[] { g.Status.ToString(), g.Platform }.Where(s => s.Length > 0)), Haystack = $"{g.Title} {g.Platform} {g.Status}"
                    }).ToList();
            case "book":
                return db.Books.OrderBy(b => b.SortOrder).Take(400)
                    .Select(b => new { b.Id, b.Title, b.Author, b.Cover, b.Status, b.Color, b.IsPrivate })
                    .AsEnumerable()
                    .Select(b => new InsertItem
                    {
                        Source = "book", Id = b.Id, Title = b.Title, Subtitle = b.Author, Glyph = Glyphs.Book, ColorKey = b.Color, Thumb = b.Cover,
                        IsPrivate = b.IsPrivate, Haystack = $"{b.Title} {b.Author} {b.Status}"
                    }).ToList();
            case "show":
                return db.WatchItems.OrderBy(w => w.SortOrder).Take(400)
                    .Select(w => new { w.Id, w.Title, w.Poster, w.Kind, w.Year, w.Status, w.Color, w.IsPrivate })
                    .AsEnumerable()
                    .Select(w => new InsertItem
                    {
                        Source = "show", Id = w.Id, Title = w.Title, Glyph = Glyphs.Monitor, ColorKey = w.Color, Thumb = w.Poster, IsPrivate = w.IsPrivate,
                        Subtitle = string.Join(" · ", new[] { w.Year > 0 ? w.Year.ToString() : "", w.Kind == WatchKind.Show ? "TV" : "Movie" }.Where(s => s.Length > 0)),
                        Haystack = $"{w.Title} {w.Year} {w.Kind}"
                    }).ToList();
            case "bookmark":
                return db.Bookmarks.OrderBy(b => b.SortOrder).Take(400)
                    .Select(b => new { b.Id, b.Title, b.Url, b.Icon, b.Image, b.Color, b.IsPrivate, b.Description })
                    .AsEnumerable()
                    .Select(b =>
                    {
                        var domain = Uri.TryCreate(b.Url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : b.Url;
                        return new InsertItem
                        {
                            Source = "bookmark", Id = b.Id, Title = b.Title.Trim().Length > 0 ? b.Title : domain, Subtitle = domain, Glyph = Glyphs.Bookmark,
                            ColorKey = b.Color, Thumb = b.Image ?? b.Icon, IsPrivate = b.IsPrivate, Haystack = $"{b.Title} {b.Url} {b.Description}"
                        };
                    }).ToList();
            case "project":
                return db.Projects.OrderBy(p => p.SortOrder).Take(400)
                    .Select(p => new { p.Id, p.Name, p.Status, p.Color, p.Icon, p.IsPrivate })
                    .AsEnumerable()
                    .Select(p => new InsertItem
                    {
                        Source = "project", Id = p.Id, Title = p.Name, Subtitle = p.Status.ToString(), Glyph = string.IsNullOrEmpty(p.Icon) ? Glyphs.Projects : p.Icon,
                        ColorKey = p.Color, IsPrivate = p.IsPrivate, Haystack = $"{p.Name} {p.Status}"
                    }).ToList();
            case "tasks":
            {
                var lists = db.TaskLists.OrderBy(l => l.SortOrder)
                    .Select(l => new { l.Id, l.Name, l.Color, l.IsPrivate })
                    .AsEnumerable()
                    .Select(l => new InsertItem
                    {
                        Source = "tasks", Id = l.Id, Title = l.Name, Subtitle = "Task list", Glyph = Glyphs.List, ColorKey = l.Color, IsPrivate = l.IsPrivate, Haystack = l.Name
                    }).ToList();
                lists.Insert(0, new InsertItem { Source = "tasks", Id = 0, Title = "Today", Subtitle = "Due today and overdue", Glyph = Glyphs.CalendarDay, ColorKey = "teal", Haystack = "today due overdue" });
                return lists;
            }
            default:
                return [];
        }
    }
}
