using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using System.Xml.Linq;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;

namespace Satchel.Services;

/// <summary>
/// Follows YouTube channels through their public RSS feeds (no account, no API key) and imports new uploads.
/// Runs on a timer while the vault is unlocked and online features are enabled.
/// </summary>
public sealed partial class VideoFeedService
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Yt = "http://www.youtube.com/xml/schemas/2015";
    private static readonly XNamespace Media = "http://search.yahoo.com/mrss/";

    private readonly DataStore _store;
    private readonly WebService _web;
    private readonly SettingsService _settings;
    private readonly ToastService _toasts;
    private DispatcherTimer? _timer;
    private bool _busy;

    public VideoFeedService(DataStore store, WebService web, SettingsService settings, ToastService toasts)
    {
        _store = store;
        _web = web;
        _settings = settings;
        _toasts = toasts;
    }

    /// <summary>Number of unseen videos changed (for the sidebar badge).</summary>
    public event Action<int>? UnseenChanged;
    /// <summary>Feeds were refreshed (the Videos page reloads).</summary>
    public event Action? Updated;
    /// <summary>The user chose "Show" on a new-videos notification.</summary>
    public event Action? OpenRequested;

    public bool IsChecking => _busy;
    public DateTime? LastRun { get; private set; }

    public void Start()
    {
        Stop();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(Math.Max(15, _settings.Current.VideoCheckMinutes)) };
        _timer.Tick += async (_, _) => await RefreshAllAsync(notify: true);
        _timer.Start();
        // First check shortly after unlocking, so startup stays snappy.
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        first.Tick += async (_, _) =>
        {
            first.Stop();
            await PublishUnseenAsync();
            await RefreshAllAsync(notify: true);
        };
        first.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    public async Task PublishUnseenAsync()
    {
        try
        {
            var count = await _store.CountAsync<Video>(q => q.Where(v => v.State == VideoState.New && v.ChannelId != null && (!_settings.Current.VideoHideShorts || !v.IsShort)));
            UnseenChanged?.Invoke(count);
        }
        catch { /* locked meanwhile */ }
    }

    /// <summary>Checks every followed channel. Returns how many new videos were found.</summary>
    public async Task<int> RefreshAllAsync(bool notify)
    {
        if (_busy || !_web.IsAvailable || !Security.Crypto.IsUnlocked) return 0;
        _busy = true;
        int added = 0;
        try
        {
            var channels = await _store.ListAsync<VideoChannel>(q => q.OrderBy(c => c.SortOrder));
            if (channels.Count == 0) return 0;
            var known = (await _store.RunAsync(db => db.Videos.Select(v => v.YoutubeId).ToList())).ToHashSet();
            var newOnes = new List<(VideoChannel Channel, int Count)>();
            foreach (var ch in channels)
            {
                int n = await RefreshChannelAsync(ch, known, firstImport: false);
                if (n > 0) newOnes.Add((ch, n));
                added += n;
            }
            LastRun = DateTime.Now;
            if (added > 0 && notify && _settings.Current.VideoNotify)
            {
                var notifying = newOnes.Where(x => x.Channel.Notify).ToList();
                if (notifying.Count > 0)
                {
                    var names = string.Join(", ", notifying.Take(3).Select(x => x.Channel.Title));
                    var total = notifying.Sum(x => x.Count);
                    _toasts.Show($"{names}{(notifying.Count > 3 ? " and more" : "")}", ToastKind.Info, "Show", () => OpenRequested?.Invoke(), 6000,
                        title: total == 1 ? "1 new video" : $"{total} new videos");
                }
            }
        }
        catch (Exception ex)
        {
            App.Log("Video feed refresh", ex);
        }
        finally
        {
            _busy = false;
            Updated?.Invoke();
            await PublishUnseenAsync();
        }
        return added;
    }

    /// <summary>Imports a channel's latest uploads. On the first import only very recent videos are marked new.</summary>
    public async Task<int> RefreshChannelAsync(VideoChannel channel, HashSet<string> known, bool firstImport)
    {
        var xml = await _web.GetStringAsync(YouTube.FeedUrl(channel.YoutubeId));
        if (xml == null)
        {
            channel.LastError = "Couldn't reach YouTube";
            await _store.UpdateAsync(channel);
            return 0;
        }
        List<FeedEntry> entries;
        try { entries = ParseFeed(xml, out _); }
        catch (Exception)
        {
            channel.LastError = "Unreadable feed";
            await _store.UpdateAsync(channel);
            return 0;
        }

        var fresh = new List<Video>();
        foreach (var e in entries.Where(e => known.Add(e.VideoId)))
        {
            bool isNew = !firstImport || (e.Published.HasValue && e.Published.Value > DateTime.Now.AddDays(-3));
            fresh.Add(new Video
            {
                YoutubeId = e.VideoId, ChannelId = channel.Id, Title = e.Title, Author = channel.Title, PublishedAt = e.Published,
                IsShort = e.IsShort, Views = e.Views, State = isNew ? VideoState.New : VideoState.Seen
            });
        }
        foreach (var v in fresh) v.Thumbnail = await _web.GetImageAsync(YouTube.ThumbnailUrl(v.YoutubeId), 360);
        if (fresh.Count > 0) await _store.AddRangeAsync(fresh);
        channel.LastCheckedAt = DateTime.Now;
        channel.LastError = "";
        await _store.UpdateAsync(channel);
        return fresh.Count(v => v.State == VideoState.New);
    }

    private sealed record FeedEntry(string VideoId, string Title, DateTime? Published, bool IsShort, long Views);

    private static List<FeedEntry> ParseFeed(string xml, out string title)
    {
        var doc = XDocument.Parse(xml);
        title = doc.Root?.Element(Atom + "author")?.Element(Atom + "name")?.Value ?? doc.Root?.Element(Atom + "title")?.Value ?? "";
        var list = new List<FeedEntry>();
        foreach (var e in doc.Root?.Elements(Atom + "entry") ?? [])
        {
            var id = e.Element(Yt + "videoId")?.Value;
            if (string.IsNullOrEmpty(id)) continue;
            DateTime? published = DateTime.TryParse(e.Element(Atom + "published")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var p)
                ? p.ToLocalTime() : null;
            var link = e.Element(Atom + "link")?.Attribute("href")?.Value ?? "";
            long.TryParse(e.Element(Media + "group")?.Element(Media + "community")?.Element(Media + "statistics")?.Attribute("views")?.Value, out var views);
            list.Add(new FeedEntry(id, e.Element(Atom + "title")?.Value ?? "", published, link.Contains("/shorts/", StringComparison.OrdinalIgnoreCase), views));
        }
        return list;
    }

    [GeneratedRegex(@"""(?:externalId|channelId|browseId)""\s*:\s*""(UC[A-Za-z0-9_-]{22})""")]
    private static partial Regex ChannelIdInPage();

    [GeneratedRegex(@"<link rel=""canonical"" href=""https://www\.youtube\.com/channel/(UC[A-Za-z0-9_-]{22})""")]
    private static partial Regex CanonicalChannel();

    [GeneratedRegex(@"<meta property=""og:image"" content=""([^""]+)""")]
    private static partial Regex OgImage();

    [GeneratedRegex(@"""canonicalBaseUrl""\s*:\s*""/(@[^""]+)""")]
    private static partial Regex HandleInPage();

    /// <summary>Resolves a channel link, @handle, channel id or a video link into a channel (with title and avatar).</summary>
    public async Task<(VideoChannel? Channel, string? Error)> ResolveAsync(string input)
    {
        if (!_web.IsAvailable) return (null, "You're offline (or online features are turned off in Settings).");
        input = input.Trim();
        string? id = YouTube.ChannelId(input);
        string? html = null;
        string? pageUrl = id != null ? $"https://www.youtube.com/channel/{id}" : null;

        if (id == null && YouTube.VideoId(input) is { } videoId)
        {
            var json = await _web.GetStringAsync($"https://www.youtube.com/oembed?url={Uri.EscapeDataString(YouTube.WatchUrl(videoId))}&format=json");
            if (json != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("author_url", out var a)) pageUrl = a.GetString();
                }
                catch (JsonException) { }
            }
        }
        pageUrl ??= YouTube.ChannelPageUrl(input);
        if (pageUrl == null) return (null, "That doesn't look like a YouTube channel. Paste the channel's link or its @handle.");

        html = await _web.GetStringAsync(pageUrl);
        if (id == null && html != null)
        {
            var m = CanonicalChannel().Match(html);
            if (!m.Success) m = ChannelIdInPage().Match(html);
            if (m.Success) id = m.Groups[1].Value;
        }
        if (id == null) return (null, "Couldn't find that channel on YouTube.");

        var feed = await _web.GetStringAsync(YouTube.FeedUrl(id));
        if (feed == null) return (null, "YouTube didn't return this channel's feed — try again in a moment.");
        string title;
        try { ParseFeed(feed, out title); }
        catch { return (null, "The channel's feed couldn't be read."); }

        byte[]? avatar = null;
        string handle = "";
        if (html != null)
        {
            var og = OgImage().Match(html);
            if (og.Success) avatar = await _web.GetImageAsync(WebUtility.HtmlDecode(og.Groups[1].Value), 96);
            var h = HandleInPage().Match(html);
            if (h.Success) handle = h.Groups[1].Value;
        }
        return (new VideoChannel { YoutubeId = id, Title = WebUtility.HtmlDecode(title), Handle = handle, Avatar = avatar }, null);
    }

    /// <summary>Title, channel and thumbnail of a single video (oEmbed).</summary>
    public async Task<Video?> FetchVideoAsync(string videoId)
    {
        var video = new Video { YoutubeId = videoId, Title = "YouTube video", State = VideoState.Seen };
        if (_web.IsAvailable)
        {
            var json = await _web.GetStringAsync($"https://www.youtube.com/oembed?url={Uri.EscapeDataString(YouTube.WatchUrl(videoId))}&format=json");
            if (json != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("title", out var t)) video.Title = t.GetString() ?? video.Title;
                    if (doc.RootElement.TryGetProperty("author_name", out var a)) video.Author = a.GetString() ?? "";
                }
                catch (JsonException) { }
            }
            video.Thumbnail = await _web.GetImageAsync(YouTube.ThumbnailUrl(videoId), 360);
        }
        return video;
    }
}
