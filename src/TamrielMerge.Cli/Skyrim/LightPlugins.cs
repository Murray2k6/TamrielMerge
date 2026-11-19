using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// The light (ESL) flag on every converted plugin small enough for it (user, 2026-10-28: "the esms need to be esl too"),
/// so they take no load-order slot of the 254: a plugin with at most <see cref="MaxRecords"/> new records, all with
/// FormIDs up to <see cref="MaxId"/> (Skyrim 1.6.1130 and later; the user's is 1.7.104). Measured 2026-10-28 on the
/// output: Start 34 new records, Travel 121, Progression 448, Menu 0, Daggerfall Overland Doors 0, Daggerfall Creatures
/// 110, Morrowind Creatures 1,701 fit; the world plugins (millions of records) can't. The flag is set in the file's
/// header (TES4 flags, bit 0x200) without rewriting the file. It doesn't change what a plugin takes of the reference
/// handles (<see cref="HandleCount"/>: persistent references, and a plugin without the master flag's worldspace
/// references); the master flag does that, and the world plugins have it.
/// </summary>
public static class LightPlugins
{
    public const int MaxRecords = 4096;
    public const uint MaxId = 0xFFF;
    const uint LightFlag = 0x200;
    const uint MasterFlag = 0x1;
    /// <summary>Plugins this big and bigger aren't looked at (an ESL of 4,096 records is never near it).</summary>
    const long TooBig = 512L << 20;

    public sealed record Fit(string Plugin, int NewRecords, uint HighestId, bool Fits, bool WasLight);

    /// <summary>Whether <paramref name="path"/> can be light, by its own records.</summary>
    public static Fit Check(string path)
    {
        string name = Path.GetFileName(path);
        if (new FileInfo(path).Length >= TooBig) return new Fit(name, -1, 0, false, false);
        using var m = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        bool light = ((uint)m.ModHeader.Flags & LightFlag) != 0;
        int count = 0; uint max = 0;
        foreach (var r in m.EnumerateMajorRecords())
        {
            if (r.FormKey.ModKey != m.ModKey) continue;
            count++;
            if (r.FormKey.ID > max) max = r.FormKey.ID;
            if (count > MaxRecords) break;
        }
        return new Fit(name, count, max, count <= MaxRecords && max <= MaxId, light);
    }

    /// <summary>Sets <paramref name="flag"/> (the light or the master flag) in the header of <paramref name="path"/> (true
    /// when it was set now).</summary>
    public static bool Flag(string path, uint flag = LightFlag)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var h = new byte[12];
        s.ReadExactly(h);
        if (h[0] != (byte)'T' || h[1] != (byte)'E' || h[2] != (byte)'S' || h[3] != (byte)'4') throw new InvalidDataException($"{Path.GetFileName(path)}: not a plugin");
        uint flags = BitConverter.ToUInt32(h, 8);
        if ((flags & flag) != 0) return false;
        s.Position = 8;
        s.Write(BitConverter.GetBytes(flags | flag));
        return true;
    }

    /// <summary>Sets the master flag on every plugin of <paramref name="plugins"/> in <paramref name="dir"/> (user,
    /// 2026-10-28: "the other esps we have need to be esm's"; a plugin without it takes a reference handle for every
    /// reference in its worldspaces, HandleCount); returns the names flagged now.</summary>
    public static List<string> MasterAll(string dir, IEnumerable<string> plugins, Action<string> log)
    {
        var flagged = new List<string>();
        foreach (var p in plugins)
        {
            string path = Path.Combine(dir, p);
            if (!File.Exists(path)) continue;
            if (Flag(path, MasterFlag)) { flagged.Add(p); log($"  {p}: master flag set"); }
        }
        return flagged;
    }

    /// <summary>Flags every plugin of <paramref name="plugins"/> in <paramref name="dir"/> that fits, logging each; returns
    /// the names flagged now.</summary>
    public static List<string> FlagAll(string dir, IEnumerable<string> plugins, Action<string> log)
    {
        var flagged = new List<string>();
        foreach (var p in plugins)
        {
            string path = Path.Combine(dir, p);
            if (!File.Exists(path)) continue;
            Fit fit;
            try { fit = Check(path); }
            catch (Exception e) { log($"  {p}: not checked for the light flag ({e.Message})"); continue; }
            if (!fit.Fits) continue;
            if (fit.WasLight) { log($"  {p}: light already ({fit.NewRecords} new records)"); continue; }
            if (Flag(path)) { flagged.Add(p); log($"  {p}: light (ESL) flag set: {fit.NewRecords} new records, highest ID {fit.HighestId:X6}"); }
        }
        return flagged;
    }
}
