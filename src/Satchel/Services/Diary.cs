using Satchel.Core;
using Satchel.Data;
using Satchel.Models;

namespace Satchel.Services;

/// <summary>One line of the auto-diary.</summary>
public sealed record DiaryEntry(DateTime Time, bool HasTime, string Glyph, string Text, string ColorKey, string Page, object? Parameter)
{
    public string TimeText => HasTime ? Format.Time(Time) : "";
}

/// <summary>
/// The auto-diary: what happened on a day, compiled from everything Satchel already knows — tasks done, notes written,
/// events, reading and play sessions, videos, episodes, flashcards, payments, photos… Nothing extra is tracked.
/// Private items are left out automatically (query filters) unless they're shown.
/// </summary>
public static class Diary
{
    public static List<DiaryEntry> Build(AppDbContext db, DateTime date)
    {
        var day = date.Date;
        var next = day.AddDays(1);
        bool On(DateTime? t) => t.HasValue && t.Value >= day && t.Value < next;
        var list = new List<DiaryEntry>();
        void Add(DateTime time, string glyph, string text, string color, string page, object? parameter = null, bool hasTime = true) =>
            list.Add(new DiaryEntry(time, hasTime && time.TimeOfDay != TimeSpan.Zero, glyph, text, color, page, parameter));

        // Calendar
        var calendars = db.CalendarCategories.ToList().ToDictionary(c => c.Id, c => c.Color);
        foreach (var e in db.Events.ToList())
            foreach (var (start, _) in Recurrence.Occurrences(e, day, next))
                Add(start, Glyphs.Calendar, e.Title, e.Color ?? (e.CategoryId is int c && calendars.TryGetValue(c, out var col) ? col : "sky"), "Calendar", day, !e.IsAllDay);

        // Tasks
        foreach (var t in db.Tasks.Where(t => (t.CompletedAt >= day && t.CompletedAt < next) || (t.CreatedAt >= day && t.CreatedAt < next)).ToList())
        {
            if (On(t.CompletedAt)) Add(t.CompletedAt!.Value, Glyphs.Completed, $"Completed “{t.Title}”", "green", "Tasks", t.Id);
            else Add(t.CreatedAt, Glyphs.Tasks, $"Added task “{t.Title}”", "slate", "Tasks", t.Id);
        }

        // Notes (not the day's own journal entry)
        foreach (var n in db.Notes.Where(n => !n.IsTrashed && ((n.CreatedAt >= day && n.CreatedAt < next) || (n.UpdatedAt >= day && n.UpdatedAt < next)))
                     .Select(n => new { n.Id, n.Title, n.Type, n.JournalDate, n.CreatedAt, n.UpdatedAt }).ToList())
        {
            if (n.Type == NoteType.Journal && n.JournalDate?.Date == day) continue;
            var title = string.IsNullOrWhiteSpace(n.Title) ? "an untitled note" : $"“{n.Title}”";
            if (On(n.CreatedAt)) Add(n.CreatedAt, Glyphs.Notes, $"Wrote {title}", "teal", "Notes", n.Id);
            else Add(n.UpdatedAt, Glyphs.Edit, $"Edited {title}", "teal", "Notes", n.Id);
        }

        // Projects
        var projects = db.Projects.Select(p => new { p.Id, p.Name }).ToList().ToDictionary(p => p.Id, p => p.Name);
        foreach (var u in db.ProjectUpdates.Where(u => u.Date >= day && u.Date < next).ToList())
        {
            var kind = u.Kind switch { UpdateKind.Win => "a win", UpdateKind.Blocker => "a blocker", UpdateKind.Decision => "a decision", _ => "an update" };
            Add(u.Date, Glyphs.Projects, $"Logged {kind} on {projects.GetValueOrDefault(u.ProjectId, "a project")}: {Snip(u.Text)}", "amber", "Projects", u.ProjectId);
        }
        foreach (var m in db.Milestones.Where(m => m.DoneAt >= day && m.DoneAt < next).ToList())
            Add(m.DoneAt!.Value, Glyphs.Flag, $"Reached “{m.Title}” ({projects.GetValueOrDefault(m.ProjectId, "project")})", "amber", "Projects", m.ProjectId);

        // Reading
        var books = db.Books.Select(b => new { b.Id, b.Title, b.FinishedAt }).ToList();
        var bookNames = books.ToDictionary(b => b.Id, b => b.Title);
        foreach (var g in db.ReadingSessions.Where(s => s.Date >= day && s.Date < next).ToList().GroupBy(s => s.BookId))
        {
            var pages = g.Sum(s => s.Pages);
            var minutes = g.Sum(s => s.Minutes);
            var amount = pages > 0 ? $"{pages} pages" : minutes > 0 ? $"{minutes} min" : "a while";
            Add(g.Max(s => s.Date), Glyphs.Book, $"Read {amount} of “{bookNames.GetValueOrDefault(g.Key, "a book")}”", "sky", "Reading", g.Key);
        }
        foreach (var b in books.Where(b => On(b.FinishedAt))) Add(b.FinishedAt!.Value, Glyphs.Completed, $"Finished reading “{b.Title}” 📚", "sky", "Reading", b.Id, hasTime: false);
        foreach (var q in db.BookQuotes.Where(q => q.CreatedAt >= day && q.CreatedAt < next).ToList())
            Add(q.CreatedAt, Glyphs.Quote, $"Saved a quote from “{bookNames.GetValueOrDefault(q.BookId, "a book")}”", "sky", "Reading", q.BookId);
        foreach (var d in db.LibraryDocs.Where(d => d.LastOpenedAt >= day && d.LastOpenedAt < next).Select(d => new { d.Id, d.Title, d.LastOpenedAt }).ToList())
            Add(d.LastOpenedAt!.Value, Glyphs.Library, $"Read “{d.Title}” in the Library", "cyan", "Library", d.Id);

        // Games
        var games = db.Games.Select(g => new { g.Id, g.Title, g.FinishedAt }).ToList();
        var gameNames = games.ToDictionary(g => g.Id, g => g.Title);
        foreach (var g in db.PlaySessions.Where(s => s.Date >= day && s.Date < next).ToList().GroupBy(s => s.GameId))
            Add(g.Max(s => s.Date), Glyphs.Game, $"Played “{gameNames.GetValueOrDefault(g.Key, "a game")}” for {Duration(g.Sum(s => s.Minutes))}", "blue", "Games", g.Key);
        foreach (var g in games.Where(g => On(g.FinishedAt))) Add(g.FinishedAt!.Value, Glyphs.Completed, $"Completed “{g.Title}” 🏆", "blue", "Games", g.Id, hasTime: false);

        // Videos & watching
        foreach (var v in db.Videos.Where(v => v.WatchedAt >= day && v.WatchedAt < next).Select(v => new { v.Id, v.Title, v.WatchedAt }).ToList())
            Add(v.WatchedAt!.Value, Glyphs.Video, $"Watched “{v.Title}”", "red", "Videos", v.Id);
        var videoNames = db.Videos.Select(v => new { v.Id, v.Title }).ToList().ToDictionary(v => v.Id, v => v.Title);
        foreach (var g in db.VideoNotes.Where(n => n.CreatedAt >= day && n.CreatedAt < next).ToList().GroupBy(n => n.VideoId))
            Add(g.Max(n => n.CreatedAt), Glyphs.Clock, $"Took {Plural(g.Count(), "note")} on “{videoNames.GetValueOrDefault(g.Key, "a video")}”", "red", "Videos", g.Key);
        foreach (var w in db.WatchItems.Select(w => new { w.Id, w.Title, w.Kind, w.FinishedAt, w.EpisodesJson, w.LastWatchedAt }).ToList())
        {
            if (w.Kind == WatchKind.Movie && On(w.FinishedAt)) Add(w.FinishedAt!.Value, Glyphs.Monitor, $"Watched the movie “{w.Title}” 🎬", "rose", "Watching", w.Id, hasTime: false);
            if (w.Kind != WatchKind.Show || !On(w.LastWatchedAt)) continue;
            var eps = WatchItem.ParseEpisodes(w.EpisodesJson).Where(e => On(e.WatchedAt)).ToList();
            if (eps.Count == 1) Add(eps[0].WatchedAt!.Value, Glyphs.Monitor, $"Watched {eps[0].Code} of “{w.Title}”", "rose", "Watching", w.Id);
            else if (eps.Count > 1) Add(eps.Max(e => e.WatchedAt!.Value), Glyphs.Monitor, $"Watched {eps.Count} episodes of “{w.Title}”", "rose", "Watching", w.Id);
        }
        var listened = db.MusicTracks.Count(t => t.LastPlayedAt >= day && t.LastPlayedAt < next);
        if (listened > 0) Add(day, Glyphs.Music, $"Listened to {Plural(listened, "track")}", "teal", "Music", null, hasTime: false);

        // Boards, sketches, photos, bookmarks, contacts
        var boards = db.Boards.Select(b => new { b.Id, b.Name }).ToList().ToDictionary(b => b.Id, b => b.Name);
        foreach (var g in db.Stickies.Where(s => s.CreatedAt >= day && s.CreatedAt < next).Select(s => new { s.BoardId, s.CreatedAt }).ToList().GroupBy(s => s.BoardId))
            Add(g.Max(s => s.CreatedAt), Glyphs.Tiles, $"Added {Plural(g.Count(), "card")} to the “{boards.GetValueOrDefault(g.Key, "board")}” board", "orange", "Board", g.Key);
        foreach (var s in db.Sketches.Where(s => (s.CreatedAt >= day && s.CreatedAt < next) || (s.UpdatedAt >= day && s.UpdatedAt < next)).Select(s => new { s.Id, s.Title, s.CreatedAt, s.UpdatedAt }).ToList())
            Add(On(s.CreatedAt) ? s.CreatedAt : s.UpdatedAt, Glyphs.Pen, $"Sketched “{(string.IsNullOrWhiteSpace(s.Title) ? "untitled" : s.Title)}”", "lime", "Sketch", s.Id);
        var photos = db.Photos.Count(p => p.CreatedAt >= day && p.CreatedAt < next);
        if (photos > 0) Add(db.Photos.Where(p => p.CreatedAt >= day && p.CreatedAt < next).Max(p => p.CreatedAt), Glyphs.Photo, $"Added {Plural(photos, "photo")}", "cyan", "Photos");
        var marks = db.Bookmarks.Where(b => b.CreatedAt >= day && b.CreatedAt < next).Select(b => new { b.Title, b.CreatedAt }).ToList();
        if (marks.Count == 1) Add(marks[0].CreatedAt, Glyphs.Bookmark, $"Bookmarked “{marks[0].Title}”", "slate", "Bookmarks");
        else if (marks.Count > 1) Add(marks.Max(m => m.CreatedAt), Glyphs.Bookmark, $"Saved {marks.Count} bookmarks", "slate", "Bookmarks");
        foreach (var c in db.Contacts.Where(c => c.CreatedAt >= day && c.CreatedAt < next).ToList())
            Add(c.CreatedAt, Glyphs.AddPerson, $"Added {c.FullName} to contacts", "teal", "Contacts", c.Id);

        // Flashcards, money
        var reviews = db.ReviewLogs.Where(r => r.ReviewedAt >= day && r.ReviewedAt < next).ToList();
        if (reviews.Count > 0)
        {
            var good = reviews.Count(r => r.Grade >= 3);
            Add(reviews.Max(r => r.ReviewedAt), Glyphs.Education, $"Studied {Plural(reviews.Count, "flashcard")} ({Math.Round(100.0 * good / reviews.Count)}% right)", "green", "Flashcards");
        }
        var subscriptions = db.Subscriptions.Select(s => new { s.Id, s.Name }).ToList().ToDictionary(s => s.Id, s => s.Name);
        foreach (var p in db.Payments.Where(p => p.Date >= day && p.Date < next).ToList())
            Add(p.Date, Glyphs.Card, $"Paid {p.AmountDisplay} for {subscriptions.GetValueOrDefault(p.SubscriptionId, "a bill")}", "amber", "Money", p.SubscriptionId, hasTime: false);

        return list.OrderBy(e => e.HasTime ? 0 : 1).ThenBy(e => e.Time).ToList();
    }

    /// <summary>Plain-text summary for saving the day into the journal.</summary>
    public static List<string> ToLines(IEnumerable<DiaryEntry> entries) =>
        entries.Select(e => (e.HasTime ? Format.Time(e.Time) + "  " : "• ") + e.Text).ToList();

    private static string Snip(string text) => text.Length > 70 ? text[..70].TrimEnd() + "…" : text;
    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
    private static string Duration(int minutes) => minutes < 60 ? $"{minutes} min" : minutes % 60 == 0 ? $"{minutes / 60} h" : $"{minutes / 60} h {minutes % 60} min";
}
