using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Satchel.Core;

namespace Satchel.Services.Sheets;

/// <summary>How values look: Excel-style number format codes ("#,##0.00", "0%", "dd.mm.yyyy", "h:mm AM/PM"…).</summary>
public static partial class CellFormat
{
    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    /// <summary>The formats offered in the toolbar (label, code). Null code = General.</summary>
    public static IReadOnlyList<(string Label, string? Code, string Sample)> Presets => _presets ??= BuildPresets();
    private static IReadOnlyList<(string, string?, string)>? _presets;

    private static IReadOnlyList<(string, string?, string)> BuildPresets()
    {
        var date = DateCode();
        return
        [
            ("General", null, "1234.5"),
            ("Number", "#,##0.00", Display(XValue.Num(1234.5), "#,##0.00")),
            ("Whole number", "#,##0", Display(XValue.Num(1234.5), "#,##0")),
            ("Percent", "0%", "12%"),
            ("Percent (0.0)", "0.0%", Display(XValue.Num(0.125), "0.0%")),
            ("Currency", CurrencyCode(), Display(XValue.Num(1234.5), CurrencyCode())),
            ("Date", date, Display(XValue.Num(DateToSerial(DateTime.Today)), date)),
            ("Time", "hh:mm", "14:30"),
            ("Date and time", date + " hh:mm", Display(XValue.Num(DateToSerial(DateTime.Today.AddHours(14.5))), date + " hh:mm")),
            ("Text", "@", "001"),
        ];
    }

    /// <summary>The main currency from Settings as a format, e.g. <c>#,##0.00 "€"</c>.</summary>
    public static string CurrencyCode()
    {
        var currency = App.Services?.GetService(typeof(Services.SettingsService)) is Services.SettingsService s ? s.Current.Currency : "EUR";
        return currency switch
        {
            "USD" => "$#,##0.00",
            "GBP" => "£#,##0.00",
            "JPY" => "¥#,##0",
            "BGN" => "#,##0.00 \"лв\"",
            "EUR" => "#,##0.00 \"€\"",
            _ => "#,##0.00 \"" + currency + "\""
        };
    }

    /// <summary>The app's date pattern (Settings → General) as a format code.</summary>
    public static string DateCode()
    {
        var pattern = string.IsNullOrWhiteSpace(Format.DatePattern) ? Culture.DateTimeFormat.ShortDatePattern : Format.DatePattern;
        // .NET quotes literals with '…' (e.g. Bulgarian "d.M.yyyy 'г.'"); format codes use "…".
        pattern = NetLiteral().Replace(pattern, "\"$1\"");
        return pattern.Replace("M", "m").Replace("H", "h").Replace("tt", "AM/PM");
    }

    [GeneratedRegex("'([^']*)'")]
    private static partial Regex NetLiteral();

    public static string Display(XValue v, string? code) => v.Kind switch
    {
        XKind.Empty => "",
        XKind.Text => v.Text ?? "",
        XKind.Bool => v.Number != 0 ? "TRUE" : "FALSE",
        XKind.Error => v.ErrorCode,
        _ => code == null || code == "General" || code == "@" ? General(v.Number) : Number(v.Number, code)
    };

    /// <summary>Excel's "General": up to 10 significant digits, scientific for very big or small numbers.</summary>
    public static string General(double n)
    {
        if (n == 0) return "0";
        double abs = Math.Abs(n);
        if (abs >= 1e11 || abs < 1e-9) return n.ToString("0.#####E+00", Culture);
        int digits = (int)Math.Floor(Math.Log10(abs)) + 1;
        var rounded = Math.Round(n, Math.Clamp(10 - digits, 0, 15), MidpointRounding.AwayFromZero);
        return rounded.ToString("0.##########", Culture);
    }

    // ----- number formats -----

    private static readonly Dictionary<string, Section[]> Parsed = new();

    private sealed class Section
    {
        public bool IsDate, IsText, Percent, Scientific, Grouping, HasDigits;
        public int Decimals, MinDecimals, MinInt, ScaleThousands;
        public string Prefix = "", Suffix = "", Pattern = "";
        public List<string> DateTokens = [];
        public bool TwelveHour;
    }

    public static string Number(double value, string code)
    {
        Section[] sections;
        lock (Parsed)
        {
            if (!Parsed.TryGetValue(code, out sections!))
            {
                sections = SplitSections(code).Select(ParseSection).ToArray();
                if (Parsed.Count > 500) Parsed.Clear();
                Parsed[code] = sections;
            }
        }
        if (sections.Length == 0) return General(value);
        Section s = sections[0];
        bool autoMinus = value < 0;
        if (value < 0 && sections.Length >= 2 && !sections[1].IsText) { s = sections[1]; value = -value; autoMinus = false; }
        else if (value == 0 && sections.Length >= 3 && !sections[2].IsText) s = sections[2];
        try
        {
            if (s.IsDate) return DateText(value, s);
            if (!s.HasDigits) return s.Prefix + s.Suffix;
            return (autoMinus ? "-" : "") + s.Prefix + NumberText(Math.Abs(value), s, autoMinus ? -1 : 1) + s.Suffix;
        }
        catch (Exception)
        {
            return General(value);
        }
    }

    private static List<string> SplitSections(string code)
    {
        var list = new List<string>();
        var sb = new StringBuilder();
        bool quote = false, bracket = false;
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            if (c == '"' && !bracket) quote = !quote;
            else if (c == '[' && !quote) bracket = true;
            else if (c == ']' && !quote) bracket = false;
            else if (c == '\\' && !quote && i + 1 < code.Length) { sb.Append(c).Append(code[++i]); continue; }
            if (c == ';' && !quote && !bracket) { list.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        list.Add(sb.ToString());
        return list;
    }

    private static Section ParseSection(string text)
    {
        var s = new Section();
        // Date or time? (letters outside quotes and brackets)
        var bare = Bare(text);
        if (DateLetters().IsMatch(bare) || bare.Contains("AM/PM", StringComparison.OrdinalIgnoreCase) || text.Contains("[h]", StringComparison.OrdinalIgnoreCase))
        {
            s.IsDate = true;
            s.TwelveHour = bare.Contains("AM/PM", StringComparison.OrdinalIgnoreCase) || bare.Contains("A/P", StringComparison.OrdinalIgnoreCase);
            s.DateTokens = DateTokens(text);
            return s;
        }
        if (bare.Contains('@') && !bare.Any(c => c is '0' or '#' or '?')) s.IsText = true;
        var prefix = new StringBuilder();
        var suffix = new StringBuilder();
        var pattern = new StringBuilder();
        bool inPattern = false, afterPattern = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            var lit = afterPattern || inPattern ? suffix : prefix;
            if (c == '"')
            {
                int end = text.IndexOf('"', i + 1);
                if (end < 0) end = text.Length;
                if (inPattern) { inPattern = false; afterPattern = true; lit = suffix; }
                lit.Append(text, i + 1, Math.Max(0, end - i - 1));
                i = end;
                continue;
            }
            if (c == '[')
            {
                int end = text.IndexOf(']', i + 1);
                if (end < 0) break;
                var inner = text[(i + 1)..end];
                if (inner.StartsWith('$'))
                {
                    var symbol = inner[1..].Split('-')[0];
                    if (inPattern) { inPattern = false; afterPattern = true; lit = suffix; }
                    lit.Append(symbol);
                }
                i = end;
                continue;
            }
            if (c == '\\' && i + 1 < text.Length) { if (inPattern) { inPattern = false; afterPattern = true; lit = suffix; } lit.Append(text[++i]); continue; }
            if (c == '_' && i + 1 < text.Length) { i++; if (inPattern) { inPattern = false; afterPattern = true; lit = suffix; } lit.Append(' '); continue; }
            if (c == '*' && i + 1 < text.Length) { i++; continue; }
            if (c == '@') continue;
            if (!afterPattern && (c is '0' or '#' or '?' or '.' || (c == ',' && inPattern) || ((c is 'E' or 'e') && inPattern && i + 1 < text.Length && text[i + 1] is '+' or '-')))
            {
                inPattern = true;
                s.HasDigits |= c is '0' or '#' or '?';
                pattern.Append(c);
                if (c is 'E' or 'e') { pattern.Append(text[++i]); s.Scientific = true; }
                continue;
            }
            if (c == '%') { s.Percent = true; }
            if (inPattern) { inPattern = false; afterPattern = true; lit = suffix; }
            lit.Append(c);
        }
        var p = pattern.ToString();
        // Commas right after the digits scale by 1000 each; commas between digits mean grouping.
        while (p.EndsWith(',')) { s.ScaleThousands++; p = p[..^1]; }
        int dot = p.IndexOf('.');
        var intPart = dot >= 0 ? p[..dot] : p;
        var decPart = dot >= 0 ? p[(dot + 1)..] : "";
        if (s.Scientific) decPart = decPart.Split('E', 'e')[0];
        s.Grouping = intPart.Contains(',');
        s.MinInt = intPart.Count(c => c == '0');
        s.Decimals = decPart.Count(c => c is '0' or '#' or '?');
        s.MinDecimals = decPart.Count(c => c == '0');
        s.Pattern = p;
        s.Prefix = prefix.ToString();
        s.Suffix = suffix.ToString();
        return s;
    }

    private static string Bare(string text)
    {
        var sb = new StringBuilder();
        bool quote = false, bracket = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"') { quote = !quote; continue; }
            if (quote) continue;
            if (c == '[') { bracket = true; continue; }
            if (c == ']') { bracket = false; continue; }
            if (bracket) continue;
            if (c == '\\') { i++; continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string NumberText(double value, Section s, int sign)
    {
        if (s.Percent) value *= 100;
        for (int i = 0; i < s.ScaleThousands; i++) value /= 1000;
        var nf = Culture.NumberFormat;
        if (s.Scientific)
        {
            var mantissa = s.Decimals > 0 ? "0." + new string('0', s.MinDecimals) + new string('#', s.Decimals - s.MinDecimals) : "0";
            return value.ToString(mantissa + "E+00", Culture);
        }
        var rounded = Math.Round((decimal)Math.Min(value, 7.9e27), s.Decimals, MidpointRounding.AwayFromZero);
        var fixedText = rounded.ToString("F" + s.Decimals, CultureInfo.InvariantCulture);
        var parts = fixedText.Split('.');
        var intDigits = parts[0].TrimStart('0');
        if (intDigits.Length < s.MinInt) intDigits = new string('0', s.MinInt - intDigits.Length) + intDigits;
        if (s.Grouping && intDigits.Length > 3)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < intDigits.Length; i++)
            {
                if (i > 0 && (intDigits.Length - i) % 3 == 0) sb.Append(nf.NumberGroupSeparator);
                sb.Append(intDigits[i]);
            }
            intDigits = sb.ToString();
        }
        var dec = parts.Length > 1 ? parts[1] : "";
        while (dec.Length > s.MinDecimals && dec.EndsWith('0')) dec = dec[..^1];
        if (dec.Length == 0) return intDigits.Length == 0 && s.MinInt == 0 && rounded == 0 ? (s.Pattern.Contains('#') && !s.Pattern.Contains('0') ? "" : "0") : intDigits;
        return intDigits + nf.NumberDecimalSeparator + dec;
    }

    // ----- dates -----

    /// <summary>Excel's day numbers (1 = 1 January 1900, with its famous non-existent 29 February 1900).</summary>
    public static DateTime SerialToDate(double serial)
    {
        if (serial < 0 || serial > 2958465) throw new ArgumentOutOfRangeException(nameof(serial));
        if (serial >= 61) return DateTime.FromOADate(serial);
        if (serial >= 60) return new DateTime(1900, 2, 28).AddDays(serial - 60);
        return DateTime.FromOADate(serial + 1);
    }

    public static double DateToSerial(DateTime d)
    {
        var oa = d.ToOADate();
        return oa >= 61 ? oa : oa - 1;
    }

    private static List<string> DateTokens(string text)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '"')
            {
                int end = text.IndexOf('"', i + 1);
                if (end < 0) end = text.Length;
                tokens.Add("'" + text[(i + 1)..end]);
                i = end + 1;
                continue;
            }
            if (c == '[')
            {
                int end = text.IndexOf(']', i + 1);
                if (end < 0) break;
                var inner = text[(i + 1)..end].ToLowerInvariant();
                if (inner is "h" or "hh" or "m" or "mm" or "s" or "ss") tokens.Add("[" + inner + "]");
                else if (inner.StartsWith('$')) tokens.Add("'" + inner[1..].Split('-')[0]);
                i = end + 1;
                continue;
            }
            if (c == '\\' && i + 1 < text.Length) { tokens.Add("'" + text[i + 1]); i += 2; continue; }
            if (text.AsSpan(i).StartsWith("AM/PM", StringComparison.OrdinalIgnoreCase)) { tokens.Add("AM/PM"); i += 5; continue; }
            if (text.AsSpan(i).StartsWith("A/P", StringComparison.OrdinalIgnoreCase)) { tokens.Add("A/P"); i += 3; continue; }
            char lower = char.ToLowerInvariant(c);
            if (lower is 'y' or 'm' or 'd' or 'h' or 's')
            {
                int j = i;
                while (j < text.Length && char.ToLowerInvariant(text[j]) == lower) j++;
                tokens.Add(new string(lower, j - i));
                i = j;
                continue;
            }
            if (c == '.' && tokens.Count > 0 && tokens[^1].StartsWith('s') && i + 1 < text.Length && text[i + 1] == '0')
            {
                int j = i + 1;
                while (j < text.Length && text[j] == '0') j++;
                tokens.Add("." + new string('0', j - i - 1));
                i = j;
                continue;
            }
            if (c is '_' && i + 1 < text.Length) { tokens.Add("' "); i += 2; continue; }
            if (c is '*' && i + 1 < text.Length) { i += 2; continue; }
            tokens.Add("'" + c);
            i++;
        }
        // "m" means minutes right after hours or right before seconds.
        for (int k = 0; k < tokens.Count; k++)
        {
            if (tokens[k] is not ("m" or "mm")) continue;
            var prev = tokens.Take(k).LastOrDefault(t => !t.StartsWith('\''));
            var next = tokens.Skip(k + 1).FirstOrDefault(t => !t.StartsWith('\''));
            if (prev is "h" or "hh" or "[h]" or "[hh]" || (next != null && next.StartsWith('s'))) tokens[k] = tokens[k] == "m" ? "n" : "nn";
        }
        return tokens;
    }

    private static string DateText(double value, Section s)
    {
        if (value < 0 || value > 2958465) return General(value);
        var dt = SerialToDate(value);
        // Round to whole seconds unless fractions are shown.
        if (!s.DateTokens.Any(t => t.StartsWith('.'))) dt = new DateTime(dt.Ticks + TimeSpan.TicksPerSecond / 2).AddTicks(-(dt.Ticks + TimeSpan.TicksPerSecond / 2) % TimeSpan.TicksPerSecond);
        var dtf = Culture.DateTimeFormat;
        var sb = new StringBuilder();
        foreach (var t in s.DateTokens)
        {
            switch (t)
            {
                case "yy" or "y": sb.Append((dt.Year % 100).ToString("00")); break;
                case "yyy" or "yyyy" or { Length: > 4 } when t[0] == 'y': sb.Append(dt.Year.ToString("0000")); break;
                case "m": sb.Append(dt.Month); break;
                case "mm": sb.Append(dt.Month.ToString("00")); break;
                case "mmm": sb.Append(dtf.GetAbbreviatedMonthName(dt.Month)); break;
                case "mmmm": sb.Append(dtf.GetMonthName(dt.Month)); break;
                case "mmmmm": sb.Append(dtf.GetMonthName(dt.Month)[..1]); break;
                case "d": sb.Append(dt.Day); break;
                case "dd": sb.Append(dt.Day.ToString("00")); break;
                case "ddd": sb.Append(dtf.GetAbbreviatedDayName(dt.DayOfWeek)); break;
                case { Length: >= 4 } when t[0] == 'd': sb.Append(dtf.GetDayName(dt.DayOfWeek)); break;
                case "h": sb.Append(s.TwelveHour ? (dt.Hour % 12 == 0 ? 12 : dt.Hour % 12) : dt.Hour); break;
                case "hh": sb.Append((s.TwelveHour ? (dt.Hour % 12 == 0 ? 12 : dt.Hour % 12) : dt.Hour).ToString("00")); break;
                case "n": sb.Append(dt.Minute); break;
                case "nn": sb.Append(dt.Minute.ToString("00")); break;
                case "s": sb.Append(dt.Second); break;
                case "ss": sb.Append(dt.Second.ToString("00")); break;
                case "[h]" or "[hh]": sb.Append(Math.Floor(value * 24).ToString(CultureInfo.InvariantCulture)); break;
                case "[m]" or "[mm]": sb.Append(Math.Floor(value * 1440).ToString(CultureInfo.InvariantCulture)); break;
                case "[s]" or "[ss]": sb.Append(Math.Floor(value * 86400).ToString(CultureInfo.InvariantCulture)); break;
                case "AM/PM": sb.Append(dt.Hour < 12 ? "AM" : "PM"); break;
                case "A/P": sb.Append(dt.Hour < 12 ? "A" : "P"); break;
                default:
                    if (t.StartsWith('.')) sb.Append(Culture.NumberFormat.NumberDecimalSeparator).Append((dt.Millisecond / 1000.0).ToString("F" + (t.Length - 1), CultureInfo.InvariantCulture)[2..]);
                    else if (t.StartsWith('\'')) sb.Append(t, 1, t.Length - 1);
                    break;
            }
        }
        return sb.ToString();
    }

    public static bool IsDateFormat(string? code)
    {
        if (string.IsNullOrEmpty(code)) return false;
        var first = SplitSections(code)[0];
        var bare = Bare(first);
        return DateLetters().IsMatch(bare) || bare.Contains("AM/PM", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("[yYdDhHsS]|[mM]")]
    private static partial Regex DateLetters();
}

/// <summary>What you type into a cell: numbers (with your region's decimal mark), percentages, money, dates, times, TRUE/FALSE.</summary>
public static partial class CellInput
{
    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    /// <summary>The value of typed text, and a format to give the cell if it has none (e.g. "0%" for "12%").</summary>
    public static (XValue Value, string? Format) Parse(string raw, string? currentFormat)
    {
        if (raw.Length == 0) return (XValue.Empty, null);
        if (raw[0] == '\'') return (XValue.Str(raw[1..]), null);
        if (currentFormat == "@") return (XValue.Str(raw), null);
        var text = raw.Trim();
        if (text.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) return (XValue.True, null);
        if (text.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) return (XValue.False, null);
        if (text.StartsWith('#') && Errors.All.FirstOrDefault(e => e != Errors.Cycle && e.Equals(text, StringComparison.OrdinalIgnoreCase)) is { } err) return (XValue.Err(err), null);
        if (TryNumber(text, out var n, out var decimals)) return (XValue.Num(n), null);
        if (text.EndsWith('%') && TryNumber(text[..^1].TrimEnd(), out var pct, out var pd))
            return (XValue.Num(pct / 100), pd > 0 ? "0." + new string('0', Math.Min(pd, 6)) + "%" : "0%");
        if (Money().Match(text) is { Success: true } m && (m.Groups["pre"].Value.Length > 0 || m.Groups["post"].Value.Length > 0)
            && TryNumber(m.Groups["n"].Value.Trim(), out var amount, out var md))
        {
            var symbol = m.Groups["pre"].Value.Length > 0 ? m.Groups["pre"].Value : m.Groups["post"].Value;
            if (m.Groups["neg"].Success && m.Groups["neg"].Value.Length > 0) amount = -amount;
            var numberPart = md > 0 ? "#,##0.00" : "#,##0";
            var code = m.Groups["pre"].Value.Length > 0 ? symbol + numberPart : numberPart + " \"" + symbol + "\"";
            return (XValue.Num(amount), code);
        }
        if (TryDate(text, out var serial, out var dateFormat)) return (XValue.Num(serial), dateFormat);
        return (XValue.Str(raw), null);
    }

    /// <summary>A number as typed: "1234.5" always works; "1 234,5" or "1,234.5" when that's how your region writes it.</summary>
    public static bool TryNumber(string text, out double value, out int decimals)
    {
        decimals = 0;
        value = 0;
        if (text.Length == 0 || text.Length > 40) return false;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            int dot = text.IndexOf('.');
            int e = text.IndexOfAny(['e', 'E']);
            decimals = dot >= 0 ? (e > dot ? e : text.Length) - dot - 1 : 0;
            return double.IsFinite(value);
        }
        // The app's format first, then the Windows region's (Satchel can show English dates on a Bulgarian PC,
        // where "320,5" is still how numbers are typed).
        if (TryCulture(text, Culture, out value, out decimals)) return true;
        var system = App.SystemCulture;
        return !ReferenceEquals(system, Culture) && system.NumberFormat.NumberDecimalSeparator != Culture.NumberFormat.NumberDecimalSeparator
            && TryCulture(text, system, out value, out decimals);
    }

    private static bool TryCulture(string text, CultureInfo culture, out double value, out int decimals)
    {
        value = 0;
        decimals = 0;
        var nf = culture.NumberFormat;
        var normalized = text;
        // Typing a normal space where the region groups with a no-break space.
        if (nf.NumberGroupSeparator.Trim().Length == 0) normalized = normalized.Replace(" ", nf.NumberGroupSeparator);
        if (!GroupingLooksRight(normalized, nf)) return false;
        if (double.TryParse(normalized, NumberStyles.Number | NumberStyles.AllowExponent, culture, out value) && double.IsFinite(value))
        {
            int sep = normalized.IndexOf(nf.NumberDecimalSeparator, StringComparison.Ordinal);
            decimals = sep >= 0 ? normalized.Length - sep - nf.NumberDecimalSeparator.Length : 0;
            return true;
        }
        return false;
    }

    /// <summary>"1,5" in English isn't fifteen: group separators must split the digits in threes.</summary>
    private static bool GroupingLooksRight(string text, NumberFormatInfo nf)
    {
        var group = nf.NumberGroupSeparator;
        if (group.Length == 0 || !text.Contains(group, StringComparison.Ordinal)) return true;
        var intPart = text.Split(nf.NumberDecimalSeparator)[0].TrimStart('-', '+');
        var groups = intPart.Split(group);
        if (groups[0].Length is < 1 or > 3) return false;
        return groups.Skip(1).All(g => g.Length == 3 && g.All(char.IsAsciiDigit));
    }

    private static readonly string[] IsoFormats = ["yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"];

    public static bool TryDate(string text, out double serial, out string? format)
    {
        serial = 0;
        format = null;
        if (DateLike().IsMatch(text))
        {
            if (DateTime.TryParseExact(text, IsoFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
            {
                serial = CellFormat.DateToSerial(iso);
                format = iso.TimeOfDay == TimeSpan.Zero ? "yyyy-mm-dd" : "yyyy-mm-dd hh:mm";
                return true;
            }
            var patterns = new List<string>();
            if (!string.IsNullOrWhiteSpace(Format.DatePattern)) patterns.Add(Format.DatePattern);
            patterns.AddRange(Culture.DateTimeFormat.GetAllDateTimePatterns('d'));
            // Also without literal text (Bulgarian dates end in " г."), and the day-first dotted form.
            patterns.AddRange(patterns.Select(p => Quoted().Replace(p, "").Trim()).ToList());
            patterns.Add("d.M.yyyy");
            text = text.Replace("г.", "").Replace("г", "").Trim();
            foreach (var p in patterns.Where(p => p.Length > 0).Distinct())
            {
                if (!DateTime.TryParseExact(text, p, Culture, DateTimeStyles.AllowWhiteSpaces, out var d)
                    && !DateTime.TryParseExact(text, p.Replace("dd", "d").Replace("MM", "M"), Culture, DateTimeStyles.AllowWhiteSpaces, out d)) continue;
                serial = CellFormat.DateToSerial(d);
                format = CellFormat.DateCode();
                return true;
            }
            return false;
        }
        if (TimeLike().IsMatch(text) && DateTime.TryParse(text, Culture, DateTimeStyles.NoCurrentDateDefault, out var t))
        {
            serial = t.TimeOfDay.TotalDays;
            bool seconds = text.Count(c => c == ':') == 2;
            bool ampm = text.Contains("AM", StringComparison.OrdinalIgnoreCase) || text.Contains("PM", StringComparison.OrdinalIgnoreCase);
            format = ampm ? (seconds ? "h:mm:ss AM/PM" : "h:mm AM/PM") : seconds ? "hh:mm:ss" : "hh:mm";
            return true;
        }
        return false;
    }

    [GeneratedRegex(@"^(?<neg>-)?\s*(?<pre>[€$£¥])?\s*(?<n>[0-9][0-9 ,. ]*)\s*(?<post>€|\$|£|лв\.?|EUR|USD|BGN|GBP)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Money();

    [GeneratedRegex("'[^']*'")]
    private static partial Regex Quoted();

    [GeneratedRegex(@"^\d{1,4}[./-]\d{1,2}[./-]\d{1,4}(\s*г\.?)?(\s+\d{1,2}:\d{2}(:\d{2})?)?$|^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?$")]
    private static partial Regex DateLike();

    [GeneratedRegex(@"^\d{1,2}:\d{2}(:\d{2})?(\s*[AaPp][Mm])?$")]
    private static partial Regex TimeLike();
}
