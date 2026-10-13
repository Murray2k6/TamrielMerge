namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's data as the game sees it after loading every plugin in order: object records by ID (a later plugin's
/// record replaces an earlier one), and cells merged reference by reference. A reference's FRMR number carries its
/// origin in the high byte (0 = the plugin itself, n = its n-th master), so a plugin can move, change or delete a
/// master's reference by repeating its number.
/// </summary>
public sealed class MwWorld
{
    /// <summary>Record types that are placeable or referable objects, looked up by ID.</summary>
    public static readonly HashSet<string> ObjectTypes =
        ["STAT", "DOOR", "CONT", "LIGH", "ACTI", "MISC", "WEAP", "ARMO", "CLOT", "BOOK", "ALCH", "INGR", "APPA", "LOCK", "PROB", "REPA",
         "NPC_", "CREA", "LEVI", "LEVC", "BODY", "SPEL", "ENCH", "SCPT", "SOUN", "FACT", "RACE", "CLAS", "REGN", "BSGN", "GLOB", "SNDG", "LTEX", "MGEF", "SKIL"];

    public List<Tes3Plugin> Plugins { get; } = [];
    public Dictionary<string, Tes3Record> Objects { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Tes3Cell> Interiors { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<(int X, int Y), Tes3Cell> Exteriors { get; } = new();
    /// <summary>Exterior terrain (LAND) by cell grid, last plugin wins, with that plugin's landscape texture table
    /// (VTEX indices are per plugin: value - 1 is the plugin's LTEX index, 0 the default texture).</summary>
    public Dictionary<(int X, int Y), (Tes3Record Land, Dictionary<int, string> Textures)> Lands { get; } = new();
    /// <summary>Game settings (GMST) by name, last plugin wins.</summary>
    public Dictionary<string, Tes3Record> Settings { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Magic effects (MGEF, identified by INDX rather than an ID), last plugin wins.</summary>
    public Dictionary<int, Tes3Record> MagicEffects { get; } = new();

    /// <summary>The plugins Morrowind loads, in order: masters first (Morrowind, Tribunal, Bloodmoon), then plugins in
    /// the order Morrowind.ini's [Game Files] lists them (falling back to file date order).</summary>
    public static List<string> LoadOrder(string dataFiles)
    {
        var files = Directory.GetFiles(dataFiles).Select(Path.GetFileName).OfType<string>()
            .Where(f => f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)).ToList();
        string ini = Path.Combine(Path.GetDirectoryName(dataFiles.TrimEnd('\\', '/'))!, "Morrowind.ini");
        var listed = File.Exists(ini)
            ? File.ReadAllLines(ini, Tes3Plugin.Encoding).SkipWhile(l => !l.Trim().Equals("[Game Files]", StringComparison.OrdinalIgnoreCase)).Skip(1)
                .TakeWhile(l => !l.TrimStart().StartsWith('[')).Select(l => l.Split('=', 2)).Where(p => p.Length == 2).Select(p => p[1].Trim()).ToList()
            : [];
        int Rank(string f)
        {
            int masters = Array.FindIndex(new[] { "Morrowind.esm", "Tribunal.esm", "Bloodmoon.esm" }, m => m.Equals(f, StringComparison.OrdinalIgnoreCase));
            if (masters >= 0) return masters;
            int i = listed.FindIndex(l => l.Equals(f, StringComparison.OrdinalIgnoreCase));
            return (f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ? 100 : 1000) + (i >= 0 ? i : 500);
        }
        return files.OrderBy(Rank).ThenBy(f => File.GetLastWriteTimeUtc(Path.Combine(dataFiles, f))).ToList();
    }

    public static MwWorld Load(string dataFiles, IEnumerable<string>? plugins = null)
    {
        var world = new MwWorld();
        foreach (var file in plugins ?? LoadOrder(dataFiles))
            world.Add(Tes3Plugin.Load(Path.Combine(dataFiles, file)));
        return world;
    }

    void Add(Tes3Plugin plugin)
    {
        Plugins.Add(plugin);
        var masters = plugin.Masters.ToList();
        // This plugin's landscape textures by index; masters' tables come first (a plugin's LAND can use them).
        var ltex = new Dictionary<int, string>();
        foreach (var m in masters)
            if (pluginTextures.TryGetValue(m, out var mt)) foreach (var (k, v) in mt) ltex[k] = v;
        foreach (var rec in plugin.Records.Where(r => r.Type == "LTEX"))
            if (rec.Get("INTV") is { } idx && rec.Str("DATA") is { } file) ltex[idx.Int()] = file;
        pluginTextures[plugin.FileName] = ltex;
        foreach (var rec in plugin.Records)
        {
            switch (rec.Type)
            {
                case "CELL": MergeCell(plugin, masters, Tes3Cell.Parse(rec)); break;
                case "GMST": Settings[rec.Id] = rec; break;
                case "MGEF":
                    if (rec.Get("INDX") is { } indx) MagicEffects[indx.Int()] = rec;
                    break;
                case "LAND":
                    if (rec.Get("INTV") is { } grid && rec.Has("VHGT")) Lands[(grid.Int(0), grid.Int(4))] = (rec, ltex);
                    break;
                default:
                    if (!ObjectTypes.Contains(rec.Type) || rec.Id.Length == 0) break;
                    if (rec.Deleted) Objects.Remove(rec.Id);
                    else Objects[rec.Id] = rec;
                    break;
            }
        }
    }

    /// <summary>Key of a reference: the plugin it was first placed by and its number there.</summary>
    static (string Origin, uint Index) RefKey(Tes3Plugin plugin, List<string> masters, uint refNum)
    {
        int master = (int)(refNum >> 24);
        string origin = master == 0 || master > masters.Count ? plugin.FileName : masters[master - 1];
        return (origin.ToLowerInvariant(), refNum & 0xFFFFFF);
    }

    readonly Dictionary<Tes3Cell, Dictionary<(string, uint), Tes3Ref>> refIndex = new();
    readonly Dictionary<string, Dictionary<int, string>> pluginTextures = new(StringComparer.OrdinalIgnoreCase);

    void MergeCell(Tes3Plugin plugin, List<string> masters, Tes3Cell incoming)
    {
        Tes3Cell? cell = incoming.IsInterior
            ? Interiors.GetValueOrDefault(incoming.Name)
            : Exteriors.GetValueOrDefault((incoming.GridX, incoming.GridY));
        if (cell is null)
        {
            cell = new Tes3Cell { Name = incoming.Name, GridX = incoming.GridX, GridY = incoming.GridY };
            if (incoming.IsInterior) Interiors[incoming.Name] = cell; else Exteriors[(incoming.GridX, incoming.GridY)] = cell;
            refIndex[cell] = new();
        }
        // Header: the latest plugin's values.
        cell.Flags = incoming.Flags;
        if (!incoming.IsInterior && incoming.Name.Length > 0) cell.Name = incoming.Name;
        cell.Region = incoming.Region ?? cell.Region;
        cell.Ambient = incoming.Ambient ?? cell.Ambient;
        cell.Sunlight = incoming.Sunlight ?? cell.Sunlight;
        cell.Fog = incoming.Fog ?? cell.Fog;
        if (incoming.Ambient is not null) cell.FogDensity = incoming.FogDensity;
        cell.WaterHeight = incoming.WaterHeight ?? cell.WaterHeight;

        var index = refIndex[cell];
        foreach (var r in incoming.Refs)
        {
            var key = RefKey(plugin, masters, r.RefNum);
            if (index.TryGetValue(key, out var old)) cell.Refs.Remove(old);
            if (r.Deleted) { index.Remove(key); continue; }
            index[key] = r;
            cell.Refs.Add(r);
        }
    }

    /// <summary>The model (MODL) of an object, or null.</summary>
    public string? Model(string id) => Objects.TryGetValue(id, out var r) ? r.Str("MODL") : null;
}
