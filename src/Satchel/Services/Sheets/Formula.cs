using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Satchel.Services.Sheets;

/// <summary>
/// Spreadsheet formulas: tokenizer, parser (Excel precedence), evaluator with the common functions, and reference
/// rewriting for copy / fill / insert / delete. Formulas are written with "," (";" works too) and "." decimals.
/// </summary>
public static partial class Formula
{
    public const string SyntaxError = "#ERROR!";

    // ===== tokens =====

    internal enum T { Number, Text, Bool, Error, Ref, ColRef, Sheet, Func, Name, Op, LParen, RParen, Comma, Colon, End }

    internal readonly record struct Token(T Kind, string Text, int Start, int Length)
    {
        public int End => Start + Length;
    }

    private sealed class FormulaException(string code) : Exception(code)
    {
        public string Code { get; } = code;
    }

    internal static List<Token> Tokenize(string f)
    {
        var list = new List<Token>();
        int i = 0;
        while (i < f.Length)
        {
            char ch = f[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            int start = i;
            if (char.IsAsciiDigit(ch) || (ch == '.' && i + 1 < f.Length && char.IsAsciiDigit(f[i + 1])))
            {
                while (i < f.Length && (char.IsAsciiDigit(f[i]) || f[i] == '.')) i++;
                if (i + 1 < f.Length && (f[i] is 'e' or 'E') && (char.IsAsciiDigit(f[i + 1]) || (f[i + 1] is '+' or '-' && i + 2 < f.Length && char.IsAsciiDigit(f[i + 2]))))
                {
                    i += 2;
                    while (i < f.Length && char.IsAsciiDigit(f[i])) i++;
                }
                list.Add(new Token(T.Number, f[start..i], start, i - start));
                continue;
            }
            if (ch == '"')
            {
                var sb = new StringBuilder();
                i++;
                bool closed = false;
                while (i < f.Length)
                {
                    if (f[i] == '"')
                    {
                        if (i + 1 < f.Length && f[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                        i++;
                        closed = true;
                        break;
                    }
                    sb.Append(f[i++]);
                }
                if (!closed) throw new FormulaException(SyntaxError);
                list.Add(new Token(T.Text, sb.ToString(), start, i - start));
                continue;
            }
            if (ch == '\'')
            {
                var sb = new StringBuilder();
                i++;
                while (i < f.Length)
                {
                    if (f[i] == '\'')
                    {
                        if (i + 1 < f.Length && f[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                        i++;
                        break;
                    }
                    sb.Append(f[i++]);
                }
                if (i < f.Length && f[i] == '!')
                {
                    i++;
                    list.Add(new Token(T.Sheet, sb.ToString(), start, i - start));
                    continue;
                }
                throw new FormulaException(SyntaxError);
            }
            if (ch == '#')
            {
                var match = Errors.All.FirstOrDefault(e => string.Compare(f, i, e, 0, e.Length, StringComparison.OrdinalIgnoreCase) == 0);
                if (match == null) throw new FormulaException(SyntaxError);
                i += match.Length;
                list.Add(new Token(T.Error, match, start, match.Length));
                continue;
            }
            if (char.IsLetter(ch) || ch is '_' or '$')
            {
                while (i < f.Length && (char.IsLetterOrDigit(f[i]) || f[i] is '_' or '.' or '$')) i++;
                var word = f[start..i];
                if (i < f.Length && f[i] == '!')
                {
                    i++;
                    list.Add(new Token(T.Sheet, word, start, i - start));
                    continue;
                }
                int j = i;
                while (j < f.Length && f[j] == ' ') j++;
                if (j < f.Length && f[j] == '(' && !word.Contains('$'))
                {
                    list.Add(new Token(T.Func, word, start, i - start));
                    continue;
                }
                if (word.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || word.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
                    list.Add(new Token(T.Bool, word, start, i - start));
                else if (CellRef.TryParse(word, out _, out _)) list.Add(new Token(T.Ref, word, start, i - start));
                else if (ColumnRef().IsMatch(word) && CellRef.ColumnIndex(word.Replace("$", "")) >= 0) list.Add(new Token(T.ColRef, word, start, i - start));
                else list.Add(new Token(T.Name, word, start, i - start));
                continue;
            }
            switch (ch)
            {
                case '(': list.Add(new Token(T.LParen, "(", i, 1)); i++; continue;
                case ')': list.Add(new Token(T.RParen, ")", i, 1)); i++; continue;
                case ',' or ';': list.Add(new Token(T.Comma, ",", i, 1)); i++; continue;
                case ':': list.Add(new Token(T.Colon, ":", i, 1)); i++; continue;
                case '<' when i + 1 < f.Length && f[i + 1] is '=' or '>': list.Add(new Token(T.Op, f.Substring(i, 2), i, 2)); i += 2; continue;
                case '>' when i + 1 < f.Length && f[i + 1] == '=': list.Add(new Token(T.Op, ">=", i, 2)); i += 2; continue;
                case '+' or '-' or '*' or '/' or '^' or '&' or '=' or '<' or '>' or '%': list.Add(new Token(T.Op, ch.ToString(), i, 1)); i++; continue;
            }
            throw new FormulaException(SyntaxError);
        }
        list.Add(new Token(T.End, "", f.Length, 0));
        return list;
    }

    [GeneratedRegex(@"^\$?[A-Za-z]{1,3}$")]
    private static partial Regex ColumnRef();

    // ===== syntax tree =====

    public abstract class Node;
    internal sealed class NumNode(double value) : Node { public readonly double Value = value; }
    internal sealed class StrNode(string value) : Node { public readonly string Value = value; }
    internal sealed class BoolNode(bool value) : Node { public readonly bool Value = value; }
    internal sealed class ErrNode(string code) : Node { public readonly string Code = code; }
    internal sealed class MissingNode : Node { public static readonly MissingNode Instance = new(); }
    internal sealed class RefNode(string? sheet, int row, int col) : Node { public readonly string? Sheet = sheet; public readonly int Row = row, Col = col; }
    internal sealed class RangeNode(string? sheet, int r1, int c1, int r2, int c2) : Node
    {
        public readonly string? Sheet = sheet;
        public readonly int R1 = Math.Min(r1, r2), C1 = Math.Min(c1, c2), R2 = Math.Max(r1, r2), C2 = Math.Max(c1, c2);
    }
    internal sealed class UnaryNode(string op, Node operand) : Node { public readonly string Op = op; public readonly Node Operand = operand; }
    internal sealed class BinaryNode(string op, Node left, Node right) : Node { public readonly string Op = op; public readonly Node Left = left, Right = right; }
    internal sealed class CallNode(string name, Node[] args) : Node { public readonly string Name = name; public readonly Node[] Args = args; }

    /// <summary>Parses "=…" (the leading = is optional). Syntax errors give a node that evaluates to #ERROR!.</summary>
    public static Node Parse(string raw)
    {
        var text = raw.StartsWith('=') ? raw[1..] : raw;
        try
        {
            var p = new Parser(Tokenize(text));
            var node = p.Comparison();
            if (p.Peek.Kind != T.End) return new ErrNode(SyntaxError);
            return node;
        }
        catch (FormulaException ex)
        {
            return new ErrNode(ex.Code);
        }
    }

    private sealed class Parser(List<Token> tokens)
    {
        private int _p;
        public Token Peek => tokens[_p];
        private Token Next() => tokens[_p++];
        private bool IsOp(string op) => Peek.Kind == T.Op && Peek.Text == op;

        private void Expect(T kind)
        {
            if (Peek.Kind != kind) throw new FormulaException(SyntaxError);
            _p++;
        }

        public Node Comparison()
        {
            var left = Concat();
            while (Peek.Kind == T.Op && Peek.Text is "=" or "<>" or "<" or ">" or "<=" or ">=")
            {
                var op = Next().Text;
                left = new BinaryNode(op, left, Concat());
            }
            return left;
        }

        private Node Concat()
        {
            var left = Additive();
            while (IsOp("&")) { Next(); left = new BinaryNode("&", left, Additive()); }
            return left;
        }

        private Node Additive()
        {
            var left = Term();
            while (IsOp("+") || IsOp("-")) { var op = Next().Text; left = new BinaryNode(op, left, Term()); }
            return left;
        }

        private Node Term()
        {
            var left = Power();
            while (IsOp("*") || IsOp("/")) { var op = Next().Text; left = new BinaryNode(op, left, Power()); }
            return left;
        }

        private Node Power()
        {
            var left = Unary();
            while (IsOp("^")) { Next(); left = new BinaryNode("^", left, Unary()); }
            return left;
        }

        private Node Unary()
        {
            if (IsOp("-") || IsOp("+")) { var op = Next().Text; return new UnaryNode(op, Unary()); }
            var node = Primary();
            while (IsOp("%")) { Next(); node = new UnaryNode("%", node); }
            return node;
        }

        private Node Primary()
        {
            var t = Next();
            switch (t.Kind)
            {
                case T.Number:
                    return double.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? new NumNode(n) : throw new FormulaException(SyntaxError);
                case T.Text: return new StrNode(t.Text);
                case T.Bool: return new BoolNode(t.Text.Equals("TRUE", StringComparison.OrdinalIgnoreCase));
                case T.Error: return new ErrNode(t.Text);
                case T.LParen:
                    var inner = Comparison();
                    Expect(T.RParen);
                    return inner;
                case T.Func:
                    var name = CleanName(t.Text);
                    Expect(T.LParen);
                    var args = new List<Node>();
                    if (Peek.Kind != T.RParen)
                    {
                        while (true)
                        {
                            args.Add(Peek.Kind is T.Comma or T.RParen ? MissingNode.Instance : Comparison());
                            if (Peek.Kind == T.Comma) { Next(); continue; }
                            break;
                        }
                    }
                    Expect(T.RParen);
                    return new CallNode(name, args.ToArray());
                case T.Sheet:
                    return Reference(t.Text, Next());
                case T.Ref or T.ColRef:
                    return Reference(null, t);
                case T.Name:
                    return new ErrNode(Errors.Name);
            }
            throw new FormulaException(SyntaxError);
        }

        private Node Reference(string? sheet, Token first)
        {
            if (first.Kind == T.Ref)
            {
                CellRef.TryParse(first.Text, out var r1, out var c1);
                if (Peek.Kind == T.Colon && tokens[_p + 1].Kind == T.Ref)
                {
                    Next();
                    CellRef.TryParse(Next().Text, out var r2, out var c2);
                    return new RangeNode(sheet, r1, c1, r2, c2);
                }
                return new RefNode(sheet, r1, c1);
            }
            if (first.Kind == T.ColRef && Peek.Kind == T.Colon && tokens[_p + 1].Kind == T.ColRef)
            {
                Next();
                var second = Next();
                int ca = CellRef.ColumnIndex(first.Text.Replace("$", "")), cb = CellRef.ColumnIndex(second.Text.Replace("$", ""));
                return new RangeNode(sheet, 0, ca, CellRef.MaxRows - 1, cb);
            }
            throw new FormulaException(first.Kind == T.ColRef ? Errors.Name : SyntaxError);
        }
    }

    private static string CleanName(string name)
    {
        var upper = name.ToUpperInvariant();
        foreach (var prefix in (string[])["_XLFN._XLWS.", "_XLFN.", "_XLWS."])
            if (upper.StartsWith(prefix, StringComparison.Ordinal)) upper = upper[prefix.Length..];
        return upper;
    }

    // ===== evaluation =====

    public sealed record Context(Workbook Book, Sheet Sheet, int Row, int Col, int Depth);

    private static Sheet? SheetOf(string? name, Context ctx) => name == null ? ctx.Sheet : ctx.Book.Find(name);

    public static XValue Evaluate(Node node, Context ctx)
    {
        switch (node)
        {
            case NumNode n: return XValue.Num(n.Value);
            case StrNode s: return XValue.Str(s.Value);
            case BoolNode b: return XValue.Bool(b.Value);
            case ErrNode e: return XValue.Err(e.Code);
            case MissingNode: return XValue.Empty;
            case RefNode r:
                return SheetOf(r.Sheet, ctx) is { } sheet ? ctx.Book.Evaluate(sheet, r.Row, r.Col, ctx.Depth) : XValue.Err(Errors.Ref);
            case RangeNode rg:
                if (SheetOf(rg.Sheet, ctx) is not { } rs) return XValue.Err(Errors.Ref);
                if (rg.R1 == rg.R2 && rg.C1 == rg.C2) return ctx.Book.Evaluate(rs, rg.R1, rg.C1, ctx.Depth);
                // A range where one value is expected: the cell in this row (or column), like Excel's implicit intersection.
                if (rg.C1 == rg.C2 && ctx.Row >= rg.R1 && ctx.Row <= rg.R2 && rs == ctx.Sheet) return ctx.Book.Evaluate(rs, ctx.Row, rg.C1, ctx.Depth);
                if (rg.R1 == rg.R2 && ctx.Col >= rg.C1 && ctx.Col <= rg.C2 && rs == ctx.Sheet) return ctx.Book.Evaluate(rs, rg.R1, ctx.Col, ctx.Depth);
                return XValue.Err(Errors.Value);
            case UnaryNode u:
            {
                var v = Evaluate(u.Operand, ctx);
                if (!ToNumber(v, out var x, out var err)) return err;
                return u.Op switch { "-" => XValue.Num(-x), "%" => XValue.Num(x / 100), _ => XValue.Num(x) };
            }
            case BinaryNode b:
                return Binary(b.Op, Evaluate(b.Left, ctx), Evaluate(b.Right, ctx));
            case CallNode c:
                return Functions.Call(c, ctx);
        }
        return XValue.Err(Errors.Value);
    }

    private static XValue Binary(string op, XValue a, XValue b)
    {
        if (op == "&")
        {
            if (a.IsError) return a;
            if (b.IsError) return b;
            return XValue.Str(ToText(a) + ToText(b));
        }
        if (op is "=" or "<>" or "<" or ">" or "<=" or ">=")
        {
            if (a.IsError) return a;
            if (b.IsError) return b;
            int c = Compare(a, b);
            return XValue.Bool(op switch { "=" => c == 0, "<>" => c != 0, "<" => c < 0, ">" => c > 0, "<=" => c <= 0, _ => c >= 0 });
        }
        if (!ToNumber(a, out var x, out var e1)) return e1;
        if (!ToNumber(b, out var y, out var e2)) return e2;
        return op switch
        {
            "+" => XValue.Num(x + y),
            "-" => XValue.Num(x - y),
            "*" => XValue.Num(x * y),
            "/" => y == 0 ? XValue.Err(Errors.Div0) : XValue.Num(x / y),
            "^" => x == 0 && y < 0 ? XValue.Err(Errors.Div0) : XValue.Num(Math.Pow(x, y)),
            _ => XValue.Err(Errors.Value)
        };
    }

    // ===== coercion =====

    public static bool ToNumber(XValue v, out double number, out XValue error)
    {
        error = default;
        switch (v.Kind)
        {
            case XKind.Number: number = v.Number; return true;
            case XKind.Bool: number = v.Number; return true;
            case XKind.Empty: number = 0; return true;
            case XKind.Text:
                if (CellInput.TryNumber(v.Text!.Trim(), out number, out _)) return true;
                if (v.Text!.Trim().Length == 0) { number = 0; error = XValue.Err(Errors.Value); return false; }
                error = XValue.Err(Errors.Value);
                return false;
            default:
                number = 0;
                error = v;
                return false;
        }
    }

    public static string ToText(XValue v) => v.Kind switch
    {
        XKind.Number => CellFormat.General(v.Number),
        XKind.Bool => v.Number != 0 ? "TRUE" : "FALSE",
        XKind.Text => v.Text ?? "",
        XKind.Error => v.ErrorCode,
        _ => ""
    };

    public static bool ToBool(XValue v, out bool result, out XValue error)
    {
        error = default;
        switch (v.Kind)
        {
            case XKind.Bool or XKind.Number: result = v.Number != 0; return true;
            case XKind.Empty: result = false; return true;
            case XKind.Text when v.Text!.Equals("TRUE", StringComparison.OrdinalIgnoreCase): result = true; return true;
            case XKind.Text when v.Text!.Equals("FALSE", StringComparison.OrdinalIgnoreCase): result = false; return true;
            case XKind.Error: result = false; error = v; return false;
            default: result = false; error = XValue.Err(Errors.Value); return false;
        }
    }

    /// <summary>Excel's ordering: numbers before text before TRUE/FALSE; text ignores case. Empty acts as 0, "" or FALSE.</summary>
    public static int Compare(XValue a, XValue b)
    {
        if (a.IsEmpty && b.IsEmpty) return 0;
        if (a.IsEmpty) a = b.Kind switch { XKind.Text => XValue.Str(""), XKind.Bool => XValue.False, _ => XValue.Num(0) };
        if (b.IsEmpty) b = a.Kind switch { XKind.Text => XValue.Str(""), XKind.Bool => XValue.False, _ => XValue.Num(0) };
        int Rank(XValue v) => v.Kind switch { XKind.Number => 0, XKind.Text => 1, XKind.Bool => 2, _ => 3 };
        int ra = Rank(a), rb = Rank(b);
        if (ra != rb) return ra.CompareTo(rb);
        return a.Kind == XKind.Text
            ? string.Compare(a.Text, b.Text, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase)
            : a.Number.CompareTo(b.Number);
    }

    // ===== rewriting references =====

    /// <summary>A reference as it appears in a formula (one cell, a range, or whole columns), with its $ marks.</summary>
    public readonly record struct RefSpan(string? Sheet, int R1, int C1, bool AbsR1, bool AbsC1, int R2, int C2, bool AbsR2, bool AbsC2, bool IsRange, bool IsColumns);

    /// <summary>Rewrites every reference in a formula; a null result turns that reference into #REF!.</summary>
    public static string Rewrite(string raw, Func<RefSpan, RefSpan?> map, Func<string?, string?>? renameSheet = null)
    {
        if (!raw.StartsWith('=')) return raw;
        var body = raw[1..];
        List<Token> tokens;
        try { tokens = Tokenize(body); }
        catch (FormulaException) { return raw; }
        var sb = new StringBuilder("=");
        int last = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            string? sheet = null;
            int start = t.Start;
            int k = i;
            if (t.Kind == T.Sheet && i + 1 < tokens.Count && tokens[i + 1].Kind is T.Ref or T.ColRef)
            {
                sheet = t.Text;
                k = i + 1;
            }
            var first = tokens[k];
            if (first.Kind is not (T.Ref or T.ColRef))
            {
                if (t.Kind is T.Func or T.Bool) { sb.Append(body, last, t.Start - last).Append(t.Kind == T.Func ? CleanName(t.Text) : t.Text.ToUpperInvariant()); last = t.End; }
                else if (t.Kind == T.Comma) { sb.Append(body, last, t.Start - last).Append(','); last = t.End; }
                continue;
            }
            bool range = k + 2 < tokens.Count && tokens[k + 1].Kind == T.Colon && tokens[k + 2].Kind == first.Kind;
            if (first.Kind == T.ColRef && !range) continue;
            var second = range ? tokens[k + 2] : first;
            var span = Parts(sheet, first.Text, second.Text, range, first.Kind == T.ColRef);
            var mapped = map(span);
            var newSheet = renameSheet != null ? renameSheet(sheet) : sheet;
            sb.Append(body, last, start - last);
            sb.Append(mapped is { } m ? Serialize(m with { Sheet = newSheet }) : Errors.Ref);
            last = second.End;
            i = range ? k + 2 : k;
        }
        sb.Append(body, last, body.Length - last);
        return sb.ToString();
    }

    private static RefSpan Parts(string? sheet, string a, string b, bool range, bool columns)
    {
        static (int Row, int Col, bool AbsRow, bool AbsCol) One(string text, bool column)
        {
            bool absCol = text.StartsWith('$');
            var rest = absCol ? text[1..] : text;
            int letters = 0;
            while (letters < rest.Length && char.IsAsciiLetter(rest[letters])) letters++;
            int col = CellRef.ColumnIndex(rest.AsSpan(0, letters));
            if (column) return (0, col, false, absCol);
            var digits = rest[letters..];
            bool absRow = digits.StartsWith('$');
            int row = int.Parse(absRow ? digits.AsSpan(1) : digits.AsSpan(), provider: CultureInfo.InvariantCulture) - 1;
            return (row, col, absRow, absCol);
        }
        var p = One(a, columns);
        var q = range ? One(b, columns) : p;
        return new RefSpan(sheet, p.Row, p.Col, p.AbsRow, p.AbsCol, q.Row, q.Col, q.AbsRow, q.AbsCol, range, columns);
    }

    private static string Serialize(RefSpan s)
    {
        string Col(int c, bool abs) => (abs ? "$" : "") + CellRef.ColumnName(c);
        string Cell(int r, int c, bool absR, bool absC) => Col(c, absC) + (absR ? "$" : "") + (r + 1).ToString(CultureInfo.InvariantCulture);
        var prefix = s.Sheet == null ? "" : QuoteSheet(s.Sheet) + "!";
        if (s.IsColumns) return prefix + Col(s.C1, s.AbsC1) + ":" + Col(s.C2, s.AbsC2);
        return prefix + Cell(s.R1, s.C1, s.AbsR1, s.AbsC1) + (s.IsRange ? ":" + Cell(s.R2, s.C2, s.AbsR2, s.AbsC2) : "");
    }

    public static string QuoteSheet(string name) =>
        SimpleSheetName().IsMatch(name) && !CellRef.TryParse(name, out _, out _) ? name : "'" + name.Replace("'", "''") + "'";

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.]*$")]
    private static partial Regex SimpleSheetName();

    /// <summary>Upper-cases functions and references and uses "," between arguments.</summary>
    public static string Normalize(string raw) => Rewrite(raw, s => s);

    /// <summary>Copying a formula by (rows, cols): relative references move with it; ones pushed off the sheet become #REF!.</summary>
    public static string Shift(string raw, int dRow, int dCol)
    {
        if ((dRow == 0 && dCol == 0) || !raw.StartsWith('=')) return raw;
        return Rewrite(raw, s =>
        {
            int r1 = s.AbsR1 || s.IsColumns ? s.R1 : s.R1 + dRow, r2 = s.AbsR2 || s.IsColumns ? s.R2 : s.R2 + dRow;
            int c1 = s.AbsC1 ? s.C1 : s.C1 + dCol, c2 = s.AbsC2 ? s.C2 : s.C2 + dCol;
            if (r1 < 0 || r2 < 0 || c1 < 0 || c2 < 0 || r1 >= CellRef.MaxRows || r2 >= CellRef.MaxRows || c1 >= CellRef.MaxCols || c2 >= CellRef.MaxCols) return null;
            return s with { R1 = r1, R2 = r2, C1 = c1, C2 = c2 };
        });
    }

    /// <summary>
    /// Rows (or columns) were inserted (<paramref name="count"/> &gt; 0) or deleted (&lt; 0) at <paramref name="index"/> on
    /// <paramref name="target"/>: references there move; references to deleted cells become #REF!.
    /// </summary>
    public static string AdjustForResize(string raw, string formulaSheet, string target, bool rows, int index, int count)
    {
        if (!raw.StartsWith('=') || count == 0) return raw;
        return Rewrite(raw, s =>
        {
            var sheet = s.Sheet ?? formulaSheet;
            if (!string.Equals(sheet, target, StringComparison.OrdinalIgnoreCase) || (rows && s.IsColumns)) return s;
            int a = rows ? s.R1 : s.C1, b = rows ? s.R2 : s.C2;
            if (count > 0)
            {
                if (a >= index) a += count;
                if (b >= index) b += count;
            }
            else
            {
                int n = -count, end = index + n;
                if (a >= index && b < end) return null;
                a = a >= end ? a - n : a >= index ? index : a;
                b = b >= end ? b - n : b >= index ? index - 1 : b;
                if (b < a) return null;
            }
            return rows ? s with { R1 = a, R2 = b } : s with { C1 = a, C2 = b };
        });
    }

    /// <summary>A sheet was renamed: references to it follow.</summary>
    public static string RenameSheet(string raw, string oldName, string newName) =>
        raw.StartsWith('=') ? Rewrite(raw, s => s, sheet => sheet != null && string.Equals(sheet, oldName, StringComparison.OrdinalIgnoreCase) ? newName : sheet) : raw;

    /// <summary>The references a formula uses (for highlighting), on its own sheet when no sheet is named.</summary>
    public static IEnumerable<RefSpan> References(string raw)
    {
        var found = new List<RefSpan>();
        Rewrite(raw, s => { found.Add(s); return s; });
        return found;
    }
}
