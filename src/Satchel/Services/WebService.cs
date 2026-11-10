using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace Satchel.Services;

/// <summary>
/// The only place that talks to the internet. Every call respects the "Online features" switch
/// and fails quietly (returns null) when offline — the app must work fully without a connection.
/// </summary>
public sealed partial class WebService
{
    private static readonly HttpClient Http = CreateClient();
    private readonly SettingsService _settings;

    public WebService(SettingsService settings) => _settings = settings;

    public bool Enabled => _settings.Current.OnlineFeatures;
    public bool IsAvailable => Enabled && NetworkInterface.GetIsNetworkAvailable();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 6
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        // A mainstream browser identity gets the same pages a person would see.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
        return http;
    }

    private static HttpRequestMessage Request(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Skip YouTube's cookie-consent interstitial (shown to EU visitors without cookies).
        if (url.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
            request.Headers.TryAddWithoutValidation("Cookie", "SOCS=CAI; CONSENT=YES+1");
        return request;
    }

    public async Task<string?> GetStringAsync(string url, CancellationToken ct = default)
    {
        if (!IsAvailable) return null;
        try
        {
            using var response = await Http.SendAsync(Request(url), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<byte[]?> GetBytesAsync(string url, int maxBytes = 12 * 1024 * 1024, CancellationToken ct = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(url)) return null;
        try
        {
            using var response = await Http.SendAsync(Request(url), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength > maxBytes) return null;
            var data = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return data.Length > maxBytes ? null : data;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Downloads an image and shrinks it for storage.</summary>
    public async Task<byte[]?> GetImageAsync(string url, int maxSize, CancellationToken ct = default)
    {
        var data = await GetBytesAsync(url, ct: ct).ConfigureAwait(false);
        if (data == null) return null;
        try { return ImageService.Resize(data, maxSize, photo: true); }
        catch { return null; }
    }

    /// <summary>Title, description and preview image of a web page (Open Graph tags, then &lt;title&gt;).</summary>
    public async Task<LinkPreview?> GetPreviewAsync(string url, CancellationToken ct = default)
    {
        var html = await GetStringAsync(url, ct).ConfigureAwait(false);
        if (html == null) return null;
        if (html.Length > 600_000) html = html[..600_000];
        string? Meta(string name)
        {
            foreach (var pattern in new[]
            {
                $@"<meta[^>]+(?:property|name)\s*=\s*[""']{Regex.Escape(name)}[""'][^>]*content\s*=\s*[""']([^""']*)[""']",
                $@"<meta[^>]+content\s*=\s*[""']([^""']*)[""'][^>]*(?:property|name)\s*=\s*[""']{Regex.Escape(name)}[""']"
            })
            {
                var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
                if (m.Success && m.Groups[1].Value.Trim().Length > 0) return WebUtility.HtmlDecode(m.Groups[1].Value.Trim());
            }
            return null;
        }
        var title = Meta("og:title") ?? Meta("twitter:title");
        if (title == null)
        {
            var t = TitleRegex().Match(html);
            if (t.Success) title = WebUtility.HtmlDecode(Regex.Replace(t.Groups[1].Value, @"\s+", " ").Trim());
        }
        var image = Meta("og:image") ?? Meta("twitter:image");
        if (image != null && Uri.TryCreate(new Uri(url), image, out var abs)) image = abs.ToString();
        // Site icon: <link rel="icon" …>, else the classic /favicon.ico.
        string? icon = null;
        foreach (Match m in IconRegex().Matches(html))
        {
            var href = HrefRegex().Match(m.Value);
            if (href.Success && Uri.TryCreate(new Uri(url), WebUtility.HtmlDecode(href.Groups[1].Value), out var iconUri)) { icon = iconUri.ToString(); break; }
        }
        if (icon == null && Uri.TryCreate(url, UriKind.Absolute, out var site)) icon = $"{site.Scheme}://{site.Host}/favicon.ico";
        return new LinkPreview(title ?? "", Meta("og:description") ?? Meta("description") ?? "", image, Meta("og:site_name") ?? "", icon);
    }

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<link[^>]+rel\s*=\s*[""'](?:shortcut icon|icon|apple-touch-icon)[""'][^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex IconRegex();

    [GeneratedRegex(@"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();
}

public sealed record LinkPreview(string Title, string Description, string? ImageUrl, string SiteName, string? IconUrl = null);
