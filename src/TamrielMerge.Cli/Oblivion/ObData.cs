namespace TamrielMerge.Oblivion;

/// <summary>
/// The original Oblivion install as TamrielMerge converts it: the plugins Oblivion itself loads (its Plugins.txt —
/// on this kind of install, the official game and DLC), in Oblivion's load order (masters first, then by file date),
/// and the official archives ("Oblivion - *.bsa" and the archives named after the active plugins). Loose files are
/// only a fallback: on a modded install they belong to mods, not to the game.
/// </summary>
public sealed class ObData
{
    public string DataDir { get; }
    public List<string> Plugins { get; }
    public List<ObBsa> Archives { get; } = [];
    readonly Dictionary<string, ObBsa> files = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="plugins">The plugins to convert in load order (the mod selection); null for what Oblivion loads.</param>
    public ObData(string dataDir, IEnumerable<string>? plugins = null)
    {
        DataDir = dataDir;
        Plugins = plugins?.Where(p => File.Exists(Path.Combine(dataDir, p))).ToList() ?? LoadOrder(dataDir);
        var bsas = Directory.GetFiles(dataDir, "*.bsa").Select(Path.GetFileName).OfType<string>()
            .Where(b => b.StartsWith("Oblivion - ", StringComparison.OrdinalIgnoreCase)
                     || Plugins.Any(p => b.StartsWith(Path.GetFileNameWithoutExtension(p), StringComparison.OrdinalIgnoreCase)))
            .OrderBy(b => b.StartsWith("Oblivion - ", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(b => File.GetLastWriteTimeUtc(Path.Combine(dataDir, b)));
        foreach (var bsa in bsas)
        {
            var reader = new ObBsa(Path.Combine(dataDir, bsa));
            Archives.Add(reader);
            foreach (var f in reader.Names) files[f] = reader;   // later archives override earlier ones
        }
    }

    public static string Normalize(string path) => path.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();

    /// <summary>The active plugins in load order: Oblivion's Plugins.txt (masters first, then by modified date, as
    /// Oblivion orders them). Without a Plugins.txt: Oblivion.esm and the official DLC listed in DLCList.txt.</summary>
    public static List<string> LoadOrder(string dataDir)
    {
        string list = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Oblivion", "Plugins.txt");
        var names = File.Exists(list) ? File.ReadAllLines(list) : File.Exists(Path.Combine(dataDir, "DLCList.txt")) ? File.ReadAllLines(Path.Combine(dataDir, "DLCList.txt")).Append("Oblivion.esm").ToArray() : ["Oblivion.esm"];
        return names.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#') && File.Exists(Path.Combine(dataDir, l)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => File.GetLastWriteTimeUtc(Path.Combine(dataDir, p)))
            .ToList();
    }

    public bool Exists(string path) => files.ContainsKey(Normalize(path)) || File.Exists(Path.Combine(DataDir, Normalize(path)));

    public byte[]? Read(string path)
    {
        if (files.TryGetValue(Normalize(path), out var bsa)) return bsa.Read(path);
        string loose = Path.Combine(DataDir, Normalize(path));
        return File.Exists(loose) ? File.ReadAllBytes(loose) : null;
    }

    public IEnumerable<string> List(string folder)
    {
        string prefix = Normalize(folder).TrimEnd('\\') + "\\";
        return files.Keys.Where(k => k.StartsWith(prefix)).Order();
    }
}
