using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using static Satchel.Services.Sheets.Formula;

namespace Satchel.Services.Sheets;

/// <summary>The spreadsheet functions (SUM, IF, VLOOKUP, SUMIFS, TEXT, DATE…), with Excel's rules for text and empty cells.</summary>
public static class Functions
{
    /// <summary>Every function, for autocomplete and the help list.</summary>
    public static readonly string[] Names =
    [
        "ABS", "AND", "AVERAGE", "AVERAGEIF", "AVERAGEIFS", "CEILING", "CHAR", "CHOOSE", "CODE", "CONCAT", "CONCATENATE", "COUNT", "COUNTA", "COUNTBLANK", "COUNTIF", "COUNTIFS",
        "DATE", "DATEDIF", "DAY", "DAYS", "EDATE", "EOMONTH", "EXACT", "EXP", "FALSE", "FIND", "FLOOR", "HLOOKUP", "HOUR", "IF", "IFERROR", "IFNA", "IFS", "INDEX", "INT",
        "ISBLANK", "ISERROR", "ISEVEN", "ISNA", "ISNUMBER", "ISODD", "ISTEXT", "LARGE", "LEFT", "LEN", "LN", "LOG", "LOG10", "LOWER", "MATCH", "MAX", "MEDIAN", "MID", "MIN",
        "MINUTE", "MOD", "MONTH", "NA", "NETWORKDAYS", "NOT", "NOW", "OR", "PI", "POWER", "PRODUCT", "PROPER", "RAND", "RANDBETWEEN", "RANK", "REPLACE", "REPT", "RIGHT",
        "ROUND", "ROUNDDOWN", "ROUNDUP", "SEARCH", "SECOND", "SIGN", "SMALL", "SQRT", "STDEV", "SUBSTITUTE", "SUM", "SUMIF", "SUMIFS", "SUMPRODUCT", "TEXT", "TEXTJOIN",
        "TIME", "TODAY", "TRIM", "TRUE", "TRUNC", "UPPER", "VALUE", "VLOOKUP", "WEEKDAY", "XLOOKUP", "XOR", "YEAR"
    ];

    private static readonly HashSet<string> Known = new(Names);

    internal static XValue Call(CallNode c, Context ctx)
    {
        var a = c.Args;
        XValue Arg(int i) => i < a.Length ? Evaluate(a[i], ctx) : XValue.Empty;
        bool Has(int i) => i < a.Length && a[i] is not MissingNode;
        if (!Known.Contains(c.Name)) return XValue.Err(Errors.Name);
        try
        {
            switch (c.Name)
            {
                // ----- math -----
                case "SUM": return Aggregate(a, ctx, (n, _) => n.Sum());
                case "PRODUCT": return Aggregate(a, ctx, (n, _) => n.Count == 0 ? 0 : n.Aggregate(1.0, (x, y) => x * y));
                case "AVERAGE": return Aggregate(a, ctx, (n, _) => n.Count == 0 ? double.NaN : n.Average(), Errors.Div0);
                case "MIN": return Aggregate(a, ctx, (n, _) => n.Count == 0 ? 0 : n.Min());
                case "MAX": return Aggregate(a, ctx, (n, _) => n.Count == 0 ? 0 : n.Max());
                case "MEDIAN": return Aggregate(a, ctx, (n, _) => Median(n), Errors.Num);
                case "STDEV": return Aggregate(a, ctx, (n, _) => n.Count < 2 ? double.NaN : Math.Sqrt(n.Sum(x => Math.Pow(x - n.Average(), 2)) / (n.Count - 1)), Errors.Div0);
                case "COUNT": return XValue.Num(Values(a, ctx).Count(v => v.Value.IsNumber || (!v.FromRange && Formula.ToNumber(v.Value, out _, out _) && !v.Value.IsEmpty)));
                case "COUNTA": return XValue.Num(Values(a, ctx).Count(v => !v.Value.IsEmpty));
                case "COUNTBLANK":
                {
                    if (a.Length != 1 || AreaOf(a[0], ctx) is not { } area) return XValue.Err(Errors.Value);
                    long total = (long)(area.R2 - area.R1 + 1) * (area.C2 - area.C1 + 1);
                    long filled = Cells(area, ctx).Count(v => !v.IsEmpty && !(v.IsText && v.Text!.Length == 0));
                    return XValue.Num(total - filled);
                }
                case "LARGE" or "SMALL":
                {
                    var nums = Numbers(a[..1], ctx, out var err);
                    if (err is { } e1) return e1;
                    if (!Formula.ToNumber(Arg(1), out var kf, out var e2)) return e2;
                    int k = (int)kf;
                    if (k < 1 || k > nums.Count) return XValue.Err(Errors.Num);
                    nums.Sort();
                    return XValue.Num(c.Name == "SMALL" ? nums[k - 1] : nums[^k]);
                }
                case "RANK":
                {
                    if (!Formula.ToNumber(Arg(0), out var x, out var e1)) return e1;
                    var nums = Numbers(a[1..2], ctx, out var err);
                    if (err is { } e2) return e2;
                    bool ascending = Has(2) && Num(Arg(2)) != 0;
                    if (!nums.Contains(x)) return XValue.Err(Errors.NA);
                    return XValue.Num(1 + nums.Count(n => ascending ? n < x : n > x));
                }
                case "SUMPRODUCT":
                {
                    var areas = a.Select(n => AreaOf(n, ctx)).ToList();
                    if (areas.Any(r => r == null)) return XValue.Err(Errors.Value);
                    int rows = areas[0]!.Value.R2 - areas[0]!.Value.R1, cols = areas[0]!.Value.C2 - areas[0]!.Value.C1;
                    if (areas.Any(r => r!.Value.R2 - r.Value.R1 != rows || r.Value.C2 - r.Value.C1 != cols)) return XValue.Err(Errors.Value);
                    double total = 0;
                    for (int r = 0; r <= rows; r++)
                        for (int col = 0; col <= cols; col++)
                        {
                            double p = 1;
                            foreach (var area in areas)
                            {
                                var v = ctx.Book.Evaluate(area!.Value.Sheet, area.Value.R1 + r, area.Value.C1 + col, ctx.Depth);
                                if (v.IsError) return v;
                                p *= v.IsNumber ? v.Number : 0;
                            }
                            total += p;
                        }
                    return XValue.Num(total);
                }
                case "ROUND" or "ROUNDUP" or "ROUNDDOWN":
                {
                    if (!Formula.ToNumber(Arg(0), out var x, out var e1)) return e1;
                    if (!Formula.ToNumber(Arg(1), out var d, out var e2)) return e2;
                    return XValue.Num(Round(x, (int)Math.Truncate(d), c.Name));
                }
                case "INT": return Math1(Arg(0), Math.Floor);
                case "TRUNC":
                {
                    if (!Formula.ToNumber(Arg(0), out var x, out var e1)) return e1;
                    int d = Has(1) ? (int)Num(Arg(1)) : 0;
                    return XValue.Num(Round(x, d, "ROUNDDOWN"));
                }
                case "ABS": return Math1(Arg(0), Math.Abs);
                case "SIGN": return Math1(Arg(0), x => Math.Sign(x));
                case "SQRT": return Math1(Arg(0), x => x < 0 ? double.NaN : Math.Sqrt(x));
                case "EXP": return Math1(Arg(0), Math.Exp);
                case "LN": return Math1(Arg(0), x => x <= 0 ? double.NaN : Math.Log(x));
                case "LOG10": return Math1(Arg(0), x => x <= 0 ? double.NaN : Math.Log10(x));
                case "LOG":
                {
                    if (!Formula.ToNumber(Arg(0), out var x, out var e1)) return e1;
                    double b = 10;
                    if (Has(1) && !Formula.ToNumber(Arg(1), out b, out var e2)) return e2;
                    return x <= 0 || b <= 0 || b == 1 ? XValue.Err(Errors.Num) : XValue.Num(Math.Log(x) / Math.Log(b));
                }
                case "POWER":
                {
                    if (!Formula.ToNumber(Arg(0), out var x, out var e1)) return e1;
                    if (!Formula.ToNumber(Arg(1), out var y, out var e2)) return e2;
                    return XValue.Num(Math.Pow(x, y));
                }
                case "MOD":
                {
                    if (!Formula.ToNumber(Arg(0), out var x, out var e1)) return e1;
                    if (!Formula.ToNumber(Arg(1), out var y, out var e2)) return e2;
                    if (y == 0) return XValue.Err(Errors.Div0);
                    return XValue.Num(x - y * Math.Floor(x / y));
                }
                case "CEILING" or "FLOOR":
                {
                    if (!Formula.ToNumber(Arg(0), out var x, out var e1)) return e1;
                    double s = 1;
                    if (Has(1) && !Formula.ToNumber(Arg(1), out s, out var e2)) return e2;
                    if (s == 0) return XValue.Num(0);
                    var q = x / s;
                    return XValue.Num((c.Name == "CEILING" ? Math.Ceiling(Math.Round(q, 9)) : Math.Floor(Math.Round(q, 9))) * s);
                }
                case "PI": return XValue.Num(Math.PI);
                case "RAND": return XValue.Num(Random.Shared.NextDouble());
                case "RANDBETWEEN":
                {
                    if (!Formula.ToNumber(Arg(0), out var lo, out var e1)) return e1;
                    if (!Formula.ToNumber(Arg(1), out var hi, out var e2)) return e2;
                    long l = (long)Math.Ceiling(lo), h = (long)Math.Floor(hi);
                    return h < l ? XValue.Err(Errors.Num) : XValue.Num(Random.Shared.NextInt64(l, h + 1));
                }

                // ----- logic -----
                case "IF":
                {
                    var cond = Arg(0);
                    if (!Formula.ToBool(cond, out var t, out var err)) return err;
                    return t ? (Has(1) ? Arg(1) : XValue.True) : (a.Length > 2 ? (Has(2) ? Arg(2) : XValue.Num(0)) : XValue.False);
                }
                case "IFS":
                    for (int i = 0; i + 1 < a.Length; i += 2)
                    {
                        if (!Formula.ToBool(Arg(i), out var t, out var err)) return err;
                        if (t) return Arg(i + 1);
                    }
                    return XValue.Err(Errors.NA);
                case "IFERROR": { var v = Arg(0); return v.IsError ? Arg(1) : v; }
                case "IFNA": { var v = Arg(0); return v.IsError && v.ErrorCode == Errors.NA ? Arg(1) : v; }
                case "AND" or "OR" or "XOR":
                {
                    int trues = 0, count = 0;
                    foreach (var (v, fromRange) in Values(a, ctx))
                    {
                        if (v.IsError) return v;
                        if (fromRange && v.Kind is XKind.Text or XKind.Empty) continue;
                        if (!Formula.ToBool(v, out var t, out var err)) return err;
                        count++;
                        if (t) trues++;
                    }
                    if (count == 0) return XValue.Err(Errors.Value);
                    return XValue.Bool(c.Name switch { "AND" => trues == count, "OR" => trues > 0, _ => trues % 2 == 1 });
                }
                case "NOT": { if (!Formula.ToBool(Arg(0), out var t, out var err)) return err; return XValue.Bool(!t); }
                case "TRUE": return XValue.True;
                case "FALSE": return XValue.False;
                case "NA": return XValue.Err(Errors.NA);

                // ----- information -----
                case "ISBLANK": return XValue.Bool(Arg(0).IsEmpty);
                case "ISNUMBER": return XValue.Bool(Arg(0).IsNumber);
                case "ISTEXT": return XValue.Bool(Arg(0).IsText);
                case "ISERROR": return XValue.Bool(Arg(0).IsError);
                case "ISNA": { var v = Arg(0); return XValue.Bool(v.IsError && v.ErrorCode == Errors.NA); }
                case "ISEVEN" or "ISODD":
                {
                    if (!Formula.ToNumber(Arg(0), out var x, out var err)) return err;
                    bool even = Math.Truncate(x) % 2 == 0;
                    return XValue.Bool(c.Name == "ISEVEN" ? even : !even);
                }

                // ----- text -----
                case "LEN": return Text1(Arg(0), s => XValue.Num(s.Length));
                case "UPPER": return Text1(Arg(0), s => XValue.Str(s.ToUpper(CultureInfo.CurrentCulture)));
                case "LOWER": return Text1(Arg(0), s => XValue.Str(s.ToLower(CultureInfo.CurrentCulture)));
                case "PROPER": return Text1(Arg(0), s => XValue.Str(CultureInfo.CurrentCulture.TextInfo.ToTitleCase(s.ToLower(CultureInfo.CurrentCulture))));
                case "TRIM": return Text1(Arg(0), s => XValue.Str(Regex.Replace(s.Trim(), " {2,}", " ")));
                case "LEFT" or "RIGHT":
                {
                    var v = Arg(0);
                    if (v.IsError) return v;
                    var s = Formula.ToText(v);
                    int n = 1;
                    if (Has(1)) { if (!Formula.ToNumber(Arg(1), out var nf, out var err)) return err; n = (int)nf; }
                    if (n < 0) return XValue.Err(Errors.Value);
                    n = Math.Min(n, s.Length);
                    return XValue.Str(c.Name == "LEFT" ? s[..n] : s[^n..]);
                }
                case "MID":
                {
                    var v = Arg(0);
                    if (v.IsError) return v;
                    var s = Formula.ToText(v);
                    if (!Formula.ToNumber(Arg(1), out var startF, out var e1)) return e1;
                    if (!Formula.ToNumber(Arg(2), out var lenF, out var e2)) return e2;
                    int start = (int)startF, len = (int)lenF;
                    if (start < 1 || len < 0) return XValue.Err(Errors.Value);
                    if (start > s.Length) return XValue.Str("");
                    return XValue.Str(s.Substring(start - 1, Math.Min(len, s.Length - start + 1)));
                }
                case "CONCAT" or "CONCATENATE":
                {
                    var sb = new StringBuilder();
                    foreach (var (v, _) in Values(a, ctx))
                    {
                        if (v.IsError) return v;
                        sb.Append(Formula.ToText(v));
                    }
                    return XValue.Str(sb.ToString());
                }
                case "TEXTJOIN":
                {
                    var sep = Arg(0);
                    if (sep.IsError) return sep;
                    if (!Formula.ToBool(Arg(1), out var skipEmpty, out var err)) return err;
                    var parts = new List<string>();
                    foreach (var (v, _) in Values(a[2..], ctx))
                    {
                        if (v.IsError) return v;
                        var s = Formula.ToText(v);
                        if (skipEmpty && s.Length == 0) continue;
                        parts.Add(s);
                    }
                    return XValue.Str(string.Join(Formula.ToText(sep), parts));
                }
                case "SUBSTITUTE":
                {
                    var s = Formula.ToText(Arg(0));
                    var oldText = Formula.ToText(Arg(1));
                    var newText = Formula.ToText(Arg(2));
                    if (oldText.Length == 0) return XValue.Str(s);
                    if (!Has(3)) return XValue.Str(s.Replace(oldText, newText, StringComparison.Ordinal));
                    int nth = (int)Num(Arg(3)), at = -1;
                    for (int i = 0; i < nth; i++) { at = s.IndexOf(oldText, at + 1, StringComparison.Ordinal); if (at < 0) return XValue.Str(s); }
                    return XValue.Str(s[..at] + newText + s[(at + oldText.Length)..]);
                }
                case "REPLACE":
                {
                    var s = Formula.ToText(Arg(0));
                    int start = (int)Num(Arg(1)), len = (int)Num(Arg(2));
                    if (start < 1 || len < 0) return XValue.Err(Errors.Value);
                    start = Math.Min(start - 1, s.Length);
                    len = Math.Min(len, s.Length - start);
                    return XValue.Str(s[..start] + Formula.ToText(Arg(3)) + s[(start + len)..]);
                }
                case "FIND" or "SEARCH":
                {
                    var find = Formula.ToText(Arg(0));
                    var within = Formula.ToText(Arg(1));
                    int start = Has(2) ? (int)Num(Arg(2)) : 1;
                    if (start < 1 || start > within.Length + 1) return XValue.Err(Errors.Value);
                    int at;
                    if (c.Name == "FIND") at = within.IndexOf(find, start - 1, StringComparison.Ordinal);
                    else
                    {
                        var m = Regex.Match(within[(start - 1)..], Wildcard(find, anchored: false), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                        at = m.Success ? m.Index + start - 1 : -1;
                    }
                    return at < 0 ? XValue.Err(Errors.Value) : XValue.Num(at + 1);
                }
                case "REPT":
                {
                    int n = (int)Num(Arg(1));
                    var s = Formula.ToText(Arg(0));
                    if (n < 0 || (long)n * s.Length > 32767) return XValue.Err(Errors.Value);
                    return XValue.Str(string.Concat(Enumerable.Repeat(s, n)));
                }
                case "EXACT": return XValue.Bool(string.Equals(Formula.ToText(Arg(0)), Formula.ToText(Arg(1)), StringComparison.Ordinal));
                case "CHAR": { int n = (int)Num(Arg(0)); return n is < 1 or > 65535 ? XValue.Err(Errors.Value) : XValue.Str(((char)n).ToString()); }
                case "CODE": { var s = Formula.ToText(Arg(0)); return s.Length == 0 ? XValue.Err(Errors.Value) : XValue.Num(s[0]); }
                case "TEXT":
                {
                    var v = Arg(0);
                    if (v.IsError) return v;
                    var code = Formula.ToText(Arg(1));
                    if (v.IsText && Formula.ToNumber(v, out var parsed, out _)) v = XValue.Num(parsed);
                    return XValue.Str(v.IsNumber ? CellFormat.Display(v, code.Length == 0 ? null : code) : Formula.ToText(v));
                }
                case "VALUE":
                {
                    var v = Arg(0);
                    if (v.IsError || v.IsNumber) return v;
                    var s = Formula.ToText(v).Trim();
                    var (parsed, _) = CellInput.Parse(s, null);
                    return parsed.IsNumber ? parsed : XValue.Err(Errors.Value);
                }

                // ----- dates -----
                case "TODAY": return XValue.Num(Math.Floor(CellFormat.DateToSerial(DateTime.Today)));
                case "NOW": return XValue.Num(CellFormat.DateToSerial(DateTime.Now));
                case "DATE":
                {
                    int y = (int)Num(Arg(0)), m = (int)Num(Arg(1)), d = (int)Num(Arg(2));
                    if (y < 1900) y += 1900;
                    if (y is < 1900 or > 9999) return XValue.Err(Errors.Num);
                    var date = new DateTime(y, 1, 1).AddMonths(m - 1).AddDays(d - 1);
                    return XValue.Num(CellFormat.DateToSerial(date));
                }
                case "TIME":
                {
                    double h = Num(Arg(0)), m = Num(Arg(1)), s = Num(Arg(2));
                    var total = (h * 3600 + m * 60 + s) / 86400;
                    return total < 0 ? XValue.Err(Errors.Num) : XValue.Num(total - Math.Floor(total));
                }
                case "YEAR": return Date1(Arg(0), d => d.Year);
                case "MONTH": return Date1(Arg(0), d => d.Month);
                case "DAY": return Date1(Arg(0), d => d.Day);
                case "HOUR": return Date1(Arg(0), d => d.Hour);
                case "MINUTE": return Date1(Arg(0), d => d.Minute);
                case "SECOND": return Date1(Arg(0), d => d.Second);
                case "WEEKDAY":
                {
                    int type = Has(1) ? (int)Num(Arg(1)) : 1;
                    return Date1(Arg(0), d =>
                    {
                        int dow = (int)d.DayOfWeek; // Sunday = 0
                        return type switch { 2 => dow == 0 ? 7 : dow, 3 => dow == 0 ? 6 : dow - 1, _ => dow + 1 };
                    });
                }
                case "DAYS":
                {
                    if (!DateOf(Arg(0), out var end, out var e1)) return e1;
                    if (!DateOf(Arg(1), out var start, out var e2)) return e2;
                    return XValue.Num(Math.Floor(CellFormat.DateToSerial(end)) - Math.Floor(CellFormat.DateToSerial(start)));
                }
                case "EDATE" or "EOMONTH":
                {
                    if (!DateOf(Arg(0), out var start, out var e1)) return e1;
                    int months = (int)Num(Arg(1));
                    var d = start.Date.AddMonths(months);
                    if (c.Name == "EOMONTH") d = new DateTime(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));
                    return XValue.Num(CellFormat.DateToSerial(d));
                }
                case "DATEDIF":
                {
                    if (!DateOf(Arg(0), out var start, out var e1)) return e1;
                    if (!DateOf(Arg(1), out var end, out var e2)) return e2;
                    if (end < start) return XValue.Err(Errors.Num);
                    var unit = Formula.ToText(Arg(2)).ToUpperInvariant();
                    int months = (end.Year - start.Year) * 12 + end.Month - start.Month - (end.Day < start.Day ? 1 : 0);
                    return unit switch
                    {
                        "Y" => XValue.Num(months / 12),
                        "M" => XValue.Num(months),
                        "D" => XValue.Num((end.Date - start.Date).Days),
                        "YM" => XValue.Num(months % 12),
                        "MD" => XValue.Num((end.Date - start.Date.AddMonths(months)).Days),
                        "YD" => XValue.Num((end.Date - start.Date.AddYears(months / 12)).Days),
                        _ => XValue.Err(Errors.Num)
                    };
                }
                case "NETWORKDAYS":
                {
                    if (!DateOf(Arg(0), out var start, out var e1)) return e1;
                    if (!DateOf(Arg(1), out var end, out var e2)) return e2;
                    int sign = 1;
                    if (end < start) { (start, end) = (end, start); sign = -1; }
                    int days = 0;
                    for (var d = start.Date; d <= end.Date && days < 100_000; d = d.AddDays(1))
                        if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) days++;
                    return XValue.Num(days * sign);
                }

                // ----- lookup -----
                case "CHOOSE":
                {
                    int i = (int)Num(Arg(0));
                    return i < 1 || i >= a.Length ? XValue.Err(Errors.Value) : Arg(i);
                }
                case "INDEX":
                {
                    if (AreaOf(a[0], ctx) is not { } area) return XValue.Err(Errors.Value);
                    int rows = area.R2 - area.R1 + 1, cols = area.C2 - area.C1 + 1;
                    int r = (int)Num(Arg(1)), col = Has(2) ? (int)Num(Arg(2)) : 1;
                    if (!Has(2) && rows == 1 && cols > 1) { col = r; r = 1; }
                    if (r < 1 || col < 1 || r > rows || col > cols) return XValue.Err(Errors.Ref);
                    return ctx.Book.Evaluate(area.Sheet, area.R1 + r - 1, area.C1 + col - 1, ctx.Depth);
                }
                case "MATCH":
                {
                    var key = Arg(0);
                    if (key.IsError) return key;
                    if (AreaOf(a[1], ctx) is not { } area) return XValue.Err(Errors.NA);
                    int type = Has(2) ? (int)Num(Arg(2)) : 1;
                    var list = Line(area, ctx);
                    int found = Find(list, key, type);
                    return found < 0 ? XValue.Err(Errors.NA) : XValue.Num(found + 1);
                }
                case "VLOOKUP" or "HLOOKUP":
                {
                    var key = Arg(0);
                    if (key.IsError) return key;
                    if (AreaOf(a[1], ctx) is not { } area) return XValue.Err(Errors.Value);
                    int index = (int)Num(Arg(2));
                    bool approx = !Has(3) || (Formula.ToBool(Arg(3), out var ap, out _) && ap);
                    bool vertical = c.Name == "VLOOKUP";
                    int span = vertical ? area.C2 - area.C1 + 1 : area.R2 - area.R1 + 1;
                    if (index < 1) return XValue.Err(Errors.Value);
                    if (index > span) return XValue.Err(Errors.Ref);
                    var first = vertical ? area with { C2 = area.C1 } : area with { R2 = area.R1 };
                    int found = Find(Line(first, ctx), key, approx ? 1 : 0);
                    if (found < 0) return XValue.Err(Errors.NA);
                    return vertical
                        ? ctx.Book.Evaluate(area.Sheet, area.R1 + found, area.C1 + index - 1, ctx.Depth)
                        : ctx.Book.Evaluate(area.Sheet, area.R1 + index - 1, area.C1 + found, ctx.Depth);
                }
                case "XLOOKUP":
                {
                    var key = Arg(0);
                    if (key.IsError) return key;
                    if (AreaOf(a[1], ctx) is not { } lookup || AreaOf(a[2], ctx) is not { } result) return XValue.Err(Errors.Value);
                    int mode = Has(4) ? (int)Num(Arg(4)) : 0;
                    var list = Line(lookup, ctx);
                    int found = mode == 2 || (key.IsText && key.Text!.IndexOfAny(['*', '?']) >= 0 && mode == 0) ? Find(list, key, 0) : FindExact(list, key);
                    if (found < 0 && mode is -1 or 1)
                    {
                        // next smaller (-1) or next larger (1) item
                        int best = -1;
                        for (int i = 0; i < list.Count; i++)
                        {
                            if (list[i].IsEmpty || list[i].Kind != key.Kind) continue;
                            int cmp = Formula.Compare(list[i], key);
                            if (mode == -1 && cmp < 0 && (best < 0 || Formula.Compare(list[i], list[best]) > 0)) best = i;
                            if (mode == 1 && cmp > 0 && (best < 0 || Formula.Compare(list[i], list[best]) < 0)) best = i;
                        }
                        found = best;
                    }
                    if (found < 0) return Has(3) ? Arg(3) : XValue.Err(Errors.NA);
                    bool vertical = lookup.C1 == lookup.C2;
                    return vertical
                        ? ctx.Book.Evaluate(result.Sheet, result.R1 + found, result.C1, ctx.Depth)
                        : ctx.Book.Evaluate(result.Sheet, result.R1, result.C1 + found, ctx.Depth);
                }

                // ----- conditional sums -----
                case "SUMIF" or "COUNTIF" or "AVERAGEIF":
                {
                    if (AreaOf(a[0], ctx) is not { } range) return XValue.Err(Errors.Value);
                    var test = Criteria(Arg(1));
                    var target = c.Name != "COUNTIF" && Has(2) && AreaOf(a[2], ctx) is { } t ? t with { R2 = t.R1 + (range.R2 - range.R1), C2 = t.C1 + (range.C2 - range.C1) } : range;
                    double sum = 0;
                    int count = 0;
                    for (int r = 0; r <= Math.Min(range.R2, range.Sheet.UsedRows) - range.R1; r++)
                        for (int col = 0; col <= Math.Min(range.C2, range.Sheet.UsedCols) - range.C1; col++)
                        {
                            if (!test(ctx.Book.Evaluate(range.Sheet, range.R1 + r, range.C1 + col, ctx.Depth))) continue;
                            if (c.Name == "COUNTIF") { count++; continue; }
                            var v = ctx.Book.Evaluate(target.Sheet, target.R1 + r, target.C1 + col, ctx.Depth);
                            if (v.IsError) return v;
                            if (v.IsNumber) { sum += v.Number; count++; }
                        }
                    return c.Name switch
                    {
                        "COUNTIF" => XValue.Num(count),
                        "SUMIF" => XValue.Num(sum),
                        _ => count == 0 ? XValue.Err(Errors.Div0) : XValue.Num(sum / count)
                    };
                }
                case "SUMIFS" or "COUNTIFS" or "AVERAGEIFS":
                {
                    int firstPair = c.Name == "COUNTIFS" ? 0 : 1;
                    Area? target = c.Name == "COUNTIFS" ? null : AreaOf(a[0], ctx);
                    var pairs = new List<(Area Range, Func<XValue, bool> Test)>();
                    for (int i = firstPair; i + 1 < a.Length; i += 2)
                    {
                        if (AreaOf(a[i], ctx) is not { } r) return XValue.Err(Errors.Value);
                        pairs.Add((r, Criteria(Arg(i + 1))));
                    }
                    if (pairs.Count == 0) return XValue.Err(Errors.Value);
                    var shape = pairs[0].Range;
                    int rows = shape.R2 - shape.R1, cols = shape.C2 - shape.C1;
                    int maxRows = Math.Min(rows, pairs.Max(p => p.Range.Sheet.UsedRows));
                    double sum = 0;
                    int count = 0;
                    for (int r = 0; r <= maxRows; r++)
                        for (int col = 0; col <= cols; col++)
                        {
                            bool ok = true;
                            foreach (var (range, test) in pairs)
                                if (!test(ctx.Book.Evaluate(range.Sheet, range.R1 + r, range.C1 + col, ctx.Depth))) { ok = false; break; }
                            if (!ok) continue;
                            if (target is not { } tg) { count++; continue; }
                            var v = ctx.Book.Evaluate(tg.Sheet, tg.R1 + r, tg.C1 + col, ctx.Depth);
                            if (v.IsError) return v;
                            if (v.IsNumber) { sum += v.Number; count++; }
                        }
                    return c.Name switch
                    {
                        "COUNTIFS" => XValue.Num(count),
                        "SUMIFS" => XValue.Num(sum),
                        _ => count == 0 ? XValue.Err(Errors.Div0) : XValue.Num(sum / count)
                    };
                }
            }
        }
        catch (ArgumentOutOfRangeException) { return XValue.Err(Errors.Num); }
        catch (OverflowException) { return XValue.Err(Errors.Num); }
        catch (IndexOutOfRangeException) { return XValue.Err(Errors.Value); }
        return XValue.Err(Errors.Name);
    }

    // ----- helpers -----

    public readonly record struct Area(Sheet Sheet, int R1, int C1, int R2, int C2);

    private static Area? AreaOf(Node node, Context ctx) => node switch
    {
        RangeNode r when (r.Sheet == null ? ctx.Sheet : ctx.Book.Find(r.Sheet)) is { } s => new Area(s, r.R1, r.C1, r.R2, r.C2),
        RefNode r when (r.Sheet == null ? ctx.Sheet : ctx.Book.Find(r.Sheet)) is { } s => new Area(s, r.Row, r.Col, r.Row, r.Col),
        _ => null
    };

    /// <summary>The cells of an area, only as far as the sheet has anything (whole columns stay cheap).</summary>
    private static IEnumerable<XValue> Cells(Area area, Context ctx)
    {
        int r2 = Math.Min(area.R2, area.Sheet.UsedRows - 1), c2 = Math.Min(area.C2, area.Sheet.UsedCols - 1);
        for (int r = area.R1; r <= r2; r++)
            for (int c = area.C1; c <= c2; c++)
                yield return ctx.Book.Evaluate(area.Sheet, r, c, ctx.Depth);
    }

    /// <summary>A one-row or one-column area as a list (for MATCH and lookups).</summary>
    private static List<XValue> Line(Area area, Context ctx)
    {
        var list = new List<XValue>();
        bool vertical = area.C1 == area.C2;
        int last = vertical ? Math.Min(area.R2, Math.Max(area.R1, area.Sheet.UsedRows - 1)) : Math.Min(area.C2, Math.Max(area.C1, area.Sheet.UsedCols - 1));
        int first = vertical ? area.R1 : area.C1;
        for (int i = first; i <= last; i++)
            list.Add(vertical ? ctx.Book.Evaluate(area.Sheet, i, area.C1, ctx.Depth) : ctx.Book.Evaluate(area.Sheet, area.R1, i, ctx.Depth));
        return list;
    }

    /// <summary>Every value the arguments stand for: ranges cell by cell, everything else as one value.</summary>
    private static IEnumerable<(XValue Value, bool FromRange)> Values(IEnumerable<Node> args, Context ctx)
    {
        foreach (var arg in args)
        {
            if (arg is RangeNode or RefNode && AreaOf(arg, ctx) is { } area)
            {
                foreach (var v in Cells(area, ctx)) yield return (v, true);
            }
            else if (arg is RangeNode or RefNode) yield return (XValue.Err(Errors.Ref), false);
            else if (arg is not MissingNode) yield return (Evaluate(arg, ctx), false);
        }
    }

    /// <summary>Numbers for SUM-like functions: in ranges only real numbers count; typed arguments are converted.</summary>
    private static List<double> Numbers(IEnumerable<Node> args, Context ctx, out XValue? error)
    {
        error = null;
        var list = new List<double>();
        foreach (var (v, fromRange) in Values(args, ctx))
        {
            if (v.IsError) { error = v; return list; }
            if (fromRange) { if (v.IsNumber) list.Add(v.Number); continue; }
            if (v.IsEmpty) continue;
            if (!Formula.ToNumber(v, out var n, out var err)) { error = err; return list; }
            list.Add(n);
        }
        return list;
    }

    private static XValue Aggregate(Node[] args, Context ctx, Func<List<double>, int, double> f, string emptyError = Errors.Num)
    {
        var nums = Numbers(args, ctx, out var error);
        if (error is { } e) return e;
        var result = f(nums, nums.Count);
        return double.IsNaN(result) ? XValue.Err(emptyError) : XValue.Num(result);
    }

    private static double Median(List<double> n)
    {
        if (n.Count == 0) return double.NaN;
        var sorted = n.OrderBy(x => x).ToList();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private static double Num(XValue v) => Formula.ToNumber(v, out var n, out _) ? n : 0;

    private static XValue Math1(XValue v, Func<double, double> f)
    {
        if (!Formula.ToNumber(v, out var x, out var err)) return err;
        var r = f(x);
        return double.IsNaN(r) ? XValue.Err(Errors.Num) : XValue.Num(r);
    }

    private static XValue Text1(XValue v, Func<string, XValue> f) => v.IsError ? v : f(Formula.ToText(v));

    private static bool DateOf(XValue v, out DateTime date, out XValue error)
    {
        date = default;
        error = default;
        if (v.IsText && CellInput.TryDate(v.Text!.Trim(), out var s, out _)) v = XValue.Num(s);
        if (!Formula.ToNumber(v, out var n, out error)) return false;
        if (n < 0 || n > 2958465) { error = XValue.Err(Errors.Num); return false; }
        date = CellFormat.SerialToDate(n);
        return true;
    }

    private static XValue Date1(XValue v, Func<DateTime, int> f) => DateOf(v, out var d, out var err) ? XValue.Num(f(d)) : err;

    /// <summary>Excel rounding: half away from zero, on the decimal value you see (2.675 → 2.68).</summary>
    private static double Round(double x, int digits, string mode)
    {
        digits = Math.Clamp(digits, -15, 15);
        if (Math.Abs(x) >= 7.9e27 || (digits > 0 && Math.Abs(x) * Math.Pow(10, digits) >= 7.9e27)) return x;
        var d = (decimal)x;
        decimal factor = 1;
        for (int i = 0; i < Math.Abs(digits); i++) factor *= 10;
        decimal scaled = digits >= 0 ? d * factor : d / factor;
        scaled = mode switch
        {
            "ROUNDUP" => scaled >= 0 ? Math.Ceiling(scaled) : Math.Floor(scaled),
            "ROUNDDOWN" => decimal.Truncate(scaled),
            _ => Math.Round(scaled, MidpointRounding.AwayFromZero)
        };
        return (double)(digits >= 0 ? scaled / factor : scaled * factor);
    }

    /// <summary>MATCH rules: 0 exact (with * and ? wildcards for text), 1 largest ≤ key in an ascending list, -1 smallest ≥ key.</summary>
    private static int Find(List<XValue> list, XValue key, int type)
    {
        if (type == 0)
        {
            if (key.IsText && key.Text!.IndexOfAny(['*', '?', '~']) >= 0)
            {
                var re = new Regex(Wildcard(key.Text, anchored: true), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                for (int i = 0; i < list.Count; i++) if (list[i].IsText && re.IsMatch(list[i].Text!)) return i;
                return -1;
            }
            return FindExact(list, key);
        }
        int best = -1;
        for (int i = 0; i < list.Count; i++)
        {
            var v = list[i];
            if (v.IsEmpty || (v.Kind != key.Kind && !(v.IsNumber && key.IsNumber))) continue;
            int cmp = Formula.Compare(v, key);
            if (type > 0) { if (cmp <= 0) best = i; else break; }
            else { if (cmp >= 0) best = i; else break; }
        }
        return best;
    }

    private static int FindExact(List<XValue> list, XValue key)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var v = list[i];
            if (v.IsEmpty && !key.IsEmpty) continue;
            if (v.Kind == key.Kind && Formula.Compare(v, key) == 0) return i;
            if (key.IsNumber && v.IsText && Formula.ToNumber(v, out var n, out _) && n == key.Number) return i;
        }
        return -1;
    }

    /// <summary>The test behind SUMIF / COUNTIF criteria: "&gt;5", "&lt;&gt;done", "=", "a*", 12…</summary>
    private static Func<XValue, bool> Criteria(XValue criteria)
    {
        if (criteria.IsNumber || criteria.Kind == XKind.Bool)
            return v => (v.Kind == criteria.Kind || (v.IsText && criteria.IsNumber && Formula.ToNumber(v, out var n, out _) && n == criteria.Number)) && Formula.Compare(v, criteria) == 0;
        if (criteria.IsEmpty) return v => v.IsEmpty;
        var text = Formula.ToText(criteria);
        string op = "=";
        foreach (var candidate in (string[])["<=", ">=", "<>", "<", ">", "="])
            if (text.StartsWith(candidate, StringComparison.Ordinal)) { op = candidate; text = text[candidate.Length..]; break; }
        if (CellInput.TryNumber(text.Trim(), out var number, out _) || CellInput.TryDate(text.Trim(), out number, out _))
        {
            return v =>
            {
                bool isNum = v.IsNumber || (v.IsText && Formula.ToNumber(v, out _, out _));
                if (!isNum) return op == "<>";
                Formula.ToNumber(v, out var n, out _);
                int cmp = n.CompareTo(number);
                return op switch { "=" => cmp == 0, "<>" => cmp != 0, "<" => cmp < 0, ">" => cmp > 0, "<=" => cmp <= 0, _ => cmp >= 0 };
            };
        }
        if (text.Length == 0) return op == "<>" ? v => !v.IsEmpty && !(v.IsText && v.Text!.Length == 0) : v => v.IsEmpty || (v.IsText && v.Text!.Length == 0);
        if (op is "=" or "<>")
        {
            var re = new Regex(Wildcard(text, anchored: true), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            bool equal = op == "=";
            return v => (!v.IsEmpty && re.IsMatch(Formula.ToText(v))) == equal;
        }
        var key = XValue.Str(text);
        return v =>
        {
            if (!v.IsText) return false;
            int cmp = Formula.Compare(v, key);
            return op switch { "<" => cmp < 0, ">" => cmp > 0, "<=" => cmp <= 0, _ => cmp >= 0 };
        };
    }

    /// <summary>Excel wildcards as a regex: * any text, ? one character, ~ escapes the next one.</summary>
    private static string Wildcard(string pattern, bool anchored)
    {
        var sb = new StringBuilder(anchored ? "^" : "");
        for (int i = 0; i < pattern.Length; i++)
        {
            char ch = pattern[i];
            if (ch == '~' && i + 1 < pattern.Length) { sb.Append(Regex.Escape(pattern[++i].ToString())); continue; }
            sb.Append(ch switch { '*' => ".*", '?' => ".", _ => Regex.Escape(ch.ToString()) });
        }
        if (anchored) sb.Append('$');
        return sb.ToString();
    }
}
