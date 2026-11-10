using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Behaviors;
using Satchel.Controls;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Services;

namespace Satchel.ViewModels;

/// <summary>Free-form canvases of notes, images and links that can be connected (multiple boards).</summary>
public sealed partial class BoardViewModel : PageViewModel, IDisposable
{
    public const double CanvasWidth = 4000;
    public const double CanvasHeight = 2600;

    private readonly INavigator _nav;
    private readonly DataStore _store;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly SettingsService _settings;
    private readonly WebService _web;
    private readonly WeatherService _weather;
    private readonly MusicService _music;
    private readonly HashSet<StickyNote> _dirty = [];
    private readonly DispatcherTimer _saveTimer;
    private bool _loaded;
    private int _loadVersion;

    public BoardViewModel(INavigator nav, DataStore store, DialogService dialogs, ToastService toasts, SettingsService settings, WebService web,
        WeatherService weather, MusicService music)
    {
        _weather = weather;
        _music = music;
        _nav = nav;
        _store = store;
        _dialogs = dialogs;
        _toasts = toasts;
        _settings = settings;
        _web = web;
        _zoom = Math.Clamp(settings.Current.BoardZoom, 0.4, 2);
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += async (_, _) => await FlushAsync();
    }

    public override string Title => "Board";
    public ObservableCollection<Board> Boards { get; } = [];
    public ObservableCollection<StickyNote> Stickies { get; } = [];
    public ObservableCollection<BoardLink> Links { get; } = [];
    public double Width => CanvasWidth;
    public double Height => CanvasHeight;
    public IReadOnlyList<ColorOption> Colors => Palettes.Stickies;
    public IReadOnlyList<ColorOption> LinkColors => Palettes.Tags;

    [ObservableProperty] private Board? _selectedBoard;
    [ObservableProperty] private double _zoom;
    [ObservableProperty] private BoardLink? _selectedLink;

    public string ZoomText => $"{Math.Round(Zoom * 100)}%";
    public bool IsEmpty => Stickies.Count == 0;
    public bool IsCork => SelectedBoard?.IsCork == true;

    /// <summary>Where the view's viewport centre is (canvas coordinates) — used to drop new stickies in view.</summary>
    public Point ViewCenter { get; set; } = new(500, 300);

    public event Action<StickyNote>? StickyAdded;
    public event Action? BoardLoaded;

    partial void OnZoomChanged(double value)
    {
        _settings.Current.BoardZoom = value;
        OnPropertyChanged(nameof(ZoomText));
    }

    partial void OnSelectedBoardChanged(Board? oldValue, Board? newValue)
    {
        IsStudyOpen = false;
        if (oldValue != null) oldValue.PropertyChanged -= OnBoardPropertyChanged;
        if (newValue != null) newValue.PropertyChanged += OnBoardPropertyChanged;
        OnPropertyChanged(nameof(IsCork));
        SelectedLink = null;
        _ = LoadStickiesAsync();
    }

    private void OnBoardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Board.Style)) OnPropertyChanged(nameof(IsCork));
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_loaded) await LoadAsync();
        else if (_externalChange.Contains(SelectedBoard?.Id ?? -1))
        {
            _externalChange.Clear();
            await LoadStickiesAsync();
        }
        else await RefreshLiveAsync();
        if (parameter is "insert") await InsertFromSatchelCommand.ExecuteAsync(null);
        else if (parameter is int boardId && Boards.FirstOrDefault(b => b.Id == boardId) is { } b) SelectedBoard = b;
        else if (parameter is BoardStudyRequest study && Boards.FirstOrDefault(x => x.Id == study.BoardId) is { } sb)
        {
            if (!ReferenceEquals(SelectedBoard, sb)) { SelectedBoard = sb; await Task.Delay(400); }
            OpenStudy(study.VideoId, study.Title, study.Start);
        }
    }

    private readonly HashSet<int> _externalChange = [];

    /// <summary>Another page added cards to a board (e.g. a note taken in Videos): reload it next time it's shown.</summary>
    public void MarkChanged(int boardId) => _externalChange.Add(boardId);

    public override Task OnNavigatedFromAsync()
    {
        _liveTimer?.Stop();
        return base.OnNavigatedFromAsync();
    }

    private async Task LoadAsync()
    {
        var boards = await _store.ListAsync<Board>(q => q.OrderBy(b => b.SortOrder));
        if (boards.Count == 0)
        {
            var first = new Board { Name = "My board" };
            await _store.AddAsync(first);
            boards.Add(first);
        }
        Boards.Clear();
        foreach (var b in boards) Boards.Add(b);
        _loaded = true;
        SelectedBoard = Boards[0];
    }

    /// <summary>Reloads the board list (e.g. after another page created a board).</summary>
    public async Task ReloadBoardsAsync(int? select = null)
    {
        var boards = await _store.ListAsync<Board>(q => q.OrderBy(b => b.SortOrder));
        Boards.Clear();
        foreach (var b in boards) Boards.Add(b);
        SelectedBoard = Boards.FirstOrDefault(b => b.Id == select) ?? Boards.FirstOrDefault(b => b.Id == SelectedBoard?.Id) ?? Boards.FirstOrDefault();
    }

    private async Task LoadStickiesAsync()
    {
        int version = ++_loadVersion;
        await FlushAsync();
        foreach (var s in Stickies) s.PropertyChanged -= OnStickyChanged;
        Stickies.Clear();
        Links.Clear();
        if (SelectedBoard == null) return;
        var id = SelectedBoard.Id;
        var stickies = await _store.ListAsync<StickyNote>(q => q.Where(s => s.BoardId == id).OrderBy(s => s.ZIndex));
        var links = await _store.ListAsync<BoardLink>(q => q.Where(l => l.BoardId == id));
        if (version != _loadVersion) return;
        foreach (var s in stickies)
        {
            // Notes written before rich text: convert their plain text once.
            if (s.Kind == StickyKind.Text && s.Content.Length == 0 && s.Text.Length > 0)
            {
                s.Content = PlainToRich(s.Text);
                _dirty.Add(s);
            }
            s.PropertyChanged += OnStickyChanged;
            Stickies.Add(s);
        }
        var ids = stickies.Select(s => s.Id).ToHashSet();
        foreach (var l in links.Where(l => ids.Contains(l.FromId) && ids.Contains(l.ToId))) Links.Add(l);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasVideoCards));
        if (_dirty.Count > 0) ScheduleSave();
        BoardLoaded?.Invoke();
        await RefreshLiveAsync();
    }

    public static string PlainToRich(string text)
    {
        var doc = new FlowDocument();
        foreach (var block in text.Replace("\r\n", "\n").Split("\n\n"))
        {
            var p = new Paragraph();
            var lines = block.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) p.Inlines.Add(new LineBreak());
                p.Inlines.Add(new Run(lines[i]));
            }
            doc.Blocks.Add(p);
        }
        return RichTextEditor.SerializeDocument(doc).Content;
    }

    private void OnStickyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not StickyNote s || e.PropertyName is nameof(StickyNote.UpdatedAt) or nameof(StickyNote.IsText) or nameof(StickyNote.IsImage)
            or nameof(StickyNote.IsLink) or nameof(StickyNote.IsVideo) or nameof(StickyNote.VideoId) or nameof(StickyNote.Domain) or nameof(StickyNote.EffectiveFontSize)
            or nameof(StickyNote.Live) or nameof(StickyNote.IsLive) or nameof(StickyNote.TimeVideoId) or nameof(StickyNote.TimeSeconds) or nameof(StickyNote.HasTimeLink)
            or nameof(StickyNote.TimeText) or nameof(StickyNote.PinPoint)) return;
        _dirty.Add(s);
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public override async Task FlushAsync()
    {
        _saveTimer.Stop();
        if (_dirty.Count == 0) return;
        var batch = _dirty.Where(s => s.Id != 0).ToList();
        _dirty.Clear();
        foreach (var s in batch) s.UpdatedAt = DateTime.Now;
        await _store.UpdateRangeAsync(batch);
    }

    public override void OnNewShortcut() => _ = AddSticky(null);

    // ----- adding -----

    [RelayCommand]
    private Task AddSticky(string? color) => AddStickyAt(ViewCenter, color);

    public Task<StickyNote?> AddStickyAt(Point at, string? color = null) => AddAsync(at, StickyKind.Text, s => s.Color = color ?? NextColor());

    private string NextColor() => Palettes.Stickies[Stickies.Count % 6].Key;

    private async Task<StickyNote?> AddAsync(Point at, StickyKind kind, Action<StickyNote> init)
    {
        if (SelectedBoard == null) return null;
        var sticky = new StickyNote
        {
            BoardId = SelectedBoard.Id,
            Kind = kind,
            Rotation = Math.Round(Random.Shared.NextDouble() * (IsCork ? 6 : 4) - (IsCork ? 3 : 2), 1),
            ZIndex = Stickies.Count == 0 ? 1 : Stickies.Max(s => s.ZIndex) + 1
        };
        init(sticky);
        sticky.X = Math.Clamp(at.X - sticky.Width / 2, 0, CanvasWidth - sticky.Width);
        sticky.Y = Math.Clamp(at.Y - 40, 0, CanvasHeight - sticky.Height);
        await _store.AddAsync(sticky);
        sticky.PropertyChanged += OnStickyChanged;
        Stickies.Add(sticky);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasVideoCards));
        StickyAdded?.Invoke(sticky);
        return sticky;
    }

    public Task<StickyNote?> AddTextAt(Point at, string text) => AddAsync(at, StickyKind.Text, s =>
    {
        s.Color = NextColor();
        s.Text = text;
        s.Content = PlainToRich(text);
        if (text.Length > 160) { s.Width = 280; s.Height = 260; }
    });

    public Task<StickyNote?> AddImageAt(Point at, byte[] image, string caption = "") => AddAsync(at, StickyKind.Image, s =>
    {
        s.Image = image;
        s.Caption = caption;
        s.Color = "white";
        var size = ImageService.PixelSize(image) ?? new Size(4, 3);
        double imageWidth = size.Width >= size.Height ? 280 : 220;
        double imageHeight = Math.Clamp(imageWidth * size.Height / Math.Max(1, size.Width), 110, 380);
        s.Width = imageWidth + 20;
        s.Height = imageHeight + 50;
    });

    public async Task<StickyNote?> AddLinkAt(Point at, string url)
    {
        url = url.Trim();
        if (!url.Contains("://")) url = "https://" + url;
        bool video = YouTube.VideoId(url) != null;
        var sticky = await AddAsync(at, StickyKind.Link, s =>
        {
            s.Url = url;
            s.Color = "white";
            s.Caption = video ? "YouTube video" : (Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : url);
            s.Width = 300;
            s.Height = 252;
        });
        if (sticky != null) _ = FillLinkAsync(sticky);
        return sticky;
    }

    /// <summary>Fetches title and preview image for a link note (when online).</summary>
    private async Task FillLinkAsync(StickyNote s)
    {
        if (!_web.IsAvailable)
        {
            if (YouTube.VideoId(s.Url) == null) s.Height = 116;
            return;
        }
        if (YouTube.VideoId(s.Url) is { } id)
        {
            var json = await _web.GetStringAsync($"https://www.youtube.com/oembed?url={Uri.EscapeDataString(YouTube.WatchUrl(id))}&format=json");
            if (json != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("title", out var t)) s.Caption = t.GetString() ?? s.Caption;
                    if (doc.RootElement.TryGetProperty("author_name", out var a)) s.Subtitle = a.GetString() ?? "";
                }
                catch (JsonException) { }
            }
            s.Image = await _web.GetImageAsync(YouTube.ThumbnailUrl(id, "maxresdefault"), 640)
                      ?? await _web.GetImageAsync(YouTube.ThumbnailUrl(id, "mqdefault"), 640);
            return;
        }
        var preview = await _web.GetPreviewAsync(s.Url);
        if (preview != null)
        {
            if (preview.Title.Length > 0) s.Caption = preview.Title;
            s.Subtitle = preview.SiteName.Length > 0 ? preview.SiteName : s.Domain;
            if (preview.ImageUrl != null) s.Image = await _web.GetImageAsync(preview.ImageUrl, 640);
        }
        if (s.Image == null) s.Height = 116;
    }

    /// <summary>Paste or drop: images become photo notes, links become link cards, text becomes a note.</summary>
    public async Task<bool> AddFromDataAsync(IDataObject data, Point at)
    {
        var images = ImageService.FromDataObject(data, ImageService.PhotoSize);
        if (images.Count > 0)
        {
            for (int i = 0; i < images.Count; i++) await AddImageAt(at + new Vector(i * 28, i * 28), images[i]);
            return true;
        }
        var text = ReadText(data);
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (LooksLikeUrl(text)) await AddLinkAt(at, text);
        else await AddTextAt(at, text);
        return true;
    }

    private static string? ReadText(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string s) return s;
            if (data.GetDataPresent("UniformResourceLocatorW") && data.GetData("UniformResourceLocatorW") is MemoryStream ms)
                return System.Text.Encoding.Unicode.GetString(ms.ToArray()).TrimEnd('\0');
            if (data.GetDataPresent(DataFormats.Text) && data.GetData(DataFormats.Text) is string t) return t;
        }
        catch { /* unsupported clipboard format */ }
        return null;
    }

    public static bool LooksLikeUrl(string text) =>
        !text.Contains(' ') && !text.Contains('\n') &&
        (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
         text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) || YouTube.VideoId(text) != null);

    [RelayCommand]
    private async Task AddImage()
    {
        var image = ImageService.PickImage(ImageService.PhotoSize, photo: true);
        if (image != null) await AddImageAt(ViewCenter, image);
    }

    [RelayCommand]
    private async Task AddLink()
    {
        var url = await _dialogs.PromptAsync("Add a link", "Paste a web address. YouTube links show the video thumbnail and title.", "", "https://…", "Add");
        if (!string.IsNullOrWhiteSpace(url)) await AddLinkAt(ViewCenter, url);
    }

    [RelayCommand]
    private async Task PasteFromClipboard()
    {
        if (!await AddFromDataAsync(Clipboard.GetDataObject(), ViewCenter))
            _toasts.Info("Nothing to paste — copy an image, a link or some text first");
    }

    [RelayCommand]
    private async Task RefreshPreview(StickyNote? sticky)
    {
        if (sticky is not { IsLink: true }) return;
        if (!_web.IsAvailable) { _toasts.Info("You're offline (or online features are turned off in Settings)"); return; }
        if (sticky.Height < 200) sticky.Height = 252;
        await FillLinkAsync(sticky);
    }

    [RelayCommand]
    private void OpenLink(StickyNote? sticky)
    {
        if (sticky == null || string.IsNullOrWhiteSpace(sticky.Url)) return;
        if (sticky.VideoId is { } id && _settings.Current.VideoPlayInApp && (_nav as ShellViewModel)?.FindNav("Videos") != null)
            _nav.NavigateTo("Videos", new VideoRequest(id, sticky.Caption, sticky.Subtitle, YouTube.StartSeconds(sticky.Url)));
        else UiCommands.OpenUrl.Execute(sticky.Url);
    }

    // ----- editing -----

    [RelayCommand]
    private async Task DeleteSticky(StickyNote? sticky)
    {
        if (sticky == null) return;
        sticky.PropertyChanged -= OnStickyChanged;
        _dirty.Remove(sticky);
        var removedLinks = Links.Where(l => l.FromId == sticky.Id || l.ToId == sticky.Id).ToList();
        foreach (var l in removedLinks) Links.Remove(l);
        if (removedLinks.Contains(SelectedLink!)) SelectedLink = null;
        Stickies.Remove(sticky);
        await _store.DeleteAsync(sticky); // links go with it (cascade)
        OnPropertyChanged(nameof(IsEmpty));
        _toasts.Show(sticky.IsImage ? "Image removed" : sticky.IsLink ? "Link removed" : "Note removed", ToastKind.Info, "Undo", async () =>
        {
            int oldId = sticky.Id;
            sticky.Id = 0;
            await _store.AddAsync(sticky);
            sticky.PropertyChanged += OnStickyChanged;
            Stickies.Add(sticky);
            foreach (var l in removedLinks)
            {
                l.Id = 0;
                if (l.FromId == oldId) l.FromId = sticky.Id;
                if (l.ToId == oldId) l.ToId = sticky.Id;
            }
            await _store.AddRangeAsync(removedLinks);
            foreach (var l in removedLinks) Links.Add(l);
            OnPropertyChanged(nameof(IsEmpty));
        });
    }

    [RelayCommand]
    private async Task DuplicateSticky(StickyNote? sticky)
    {
        if (sticky == null || SelectedBoard == null) return;
        var copy = sticky.CloneFor(SelectedBoard.Id);
        copy.X = Math.Min(CanvasWidth - copy.Width, sticky.X + 28);
        copy.Y = Math.Min(CanvasHeight - copy.Height, sticky.Y + 28);
        copy.ZIndex = Stickies.Max(s => s.ZIndex) + 1;
        await _store.AddAsync(copy);
        copy.PropertyChanged += OnStickyChanged;
        Stickies.Add(copy);
        StickyAdded?.Invoke(copy);
    }

    public void SetColor(StickyNote sticky, string color) => sticky.Color = color;
    public void SetPin(StickyNote sticky, string pin) => sticky.Pin = pin;
    public void SetFontSize(StickyNote sticky, double size) => sticky.FontSize = size;

    public void BringToFront(StickyNote sticky)
    {
        int max = Stickies.Count == 0 ? 0 : Stickies.Max(s => s.ZIndex);
        if (sticky.ZIndex < max) sticky.ZIndex = max + 1;
    }

    // ----- connections -----

    public async Task ConnectAsync(StickyNote from, StickyNote to)
    {
        if (SelectedBoard == null || ReferenceEquals(from, to) || from.Id == 0 || to.Id == 0) return;
        if (Links.Any(l => (l.FromId == from.Id && l.ToId == to.Id) || (l.FromId == to.Id && l.ToId == from.Id)))
        {
            _toasts.Info("Those two are already connected");
            return;
        }
        var link = new BoardLink { BoardId = SelectedBoard.Id, FromId = from.Id, ToId = to.Id };
        await _store.AddAsync(link);
        Links.Add(link);
    }

    [RelayCommand]
    private async Task DeleteLink(BoardLink? link)
    {
        link ??= SelectedLink;
        if (link == null) return;
        Links.Remove(link);
        if (ReferenceEquals(SelectedLink, link)) SelectedLink = null;
        await _store.DeleteAsync(link);
        _toasts.Show("Connection removed", ToastKind.Info, "Undo", async () =>
        {
            if (!Stickies.Any(s => s.Id == link.FromId) || !Stickies.Any(s => s.Id == link.ToId)) return;
            link.Id = 0;
            await _store.AddAsync(link);
            Links.Add(link);
        });
    }

    public async Task SetLinkColorAsync(BoardLink link, string color)
    {
        link.Color = color;
        await _store.UpdateAsync(link);
    }

    [RelayCommand]
    private async Task ToggleLinkArrow(BoardLink? link)
    {
        link ??= SelectedLink;
        if (link == null) return;
        link.Arrow = !link.Arrow;
        await _store.UpdateAsync(link);
    }

    [RelayCommand]
    private async Task EditLinkLabel(BoardLink? link)
    {
        link ??= SelectedLink;
        if (link == null) return;
        var label = await _dialogs.PromptAsync("Connection label", "A word or two shown on the connection, e.g. “leads to” or “depends on”.", link.Label, "Label", "Save");
        if (label == null) return;
        link.Label = label.Trim();
        await _store.UpdateAsync(link);
    }

    // ----- board style -----

    [RelayCommand]
    private async Task ToggleCork()
    {
        if (SelectedBoard == null) return;
        SelectedBoard.Style = IsCork ? "" : "cork";
        await _store.UpdateAsync(SelectedBoard);
        _toasts.Show(IsCork ? "Corkboard on — drag from a pin to tie a string" : "Clean board — drag the dot on a note's edge to connect", ToastKind.Info, durationMs: 2600);
    }

    /// <summary>Lines all stickies up in a tidy grid with a glide animation.</summary>
    [RelayCommand]
    private void TidyUp()
    {
        if (Stickies.Count == 0) return;
        const double margin = 40, gap = 30;
        int perRow = Math.Max(1, (int)Math.Floor((Math.Max(900, ViewCenter.X * 2) - margin) / (260 + gap)));
        var ordered = Stickies.OrderBy(s => Math.Round(s.Y / 150)).ThenBy(s => s.X).ToList();
        var targets = new Dictionary<StickyNote, (double X, double Y, double R)>();
        double y = margin + (IsCork ? 10 : 0);
        for (int i = 0; i < ordered.Count; i += perRow)
        {
            var row = ordered.Skip(i).Take(perRow).ToList();
            double x = margin;
            foreach (var s in row)
            {
                targets[s] = (x, y, IsCork ? Math.Round(Random.Shared.NextDouble() * 4 - 2, 1) : 0);
                x += s.Width + gap;
            }
            y += row.Max(s => s.Height) + gap;
        }
        Tween(targets, 520);
    }

    private void Tween(Dictionary<StickyNote, (double X, double Y, double R)> targets, double ms)
    {
        var starts = targets.Keys.ToDictionary(s => s, s => (s.X, s.Y, s.Rotation));
        if (!Anim.Enabled)
        {
            foreach (var (s, t) in targets) { s.X = t.X; s.Y = t.Y; s.Rotation = t.R; }
            return;
        }
        var began = DateTime.Now;
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            double p = Math.Min(1, (DateTime.Now - began).TotalMilliseconds / ms);
            double e = 1 - Math.Pow(1 - p, 4);
            foreach (var (s, t) in targets)
            {
                var (sx, sy, sr) = starts[s];
                s.X = sx + (t.X - sx) * e;
                s.Y = sy + (t.Y - sy) * e;
                s.Rotation = sr + (t.R - sr) * e;
            }
            if (p >= 1) timer.Stop();
        };
        timer.Start();
    }

    [RelayCommand]
    private void ZoomIn() => Zoom = Math.Min(2, Math.Round(Zoom + 0.1, 2));

    [RelayCommand]
    private void ZoomOut() => Zoom = Math.Max(0.4, Math.Round(Zoom - 0.1, 2));

    [RelayCommand]
    private void ResetZoom() => Zoom = 1;

    [RelayCommand]
    private void SelectBoard(Board? board)
    {
        if (board != null) SelectedBoard = board;
    }

    [RelayCommand]
    private async Task NewBoard()
    {
        var name = await _dialogs.PromptAsync("New board", "Boards are free canvases for notes, images and links — brainstorms, plans, mood boards, investigations.", "", "e.g. Trip ideas", "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        var board = new Board { Name = name, SortOrder = Boards.Count };
        await _store.AddAsync(board);
        Boards.Add(board);
        SelectedBoard = board;
    }

    [RelayCommand]
    private async Task RenameBoard()
    {
        if (SelectedBoard == null) return;
        var name = await _dialogs.PromptAsync("Rename board", null, SelectedBoard.Name, "Board name", "Rename");
        if (string.IsNullOrWhiteSpace(name)) return;
        SelectedBoard.Name = name;
        await _store.UpdateAsync(SelectedBoard);
    }

    [RelayCommand]
    private async Task DeleteBoard()
    {
        if (SelectedBoard == null) return;
        if (!await _dialogs.ConfirmAsync("Delete board?", $"“{SelectedBoard.Name}” and its {Stickies.Count} item(s) will be deleted.", "Delete", danger: true)) return;
        var board = SelectedBoard;
        await _store.DeleteAsync(board);
        Boards.Remove(board);
        if (Boards.Count == 0)
        {
            var fresh = new Board { Name = "My board" };
            await _store.AddAsync(fresh);
            Boards.Add(fresh);
        }
        SelectedBoard = Boards[0];
    }

    public void Dispose()
    {
        _saveTimer.Stop();
        _liveTimer?.Stop();
        foreach (var s in Stickies) s.PropertyChanged -= OnStickyChanged;
        if (SelectedBoard != null) SelectedBoard.PropertyChanged -= OnBoardPropertyChanged;
    }
}

/// <summary>Request to open a YouTube video in the Videos page (from boards, search, projects…).</summary>
public sealed record VideoRequest(string VideoId, string Title = "", string Author = "", double Start = 0);

/// <summary>Open a board with its study panel playing a video.</summary>
public sealed record BoardStudyRequest(int BoardId, string VideoId, string Title, double Start = 0);
