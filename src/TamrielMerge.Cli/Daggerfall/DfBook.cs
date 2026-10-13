using System.Text;

namespace TamrielMerge.Daggerfall;

public enum DfAlign { Left, Center }

public sealed record DfParagraph(string Text, DfAlign Align, int Font);

/// <summary>
/// A Daggerfall book from ARENA2/BOOKS/BOKxxxxx.TXT, with its pages joined and re-flowed into paragraphs.
/// Format reference: Daggerfall Unity BookFile.cs / TextFile.cs.
/// </summary>
public sealed class DfBook
{
    public required string FileName { get; init; }
    public required string Title { get; init; }
    public required string Author { get; init; }
    public required bool IsNaughty { get; init; }
    public required uint Price { get; init; }
    public required int PageCount { get; init; }
    public required IReadOnlyList<DfParagraph> Paragraphs { get; init; }

    /// <summary>Book number encoded in the file name (BOK00042.TXT -> 42).</summary>
    public int Id => int.Parse(Path.GetFileNameWithoutExtension(FileName)[3..]);

    const byte NewLine = 0x00, EndOfPage = 0xf6, FontPrefix = 0xf9, PositionPrefix = 0xfb,
        JustifyLeft = 0xfc, JustifyCenter = 0xfd;

    public static IEnumerable<DfBook> LoadAll(string arena2)
    {
        var dir = Path.Combine(arena2, "BOOKS");
        foreach (var path in Directory.GetFiles(dir, "BOK*.TXT").Order(StringComparer.OrdinalIgnoreCase))
            yield return Load(path);
    }

    public static DfBook Load(string path)
    {
        var data = File.ReadAllBytes(path);
        using var r = new BinaryReader(new MemoryStream(data));
        string title = ReadFixed(r, 64), author = ReadFixed(r, 64), naughty = ReadFixed(r, 8);
        r.ReadBytes(88);
        uint price = r.ReadUInt32();
        r.ReadUInt16(); r.ReadUInt16(); r.ReadUInt16();
        int pageCount = r.ReadUInt16();
        var offsets = new uint[pageCount];
        for (int i = 0; i < pageCount; i++) offsets[i] = r.ReadUInt32();

        // Pages are contiguous and sentences run across page breaks, so tokenize from the
        // first page to the end of the last one and let Skyrim paginate on its own.
        var lines = new List<Line>();
        foreach (var off in offsets)
            ReadPage(data, (int)off, lines);

        return new DfBook
        {
            FileName = Path.GetFileName(path),
            Title = title,
            Author = author,
            IsNaughty = naughty.StartsWith("naughty", StringComparison.OrdinalIgnoreCase),
            Price = price,
            PageCount = pageCount,
            Paragraphs = Reflow(lines),
        };
    }

    sealed record Line(string Text, DfAlign Align, int Font);

    static void ReadPage(byte[] data, int pos, List<Line> lines)
    {
        var sb = new StringBuilder();
        var align = DfAlign.Left;
        int font = 4;
        bool seenText = false;
        void Flush() { lines.Add(new Line(sb.ToString(), align, font)); sb.Clear(); }

        while (pos < data.Length)
        {
            byte b = data[pos++];
            if (b >= 0x20 && b <= 0x7f) { sb.Append((char)b); seenText |= b != ' '; continue; }
            switch (b)
            {
                // Pages open with blank lines; dropping them keeps sentences that span a page break intact.
                case NewLine: if (seenText) Flush(); else sb.Clear(); align = DfAlign.Left; break;
                case EndOfPage: if (sb.Length > 0) Flush(); return;
                case FontPrefix: if (pos < data.Length) font = data[pos++]; break;
                case PositionPrefix: pos++; break;
                case JustifyCenter: align = DfAlign.Center; break;
                case JustifyLeft: align = DfAlign.Left; break;
                default: break; // other control bytes carry no text
            }
        }
        if (sb.Length > 0) Flush();
    }

    /// <summary>
    /// Daggerfall hard-wraps lines to its page width. Rebuild paragraphs: a paragraph ends at a blank
    /// line, a center code, a font change, an indented line, or a short line ending in punctuation.
    /// </summary>
    static List<DfParagraph> Reflow(List<Line> lines)
    {
        int wrapWidth = lines.Select(l => l.Text.Trim().Length).DefaultIfEmpty(60).Max();
        var result = new List<DfParagraph>();
        StringBuilder? para = null;
        Line? first = null, prev = null;
        bool blankSeen = false;

        void Close()
        {
            // Authors also marked paragraphs with a run of spaces mid-line ("Truth?         Baan Dar"),
            // but used the same padding mid-sentence, so only split where a sentence has ended.
            if (para is { Length: > 0 } && first is not null)
                foreach (var piece in IndentBreak.Split(para.ToString()).Select(t => SpaceRun.Replace(t, " ").Trim()).Where(t => t.Length > 0))
                    result.Add(new DfParagraph(piece, first.Align, first.Font));
            para = null; first = null; prev = null;
        }

        foreach (var line in lines)
        {
            var text = line.Text.TrimEnd();
            if (text.Trim().Length == 0) { blankSeen = true; continue; }

            // A line starting with a lowercase word continues the current sentence no matter how it was
            // padded (blank lines, indents and short lines all appear mid-sentence at page breaks).
            // The center code is only written on a paragraph's first line, so wrapped continuation
            // lines come back left-aligned; alignment changes alone don't end a paragraph.
            bool continues = char.IsLower(text.TrimStart()[0]);
            bool newPara = para is null
                || line.Font != first!.Font
                || line.Align == DfAlign.Center
                || (!continues && (blankSeen
                    || text.StartsWith(' ')
                    || (prev is not null && EndsParagraph(prev.Text.TrimEnd(), wrapWidth))
                    || (prev is not null && first.Align == DfAlign.Center && prev.Text.TrimEnd().Length < wrapWidth * 0.6)));
            blankSeen = false;
            if (newPara) { Close(); para = new StringBuilder(); first = line; }

            var piece = text.Trim();
            if (para!.Length > 0)
            {
                // Keep line-end hyphens attached ("well-\nknown" -> "well-known").
                bool hyphenJoin = para[^1] == '-' && para.Length > 1 && char.IsLetter(para[^2]) && char.IsLetter(piece[0]);
                if (!hyphenJoin) para.Append(' ');
            }
            para.Append(piece);
            prev = line;
        }
        Close();
        return result;
    }

    static readonly System.Text.RegularExpressions.Regex IndentBreak = new(@"(?<=[.!?:""']) {3,}(?=[^a-z])");
    static readonly System.Text.RegularExpressions.Regex SpaceRun = new(@" {2,}");

    static bool EndsParagraph(string prevLine, int wrapWidth) =>
        prevLine.Length < wrapWidth * 0.75 && prevLine.Length > 0 && ".!?:\"'".Contains(prevLine[^1]);

    static string ReadFixed(BinaryReader r, int len)
    {
        var bytes = r.ReadBytes(len);
        int end = Array.IndexOf(bytes, (byte)0);
        return Encoding.Latin1.GetString(bytes, 0, end < 0 ? len : end).Trim();
    }
}
