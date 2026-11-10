using System.IO;
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Satchel.Services.Import;

/// <summary>
/// Word 97–2003 (.doc) → Markdown, read straight from the binary format ([MS-DOC]): the text through the piece table,
/// paragraphs, tables, headings (built-in heading styles and outline levels), bulleted and numbered lists, bold /
/// italic / strike, and links. Pictures stay behind (they live in a separate drawing format).
/// </summary>
public static partial class DocConverter
{
    private sealed record Piece(int CpStart, int CpEnd, int Fc, bool Compressed);
    private sealed record PapRun(int FcStart, int FcEnd, PapInfo Props);
    private sealed record ChpRun(int FcStart, int FcEnd, ChpInfo Props);

    private sealed class PapInfo
    {
        public int Istd;
        public bool InTable, RowEnd;
        public int Depth;
        public int Outline = 9;
        public int Ilfo, Ilvl;
    }

    private sealed class ChpInfo
    {
        public static readonly ChpInfo Plain = new();
        public bool Bold, Italic, Strike, Hidden;
    }

    private sealed record StyleInfo(int Sti, string Name, int Outline, int Ilfo, int Ilvl);

    public static ConvertedDoc Convert(byte[] bytes)
    {
        TextFiles.EnsureCodePages();
        var cf = new CompoundFile(bytes);
        var word = cf.Stream("WordDocument") ?? throw new InvalidDataException("This file isn't a Word document.");
        if (word.Length < 0x100 || U16(word, 0) != 0xA5EC) throw new InvalidDataException("This file isn't a Word document.");
        int nFib = U16(word, 2);
        int flags = U16(word, 0x0A);
        if ((flags & 0x0100) != 0) throw new InvalidDataException("This document is password-protected. Open it in Word, remove the password and save it again.");
        if (nFib < 0x00C1) return OldWord(word);

        // The FIB: a few variable-length arrays, then pairs of (offset, length) into the table stream.
        int pos = 32;
        int csw = U16(word, pos); pos += 2 + csw * 2;
        int cslw = U16(word, pos); pos += 2;
        int rgLw = pos; pos += cslw * 4;
        int cbRgFcLcb = U16(word, pos); pos += 2;
        int rgFcLcb = pos;
        int ccpText = I32(word, rgLw + 3 * 4);
        (int Fc, int Lcb) FcLcb(int index) => index < cbRgFcLcb && rgFcLcb + index * 8 + 8 <= word.Length
            ? (I32(word, rgFcLcb + index * 8), I32(word, rgFcLcb + index * 8 + 4)) : (0, 0);

        var table = cf.Stream((flags & 0x0200) != 0 ? "1Table" : "0Table") ?? throw new InvalidDataException("This Word document is damaged (no table stream).");
        var (fcClx, lcbClx) = FcLcb(33);
        var pieces = Pieces(table, fcClx, lcbClx);
        var paps = PapRuns(word, table, FcLcb(13));
        var chps = ChpRuns(word, table, FcLcb(12));
        var styles = Styles(table, FcLcb(1));
        var lists = ListFormats(table, FcLcb(73), FcLcb(74));
        // "Compressed" (8-bit) text is Windows-1252 — the same characters the spec lists for its special bytes.
        var cp1252 = Encoding.GetEncoding(1252).GetChars(Enumerable.Range(0, 256).Select(b => (byte)b).ToArray());

        var output = new MarkdownOut();
        var line = new InlineBuilder();
        var fields = new Stack<(StringBuilder Instruction, bool InResult, int ResultStart)>();
        List<List<string>>? tableRows = null;
        List<string>? row = null;
        var cell = new List<string>();
        var counters = new Dictionary<int, int[]>();

        void FlushTable()
        {
            if (tableRows == null) return;
            if (row is { Count: > 0 }) tableRows.Add(row);
            if (tableRows.Count > 0 && tableRows.Any(r => r.Count > 1)) output.Table(tableRows);
            else foreach (var r in tableRows) foreach (var c in r) output.Paragraph(c);
            tableRows = null;
            row = null;
            cell.Clear();
        }

        void EndParagraph(int fc, char mark)
        {
            var pap = FindPap(paps, fc);
            if (pap.InTable || pap.Depth > 0)
            {
                tableRows ??= [];
                row ??= [];
                var text = line.ToMarkdown(inTable: true);
                if (mark == '\u0007' && pap.RowEnd)
                {
                    // The row-end mark: the row is complete.
                    if (row.Count > 0) tableRows.Add(row);
                    row = [];
                    cell.Clear();
                }
                else if (mark == '\u0007')
                {
                    if (text.Length > 0) cell.Add(text);
                    row.Add(string.Join(" ", cell));
                    cell.Clear();
                }
                else if (text.Length > 0) cell.Add(pap.Ilfo > 0 ? "• " + text : text);
                line = new InlineBuilder();
                return;
            }
            FlushTable();
            if (!line.HasContent) { line = new InlineBuilder(); return; }

            var style = styles.Count > pap.Istd ? styles[pap.Istd] : null;
            int heading = pap.Outline < 9 ? pap.Outline + 1
                : style is { Sti: >= 1 and <= 9 } ? style.Sti
                : style is { Sti: 62 } || style?.Name == "Title" ? 1
                : style is { Sti: 74 } || style?.Name == "Subtitle" ? 2
                : style is { Outline: < 9 } ? style.Outline + 1
                : HeadingName().Match(style?.Name ?? "") is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;
            int ilfo = pap.Ilfo > 0 ? pap.Ilfo : style?.Ilfo ?? 0;
            int ilvl = pap.Ilfo > 0 ? pap.Ilvl : style?.Ilvl ?? 0;
            bool bulletStyle = style is { Sti: 48 or (>= 54 and <= 57) };
            bool numberStyle = style is { Sti: 49 or (>= 58 and <= 61) };

            if (heading > 0) output.Heading(heading, line.ToMarkdown());
            else if (ilfo > 0 || bulletStyle || numberStyle)
            {
                int level = Math.Clamp(ilvl, 0, 8);
                bool numbered = numberStyle || (ilfo > 0 && lists.TryGetValue(ilfo, out var levels) && level < levels.Length && levels[level].Numbered);
                int? number = null;
                if (numbered)
                {
                    if (!counters.TryGetValue(ilfo, out var c)) counters[ilfo] = c = new int[9];
                    int start = ilfo > 0 && lists.TryGetValue(ilfo, out var lv) && level < lv.Length ? lv[level].Start : 1;
                    c[level] = c[level] == 0 ? start : c[level] + 1;
                    for (int d = level + 1; d < 9; d++) c[d] = 0;
                    number = c[level];
                }
                output.ListItem(level, number, line.ToMarkdown(), ilfo > 0 ? "lfo" + ilfo : numbered ? "style-numbers" : "style-bullets");
            }
            else if (style != null && (style.Name.Contains("Quote", StringComparison.OrdinalIgnoreCase))) output.Quote(line.ToMarkdown());
            else output.Paragraph(line.ToMarkdown());
            line = new InlineBuilder();
        }

        int chpIndex = 0;
        foreach (var piece in pieces)
        {
            if (piece.CpStart >= ccpText) break;
            int end = Math.Min(piece.CpEnd, ccpText);
            int fc0 = piece.Fc;
            chpIndex = FindChpIndex(chps, fc0);
            var buffer = new StringBuilder();
            ChpInfo? bufferProps = null;
            string? bufferLink = null;

            void FlushText()
            {
                if (buffer.Length == 0) return;
                var p = bufferProps ?? ChpInfo.Plain;
                if (!p.Hidden) line.Add(buffer.ToString(), p.Bold, p.Italic, p.Strike, false, bufferLink);
                buffer.Clear();
            }

            for (int cp = piece.CpStart; cp < end; cp++)
            {
                int i = cp - piece.CpStart;
                int fc = piece.Compressed ? fc0 + i : fc0 + i * 2;
                if (fc < 0 || fc + (piece.Compressed ? 1 : 2) > word.Length) break;
                char ch = piece.Compressed ? cp1252[word[fc]] : (char)U16(word, fc);

                while (chpIndex < chps.Count - 1 && fc >= chps[chpIndex].FcEnd) chpIndex++;
                var props = chpIndex < chps.Count && fc >= chps[chpIndex].FcStart && fc < chps[chpIndex].FcEnd ? chps[chpIndex].Props : ChpInfo.Plain;

                switch (ch)
                {
                    case '\u0013': // field begin
                        FlushText();
                        fields.Push((new StringBuilder(), false, line.Count));
                        continue;
                    case '\u0014': // field separator: the result follows
                        FlushText();
                        if (fields.Count > 0) { var f = fields.Pop(); fields.Push((f.Instruction, true, line.Count)); }
                        continue;
                    case '\u0015': // field end
                        FlushText();
                        if (fields.Count > 0)
                        {
                            var f = fields.Pop();
                            if (f.InResult && HyperlinkFrom(f.Instruction.ToString()) is { } url) line.LinkFrom(f.ResultStart, url);
                        }
                        continue;
                }
                if (fields.Count > 0 && !fields.Peek().InResult)
                {
                    fields.Peek().Instruction.Append(ch);
                    continue;
                }
                if (fields.Any(f => !f.InResult)) continue;

                if (ch is '\r' or '\u0007' or '\u000C')
                {
                    FlushText();
                    fields.Clear();
                    EndParagraph(fc, ch);
                    continue;
                }
                if (ch is '\u000B' or '\n')
                {
                    FlushText();
                    line.LineBreak();
                    continue;
                }
                // Pictures, drawn objects, footnote and comment marks; optional hyphens.
                if (ch is '\u0001' or '\u0008' or '\u0002' or '\u0005' or '\u0003' or '\u0004' or '\u001F') continue;
                if (ch == '\u001E') ch = '-';
                if (ch == '\t' || ch == '\u000E') ch = ' ';
                if (ch < ' ') continue;
                if (!ReferenceEquals(props, bufferProps))
                {
                    FlushText();
                    bufferProps = props;
                }
                buffer.Append(ch);
            }
            FlushText();
        }
        if (line.HasContent) EndParagraph(int.MaxValue, '\r');
        FlushTable();

        var notes = new List<string>();
        if ((flags & 0x0008) != 0) notes.Add("Pictures in .doc files aren't brought over — save the file as .docx in Word to keep them.");
        return new ConvertedDoc(output.ToString(), null, [], notes);
    }

    // ----- text -----

    private static List<Piece> Pieces(byte[] table, int fcClx, int lcbClx)
    {
        int i = fcClx, end = Math.Min(table.Length, fcClx + lcbClx);
        while (i < end)
        {
            byte clxt = table[i];
            if (clxt == 0x01)
            {
                int cb = I16(table, i + 1);
                i += 3 + cb;
                continue;
            }
            if (clxt != 0x02) break;
            int lcb = I32(table, i + 1);
            int start = i + 5;
            int n = (lcb - 4) / 12;
            var pieces = new List<Piece>(n);
            for (int k = 0; k < n; k++)
            {
                int cp = I32(table, start + k * 4);
                int cpNext = I32(table, start + (k + 1) * 4);
                int pcd = start + (n + 1) * 4 + k * 8;
                uint fcValue = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(pcd + 2));
                bool compressed = (fcValue & 0x40000000) != 0;
                int fc = (int)(fcValue & 0x3FFFFFFF);
                if (compressed) fc /= 2;
                if (cpNext > cp) pieces.Add(new Piece(cp, cpNext, fc, compressed));
            }
            pieces.Sort((a, b) => a.CpStart.CompareTo(b.CpStart));
            return pieces;
        }
        throw new InvalidDataException("This Word document is damaged (no piece table).");
    }

    // ----- paragraph properties -----

    private static List<PapRun> PapRuns(byte[] word, byte[] table, (int Fc, int Lcb) plc)
    {
        var runs = new List<PapRun>();
        if (plc.Lcb < 12 || plc.Fc + plc.Lcb > table.Length) return runs;
        int n = (plc.Lcb - 4) / 8;
        for (int k = 0; k < n; k++)
        {
            int pn = I32(table, plc.Fc + (n + 1) * 4 + k * 4) & 0x3FFFFF;
            int page = pn * 512;
            if (page + 512 > word.Length) continue;
            int crun = word[page + 511];
            for (int i = 0; i < crun; i++)
            {
                int fcStart = I32(word, page + i * 4), fcEnd = I32(word, page + (i + 1) * 4);
                int bOffset = word[page + (crun + 1) * 4 + i * 13];
                var info = new PapInfo();
                if (bOffset != 0)
                {
                    int off = page + bOffset * 2;
                    int cb = word[off];
                    int len, data;
                    if (cb != 0) { len = cb * 2 - 1; data = off + 1; }
                    else { len = word[off + 1] * 2; data = off + 2; }
                    if (len >= 2 && data + len <= word.Length)
                    {
                        info.Istd = U16(word, data);
                        ParseSprms(word, data + 2, data + len, (sprm, at, size) => ApplyPap(info, word, sprm, at, size));
                    }
                }
                runs.Add(new PapRun(fcStart, fcEnd, info));
            }
        }
        runs.Sort((a, b) => a.FcStart.CompareTo(b.FcStart));
        return runs;
    }

    private static void ApplyPap(PapInfo info, byte[] b, ushort sprm, int at, int size)
    {
        switch (sprm)
        {
            case 0x2416: info.InTable = b[at] != 0; break;          // sprmPFInTable
            case 0x2417: info.RowEnd = b[at] != 0; break;           // sprmPFTtp
            case 0x244C: info.RowEnd |= b[at] != 0; break;          // sprmPFInnerTtp
            case 0x6649 when size >= 4: info.Depth = I32(b, at); break; // sprmPItap
            case 0x2640: info.Outline = b[at]; break;               // sprmPOutLvl
            case 0x260A: info.Ilvl = b[at]; break;                  // sprmPIlvl
            case 0x460B when size >= 2: info.Ilfo = I16(b, at); break;  // sprmPIlfo
        }
    }

    private static PapInfo FindPap(List<PapRun> runs, int fc)
    {
        int lo = 0, hi = runs.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (fc < runs[mid].FcStart) hi = mid - 1;
            else if (fc >= runs[mid].FcEnd) lo = mid + 1;
            else return runs[mid].Props;
        }
        return new PapInfo();
    }

    // ----- character properties -----

    private static List<ChpRun> ChpRuns(byte[] word, byte[] table, (int Fc, int Lcb) plc)
    {
        var runs = new List<ChpRun>();
        if (plc.Lcb < 12 || plc.Fc + plc.Lcb > table.Length) return runs;
        int n = (plc.Lcb - 4) / 8;
        for (int k = 0; k < n; k++)
        {
            int pn = I32(table, plc.Fc + (n + 1) * 4 + k * 4) & 0x3FFFFF;
            int page = pn * 512;
            if (page + 512 > word.Length) continue;
            int crun = word[page + 511];
            for (int i = 0; i < crun; i++)
            {
                int fcStart = I32(word, page + i * 4), fcEnd = I32(word, page + (i + 1) * 4);
                int b = word[page + (crun + 1) * 4 + i];
                var info = ChpInfo.Plain;
                if (b != 0)
                {
                    int off = page + b * 2;
                    int cb = word[off];
                    var props = new ChpInfo();
                    ParseSprms(word, off + 1, Math.Min(word.Length, off + 1 + cb), (sprm, at, size) =>
                    {
                        bool on = word[at] is 1 or 0x81;
                        switch (sprm)
                        {
                            case 0x0835: props.Bold = on; break;     // sprmCFBold
                            case 0x0836: props.Italic = on; break;   // sprmCFItalic
                            case 0x0837: props.Strike |= on; break;  // sprmCFStrike
                            case 0x2A53: props.Strike |= on; break;  // sprmCFDStrike
                            case 0x083C: props.Hidden = on; break;   // sprmCFVanish
                        }
                    });
                    if (props.Bold || props.Italic || props.Strike || props.Hidden) info = props;
                }
                runs.Add(new ChpRun(fcStart, fcEnd, info));
            }
        }
        runs.Sort((a, b) => a.FcStart.CompareTo(b.FcStart));
        return runs;
    }

    private static int FindChpIndex(List<ChpRun> runs, int fc)
    {
        int lo = 0, hi = runs.Count - 1, best = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (runs[mid].FcStart <= fc) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return best;
    }

    // ----- styles and lists -----

    private static List<StyleInfo?> Styles(byte[] table, (int Fc, int Lcb) stsh)
    {
        var styles = new List<StyleInfo?>();
        try
        {
            if (stsh.Lcb < 4 || stsh.Fc + stsh.Lcb > table.Length) return styles;
            int cbStshi = U16(table, stsh.Fc);
            int stshi = stsh.Fc + 2;
            int cstd = U16(table, stshi);
            int cbBase = U16(table, stshi + 2);
            int p = stshi + cbStshi;
            int end = stsh.Fc + stsh.Lcb;
            for (int i = 0; i < cstd && p + 2 <= end; i++)
            {
                int cbStd = U16(table, p);
                int std = p + 2;
                p = std + cbStd;
                if (cbStd == 0) { styles.Add(null); continue; }
                int sti = U16(table, std) & 0x0FFF;
                int stk = U16(table, std + 2) & 0x000F;
                int namePos = std + cbBase;
                string name = "";
                int after = namePos;
                if (namePos + 2 <= p)
                {
                    int cch = U16(table, namePos);
                    if (namePos + 2 + cch * 2 <= p) name = Encoding.Unicode.GetString(table, namePos + 2, cch * 2);
                    after = namePos + 2 + cch * 2 + 2;
                }
                // Paragraph styles: the first UPX holds the style's paragraph properties (istd, then sprms).
                int outline = 9, ilfo = 0, ilvl = 0;
                if (stk == 1 && after + 2 <= p)
                {
                    int cbUpx = U16(table, after);
                    int upx = after + 2;
                    if (cbUpx >= 2 && upx + cbUpx <= p)
                    {
                        var info = new PapInfo();
                        ParseSprms(table, upx + 2, upx + cbUpx, (sprm, at, size) => ApplyPap(info, table, sprm, at, size));
                        outline = info.Outline;
                        ilfo = info.Ilfo;
                        ilvl = info.Ilvl;
                    }
                }
                styles.Add(new StyleInfo(sti, name, outline, ilfo, ilvl));
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
            // Without styles the text still comes through; only headings are lost.
        }
        return styles;
    }

    /// <summary>ilfo (1-based list override) → per level: numbered or bulleted, and the first number.</summary>
    private static Dictionary<int, (bool Numbered, int Start)[]> ListFormats(byte[] table, (int Fc, int Lcb) plfLst, (int Fc, int Lcb) plfLfo)
    {
        var result = new Dictionary<int, (bool, int)[]>();
        try
        {
            if (plfLst.Lcb < 2 || plfLfo.Lcb < 4) return result;
            int cLst = I16(table, plfLst.Fc);
            var lists = new List<(int Lsid, bool Simple)>();
            int p = plfLst.Fc + 2;
            for (int i = 0; i < cLst; i++)
            {
                lists.Add((I32(table, p), (table[p + 26] & 0x01) != 0));
                p += 28;
            }
            // The levels (LVL) of every list follow the list array.
            var byLsid = new Dictionary<int, (bool, int)[]>();
            foreach (var (lsid, simple) in lists)
            {
                int count = simple ? 1 : 9;
                var levels = new (bool, int)[count];
                for (int l = 0; l < count; l++)
                {
                    int start = I32(table, p);
                    int nfc = table[p + 4];
                    int cbChpx = table[p + 24];
                    int cbPapx = table[p + 25];
                    p += 28 + cbPapx + cbChpx;
                    int cch = U16(table, p);
                    p += 2 + cch * 2;
                    levels[l] = (nfc is not (23 or 255), start);
                }
                byLsid[lsid] = levels;
            }
            int lfoMac = I32(table, plfLfo.Fc);
            for (int i = 0; i < lfoMac; i++)
            {
                int lsid = I32(table, plfLfo.Fc + 4 + i * 16);
                if (byLsid.TryGetValue(lsid, out var levels)) result[i + 1] = levels;
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            // Unreadable list definitions: every list becomes bulleted.
        }
        return result;
    }

    // ----- sprms -----

    /// <summary>Walks a list of property modifiers (sprms), calling back with each one's operand.</summary>
    private static void ParseSprms(byte[] b, int start, int end, Action<ushort, int, int> onSprm)
    {
        int i = start;
        end = Math.Min(end, b.Length);
        while (i + 2 <= end)
        {
            ushort sprm = (ushort)U16(b, i);
            i += 2;
            int size = (sprm >> 13) switch
            {
                0 or 1 => 1,
                2 or 4 or 5 => 2,
                3 => 4,
                7 => 3,
                _ => -1
            };
            if (size < 0)
            {
                if (i >= end) break;
                if (sprm == 0xD608 || sprm == 0xD606) size = U16(b, i) + 1;        // sprmTDefTable / sprmTDefTable10: 2-byte size
                else if (sprm == 0xC615 && b[i] == 255)                           // sprmPChgTabs with a computed size
                {
                    int del = b[i + 1];
                    int add = b[i + 2 + del * 4];
                    size = 1 + 1 + del * 4 + 1 + add * 3;
                }
                else size = b[i] + 1;
            }
            if (i + size > end) break;
            // Only fixed-size operands are read by the callers, so the operand starts right here.
            onSprm(sprm, i, size);
            i += size;
        }
    }

    // ----- fields -----

    private static string? HyperlinkFrom(string instruction)
    {
        var m = HyperlinkField().Match(instruction);
        if (!m.Success || instruction.Contains("\\l", StringComparison.Ordinal)) return null;
        var target = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        return target.Contains("://") || target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || target.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? target : null;
    }

    // ----- Word 6 / 95 -----

    /// <summary>Word 6 and 95 files: the text sits in one block (no piece table); keep its paragraphs.</summary>
    private static ConvertedDoc OldWord(byte[] word)
    {
        int fcMin = I32(word, 0x18), fcMac = I32(word, 0x1C);
        if (fcMin <= 0 || fcMac <= fcMin || fcMac > word.Length) throw new InvalidDataException("This Word document is in a format that's too old to read.");
        var raw = word.AsSpan(fcMin, fcMac - fcMin);
        var text = Encoding.GetEncoding(TextFiles.LegacyCodePage(raw)).GetString(raw);
        var output = new MarkdownOut();
        foreach (var para in text.Split('\r', '\u000C'))
        {
            var clean = new string(para.Where(c => c >= ' ' || c == '\t').ToArray()).Replace('\t', ' ').Replace('\u0007', ' ').Trim();
            if (clean.Length > 0) output.Paragraph(InlineBuilder.Escape(clean));
        }
        return new ConvertedDoc(output.ToString(), null, [], ["This is a Word 6/95 document: only its text came across."]);
    }

    private static int U16(byte[] b, int at) => at + 2 <= b.Length ? BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at)) : throw new InvalidDataException("This Word document is damaged.");
    private static int I16(byte[] b, int at) => at + 2 <= b.Length ? BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(at)) : throw new InvalidDataException("This Word document is damaged.");
    private static int I32(byte[] b, int at) => at + 4 <= b.Length ? BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at)) : throw new InvalidDataException("This Word document is damaged.");

    [GeneratedRegex(@"^heading\s*([1-9])$", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingName();

    [GeneratedRegex(@"HYPERLINK\s+(?:""([^""]+)""|(\S+))", RegexOptions.IgnoreCase)]
    private static partial Regex HyperlinkField();
}
