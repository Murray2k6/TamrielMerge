using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Loose things left where the games put them (user, 2026-11-11: "fix some havok like clutter not being put in places
/// and fix books not lining on shelves proper"). Skyrim drops every loose object (books, bottles, plates, weapons) into
/// its physics as a cell loads and lets it settle: a row of Morrowind's books standing on a shelf, placed by hand in a
/// game without physics, tips over or is pushed out of what it touches. A placed reference with the flag "Don't Havok
/// Settle" (0x20000000) stays as placed until something moves it. Checked just before each converted plugin is written:
/// every placed reference whose base is a loose thing (a misc item, book, potion, ingredient, weapon, armour,
/// ammunition, soul gem, key, scroll, a light that can be carried, a movable static) gets the flag; bases in another
/// plugin (Daggerfall's items placed by its towns' plugins, Skyrim's own) are looked up in that plugin.
/// </summary>
public static class HavokSettle
{
    /// <summary>Skyrim's Data folder, for bases that are Skyrim's own records.</summary>
    public static string SkyrimData = "";

    const SkyrimMajorRecord.SkyrimMajorRecordFlag DontSettle = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x20000000;

    static readonly HashSet<FormKey> loose = [];
    static readonly HashSet<ModKey> looked = [];
    static readonly object gate = new();

    static void Know(ISkyrimModGetter m)
    {
        foreach (var r in m.MiscItems) loose.Add(r.FormKey);
        foreach (var r in m.Books) loose.Add(r.FormKey);
        foreach (var r in m.Ingestibles) loose.Add(r.FormKey);
        foreach (var r in m.Ingredients) loose.Add(r.FormKey);
        foreach (var r in m.Weapons) loose.Add(r.FormKey);
        foreach (var r in m.Armors) loose.Add(r.FormKey);
        foreach (var r in m.Ammunitions) loose.Add(r.FormKey);
        foreach (var r in m.SoulGems) loose.Add(r.FormKey);
        foreach (var r in m.Keys) loose.Add(r.FormKey);
        foreach (var r in m.Scrolls) loose.Add(r.FormKey);
        foreach (var r in m.MoveableStatics) loose.Add(r.FormKey);
        foreach (var r in m.Lights) if (r.Flags.HasFlag(Light.Flag.CanBeCarried)) loose.Add(r.FormKey);
    }

    /// <summary>Flags the plugin's placed loose things; <paramref name="folder"/> is where the plugins built with it are
    /// (its masters among the converted games). Returns how many were flagged.</summary>
    public static int Apply(SkyrimMod mod, string? folder, Action<string>? log = null)
    {
        lock (gate)
        {
            Know(mod);
            var placed = mod.EnumerateMajorRecords<IPlacedObject>().Where(p => !p.Base.IsNull).ToList();
            foreach (var master in placed.Select(p => p.Base.FormKey.ModKey).Distinct().Where(k => k != mod.ModKey && !looked.Contains(k)).ToList())
            {
                looked.Add(master);
                string? path = new[] { folder, SkyrimData }.Where(d => !string.IsNullOrEmpty(d)).Select(d => Path.Combine(d!, master.FileName)).FirstOrDefault(File.Exists);
                if (path is null) continue;
                try
                {
                    using var overlay = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
                    Know(overlay);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException) { log?.Invoke($"  loose things: {master.FileName} not read ({e.Message})"); }
            }
            int n = 0;
            foreach (var p in placed)
            {
                if (!loose.Contains(p.Base.FormKey) || p.SkyrimMajorRecordFlags.HasFlag(DontSettle)) continue;
                p.SkyrimMajorRecordFlags |= DontSettle;
                n++;
            }
            if (n > 0) log?.Invoke($"  loose things ({mod.ModKey.FileName}): {n:N0} placed books, bottles, weapons and other loose objects kept where they were put (not dropped into the physics as the cell loads)");
            return n;
        }
    }

    // ---- plugins already written ----

    static readonly HashSet<string> LooseTypes = ["MISC", "BOOK", "ALCH", "INGR", "WEAP", "ARMO", "AMMO", "SLGM", "KEYM", "SCRL", "MSTT", "LIGH"];
    static readonly Dictionary<string, HashSet<uint>?> looseOf = new(StringComparer.OrdinalIgnoreCase);

    static byte[] Body(FileStream f, uint size, uint flags)
    {
        var raw = new byte[size];
        f.ReadExactly(raw);
        if ((flags & 0x40000) == 0 || size < 4) return raw;
        using var z = new System.IO.Compression.ZLibStream(new MemoryStream(raw, 4, raw.Length - 4), System.IO.Compression.CompressionMode.Decompress);
        var plain = new byte[BitConverter.ToUInt32(raw, 0)];
        z.ReadExactly(plain);
        return plain;
    }

    static bool Is(byte[] b, int i, string type) => b[i] == type[0] && b[i + 1] == type[1] && b[i + 2] == type[2] && b[i + 3] == type[3];

    static ReadOnlySpan<byte> Field(byte[] body, string type)
    {
        for (int i = 0; i + 6 <= body.Length;)
        {
            int n = BitConverter.ToUInt16(body, i + 4);
            // XXXX: the next field's real size.
            if (Is(body, i, "XXXX")) { int big = BitConverter.ToInt32(body, i + 6); i += 6 + n; i += 6 + big; continue; }
            if (Is(body, i, type)) return body.AsSpan(i + 6, Math.Min(n, body.Length - i - 6));
            i += 6 + n;
        }
        return default;
    }

    static List<string> Masters(FileStream f)
    {
        var head = new byte[24];
        f.Position = 0; f.ReadExactly(head);
        var masters = new List<string>();
        if (!Is(head, 0, "TES4")) throw new InvalidDataException("not a plugin");
        var body = Body(f, BitConverter.ToUInt32(head, 4), BitConverter.ToUInt32(head, 8));
        for (int i = 0; i + 6 <= body.Length;)
        {
            int n = BitConverter.ToUInt16(body, i + 4);
            if (Is(body, i, "MAST")) masters.Add(System.Text.Encoding.ASCII.GetString(body, i + 6, n).TrimEnd((char)0));
            i += 6 + n;
        }
        return masters;
    }

    /// <summary>The loose things a plugin holds itself (their ids without the load index), read from its top groups; null
    /// when the plugin isn't in any of the folders.</summary>
    static HashSet<uint>? LooseOf(string plugin, IReadOnlyList<string> folders)
    {
        if (looseOf.TryGetValue(plugin, out var known)) return known;
        string? path = folders.Select(d => Path.Combine(d, plugin)).FirstOrDefault(File.Exists);
        if (path is null) return looseOf[plugin] = null;
        var set = new HashSet<uint>();
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        int own = Masters(f).Count;
        var head = new byte[24];
        while (f.Position + 24 <= f.Length)
        {
            long at = f.Position;
            f.ReadExactly(head);
            uint size = BitConverter.ToUInt32(head, 4);
            if (!Is(head, 0, "GRUP")) { f.Position = at + 24 + size; continue; }
            string label = System.Text.Encoding.ASCII.GetString(head, 8, 4);
            long end = at + size;
            if (BitConverter.ToUInt32(head, 12) == 0 && LooseTypes.Contains(label))
                while (f.Position + 24 <= end)
                {
                    long rec = f.Position;
                    f.ReadExactly(head);
                    if (Is(head, 0, "GRUP")) continue;
                    uint rsize = BitConverter.ToUInt32(head, 4), rflags = BitConverter.ToUInt32(head, 8), id = BitConverter.ToUInt32(head, 12);
                    bool loose = true;
                    // A light only when it can be carried (DATA: time, radius, colour, flags; 0x2).
                    if (label == "LIGH") { var data = Field(Body(f, rsize, rflags), "DATA"); loose = data.Length >= 16 && (BitConverter.ToUInt32(data[12..]) & 0x2) != 0; }
                    // Its own records only: an override of a master's keeps that master's kind.
                    if (loose && (int)(id >> 24) >= own) set.Add(id & 0xFFFFFF);
                    f.Position = rec + 24 + rsize;
                }
            f.Position = end;
        }
        return looseOf[plugin] = set;
    }

    /// <summary>
    /// The same flag on a plugin already written (one converted before 2026-11-11, or by an older tool): every placed
    /// reference in it whose base is a loose thing, its own or a master's found in <paramref name="folders"/>, gets
    /// "Don't Havok Settle" where it lacks it. The file keeps its size: four header bytes change per reference. Read
    /// and written in place a record at a time (the plugins run to 1.9 GB). Returns (placed references, flagged now,
    /// flagged before).
    /// </summary>
    public static (int Placed, int Flagged, int Already) Stamp(string path, IReadOnlyList<string> folders, Action<string>? log = null)
    {
        lock (gate)
        {
            string name = Path.GetFileName(path);
            // Its own loose things first (read through a second handle), then the file opened to write.
            var mine = LooseOf(name, [Path.GetDirectoryName(Path.GetFullPath(path))!]);
            looseOf.Remove(name);
            using var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
            var masters = Masters(f);
            var sets = masters.Select(m => LooseOf(m, folders)).Append(mine).ToList();
            for (int i = 0; i < masters.Count; i++) if (sets[i] is null) log?.Invoke($"  loose things: master {masters[i]} of {name} not found");
            int placed = 0, flagged = 0, already = 0;
            var head = new byte[24];
            while (f.Position + 24 <= f.Length)
            {
                long at = f.Position;
                f.ReadExactly(head);
                uint size = BitConverter.ToUInt32(head, 4);
                if (Is(head, 0, "GRUP"))
                {
                    // Only cells and worldspaces hold placed references; inside them, every group is walked into.
                    string label = System.Text.Encoding.ASCII.GetString(head, 8, 4);
                    if (BitConverter.ToUInt32(head, 12) == 0 && label is not ("CELL" or "WRLD")) f.Position = at + size;
                    continue;
                }
                uint flags = BitConverter.ToUInt32(head, 8);
                if (Is(head, 0, "REFR"))
                {
                    placed++;
                    var nameField = Field(Body(f, size, flags), "NAME");
                    if (nameField.Length >= 4)
                    {
                        uint b = BitConverter.ToUInt32(nameField);
                        int index = (int)Math.Min(b >> 24, (uint)masters.Count);
                        if (sets[index] is { } set && set.Contains(b & 0xFFFFFF))
                        {
                            if ((flags & 0x20000000) != 0) already++;
                            else
                            {
                                f.Position = at + 8;
                                f.Write(BitConverter.GetBytes(flags | 0x20000000));
                                flagged++;
                            }
                        }
                    }
                }
                f.Position = at + 24 + size;
            }
            return (placed, flagged, already);
        }
    }
}
