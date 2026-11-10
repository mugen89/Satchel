using System.Globalization;
using System.Text.Json;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;

namespace Satchel.Services;

/// <summary>Points at an item inside a parent (a card in a deck, a song in a playlist…). Id 0 = the parent itself.</summary>
public sealed record ItemRef(int ParentId, int Id);

public sealed record SearchResult(string Kind, string Title, string Subtitle, string Glyph, string Page, object? Parameter, string Haystack, string? ColorKey = null)
{
    public int Score { get; init; }
}

/// <summary>
/// Global search. Content is encrypted in the database, so the index is built in memory
/// (decrypted) when the palette opens and filtered as you type.
/// </summary>
public sealed class SearchService
{
    private readonly DataStore _store;

    public SearchService(DataStore store) => _store = store;

    public Task<List<SearchResult>> BuildIndexAsync() => _store.RunAsync(db =>
    {
        var list = new List<SearchResult>();

        foreach (var n in db.Notes.Where(n => !n.IsTrashed).ToList())
            list.Add(new SearchResult("Note", n.DisplayTitle, Snip(n.Type == NoteType.Code ? n.Content : n.PlainText), n.TypeGlyph, "Notes", n.Id,
                $"{n.Title} {n.PlainText} {n.Tags}", n.Color == "default" ? null : n.Color));

        foreach (var t in db.Tasks.ToList())
            list.Add(new SearchResult("Task", t.Title, t.IsDone ? "Completed" : (t.HasDue ? $"Due {t.DueText}" : "Task"), t.IsDone ? Glyphs.CheckboxChecked : Glyphs.Checkbox,
                "Tasks", t.Id, $"{t.Title} {t.Notes} {t.Tags}"));

        foreach (var e in db.Events.ToList())
            list.Add(new SearchResult("Event", e.Title, (e.IsRecurring ? "Repeats · " : "") + (e.IsAllDay ? Format.Date(e.Start) : Format.DateAndTime(e.Start)),
                Glyphs.Calendar, "Calendar", e.Start.Date, $"{e.Title} {e.Location} {e.Description}"));

        var profiles = db.Profiles.ToList();
        var byId = profiles.ToDictionary(p => p.Id);
        foreach (var p in profiles)
            list.Add(new SearchResult("Profile", p.Name, string.IsNullOrWhiteSpace(p.Website) ? p.Category : p.Website, p.Icon, "Vault", p.Id,
                $"{p.Name} {p.Website} {p.Category} {p.Tags} {p.Notes}", p.Color));

        foreach (var a in db.Accounts.ToList())
        {
            byId.TryGetValue(a.ProfileId, out var parent);
            var fields = string.Join(' ', CustomField.Parse(a.Fields).Where(f => !CustomField.IsSecretType(f.Type)).Select(f => $"{f.Label} {f.Value}"));
            list.Add(new SearchResult("Account", $"{parent?.Name} · {a.DisplayName}", a.Subtitle, VaultAccount.KindGlyph(a.Kind), "Vault", a.ProfileId,
                $"{parent?.Name} {a.Label} {a.Username} {a.Email} {a.Url} {a.Tags} {fields}", parent?.Color));
        }

        foreach (var c in db.Contacts.ToList())
            list.Add(new SearchResult("Contact", c.FullName, c.Subtitle, Glyphs.Contact, "Contacts", c.Id,
                $"{c.FullName} {c.Company} {c.Emails} {c.Phones} {c.Tags} {c.Notes}", c.Color));

        var boards = db.Boards.ToList().ToDictionary(b => b.Id, b => b.Name);
        foreach (var s in db.Stickies.ToList())
        {
            var text = s.Kind == StickyKind.Text ? s.Text : s.Caption;
            if (string.IsNullOrWhiteSpace(text)) continue;
            list.Add(new SearchResult(s.Kind == StickyKind.Link ? "Board link" : s.Kind == StickyKind.Image ? "Board image" : "Sticky", Snip(text, 60),
                boards.GetValueOrDefault(s.BoardId, "Board"), s.Kind == StickyKind.Link ? Glyphs.Link : s.Kind == StickyKind.Image ? Glyphs.Picture : Glyphs.Tiles,
                "Board", s.BoardId, text + " " + s.Url + " " + s.Subtitle));
        }

        foreach (var p in db.Projects.ToList())
            list.Add(new SearchResult("Project", p.Name, Snip(p.Description), Glyphs.Projects, "Projects", p.Id, p.Name + " " + p.Description + " " + p.Tags, p.Color));

        foreach (var m in db.Milestones.ToList())
            list.Add(new SearchResult("Milestone", m.Title, m.DueDate.HasValue ? $"Due {Format.Date(m.DueDate.Value)}" : "Milestone", Glyphs.Flag, "Projects", m.ProjectId, m.Title));

        foreach (var d in db.LibraryDocs.ToList())
            list.Add(new SearchResult("Document", d.Title, LibraryDoc.MakeExcerpt(d.Markdown), Glyphs.Document, "Library", d.Id, d.Title + " " + d.Markdown));

        foreach (var b in db.Books.ToList())
            list.Add(new SearchResult("Book", b.Title, string.IsNullOrWhiteSpace(b.Author) ? "Book" : b.Author, Glyphs.Book, "Reading", b.Id, b.Title + " " + b.Author + " " + b.Notes + " " + b.Tags));

        foreach (var q in db.BookQuotes.ToList())
            list.Add(new SearchResult("Quote", Snip(q.Text, 70), $"p. {q.Page}", Glyphs.Quote, "Reading", q.BookId, q.Text + " " + q.Note));

        foreach (var g in db.Games.ToList())
            list.Add(new SearchResult("Game", g.Title, string.IsNullOrWhiteSpace(g.Platform) ? "Game" : g.Platform, Glyphs.Game, "Games", g.Id, g.Title + " " + g.Platform + " " + g.Genre + " " + g.Notes));

        foreach (var sk in db.Sketches.ToList())
            list.Add(new SearchResult("Sketch", sk.Title, "Sketch", Glyphs.Pen, "Sketch", sk.Id, sk.Title + " " + sk.Tags));

        // Spreadsheets: titles and file names only (the cells stay unread until one is opened).
        foreach (var s in db.Spreadsheets.Select(s => new { s.Id, s.Title, s.FileName, s.SheetCount }).ToList())
            list.Add(new SearchResult("Spreadsheet", s.Title, s.SheetCount == 1 ? "Spreadsheet" : $"Spreadsheet · {s.SheetCount} sheets", Glyphs.Sheet, "Sheets", s.Id, s.Title + " " + s.FileName));

        foreach (var v in db.Videos.ToList())
            list.Add(new SearchResult("Video", v.Title, v.Author, Glyphs.Video, "Videos", v.Id, v.Title + " " + v.Author + " " + v.Notes));

        foreach (var vn in db.VideoNotes.ToList())
            list.Add(new SearchResult("Video note", Snip(vn.Text, 70), vn.TimeText, Glyphs.Clock, "Videos", vn.VideoId, vn.Text));

        // Newer modules: text columns only, so photos, posters and thumbnails are never loaded for search.
        foreach (var b in db.Bookmarks.Select(b => new { b.Id, b.Title, b.Url, b.Description, b.Folder, b.Tags, b.Notes, b.Color }).ToList())
            list.Add(new SearchResult("Bookmark", string.IsNullOrWhiteSpace(b.Title) ? b.Url : b.Title, Host(b.Url), Glyphs.Bookmark, "Bookmarks", b.Id,
                $"{b.Title} {b.Url} {b.Description} {b.Folder} {b.Tags} {b.Notes}", b.Color));

        foreach (var s in db.Subscriptions.Select(s => new { s.Id, s.Name, s.Category, s.AmountText, s.Currency, s.Notes, s.Kind, s.Color }).ToList())
        {
            decimal.TryParse(s.AmountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount);
            list.Add(new SearchResult(s.Kind == MoneyKind.Bill ? "Bill" : "Subscription", s.Name, Subscription.FormatMoney(amount, s.Currency) + (string.IsNullOrWhiteSpace(s.Category) ? "" : " · " + s.Category),
                Glyphs.Card, "Money", s.Id, $"{s.Name} {s.Category} {s.Notes}", s.Color));
        }

        foreach (var w in db.WatchItems.Select(w => new { w.Id, w.Title, w.Kind, w.Status, w.Year, w.Network, w.Genres, w.Notes, w.Tags, w.Color }).ToList())
            list.Add(new SearchResult(w.Kind == WatchKind.Show ? "Show" : "Movie", w.Title, (w.Year > 0 ? w.Year + " · " : "") + w.Status, Glyphs.Monitor, "Watching", w.Id,
                $"{w.Title} {w.Network} {w.Genres} {w.Notes} {w.Tags}", w.Color));

        var feedNames = new Dictionary<int, string>();
        foreach (var f in db.Feeds.Select(f => new { f.Id, f.Title, f.Description, f.Url, f.Kind, f.Color }).ToList())
        {
            feedNames[f.Id] = f.Title;
            list.Add(new SearchResult(f.Kind == FeedKind.Podcast ? "Podcast" : "Feed", f.Title, Snip(f.Description), Glyphs.Feed, "Feeds", new ItemRef(f.Id, 0), $"{f.Title} {f.Description} {f.Url}", f.Color));
        }
        foreach (var i in db.FeedItems.OrderByDescending(i => i.PublishedAt).Take(800).Select(i => new { i.Id, i.FeedId, i.Title, i.Summary, i.IsStarred }).ToList())
            list.Add(new SearchResult("Post", i.Title, feedNames.GetValueOrDefault(i.FeedId, "Feed"), i.IsStarred ? Glyphs.StarFilled : Glyphs.Feed, "Feeds", new ItemRef(i.FeedId, i.Id),
                i.Title + " " + Snip(i.Summary, 400)));

        var trackers = db.Trackers.Select(t => new { t.Id, t.Name, t.Description, t.Icon, t.Color }).ToList();
        foreach (var t in trackers)
            list.Add(new SearchResult("Collection", t.Name, string.IsNullOrWhiteSpace(t.Description) ? "Collection" : Snip(t.Description), t.Icon, "Collections", new ItemRef(t.Id, 0), $"{t.Name} {t.Description}", t.Color));
        var trackerNames = trackers.ToDictionary(t => t.Id, t => t.Name);
        foreach (var it in db.TrackerItems.Select(i => new { i.Id, i.TrackerId, i.Title, i.ValuesJson, i.Notes, i.Tags }).ToList())
            list.Add(new SearchResult("Collection item", it.Title, trackerNames.GetValueOrDefault(it.TrackerId, "Collection"), Glyphs.Grid9, "Collections", new ItemRef(it.TrackerId, it.Id),
                $"{it.Title} {it.Notes} {it.Tags} {JsonValues(it.ValuesJson)}"));

        var decks = db.Decks.Select(d => new { d.Id, d.Name, d.Description, d.Color }).ToList();
        foreach (var d in decks)
            list.Add(new SearchResult("Deck", d.Name, string.IsNullOrWhiteSpace(d.Description) ? "Flashcard deck" : Snip(d.Description), Glyphs.Education, "Flashcards", new ItemRef(d.Id, 0), $"{d.Name} {d.Description}", d.Color));
        var deckNames = decks.ToDictionary(d => d.Id, d => d.Name);
        foreach (var c in db.Flashcards.Select(c => new { c.Id, c.DeckId, c.Front, c.Back, c.Tags }).ToList())
            list.Add(new SearchResult("Flashcard", Snip(c.Front, 70), deckNames.GetValueOrDefault(c.DeckId, "Deck") + " · " + Snip(c.Back, 50), Glyphs.Education, "Flashcards", new ItemRef(c.DeckId, c.Id),
                $"{c.Front} {c.Back} {c.Tags}"));

        foreach (var p in db.Photos.Select(p => new { p.Id, p.Caption, p.FileName, p.Tags, p.TakenAt }).ToList())
            list.Add(new SearchResult("Photo", string.IsNullOrWhiteSpace(p.Caption) ? (string.IsNullOrWhiteSpace(p.FileName) ? "Photo" : p.FileName) : Snip(p.Caption, 70), Format.Date(p.TakenAt),
                Glyphs.Photo, "Photos", p.Id, $"{p.Caption} {p.FileName} {p.Tags}"));

        var playlists = db.MusicPlaylists.Select(p => new { p.Id, p.Name, p.Color }).ToList();
        foreach (var p in playlists)
            list.Add(new SearchResult("Playlist", p.Name, "Music", Glyphs.Music, "Music", new ItemRef(p.Id, 0), p.Name, p.Color));
        var playlistNames = playlists.ToDictionary(p => p.Id, p => p.Name);
        foreach (var t in db.MusicTracks.Select(t => new { t.Id, t.PlaylistId, t.Title, t.Artist }).ToList())
            list.Add(new SearchResult("Song", string.IsNullOrWhiteSpace(t.Title) ? "YouTube track" : t.Title,
                (string.IsNullOrWhiteSpace(t.Artist) ? "" : t.Artist + " · ") + playlistNames.GetValueOrDefault(t.PlaylistId, "Music"), Glyphs.Music, "Music", new ItemRef(t.PlaylistId, t.Id),
                $"{t.Title} {t.Artist}"));

        foreach (var c in db.Countdowns.Select(c => new { c.Title, c.Emoji, c.Date, c.Color }).ToList())
            list.Add(new SearchResult("Countdown", (c.Emoji + " " + c.Title).Trim(), Format.Date(c.Date), Glyphs.Timer, "Clock", null, c.Title, c.Color));

        return list;
    });

    public static List<SearchResult> Filter(IEnumerable<SearchResult> index, string query, int max = 40)
    {
        var terms = query.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return [];
        var results = new List<SearchResult>();
        foreach (var r in index)
        {
            int score = 0;
            bool all = true;
            foreach (var term in terms)
            {
                int s = 0;
                if (r.Title.StartsWith(term, StringComparison.OrdinalIgnoreCase)) s = 100;
                else if (r.Title.Contains(term, StringComparison.OrdinalIgnoreCase)) s = 60;
                else if (r.Haystack.Contains(term, StringComparison.OrdinalIgnoreCase)) s = 20;
                if (s == 0) { all = false; break; }
                score += s;
            }
            if (all) results.Add(r with { Score = score });
        }
        return results.OrderByDescending(r => r.Score).ThenBy(r => r.Title.Length).Take(max).ToList();
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : url;

    /// <summary>The values of a collection item's fields, for searching.</summary>
    private static string JsonValues(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "";
            return string.Join(" ", doc.RootElement.EnumerateObject().Select(p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString()));
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string Snip(string? text, int max = 90)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var line = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length > max ? line[..max] + "…" : line;
    }
}
