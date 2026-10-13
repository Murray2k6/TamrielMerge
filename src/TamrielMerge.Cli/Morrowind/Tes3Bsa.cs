namespace TamrielMerge.Morrowind;

/// <summary>
/// A Morrowind BSA archive (version 0x100): header [version u32][hash table offset u32][file count u32], then per
/// file (size, offset) pairs, name offsets, the NUL-terminated names, 8-byte hashes, and finally the file data
/// (offsets relative to its start). Names are lower-case paths with backslashes.
/// </summary>
public sealed class Tes3Bsa
{
    readonly string path;
    readonly Dictionary<string, (long Offset, int Size)> files = new(StringComparer.OrdinalIgnoreCase);

    public string FilePath => path;
    public IEnumerable<string> Names => files.Keys;
    public bool Contains(string name) => files.ContainsKey(Normalize(name));

    public Tes3Bsa(string path)
    {
        this.path = path;
        using var r = new BinaryReader(File.OpenRead(path));
        if (r.ReadUInt32() != 0x100) throw new InvalidDataException($"{path} is not a Morrowind BSA");
        uint hashOffset = r.ReadUInt32();
        int count = r.ReadInt32();
        var sizes = new (int Size, uint Offset)[count];
        for (int i = 0; i < count; i++) sizes[i] = (r.ReadInt32(), r.ReadUInt32());
        var nameOffsets = new uint[count];
        for (int i = 0; i < count; i++) nameOffsets[i] = r.ReadUInt32();
        var names = r.ReadBytes((int)(hashOffset - 8 * count - 4 * count));
        long dataStart = 12 + hashOffset + 8L * count;
        for (int i = 0; i < count; i++)
        {
            int s = (int)nameOffsets[i], e = Array.IndexOf(names, (byte)0, s);
            string name = Tes3Plugin.Encoding.GetString(names, s, e - s);
            files[Normalize(name)] = (dataStart + sizes[i].Offset, sizes[i].Size);
        }
    }

    public static string Normalize(string name) => name.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();

    public byte[]? Read(string name)
    {
        if (!files.TryGetValue(Normalize(name), out var f)) return null;
        using var s = File.OpenRead(path);
        s.Position = f.Offset;
        var buf = new byte[f.Size];
        s.ReadExactly(buf);
        return buf;
    }
}

/// <summary>
/// Morrowind's file system: loose files in Data Files override the archives, and later archives override earlier ones
/// (Morrowind.bsa, then Tribunal.bsa, then Bloodmoon.bsa, as the expansions load).
/// </summary>
public sealed class Tes3Data(string dataFiles, IEnumerable<string> archives)
{
    readonly List<Tes3Bsa> bsas = archives.Select(a => Path.Combine(dataFiles, a)).Where(File.Exists).Select(p => new Tes3Bsa(p)).ToList();

    public string DataFiles => dataFiles;
    public IReadOnlyList<Tes3Bsa> Archives => bsas;

    public static Tes3Data Open(string dataFiles) => new(dataFiles, ["Morrowind.bsa", "Tribunal.bsa", "Bloodmoon.bsa"]);

    /// <summary>The archives for a set of plugins, as Morrowind loads them: Morrowind.bsa, those Morrowind.ini's [Archives]
    /// lists, then any archive named after a plugin being converted (mods often ship their art that way).</summary>
    public static Tes3Data Open(string dataFiles, IEnumerable<string> plugins)
    {
        var list = plugins.ToList();
        var archives = new List<string> { "Morrowind.bsa" };
        string ini = Path.Combine(Path.GetDirectoryName(dataFiles.TrimEnd('\\', '/'))!, "Morrowind.ini");
        if (File.Exists(ini))
            foreach (var line in File.ReadAllLines(ini, System.Text.Encoding.Latin1).SkipWhile(l => !l.Trim().Equals("[Archives]", StringComparison.OrdinalIgnoreCase)).Skip(1)
                         .TakeWhile(l => !l.TrimStart().StartsWith('[')))
                if (line.Split('=', 2) is [_, var name] && name.Trim().Length > 0) archives.Add(name.Trim());
        foreach (var official in new[] { "Tribunal.bsa", "Bloodmoon.bsa" })
            if (list.Contains(Path.ChangeExtension(official, ".esm"), StringComparer.OrdinalIgnoreCase)) archives.Add(official);
        foreach (var p in list)
        {
            string bsa = Path.ChangeExtension(p, ".bsa");
            if (File.Exists(Path.Combine(dataFiles, bsa))) archives.Add(bsa);
        }
        return new(dataFiles, archives.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public byte[]? Read(string name)
    {
        string loose = Path.Combine(dataFiles, Tes3Bsa.Normalize(name));
        if (File.Exists(loose)) return File.ReadAllBytes(loose);
        for (int i = bsas.Count - 1; i >= 0; i--)
            if (bsas[i].Read(name) is { } bytes) return bytes;
        return null;
    }

    public bool Exists(string name) =>
        File.Exists(Path.Combine(dataFiles, Tes3Bsa.Normalize(name))) || bsas.Any(b => b.Contains(name));

    /// <summary>Every file name known to the archives plus loose files under the given top folder (e.g. "meshes").</summary>
    public IEnumerable<string> List(string folder)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string prefix = Tes3Bsa.Normalize(folder).TrimEnd('\\') + "\\";
        foreach (var b in bsas) foreach (var n in b.Names) if (n.StartsWith(prefix)) set.Add(n);
        string dir = Path.Combine(dataFiles, folder);
        if (Directory.Exists(dir))
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                set.Add(Tes3Bsa.Normalize(Path.GetRelativePath(dataFiles, f)));
        return set.OrderBy(n => n);
    }
}
