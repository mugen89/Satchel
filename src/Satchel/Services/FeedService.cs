using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Linq;
using Satchel.Data;
using Satchel.Models;

namespace Satchel.Services;

public sealed record ParsedFeedItem(string Guid, string Title, string Link, string Summary, string Content, string Author, DateTime? Published,
    string EnclosureUrl, string EnclosureType, double Duration);

public sealed record ParsedFeed(string Title, string Link, string Description, string? ImageUrl, bool IsPodcast, List<ParsedFeedItem> Items);

/// <summary>RSS / Atom feeds and podcasts: subscribe by page or feed link, background refresh while unlocked, unread badge.</summary>
public sealed partial class FeedService
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Content = "http://purl.org/rss/1.0/modules/content/";
    private static readonly XNamespace Itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Media = "http://search.yahoo.com/mrss/";

    private readonly DataStore _store;
    private readonly WebService _web;
    private readonly SettingsService _settings;
    private readonly ToastService _toasts;
    private DispatcherTimer? _timer;
    private bool _busy;

    public FeedService(DataStore store, WebService web, SettingsService settings, ToastService toasts)
    {
        _store = store;
        _web = web;
        _settings = settings;
        _toasts = toasts;
    }

    public event Action<int>? UnreadChanged;
    public event Action? Updated;
    public event Action? OpenRequested;
    public bool IsChecking => _busy;

    public void Start()
    {
        Stop();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(Math.Max(15, _settings.Current.FeedCheckMinutes)) };
        _timer.Tick += async (_, _) => await RefreshAllAsync(notify: true);
        _timer.Start();
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(14) };
        first.Tick += async (_, _) =>
        {
            first.Stop();
            await PublishUnreadAsync();
            await RefreshAllAsync(notify: true);
        };
        first.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
        _timer = null;
    }

    public async Task PublishUnreadAsync()
    {
        try
        {
            var count = await _store.CountAsync<FeedItem>(q => q.Where(i => !i.IsRead));
            UnreadChanged?.Invoke(count);
        }
        catch { /* locked meanwhile */ }
    }

    // ----- subscribing -----

    /// <summary>Finds and reads the feed behind a link (a feed URL or a web page that advertises one).</summary>
    public async Task<(Feed? Feed, ParsedFeed? Parsed, string? Error)> ResolveAsync(string input)
    {
        if (!_web.IsAvailable) return (null, null, _web.Enabled ? "You're offline." : "Online features are turned off in Settings.");
        var url = input.Trim();
        if (!url.Contains("://")) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return (null, null, "That doesn't look like a web address.");
        var text = await _web.GetStringAsync(uri.ToString());
        if (text == null) return (null, null, "Couldn't open that address.");
        var parsed = Parse(text);
        if (parsed == null)
        {
            // A web page: look for <link rel="alternate" type="application/rss+xml" href="…">.
            foreach (Match m in FeedLinkRegex().Matches(text))
            {
                var tag = m.Value;
                var href = HrefRegex().Match(tag);
                if (!href.Success) continue;
                if (!Uri.TryCreate(uri, WebUtility.HtmlDecode(href.Groups[1].Value), out var feedUri)) continue;
                var feedText = await _web.GetStringAsync(feedUri.ToString());
                if (feedText != null && Parse(feedText) is { } p)
                {
                    parsed = p;
                    uri = feedUri;
                    break;
                }
            }
        }
        if (parsed == null) return (null, null, "No RSS, Atom or podcast feed found there.");
        var feed = new Feed
        {
            Url = uri.ToString(),
            Title = string.IsNullOrWhiteSpace(parsed.Title) ? uri.Host : parsed.Title,
            SiteUrl = parsed.Link,
            Description = Truncate(StripHtml(parsed.Description), 400),
            Kind = parsed.IsPodcast ? FeedKind.Podcast : FeedKind.Articles,
            Color = parsed.IsPodcast ? "teal" : "orange"
        };
        if (parsed.ImageUrl != null) feed.Image = await _web.GetImageAsync(parsed.ImageUrl, 256);
        return (feed, parsed, null);
    }

    // ----- refreshing -----

    public async Task<int> RefreshAllAsync(bool notify)
    {
        if (_busy || !_web.IsAvailable || !Security.Crypto.IsUnlocked) return 0;
        _busy = true;
        int added = 0;
        var notifyFeeds = new List<string>();
        try
        {
            var feeds = await _store.ListAsync<Feed>(q => q.OrderBy(f => f.SortOrder));
            if (feeds.Count == 0) return 0;
            var known = (await _store.RunAsync(db => db.FeedItems.Select(i => new { i.FeedId, i.Guid }).ToList()))
                .GroupBy(x => x.FeedId).ToDictionary(g => g.Key, g => g.Select(x => x.Guid).ToHashSet());
            foreach (var feed in feeds)
            {
                var n = await RefreshAsync(feed, known.GetValueOrDefault(feed.Id) ?? [], firstImport: false);
                added += n;
                if (n > 0 && feed.Notify) notifyFeeds.Add(feed.Title);
            }
        }
        catch
        {
            // best effort
        }
        finally
        {
            _busy = false;
        }
        Updated?.Invoke();
        await PublishUnreadAsync();
        if (notify && notifyFeeds.Count > 0 && _settings.Current.FeedNotify)
        {
            var count = added;
            _toasts.Show(notifyFeeds.Count == 1 ? $"{count} new from {notifyFeeds[0]}" : $"{count} new posts and episodes from {notifyFeeds.Count} feeds",
                ToastKind.Info, "Show", () => OpenRequested?.Invoke(), durationMs: 8000, title: "📰 Feeds");
        }
        return added;
    }

    /// <summary>Reads one feed and stores items not seen before. The first import keeps only the latest few unread.</summary>
    public async Task<int> RefreshAsync(Feed feed, HashSet<string> known, bool firstImport, ParsedFeed? already = null)
    {
        var parsed = already;
        if (parsed == null)
        {
            var text = await _web.GetStringAsync(feed.Url);
            parsed = text == null ? null : Parse(text);
        }
        feed.LastCheckedAt = DateTime.Now;
        if (parsed == null)
        {
            feed.LastError = "Couldn't read the feed";
            await _store.UpdateAsync(feed);
            return 0;
        }
        feed.LastError = "";
        var fresh = parsed.Items.Where(i => i.Guid.Length > 0 && !known.Contains(i.Guid)).Take(firstImport ? 40 : 100).ToList();
        int i = 0;
        var items = fresh.Select(p => new FeedItem
        {
            FeedId = feed.Id, Guid = p.Guid, Title = p.Title, Link = p.Link, Summary = p.Summary, Content = p.Content, Author = p.Author,
            PublishedAt = p.Published ?? DateTime.Now, EnclosureUrl = p.EnclosureUrl, EnclosureType = p.EnclosureType, Duration = p.Duration,
            // On the first import only the newest few are "new"; the rest are already read.
            IsRead = firstImport && i++ >= 5
        }).ToList();
        if (items.Count > 0) await _store.AddRangeAsync(items);
        await _store.UpdateAsync(feed);
        foreach (var it in items) known.Add(it.Guid);
        return items.Count(x => !x.IsRead);
    }

    // ----- parsing -----

    public static ParsedFeed? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (!trimmed.StartsWith('<')) return null;
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(new StringReader(trimmed), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            doc = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
        var root = doc.Root;
        if (root == null) return null;
        return root.Name.LocalName switch
        {
            "rss" => ParseRss(root),
            "feed" => ParseAtom(root),
            "RDF" => ParseRdf(root),
            _ => null
        };
    }

    private static ParsedFeed? ParseRss(XElement root)
    {
        var channel = root.Element("channel");
        if (channel == null) return null;
        bool podcast = channel.Element(Itunes + "author") != null || channel.Element(Itunes + "image") != null;
        var image = channel.Element("image")?.Element("url")?.Value ?? channel.Element(Itunes + "image")?.Attribute("href")?.Value;
        var items = new List<ParsedFeedItem>();
        foreach (var item in channel.Elements("item"))
        {
            var link = item.Element("link")?.Value.Trim() ?? "";
            var guid = item.Element("guid")?.Value.Trim();
            var enclosure = item.Element("enclosure");
            var encUrl = enclosure?.Attribute("url")?.Value ?? "";
            var encType = enclosure?.Attribute("type")?.Value ?? "";
            var html = item.Element(Content + "encoded")?.Value ?? item.Element("description")?.Value ?? "";
            var summarySource = item.Element(Itunes + "summary")?.Value ?? item.Element("description")?.Value ?? html;
            if (encType.StartsWith("audio", StringComparison.OrdinalIgnoreCase)) podcast = true;
            items.Add(new ParsedFeedItem(
                string.IsNullOrEmpty(guid) ? (link.Length > 0 ? link : item.Element("title")?.Value ?? "") : guid,
                WebUtility.HtmlDecode(item.Element("title")?.Value.Trim() ?? "Untitled"),
                link.Length > 0 ? link : (guid?.StartsWith("http") == true ? guid : ""),
                Truncate(StripHtml(summarySource), 500),
                Truncate(html, 200_000),
                item.Element(Itunes + "author")?.Value ?? item.Element(Dc + "creator")?.Value ?? item.Element("author")?.Value ?? "",
                ParseDate(item.Element("pubDate")?.Value ?? item.Element(Dc + "date")?.Value),
                encUrl, encType, ParseDuration(item.Element(Itunes + "duration")?.Value)));
        }
        return new ParsedFeed(WebUtility.HtmlDecode(channel.Element("title")?.Value.Trim() ?? ""), channel.Element("link")?.Value.Trim() ?? "",
            channel.Element("description")?.Value ?? channel.Element(Itunes + "summary")?.Value ?? "", image, podcast, items);
    }

    private static ParsedFeed ParseAtom(XElement root)
    {
        string? Link(XElement e, string rel = "alternate") =>
            e.Elements(Atom + "link").FirstOrDefault(l => (l.Attribute("rel")?.Value ?? "alternate") == rel)?.Attribute("href")?.Value;
        var items = new List<ParsedFeedItem>();
        bool podcast = false;
        foreach (var entry in root.Elements(Atom + "entry"))
        {
            var html = entry.Element(Atom + "content")?.Value ?? entry.Element(Atom + "summary")?.Value ?? "";
            var enclosure = entry.Elements(Atom + "link").FirstOrDefault(l => l.Attribute("rel")?.Value == "enclosure");
            var encType = enclosure?.Attribute("type")?.Value ?? "";
            if (encType.StartsWith("audio", StringComparison.OrdinalIgnoreCase)) podcast = true;
            var link = Link(entry) ?? "";
            items.Add(new ParsedFeedItem(entry.Element(Atom + "id")?.Value ?? link, WebUtility.HtmlDecode(entry.Element(Atom + "title")?.Value.Trim() ?? "Untitled"), link,
                Truncate(StripHtml(entry.Element(Atom + "summary")?.Value ?? html), 500), Truncate(html, 200_000),
                entry.Element(Atom + "author")?.Element(Atom + "name")?.Value ?? "",
                ParseDate(entry.Element(Atom + "published")?.Value ?? entry.Element(Atom + "updated")?.Value),
                enclosure?.Attribute("href")?.Value ?? "", encType, 0));
        }
        var icon = root.Element(Atom + "icon")?.Value ?? root.Element(Atom + "logo")?.Value;
        return new ParsedFeed(WebUtility.HtmlDecode(root.Element(Atom + "title")?.Value.Trim() ?? ""), Link(root) ?? "", root.Element(Atom + "subtitle")?.Value ?? "", icon, podcast, items);
    }

    private static ParsedFeed ParseRdf(XElement root)
    {
        XNamespace rss = "http://purl.org/rss/1.0/";
        var channel = root.Element(rss + "channel");
        var items = root.Elements(rss + "item").Select(item =>
        {
            var link = item.Element(rss + "link")?.Value ?? "";
            var desc = item.Element(rss + "description")?.Value ?? "";
            return new ParsedFeedItem(item.Attribute(XName.Get("about", "http://www.w3.org/1999/02/22-rdf-syntax-ns#"))?.Value ?? link,
                WebUtility.HtmlDecode(item.Element(rss + "title")?.Value ?? "Untitled"), link, Truncate(StripHtml(desc), 500), desc, item.Element(Dc + "creator")?.Value ?? "",
                ParseDate(item.Element(Dc + "date")?.Value), "", "", 0);
        }).ToList();
        return new ParsedFeed(channel?.Element(rss + "title")?.Value ?? "", channel?.Element(rss + "link")?.Value ?? "", channel?.Element(rss + "description")?.Value ?? "", null, false, items);
    }

    private static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        // RFC 822 time zones .NET doesn't know.
        foreach (var (zone, offset) in new[] { ("GMT", "+0000"), ("UTC", "+0000"), ("UT", "+0000"), ("EST", "-0500"), ("EDT", "-0400"), ("CST", "-0600"), ("CDT", "-0500"),
                     ("MST", "-0700"), ("MDT", "-0600"), ("PST", "-0800"), ("PDT", "-0700"), ("CET", "+0100"), ("CEST", "+0200"), ("BST", "+0100"), ("Z", "+0000") })
        {
            if (text.EndsWith(" " + zone, StringComparison.Ordinal)) { text = text[..^zone.Length] + offset; break; }
        }
        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dto)) return dto.LocalDateTime;
        string[] formats = ["ddd, d MMM yyyy HH:mm:ss zzz", "ddd, dd MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm:ss zzz", "ddd, d MMM yyyy HH:mm zzz", "yyyy-MM-dd"];
        foreach (var f in formats)
            if (DateTimeOffset.TryParseExact(text.Replace("+0000", "+00:00"), f, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out dto)) return dto.LocalDateTime;
        return null;
    }

    private static double ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var parts = text.Trim().Split(':');
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return 0;
            total = total * 60 + v;
        }
        return total;
    }

    /// <summary>HTML → readable plain text (paragraphs kept as blank lines).</summary>
    public static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var s = ScriptRegex().Replace(html, " ");
        s = BlockRegex().Replace(s, "\n");
        s = TagRegex().Replace(s, "");
        s = WebUtility.HtmlDecode(s);
        s = SpacesRegex().Replace(s, " ");
        s = BlankLinesRegex().Replace(s, "\n\n");
        return s.Trim();
    }

    private static string Truncate(string s, int max) => s.Length > max ? s[..max].TrimEnd() + "…" : s;

    /// <summary>OPML (the usual feed list export) → feed URLs.</summary>
    public static List<(string Title, string Url)> ParseOpml(string xml)
    {
        var list = new List<(string, string)>();
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
            var doc = XDocument.Load(reader);
            foreach (var o in doc.Descendants("outline"))
            {
                var url = o.Attribute("xmlUrl")?.Value;
                if (!string.IsNullOrWhiteSpace(url)) list.Add((o.Attribute("title")?.Value ?? o.Attribute("text")?.Value ?? url, url));
            }
        }
        catch (XmlException) { }
        return list;
    }

    public static string ToOpml(IEnumerable<Feed> feeds)
    {
        var body = new XElement("body", feeds.Select(f => new XElement("outline", new XAttribute("type", "rss"), new XAttribute("text", f.Title),
            new XAttribute("title", f.Title), new XAttribute("xmlUrl", f.Url), new XAttribute("htmlUrl", f.SiteUrl))));
        var doc = new XDocument(new XElement("opml", new XAttribute("version", "2.0"), new XElement("head", new XElement("title", "Satchel feeds")), body));
        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true })) doc.Save(writer);
        return sb.ToString();
    }

    [GeneratedRegex(@"<link[^>]+type\s*=\s*[""']application/(?:rss|atom)\+xml[""'][^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex FeedLinkRegex();

    [GeneratedRegex(@"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptRegex();

    [GeneratedRegex(@"<\s*(br|/p|/div|/h[1-6]|/li|/blockquote|/tr)\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex SpacesRegex();

    [GeneratedRegex(@"\s*\n\s*\n\s*")]
    private static partial Regex BlankLinesRegex();
}
