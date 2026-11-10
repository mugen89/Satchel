using System.Globalization;
using System.Text;

namespace Satchel.Services.Sheets;

public enum XKind : byte { Empty, Number, Text, Bool, Error }

/// <summary>A cell's value: empty, a number (dates are numbers too), text, TRUE/FALSE or an error like #DIV/0!.</summary>
public readonly record struct XValue(XKind Kind, double Number, string? Text)
{
    public static readonly XValue Empty = new(XKind.Empty, 0, null);
    public static readonly XValue True = new(XKind.Bool, 1, null);
    public static readonly XValue False = new(XKind.Bool, 0, null);

    public static XValue Num(double d) => double.IsFinite(d) ? new(XKind.Number, d, null) : Err(Errors.Num);
    public static XValue Str(string s) => new(XKind.Text, 0, s);
    public static XValue Bool(bool b) => b ? True : False;
    public static XValue Err(string code) => new(XKind.Error, 0, code);

    public bool IsError => Kind == XKind.Error;
    public bool IsEmpty => Kind == XKind.Empty;
    public bool IsNumber => Kind == XKind.Number;
    public bool IsText => Kind == XKind.Text;
    public string ErrorCode => Text ?? Errors.Value;

    public override string ToString() => Kind switch
    {
        XKind.Number => Number.ToString("R", CultureInfo.InvariantCulture),
        XKind.Text => Text ?? "",
        XKind.Bool => Number != 0 ? "TRUE" : "FALSE",
        XKind.Error => ErrorCode,
        _ => ""
    };
}

public static class Errors
{
    public const string Div0 = "#DIV/0!", Value = "#VALUE!", Ref = "#REF!", Name = "#NAME?", Num = "#NUM!", NA = "#N/A", Null = "#NULL!";
    /// <summary>A formula that (through other cells) uses its own result.</summary>
    public const string Cycle = "#CYCLE!";

    public static readonly string[] All = [Div0, Value, Ref, Name, Num, NA, Null, Cycle];
}

/// <summary>A1-style addresses (zero-based row and column inside).</summary>
public static class CellRef
{
    public const int MaxRows = 1_048_576, MaxCols = 16_384;

    public static string ColumnName(int col)
    {
        var sb = new StringBuilder(3);
        col++;
        while (col > 0)
        {
            int m = (col - 1) % 26;
            sb.Insert(0, (char)('A' + m));
            col = (col - 1) / 26;
        }
        return sb.ToString();
    }

    public static int ColumnIndex(ReadOnlySpan<char> letters)
    {
        int n = 0;
        foreach (var ch in letters)
        {
            int v = char.ToUpperInvariant(ch) - 'A' + 1;
            if (v < 1 || v > 26) return -1;
            n = n * 26 + v;
            if (n > MaxCols) return -1;
        }
        return n - 1;
    }

    public static string Name(int row, int col) => ColumnName(col) + (row + 1).ToString(CultureInfo.InvariantCulture);

    /// <summary>"B12" (or "$B$12") → row 11, column 1.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out int row, out int col)
    {
        row = col = -1;
        int i = 0;
        if (i < text.Length && text[i] == '$') i++;
        int letters = i;
        while (i < text.Length && char.IsAsciiLetter(text[i])) i++;
        if (i == letters || i - letters > 3) return false;
        col = ColumnIndex(text[letters..i]);
        if (i < text.Length && text[i] == '$') i++;
        int digits = i;
        while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
        if (i == digits || i != text.Length) return false;
        if (!int.TryParse(text[digits..i], NumberStyles.None, CultureInfo.InvariantCulture, out var r) || r < 1 || r > MaxRows) return false;
        row = r - 1;
        return col >= 0;
    }

    /// <summary>"A1:C4" for a range, "A1" for one cell.</summary>
    public static string RangeName(int r1, int c1, int r2, int c2) =>
        r1 == r2 && c1 == c2 ? Name(r1, c1) : $"{Name(Math.Min(r1, r2), Math.Min(c1, c2))}:{Name(Math.Max(r1, r2), Math.Max(c1, c2))}";
}
