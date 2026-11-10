using System.Globalization;
using System.Text;

namespace Satchel.Services.Import;

/// <summary>Reading text files whose encoding nobody wrote down: UTF-8 / UTF-16 (with or without a BOM) or an old Windows code page.</summary>
public static class TextFiles
{
    static TextFiles() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Makes the legacy code pages (1251, 1252…) available. Safe to call more than once.</summary>
    public static void EnsureCodePages() => _ = Strict;

    private static readonly UTF8Encoding Strict = new(false, throwOnInvalidBytes: true);

    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (LooksLikeUtf16(bytes, out bool bigEndian)) return (bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(bytes);
        try
        {
            return Strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(LegacyCodePage(bytes)).GetString(bytes);
        }
    }

    /// <summary>UTF-16 without a byte order mark: every other byte of plain text is zero.</summary>
    private static bool LooksLikeUtf16(byte[] bytes, out bool bigEndian)
    {
        bigEndian = false;
        int n = Math.Min(bytes.Length & ~1, 4096);
        if (n < 8) return false;
        int evenZeros = 0, oddZeros = 0;
        for (int i = 0; i < n; i += 2)
        {
            if (bytes[i] == 0) evenZeros++;
            if (bytes[i + 1] == 0) oddZeros++;
        }
        int pairs = n / 2;
        if (oddZeros > pairs * 0.4 && evenZeros < pairs * 0.05) return true;
        if (evenZeros > pairs * 0.4 && oddZeros < pairs * 0.05) { bigEndian = true; return true; }
        return false;
    }

    /// <summary>
    /// The Windows code page an old file was most likely written in. Cyrillic text (Windows-1251) is almost all bytes
    /// 0xC0–0xFF; Western text (Windows-1252) only has an accented letter here and there.
    /// </summary>
    public static int LegacyCodePage(ReadOnlySpan<byte> bytes)
    {
        int system = CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        if (system == 1251) return 1251;
        int high = 0, letters = 0;
        foreach (var b in bytes)
        {
            if (b >= 0xC0) { high++; letters++; }
            else if ((b | 0x20) is >= (byte)'a' and <= (byte)'z') letters++;
        }
        if (letters > 0 && high > letters * 0.4) return 1251;
        return system is > 0 and not 65001 and not 1200 ? system : 1252;
    }

    /// <summary>
    /// Plain text as Markdown that reads the same: every line break is kept (a hard break — ignored at the end of a
    /// paragraph, so lists and blank lines behave as usual). Lines starting with #, &gt; or &lt; stay literal (a comment
    /// in a text file isn't a heading); fenced code blocks are left alone.
    /// </summary>
    public static string PlainTextToMarkdown(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder(text.Length + lines.Length * 2);
        bool inFence = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            bool fence = line.TrimStart().StartsWith("```", StringComparison.Ordinal);
            if (fence) inFence = !inFence;
            if (!inFence && !fence)
            {
                int indent = line.Length - line.TrimStart().Length;
                if (indent < 4 && indent < line.Length && line[indent] is '#' or '>' or '<') line = line.Insert(indent, "\\");
            }
            sb.Append(line);
            if (!inFence && !fence && line.Length > 0) sb.Append("  ");
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n', ' ') + "\n";
    }
}
