using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Every Daggerfall location with an outside (its RMB blocks: towns, and the farms, homes, temples, taverns, covens and
/// dungeons of the wilds) beyond those in Daggerfall.esm, in one plugin "Daggerfall Towns.esm" with Daggerfall.esm and the
/// land plugins as masters, built in parts (one process each; every town at once would not fit in memory) that
/// <see cref="PluginMerger"/> then joins into that one plugin. A batch builds its locations as Daggerfall.esm builds its
/// own (streets, buildings, people, shops, conversations, guild services, banks, town dungeons), each on the overland at
/// its place in the layout (<see cref="Terrain.LocationLayout"/>), in overrides of the land plugins' own cells of
/// Daggerfall.esm's tiles. Everything shared is then Daggerfall.esm's own record, matched by EditorID (<see cref="UseMasterRecords"/>):
/// factions and reputation, guilds, spells, creatures, items, the street people of a region; so joining a guild, a
/// faction's regard or a bank account is the same in every town. A batch keeps its own conversation quest (its towns'
/// "Where is", taverns, street population) and its banks keep their accounts in Daggerfall.esm's bank.
/// </summary>
public static class DaggerfallTowns
{
    public const string PluginName = "Daggerfall Towns.esm";
    public static string PartPath(string dir, int part) => Path.Combine(dir, $"Daggerfall Towns.part{part}.esm");

    /// <summary>Town blocks per part: a part of ~250 blocks peaks at ~4 GB while building.</summary>
    public const int BlocksPerPart = 400;

    public static int PartCount(List<DfLocation> all) => (int)Math.Max(1, (all.Sum(l => (long)l.ExteriorBlocks.Length) + BlocksPerPart - 1) / BlocksPerPart);

    /// <summary>The locations the batches cover, in a fixed order: every one with blocks that Daggerfall.esm doesn't build
    /// (it has its own as a location record, "DFLoc" and its name and region): towns, the witches' covens (the witches
    /// in the open, their summoning and quests) and every other location's outside.</summary>
    public static List<DfLocation> All(string arena2, ISkyrimModGetter master)
    {
        var inMaster = master.Locations.Select(l => l.EditorID).Where(e => e?.StartsWith("DFLoc") == true).ToHashSet();
        return DfLocations.LoadAll(arena2)
            .Where(l => l.ExteriorBlocks.Length > 0 && l.BlockWidth > 0 && l.BlockHeight > 0)
            .Where(l => !inMaster.Contains("DFLoc" + LocationPlacer.Sanitize(l.Name) + l.Region))
            .OrderBy(l => l.Region).ThenBy(l => l.Name, StringComparer.Ordinal).ThenBy(l => l.PixelX).ThenBy(l => l.PixelY)
            .ToList();
    }

    /// <summary>Batch <paramref name="batch"/> of <paramref name="batches"/>: a run of towns holding an even share of the blocks.</summary>
    public static List<DfLocation> Slice(List<DfLocation> all, int batch, int batches) =>
        Batches(all, batches).Where(x => x.Batch == batch).Select(x => x.Town).ToList();

    /// <summary>Every town with the batch it belongs to: the batch its first block falls in.</summary>
    static IEnumerable<(DfLocation Town, int Batch)> Batches(List<DfLocation> all, int batches)
    {
        long total = all.Sum(l => (long)l.ExteriorBlocks.Length), before = 0;
        foreach (var l in all)
        {
            yield return (l, (int)Math.Min(batches, before * batches / Math.Max(total, 1) + 1));
            before += l.ExteriorBlocks.Length;
        }
    }

    /// <summary>
    /// Every shared interior of every batch's towns, numbered in the towns' order, each built by the batch of the first
    /// town to have it (TownBuilder.SharedOwners): the same numbers in every batch, worked out by each.
    /// </summary>
    static Dictionary<string, (int Index, bool Own)> InteriorOwners(TownBuilder builder, List<DfLocation> all, int batch, int batches)
    {
        var owners = new Dictionary<string, (int, bool)>();
        foreach (var (town, b) in Batches(all, batches))
            foreach (var key in builder.InteriorKeys(town))
                owners.TryAdd(key, (owners.Count, b == batch));
        return owners;
    }

    /// <summary>A part's build, with where its time went (seconds): setup (shared records, sprite models), laying the towns
    /// out, joining their talk and guilds, and writing.</summary>
    public sealed record Report(int Built, int Failed, int People, string Path, double SetupSeconds = 0, double TownsSeconds = 0, double TalkSeconds = 0, double WriteSeconds = 0);

    /// <param name="buildDir">Where the conversion builds (the layout beside Daggerfall.esm, the land plugins): a part's
    /// Daggerfall.esm is a copy in a folder of its own. Unset: the main plugin's folder.</param>
    public static Report Build(string arena2, string skyrimData, string mainPlugin, int batch, int batches, Terrain.TerrainSettings terrain, string? buildDir = null)
    {
        string outDir = Path.GetDirectoryName(Path.GetFullPath(mainPlugin))!;
        string dataDir = buildDir ?? outDir;
        string outPath = PartPath(outDir, batch);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
        using var master = SkyrimMod.CreateFromBinaryOverlay(mainPlugin, SkyrimRelease.SkyrimSE);
        var mod = new SkyrimMod(ModKey.FromFileName(PluginName), SkyrimRelease.SkyrimSE);
        mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
        PluginMerger.PreparePart(mod, batch, batches);

        var map = DfWorldMap.Load(arena2);
        var field = Terrain.HeightField.Shared(map, terrain, DfLocations.LoadAll(arena2));
        var layout = Terrain.LocationLayout.In(dataDir) ?? throw new InvalidOperationException($"no {Terrain.LocationLayout.FileName} in {dataDir}: build Daggerfall.esm first");
        var tiles = new Terrain.OverlandTiles(field);
        // The locations go in the overland's own cells, which the land plugins hold: their masters too.
        var landNames = Terrain.OverlandTiles.LandPluginsIn(dataDir);
        if (landNames.Count == 0) throw new InvalidOperationException($"no Daggerfall land plugins in {dataDir}: the locations stand in their cells");
        var lands = landNames.Select(n => (ISkyrimModGetter)SkyrimMod.CreateFromBinaryOverlay(Path.Combine(dataDir, n), SkyrimRelease.SkyrimSE)).ToList();
        try
        {
        var (world, cellAt, moveIntoTiles) = tiles.OverrideTiles(mod, master, lands);
        var iliacBay = master.Locations.First(l => l.EditorID == "DFLocIliacBay").FormKey;

        // What the towns use, made here as Daggerfall.esm makes it, then swapped for Daggerfall.esm's own records.
        var spells = new SpellBuilder(mod, skyrim, DoorPlacer.BaseDoors);
        spells.Build(DfSpells.Load(arena2), new PlacedObject(mod));
        var propReplacer = new PropReplacer(skyrim, skyrimData);
        var clutter = new ClutterPlacer(skyrim, propReplacer, master.Books.Where(b => b.EditorID?.StartsWith("DF_Book") == true).Select(b => b.FormKey));
        clutter.UseDaggerfallItems(DfItems.Lookup(master));
        // Daggerfall.esm's sprite models and grass, made once by its build; imported here only if it has none.
        int fromMaster = DaggerfallPluginBuilder.SpriteModelsFromMaster(master, clutter);
        if (fromMaster == 0) DaggerfallPluginBuilder.ImportSpriteModels(mod, outDir, arena2, clutter);
        else Console.WriteLine($"Sprite models and grass: {fromMaster} from {Path.GetFileName(mainPlugin)}");
        var creatures = new CreatureBuilder(mod, skyrimData);
        creatures.Build();
        var people = new TownPeople(mod, new VanillaActors(skyrimData), DfFactions.Load(arena2), DfNames.Load(arena2));
        var dungeons = new DungeonCellBuilder(mod, arena2, DfModel.LoadAll(arena2), new DfBsa(Path.Combine(arena2, "BLOCKS.BSA")), outDir, new Navmesh.NavmeshBuilder(mod), propReplacer)
        {
            Spells = spells, Clutter = clutter, Creatures = creatures, People = people, BackgroundInteriors = true, GroundAt = (x, y) => LocationPlacer.GroundAt(field, x, y),
            // The town dungeons' blocks from Daggerfall.esm (it makes every one), as the dungeon parts take them.
            MasterStatics = master.Statics.Where(s => s.EditorID?.StartsWith("DFDunBlk") == true).GroupBy(s => s.EditorID!).ToDictionary(g => g.Key, g => g.First().FormKey),
        };
        var townBuilder = new TownBuilder(mod, arena2, DfModel.LoadAll(arena2), new DfBsa(Path.Combine(arena2, "BLOCKS.BSA")), map, dungeons)
        {
            People = people,
            Clutter = clutter,
            Creatures = creatures,
            Shops = new TownShops(mod, skyrim) { SpellTomes = [.. spells.Tomes.Values], DaggerfallSmith = DfItems.Lists(master) },
            // The way back out and the occupants' names on Daggerfall.esm's conversation quest, which every part's
            // street doors and interiors share; the shared interiors named apart from Daggerfall.esm's own of the same layout.
            WayQuest = master.Quests.FirstOrDefault(q => q.EditorID == "DFTalk")?.FormKey,
            InteriorSuffix = "T",
        };

        var allTowns = All(arena2, master);
        // Each shared interior once in the joined plugin: built by the part of the first town to have it, the others'
        // street doors pointing at it.
        var ownersClock = System.Diagnostics.Stopwatch.StartNew();
        townBuilder.SharedOwners = InteriorOwners(townBuilder, allTowns, batch, batches);
        Console.WriteLine($"Shared interiors: {townBuilder.SharedOwners.Count} in all the parts' towns, {townBuilder.SharedOwners.Values.Count(o => o.Own)} built by this part ({ownersClock.Elapsed.TotalSeconds:F0} s)");
        var towns = Slice(allTowns, batch, batches);
        double setup = clock.Elapsed.TotalSeconds;
        var built = new List<TownBuilder.Built>();
        var questTowns = new List<QuestSites.Town>();
        int failed = 0;
        foreach (var town in towns)
        {
            try
            {
                if (layout.Of(town) is not { } site) { failed++; Console.WriteLine($"  {town.Name} ({town.RegionName}): no place in the layout"); continue; }
                var t = townBuilder.Build(town, skyrim, new TownBuilder.OverlandSite(world, cellAt, Terrain.LocationLayout.Corner(site), site.CellsW, site.CellsH));
                built.Add(t);
                // Towns as quest sites (QuestSites: their buildings); the dungeons' parts make the other locations' sites.
                if (TownBuilder.IsTown(town) || town.Type == DfLocationType.Coven) questTowns.Add(new QuestSites.Town(town, t));
            }
            catch (Exception e) { failed++; Console.WriteLine($"  {town.Name} ({town.RegionName}): {e.Message}"); }
        }
        Console.WriteLine($"Batch {batch}/{batches}: {built.Count} locations ({towns.Sum(t => t.ExteriorBlocks.Length)} blocks), {failed} failed, {townBuilder.SharedInteriors} shared interiors ({townBuilder.SharedInteriorsBuilt} built here), {Navmesh.NavmeshBuilder.InteriorsShared} interior navmeshes made by other parts; overland cells: {tiles.OverriddenCells} of the land's, {tiles.NewCells} new");
        dungeons.FinishInteriors();
        double laidOut = clock.Elapsed.TotalSeconds;
        if (built.Count > 0)
        {
            var everyone = townBuilder.Placed.Concat(dungeons.PlacedPeople).ToList();
            var talk = new TownTalk(mod, DfText.Load(arena2)) { ToneGlobals = TalkTone.Globals(mod, master) };
            talk.Enlist(everyone);
            talk.Build(built, townBuilder.TalkQuest, townBuilder.Shops?.Shopkeepers, townBuilder.Shops?.TavernKeepers, townBuilder.Shops?.Bankers);
            townBuilder.AttachDaytime();
            var guilds = new TownGuilds(mod, skyrim, DfText.Load(arena2));
            guilds.Build(talk, townBuilder.TalkQuest, everyone, townBuilder.BreakIns);
            if (master.Quests.FirstOrDefault(q => q.EditorID == "DFQuestMachine")?.FormKey is { } summonMachine) guilds.AddSummoning(summonMachine);
            // This batch's own conversation quest; its banks keep their accounts in Daggerfall.esm's.
            townBuilder.TalkQuest.EditorID = "DFTalk" + batch;
            if (master.Quests.FirstOrDefault(q => q.EditorID == "DFTalk") is { } masterTalk)
                foreach (var script in townBuilder.TalkQuest.VirtualMachineAdapter?.Scripts.Where(s => s.Name == "TMMoneyChanger") ?? [])
                    script.Properties.Add(new ScriptObjectProperty { Name = "Accounts", Object = masterTalk.FormKey.ToLink<ISkyrimMajorRecordGetter>() });
        }
        foreach (var t in built) t.Location.ParentLocation.SetTo(iliacBay);
        // This part's quest sites (its towns' buildings and dungeons, the individuals in them) for Daggerfall.esm's quest
        // machine (TMDfQuestSites registers them in game).
        if (master.Quests.FirstOrDefault(q => q.EditorID == "DFQuestMachine")?.FormKey is { } machine)
        {
            var sites = QuestSites.Build(mod, $"T{batch}", machine, questTowns, dungeons.BuiltDungeons, dungeons.QuestMarkers,
                QuestSites.Individuals(mod, townBuilder.Placed.Concat(dungeons.PlacedPeople)), QuestSites.MasterMarkers(master, arena2), townBuilder.OtherPartsMarkers);
            Console.WriteLine($"Quest sites: {questTowns.Count} towns, {dungeons.BuiltDungeons.Count} town dungeons in {sites.Count} site quests");
        }
        foreach (var cell in mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
            if (cell.Location.IsNull) cell.Location.SetTo(iliacBay);
        moveIntoTiles();
        int shared = UseMasterRecords(mod, master);
        Console.WriteLine($"Shared with {Path.GetFileName(mainPlugin)}: {shared} records");

        mod.ModHeader.Description = DoorRefFix.Marker + "; " + StatueRefFix.Marker;   // doors and statues placed as the fixes make old ones
        double talked = clock.Elapsed.TotalSeconds;
        PluginMerger.WritePart(mod, outPath, PluginMerger.Masters([Path.GetFileName(mainPlugin), .. landNames]), batch, batches);
        return new Report(built.Count, failed, townBuilder.Placed.Count, outPath, setup, laidOut - setup, talked - laidOut, clock.Elapsed.TotalSeconds - talked);
        }
        finally { foreach (var l in lands) ((IDisposable)l).Dispose(); }
    }

    /// <summary>
    /// Replaces each record this plugin made that <paramref name="master"/> already has (same type and EditorID) with the
    /// master's: links are pointed at the master's record and the copy is dropped. Places, cells, worldspaces, navmeshes,
    /// conversations, form lists and the conversation quest are this plugin's own and stay.
    /// </summary>
    public static int UseMasterRecords(SkyrimMod mod, ISkyrimModGetter master, Func<IMajorRecordGetter, bool>? only = null)
    {
        static bool Own(IMajorRecordGetter r) => r is ICellGetter or IWorldspaceGetter or IPlacedGetter or INavigationMeshGetter or ILandscapeGetter
            or IDialogTopicGetter or IDialogResponsesGetter or IDialogBranchGetter or ISceneGetter or IFormListGetter
            || r is IQuestGetter q && q.EditorID?.StartsWith("DFTalk") == true
            || r is ILocationGetter l && l.EditorID != "DFLocIliacBay";
        var theirs = new Dictionary<(Type, string), FormKey>();
        foreach (var r in master.EnumerateMajorRecords())
            if (r.EditorID is { } e && r.FormKey.ModKey == master.ModKey && !Own(r) && (only is null || only(r)))
                theirs.TryAdd((r.Registration.GetterType, e), r.FormKey);
        var remap = new Dictionary<FormKey, FormKey>();
        var drop = new List<(FormKey, Type)>();
        foreach (var r in mod.EnumerateMajorRecords())
            if (r.EditorID is { } e && !Own(r) && (only is null || only(r)) && theirs.TryGetValue((r.Registration.GetterType, e), out var key))
            {
                remap[r.FormKey] = key;
                drop.Add((r.FormKey, r.Registration.GetterType));
            }
        foreach (var (key, type) in drop) mod.Remove(key, type);
        mod.RemapLinks(remap);
        return drop.Count;
    }
}
