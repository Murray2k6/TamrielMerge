using System.Text;

namespace TamrielMerge.Oblivion;

/// <summary>Gamebryo 10.1/10.2 models (Oblivion's older effects), which nifly can't read.</summary>
public static class Ob10Nif
{
    /// <summary>The header's version and block types, or null for another kind of file.</summary>
    public static (uint Version, List<string> Types, List<int> BlockTypes)? Header(byte[] b)
    {
        int nl = Array.IndexOf(b, (byte)10);
        if (nl < 0 || nl > 60 || !Encoding.ASCII.GetString(b, 0, nl).StartsWith("Gamebryo File Format")) return null;
        int p = nl + 1;
        uint version = BitConverter.ToUInt32(b, p); p += 4;
        if (version >= 0x14000003) p++;   // endianness
        if (version < 0x0A000108) return (version, [], []);
        p += 4;   // user version
        int blocks = BitConverter.ToInt32(b, p); p += 4;
        p += 4;   // Bethesda stream version
        for (int k = 0; k < 3; k++) p += 1 + b[p];   // creator, export info
        int typeCount = BitConverter.ToUInt16(b, p); p += 2;
        var types = new List<string>();
        for (int k = 0; k < typeCount; k++) { int l = BitConverter.ToInt32(b, p); p += 4; types.Add(Encoding.ASCII.GetString(b, p, l)); p += l; }
        var index = new List<int>();
        for (int k = 0; k < blocks; k++) { index.Add(BitConverter.ToUInt16(b, p) & 0x7FFF); p += 2; }
        return (version, types, index);
    }

    /// <summary>
    /// Whether nifly would fail on the file (it ends the process rather than throw): Gamebryo 10.1 files, and 10.2
    /// files with particle systems.
    /// </summary>
    public static bool IsUnreadableByNifly(byte[] b, out string why)
    {
        why = "";
        if (Header(b) is not { } h || h.Version >= 0x14000004) return false;
        string v = $"{h.Version >> 24}.{(h.Version >> 16) & 0xFF}.{(h.Version >> 8) & 0xFF}.{h.Version & 0xFF}";
        if (h.Version < 0x0A020000) { why = $"Gamebryo {v} (nifly can't read it)"; return true; }
        var ps = h.Types.Where(t => t.StartsWith("NiPSys") || t == "NiParticleSystem").ToList();
        if (ps.Count > 0) { why = $"Gamebryo {v} particles ({string.Join(", ", ps)}), which nifly can't read"; return true; }
        return false;
    }
}
