using System.Globalization;
using System.Text.RegularExpressions;

namespace Satchel.Core;

/// <summary>Parsing helpers for YouTube links (no API key needed anywhere in the app).</summary>
public static partial class YouTube
{
    [GeneratedRegex(@"(?:youtube(?:-nocookie)?\.com/(?:watch\?(?:[^#]*&)?v=|shorts/|embed/|live/|v/)|youtu\.be/)([A-Za-z0-9_-]{11})", RegexOptions.IgnoreCase)]
    private static partial Regex VideoRegex();

    [GeneratedRegex(@"(?:^|/channel/)(UC[A-Za-z0-9_-]{22})(?:$|[/?#])")]
    private static partial Regex ChannelRegex();

    [GeneratedRegex(@"(?:^|youtube\.com/)@([A-Za-z0-9._-]{3,30})", RegexOptions.IgnoreCase)]
    private static partial Regex HandleRegex();

    [GeneratedRegex(@"youtube\.com/(?:c|user)/([A-Za-z0-9._-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyRegex();

    [GeneratedRegex(@"[?&#]t=(\d+)(?:s)?")]
    private static partial Regex TimeRegex();

    public static string? VideoId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = VideoRegex().Match(url);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static double StartSeconds(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return 0;
        var m = TimeRegex().Match(url);
        return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
    }

    public static string? ChannelId(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var m = ChannelRegex().Match(input.Trim());
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>"@name", "youtube.com/@name", "youtube.com/c/name" or "youtube.com/user/name" → a channel page URL.</summary>
    public static string? ChannelPageUrl(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim();
        var h = HandleRegex().Match(input);
        if (h.Success) return $"https://www.youtube.com/@{h.Groups[1].Value}";
        var l = LegacyRegex().Match(input);
        if (l.Success) return $"https://www.youtube.com/{(input.Contains("/user/", StringComparison.OrdinalIgnoreCase) ? "user" : "c")}/{l.Groups[1].Value}";
        if (!input.Contains('/') && !input.Contains(' ') && input.Length is >= 3 and <= 30) return $"https://www.youtube.com/@{input}";
        return null;
    }

    public static string WatchUrl(string id, double seconds = 0) =>
        seconds >= 1 ? $"https://www.youtube.com/watch?v={id}&t={(int)seconds}s" : $"https://www.youtube.com/watch?v={id}";

    public static string ThumbnailUrl(string id, string quality = "mqdefault") => $"https://i.ytimg.com/vi/{id}/{quality}.jpg";

    public static string FeedUrl(string channelId) => $"https://www.youtube.com/feeds/videos.xml?channel_id={channelId}";

    public static string FormatTime(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        var t = TimeSpan.FromSeconds(Math.Floor(seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>Parses "1:23", "01:02:03" or "75" into seconds.</summary>
    public static double? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split(':');
        double total = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return null;
            total = total * 60 + v;
        }
        return total;
    }
}
