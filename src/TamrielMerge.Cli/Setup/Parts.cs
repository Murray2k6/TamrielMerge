namespace TamrielMerge.Setup;

/// <summary>
/// TamrielMerge's parts, each switched on or off by its plugins (user, 2026-10-10: "can we add our own mod organizer to the
/// tool", "TamrielMerge's parts"): a part's files are its own (paths under tamrielmerge\..., or in its archives), so with its
/// plugins off nothing of it is used, and nothing in the game folder is ever deleted. The order here is the load order.
/// </summary>
public static class Parts
{
    /// <param name="Plugins">Its plugin file names; a name ending in "*" stands for every plugin starting with the rest
    /// (the voices mod's "TamrielMerge Voices.esp", "TamrielMerge Voices 2.esp"...).</param>
    /// <param name="Core">Needed by the others: shown, not switchable.</param>
    public sealed record Part(string Name, string Description, string[] Plugins, bool Core = false);

    public static readonly IReadOnlyList<Part> All =
    [
        new("Daggerfall", "The Iliac Bay: its land, towns, dungeons, people and quests",
            // The land is one plugin or several (OverlandTiles.LandPluginNames; those not there are left out).
            ["Daggerfall.esm", .. Skyrim.Terrain.OverlandTiles.LandPluginNames, Skyrim.DaggerfallTowns.PluginName, Skyrim.DaggerfallDungeons.PluginName,
             Skyrim.Terrain.OverlandDoorLinks.PluginName]),
        new("Redguard", "Stros M'Kai in 2E 864: Cyrus's story", [Redguard.RgPluginBuilder.PluginName]),
        new("Morrowind", "Vvardenfell, Mournhold and Solstheim, with Tribunal and Bloodmoon", [Morrowind.MwPluginBuilder.PluginName]),
        new("Oblivion", "Cyrodiil and the Shivering Isles", [Oblivion.ObPluginBuilder.PluginName]),
        new("Story and travel", "The games in story order and the Travel Through Time power: what joins them together",
            [Skyrim.AltStartBuilder.PluginName], Core: true),
        new("Progression", "The Tamriel menu: factions, skills and the passive web", [Skyrim.Progression.ProgressionBuilder.PluginName]),
        new("Voices", "Voices the games never recorded, made for them: off, the lines show as text as in the original games",
            [Skyrim.VoiceArchives.Name + "*"]),
        new("Main menu and music", "The flyby over the converted games, the red emblem and the menu's own theme: off, Skyrim's menu",
            [Skyrim.Render.MenuArchive.PluginName]),
        new("Remade models", "Models chosen in the model viewer in place of the converted ones", [Skyrim.Models.ModelCatalog.PluginName]),
        new("Creatures", "Ordinary creatures from all the games spawning in every world", [Skyrim.CreatureSpawns.PluginName]),
        new("Creature models", "The games' own creatures with their own skeletons and animations (Daggerfall's from free models): off, Skyrim bodies stand in",
            // One per game, each over its own game's plugin only.
            [.. new[] { "Daggerfall.esm", "Redguard.esm", "Morrowind.esm", "Oblivion.esm" }.Select(Skyrim.Creatures.CreatureRecords.PluginFor)]),
    ];

    /// <summary>A part's plugins as files in <paramref name="dataDir"/> (the "*" names expanded), in order.</summary>
    public static List<string> Files(Part part, string dataDir) =>
        part.Plugins.SelectMany(p => p.EndsWith('*')
                ? Directory.Exists(dataDir) ? Directory.EnumerateFiles(dataDir, p[..^1] + "*.esp").Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase) : []
                : File.Exists(Path.Combine(dataDir, p)) ? [p] : Enumerable.Empty<string>())
            .ToList();

    /// <summary>A plugin's masters, from its header (empty when it can't be read).</summary>
    public static List<string> MastersOf(string dataDir, string plugin)
    {
        try
        {
            using var mod = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(Path.Combine(dataDir, plugin), Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE);
            return mod.ModHeader.MasterReferences.Select(m => m.Master.FileName.String).ToList();
        }
        catch (Exception) { return []; }
    }

    /// <summary>
    /// The installed plugins of every part that need any of <paramref name="plugins"/>, directly or through another
    /// (user, 2026-10-11: "are they set for when a user does not have a certain game"): the procedural creatures and the
    /// creature models need each game they cover, so a game switched off takes them with it, or Skyrim stops at a
    /// missing master.
    /// </summary>
    public static List<string> Dependents(string dataDir, IEnumerable<string> plugins)
    {
        var gone = new HashSet<string>(plugins, StringComparer.OrdinalIgnoreCase);
        var installed = All.SelectMany(p => Files(p, dataDir)).Where(f => !gone.Contains(f)).ToList();
        var masters = installed.ToDictionary(f => f, f => MastersOf(dataDir, f), StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        for (bool more = true; more;)
        {
            more = false;
            foreach (var f in installed.Where(f => !gone.Contains(f) && masters[f].Any(gone.Contains)).ToList())
            {
                gone.Add(f); found.Add(f); more = true;
            }
        }
        return found;
    }

    /// <summary>Of <paramref name="plugins"/>, those whose masters are there and, when they are TamrielMerge's own, on (or
    /// among them); for the rest, the master they lack.</summary>
    public static (List<string> Ready, List<(string Plugin, string Needs)> Blocked) Switchable(string dataDir, IReadOnlyList<string> plugins, Func<string, bool> isOn)
    {
        var ours = All.SelectMany(p => Files(p, dataDir)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ready = new List<string>();
        var blocked = new List<(string, string)>();
        foreach (var f in plugins)
        {
            string? missing = MastersOf(dataDir, f).FirstOrDefault(m => !plugins.Contains(m, StringComparer.OrdinalIgnoreCase)
                && (!File.Exists(Path.Combine(dataDir, m)) || ours.Contains(m) && !isOn(m)));
            if (missing is null) ready.Add(f); else blocked.Add((f, missing));
        }
        return (ready, blocked);
    }

    /// <summary>The part's size in <paramref name="dataDir"/>: its plugins and their archives (loose files not counted).</summary>
    public static long Size(Part part, string dataDir) =>
        Files(part, dataDir).Sum(p =>
        {
            string stem = Path.GetFileNameWithoutExtension(p);
            return new FileInfo(Path.Combine(dataDir, p)).Length
                + Directory.EnumerateFiles(dataDir, stem + "*.bsa").Where(b => Path.GetFileNameWithoutExtension(b) is var n && (n == stem || n == stem + " - Textures"))
                    .Sum(b => new FileInfo(b).Length);
        });
}
