using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Satchel.Controls;
using Satchel.Core;
using Satchel.Models;
using Satchel.Services;
using Satchel.ViewModels.Dialogs;

namespace Satchel.ViewModels;

/// <summary>
/// Board extras: "Add from Satchel" (notes, photos, videos, games, books, shows, bookmarks, projects, task lists)
/// and the large editor for a note.
/// </summary>
public sealed partial class BoardViewModel
{
    /// <summary>Picks things from the other tabs and puts them on the board.</summary>
    [RelayCommand]
    private async Task InsertFromSatchel(string? source)
    {
        if (SelectedBoard == null) return;
        if (await _dialogs.ShowAsync(new BoardInsertDialogViewModel(_store, source)) is not List<InsertItem> picked || picked.Count == 0) return;
        int added = await InsertItemsAsync(picked);
        if (added > 0) _toasts.Success(added == 1 ? "Added to the board" : $"{added} things added to the board");
        else _toasts.Warning("Those items couldn't be found any more");
    }

    /// <summary>Adds the items around the middle of the view, a few per row. Returns how many were added.</summary>
    public async Task<int> InsertItemsAsync(IReadOnlyList<InsertItem> items)
    {
        if (SelectedBoard == null || items.Count == 0) return 0;
        int columns = Math.Min(4, items.Count), added = 0;
        var center = ViewCenter;
        var area = Rect.Empty;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var at = new Point(center.X + (i % columns - (columns - 1) / 2.0) * 310, center.Y - 60 + i / columns * 300);
            StickyNote? sticky;
            try
            {
                sticky = item.Source switch
                {
                    "note" => await InsertNoteAsync(item.Id, at),
                    "photo" => await InsertPhotoAsync(item.Id, at),
                    "video" => await InsertVideoAsync(item.Id, at),
                    "bookmark" => await InsertBookmarkAsync(item.Id, at),
                    "game" => await AddLiveAsync(at, "game", $"{item.Id}", 300, 150),
                    "book" => await AddLiveAsync(at, "book", $"{item.Id}", 300, 150),
                    "show" => await AddLiveAsync(at, "show", $"{item.Id}", 300, 150),
                    "project" => await AddLiveAsync(at, "project", $"{item.Id}", 280, 170),
                    "tasks" => await AddLiveAsync(at, "tasks", item.Id == 0 ? "today" : $"list:{item.Id}", 260, 300),
                    _ => null
                };
            }
            catch
            {
                sticky = null; // one bad item must not stop the rest
            }
            if (sticky == null) continue;
            added++;
            // Never on top of other cards: the nearest free spot.
            var taken = Stickies.Where(s => !ReferenceEquals(s, sticky)).Select(s => new Rect(s.X, s.Y, s.Width, s.Height)).ToList();
            var spot = FreeSpot(new Point(sticky.X, sticky.Y), new Size(sticky.Width, sticky.Height), taken);
            sticky.X = spot.X;
            sticky.Y = spot.Y;
            // A copy of something private stays private.
            if (item.IsPrivate && !sticky.IsPrivate)
            {
                sticky.IsPrivate = true;
                await _store.UpdateAsync(sticky);
            }
            area = area.IsEmpty ? new Rect(sticky.X, sticky.Y, sticky.Width, sticky.Height) : Rect.Union(area, new Rect(sticky.X, sticky.Y, sticky.Width, sticky.Height));
        }
        if (items.Any(i => i.Source is "game" or "book" or "show" or "project" or "tasks")) await RefreshLiveAsync();
        if (!area.IsEmpty) RevealRequested?.Invoke(area);
        return added;
    }

    /// <summary>The board view scrolls so this area (canvas coordinates) is in sight.</summary>
    public event Action<Rect>? RevealRequested;

    /// <summary>The spot nearest to <paramref name="wanted"/> (a top-left corner) where a card of this size covers no other card.</summary>
    public static Point FreeSpot(Point wanted, Size size, IReadOnlyList<Rect> taken)
    {
        const double gap = 24, step = 60;
        Rect At(Point p) => new(Math.Clamp(p.X, 0, CanvasWidth - size.Width), Math.Clamp(p.Y, 0, CanvasHeight - size.Height), size.Width, size.Height);
        bool Free(Rect r) => taken.All(t => !new Rect(t.X - gap, t.Y - gap, t.Width + gap * 2, t.Height + gap * 2).IntersectsWith(r));
        for (int ring = 0; ring <= 30; ring++)
        {
            var candidates = new List<Point>();
            for (int dx = -ring; dx <= ring; dx++)
                for (int dy = -ring; dy <= ring; dy++)
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == ring) candidates.Add(new Point(wanted.X + dx * step, wanted.Y + dy * step));
            foreach (var c in candidates.OrderBy(c => (c - wanted).Length))
                if (At(c) is var r && Free(r)) return r.TopLeft;
        }
        return wanted;
    }

    private Task<StickyNote?> AddLiveAsync(Point at, string kind, string reference, int width, int height) => AddAsync(at, StickyKind.Live, s =>
    {
        s.LiveKind = kind;
        s.LiveRef = reference;
        s.Color = "white";
        s.Width = width;
        s.Height = height;
    });

    private async Task<StickyNote?> InsertNoteAsync(int id, Point at)
    {
        var note = await _store.FindAsync<Note>(id);
        if (note == null) return null;
        var (content, plain) = NoteToRich(note);
        var (width, height) = SizeFor(plain);
        return await AddAsync(at, StickyKind.Text, s =>
        {
            s.Color = StickyColorFor(note.Color);
            s.Content = content;
            s.Text = plain;
            s.Width = width;
            s.Height = height;
        });
    }

    private async Task<StickyNote?> InsertPhotoAsync(int id, Point at)
    {
        var photo = await _store.FindAsync<Photo>(id);
        if (photo?.Data is not { } data) return null;
        var image = ImageService.Resize(data, ImageService.PhotoSize, photo: true) ?? data;
        return await AddImageAt(at, image, photo.Caption);
    }

    private async Task<StickyNote?> InsertVideoAsync(int id, Point at)
    {
        var video = await _store.FindAsync<Video>(id);
        if (video == null || video.YoutubeId.Length == 0) return null;
        return await AddAsync(at, StickyKind.Link, s =>
        {
            s.Url = YouTube.WatchUrl(video.YoutubeId);
            s.Caption = video.Title.Length > 0 ? video.Title : "YouTube video";
            s.Subtitle = video.Author;
            s.Image = video.Thumbnail;
            s.Color = "white";
            s.Width = 300;
            s.Height = video.Thumbnail != null ? 252 : 116;
        });
    }

    private async Task<StickyNote?> InsertBookmarkAsync(int id, Point at)
    {
        var bookmark = await _store.FindAsync<Bookmark>(id);
        if (bookmark == null || bookmark.Url.Length == 0) return null;
        bool video = YouTube.VideoId(bookmark.Url) != null;
        var sticky = await AddAsync(at, StickyKind.Link, s =>
        {
            s.Url = bookmark.Url;
            s.Caption = bookmark.DisplayTitle;
            s.Subtitle = bookmark.Domain;
            s.Image = bookmark.Image;
            s.Color = "white";
            s.Width = 300;
            s.Height = bookmark.Image != null || video ? 252 : 116;
        });
        // No preview picture saved with the bookmark: fetch one like for a pasted link (when online).
        if (sticky != null && bookmark.Image == null && (_web.IsAvailable || video)) _ = FillLinkAsync(sticky);
        return sticky;
    }

    /// <summary>The board colour closest to a note's colour.</summary>
    public static string StickyColorFor(string? noteColor) => noteColor switch
    {
        "coral" or "rose" => "pink",
        "peach" => "orange",
        "mint" or "sage" => "green",
        "sky" or "ocean" => "blue",
        "slate" => "gray",
        _ => "yellow"
    };

    /// <summary>A note as board-note rich text: its title as a heading, then the body. Checklists keep their boxes (ticked ones stay ticked).</summary>
    public static (string Content, string Plain) NoteToRich(Note note)
    {
        var doc = new FlowDocument();
        var title = note.Type == NoteType.Journal && string.IsNullOrWhiteSpace(note.Title) ? note.DisplayTitle : note.Title.Trim();
        if (title.Length > 0)
            doc.Blocks.Add(new Paragraph(new Run(title)) { FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        switch (note.Type)
        {
            case NoteType.Checklist:
                foreach (var item in note.Items) doc.Blocks.Add(RichTextEditor.CheckItem(item.Text, item.IsChecked, item.Indent));
                break;
            case NoteType.List:
            {
                var list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(24, 0, 0, 0) };
                foreach (var item in note.Items)
                    list.ListItems.Add(new ListItem(new Paragraph(new Run(item.Text)) { Margin = new Thickness(item.Indent * 16, 0, 0, 2) }));
                if (list.ListItems.Count > 0) doc.Blocks.Add(list);
                break;
            }
            case NoteType.Code:
            {
                var code = new Paragraph
                {
                    FontFamily = Application.Current?.TryFindResource("MonoFont") as FontFamily ?? new FontFamily("Consolas"),
                    FontSize = 13,
                    Background = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80))
                };
                var lines = note.Content.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i > 0) code.Inlines.Add(new LineBreak());
                    code.Inlines.Add(new Run(lines[i]));
                }
                doc.Blocks.Add(code);
                break;
            }
            default:
            {
                var body = RichTextEditor.LoadDocument(note.Content);
                if (body.Blocks.Count == 0 && note.PlainText.Length > 0) body = RichTextEditor.LoadDocument(PlainToRich(note.PlainText));
                foreach (var block in body.Blocks.ToList())
                {
                    body.Blocks.Remove(block);
                    doc.Blocks.Add(block);
                }
                break;
            }
        }
        if (doc.Blocks.Count == 0) doc.Blocks.Add(new Paragraph());
        return RichTextEditor.SerializeDocument(doc);
    }

    /// <summary>A size that fits the text roughly (you can always resize).</summary>
    public static (double Width, double Height) SizeFor(string plain)
    {
        var lines = plain.Replace("\r\n", "\n").Split('\n');
        double width = plain.Length > 420 ? 340 : plain.Length > 140 ? 290 : 250;
        int perLine = Math.Max(12, (int)((width - 26) / 7.4));
        int rows = lines.Sum(l => Math.Max(1, (int)Math.Ceiling(l.Length / (double)perLine)));
        return (width, Math.Clamp(28 + 18 + rows * 21.5, 150, 560));
    }

    /// <summary>A board note in a large editor with the full Notes toolbar.</summary>
    [RelayCommand]
    private async Task OpenStickyEditor(StickyNote? sticky)
    {
        if (sticky is not { IsText: true }) return;
        BringToFront(sticky);
        await _dialogs.ShowAsync(new StickyEditorDialogViewModel(sticky));
    }
}
