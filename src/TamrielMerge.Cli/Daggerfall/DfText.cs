using System.Text;

namespace TamrielMerge.Daggerfall;

/// <summary>
/// TEXT.RSC, Daggerfall's text database (Daggerfall Unity TextFile.cs, MIT): a UInt16 header length, then (UInt16 id,
/// UInt32 offset) per record; each record runs to 0xFE. 0xFF separates a record's variants (Daggerfall picks one at
/// random), 0x00 and 0xF6 break lines, 0xF9 and 0xFB take one argument byte, 0xFC/0xFD set justification.
/// Text keeps Daggerfall's macros (%n, %di, %ct ...) for the caller to resolve.
/// </summary>
public sealed class DfText
{
    readonly Dictionary<int, List<string>> records;

    DfText(Dictionary<int, List<string>> records) => this.records = records;

    public IReadOnlyCollection<int> Ids => records.Keys;

    /// <summary>A record's variants (empty when the id isn't there).</summary>
    public IReadOnlyList<string> this[int id] => records.TryGetValue(id, out var v) ? v : [];

    public static DfText Load(string arena2, string file = "TEXT.RSC")
    {
        var d = File.ReadAllBytes(Path.Combine(arena2, file));
        int count = BitConverter.ToUInt16(d, 0) / 6 - 1;
        var result = new Dictionary<int, List<string>>();
        for (int i = 0; i < count; i++)
        {
            int id = BitConverter.ToUInt16(d, 2 + i * 6), offset = (int)BitConverter.ToUInt32(d, 2 + i * 6 + 2);
            var variants = new List<string>();
            var sb = new StringBuilder();
            for (int p = offset; p < d.Length; p++)
            {
                byte b = d[p];
                if (b == 0xFE) break;
                switch (b)
                {
                    case 0xFF: variants.Add(Tidy(sb)); sb.Clear(); break;
                    case 0x00 or 0xF6: sb.Append('\n'); break;
                    case 0xF9 or 0xFB: p++; break;
                    case 0xFC or 0xFD or 0x01 or 0x02 or 0xF8: break;
                    default: if (b >= 0x20) sb.Append((char)b); break;
                }
            }
            variants.Add(Tidy(sb));
            result[id] = variants.Where(v => v.Length > 0).ToList();
        }
        return new DfText(result);
    }

    /// <summary>Lines joined into flowing text: single breaks become spaces, blank lines stay paragraph breaks.</summary>
    static string Tidy(StringBuilder sb)
    {
        var lines = sb.ToString().Split('\n').Select(l => l.Trim()).ToList();
        var outText = new StringBuilder();
        foreach (var l in lines)
        {
            if (l.Length == 0) { if (outText.Length > 0 && outText[^1] != '\n') outText.Append('\n'); continue; }
            if (outText.Length > 0 && outText[^1] != '\n') outText.Append(' ');
            outText.Append(l);
        }
        return outText.ToString().Trim();
    }
}
