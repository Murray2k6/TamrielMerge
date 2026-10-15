using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall's towns beyond those in Daggerfall.esm, in one plugin "Daggerfall Towns.esm" with Daggerfall.esm as
/// master, built in parts (one process each; every town at once would not fit in memory) that
/// <see cref="PluginMerger"/> then joins into that one plugin. A batch builds its towns as Daggerfall.esm builds its own (streets, buildings, people, shops, conversations,
/// guild services, banks, town dungeons), each entered from its spot on the overland (overrides of Daggerfall.esm's
/// tiles). Everything shared is then Daggerfall.esm's own record, matched by EditorID (<see cref="UseMasterRecords"/>):
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

    /// <summary>The towns the batches cover, in a fixed order: every town with blocks that Daggerfall.esm doesn't build.</summary>
    public static List<DfLocation> All(string arena2, ISkyrimModGetter master)
    {
        var inMaster = master.Worldspaces.Select(w => w.EditorID).Where(e => e?.StartsWith("DFT") == true).ToHashSet();
        return DfLocations.LoadAll(arena2)
            .Where(l => l.ExteriorBlocks.Length > 0 && l.Type is DfLocationType.TownCity or DfLocationType.TownHamlet or DfLocationType.TownVillage)
            .Where(l => !inMaster.Contains("DFT" + LocationPlacer.Sanitize(l.Name) + l.Region))
            .OrderBy(l => l.Region).ThenBy(l => l.Name, StringComparer.Ordinal).ThenBy(l => l.PixelX).ThenBy(l => l.PixelY)
            .ToList();
    }

    /// <summary>Batch <paramref name="batch"/> of <paramref name="batches"/>: a run of towns holding an even share of the blocks.</summary>
    public static List<DfLocation> Slice(List<DfLocation> all, int batch, int batches)
    {
        long total = all.Sum(l => (long)l.ExteriorBlocks.Length), before = 0;
        var mine = new List<DfLocation>();
        foreach (var l in all)
        {
            // A town belongs to the batch its first block falls in.
            int b = (int)Math.Min(batches, before * batches / Math.Max(total, 1) + 1);
            if (b == batch) mine.Add(l);
            before += l.ExteriorBlocks.Length;
        }
        return mine;
    }

    /// <summary>A part's build, with where its time went (seconds): setup (shared records, sprite models), laying the towns
    /// out, joining their talk and guilds, and writing.</summary>
    public sealed record Report(int Built, int Failed, int People, string Path, double SetupSeconds = 0, double TownsSeconds = 0, double TalkSeconds = 0, double WriteSeconds = 0);

    public static Report Build(string arena2, string skyrimData, string mainPlugin, int batch, int batches, Terrain.TerrainSettings terrain)
    {
        string outDir = Path.GetDirectoryName(Path.GetFullPath(mainPlugin))!;
        string outPath = PartPath(outDir, batch);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
        using var master = SkyrimMod.CreateFromBinaryOverlay(mainPlugin, SkyrimRelease.SkyrimSE);
        var mod = new SkyrimMod(ModKey.FromFileName(PluginName), SkyrimRelease.SkyrimSE);
        mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
        PluginMerger.PreparePart(mod, batch, batches);

        var map = DfWorldMap.Load(arena2);
        var field = Terrain.HeightField.Shared(map, terrain);
        var (world, moveIntoTiles) = new Terrain.OverlandTiles(field).OverrideTiles(mod, master);
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
        { Spells = spells, Clutter = clutter, Creatures = creatures, People = people, BackgroundInteriors = true, GroundAt = (x, y) => LocationPlacer.GroundAt(field, x, y) };
        var townBuilder = new TownBuilder(mod, arena2, DfModel.LoadAll(arena2), new DfBsa(Path.Combine(arena2, "BLOCKS.BSA")), map, dungeons)
        {
            People = people,
            Clutter = clutter,
            Creatures = creatures,
            Shops = new TownShops(mod, skyrim) { SpellTomes = [.. spells.Tomes.Values], DaggerfallSmith = DfItems.Lists(master) },
        };

        var towns = Slice(All(arena2, master), batch, batches);
        double setup = clock.Elapsed.TotalSeconds;
        var built = new List<TownBuilder.Built>();
        var questTowns = new List<QuestSites.Town>();
        int failed = 0;
        foreach (var town in towns)
        {
            try
            {
                var t = townBuilder.Build(town, skyrim, world, LocationPlacer.WorldPosition(field, town.PixelX + 0.5, town.PixelY + 0.5));
                built.Add(t);
                questTowns.Add(new QuestSites.Town(town, t));
                t.World.Music = world.Music;
            }
            catch (Exception e) { failed++; Console.WriteLine($"  {town.Name} ({town.RegionName}): {e.Message}"); }
        }
        Console.WriteLine($"Batch {batch}/{batches}: {built.Count} towns ({towns.Sum(t => t.ExteriorBlocks.Length)} blocks), {failed} failed, {townBuilder.SharedInteriors} shared interiors");
        dungeons.FinishInteriors();
        double laidOut = clock.Elapsed.TotalSeconds;
        if (built.Count > 0)
        {
            dungeons.Navmeshes.LinkExteriorEdges();
            var everyone = townBuilder.Placed.Concat(dungeons.PlacedPeople).ToList();
            var talk = new TownTalk(mod, DfText.Load(arena2), people.Factions);
            talk.Enlist(everyone);
            talk.Build(built, townBuilder.TalkQuest, townBuilder.Shops?.Shopkeepers, townBuilder.Shops?.TavernKeepers, townBuilder.Shops?.Bankers);
            townBuilder.AttachDaytime();
            var guilds = new TownGuilds(mod, skyrim, DfText.Load(arena2), people.Factions);
            guilds.Build(talk, townBuilder.TalkQuest, everyone, townBuilder.BreakIns);
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
                QuestSites.Individuals(mod, townBuilder.Placed.Concat(dungeons.PlacedPeople)), QuestSites.MasterMarkers(master, arena2));
            Console.WriteLine($"Quest sites: {questTowns.Count} towns, {dungeons.BuiltDungeons.Count} town dungeons in {sites.Count} site quests");
        }
        foreach (var cell in mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
            if (cell.Location.IsNull) cell.Location.SetTo(iliacBay);
        moveIntoTiles();
        int shared = UseMasterRecords(mod, master);
        Console.WriteLine($"Shared with {Path.GetFileName(mainPlugin)}: {shared} records");

        mod.ModHeader.Description = DoorRefFix.Marker + "; " + StatueRefFix.Marker;   // doors and statues placed as the fixes make old ones
        double talked = clock.Elapsed.TotalSeconds;
        PluginMerger.WritePart(mod, outPath, PluginMerger.Masters(Path.GetFileName(mainPlugin)), batch, batches);
        return new Report(built.Count, failed, townBuilder.Placed.Count, outPath, setup, laidOut - setup, talked - laidOut, clock.Elapsed.TotalSeconds - talked);
    }

    /// <summary>
    /// Replaces each record this plugin made that <paramref name="master"/> already has (same type and EditorID) with the
    /// master's: links are pointed at the master's record and the copy is dropped. Places, cells, worldspaces, navmeshes,
    /// conversations, form lists and the conversation quest are this plugin's own and stay.
    /// </summary>
    public static int UseMasterRecords(SkyrimMod mod, ISkyrimModGetter master)
    {
        static bool Own(IMajorRecordGetter r) => r is ICellGetter or IWorldspaceGetter or IPlacedGetter or INavigationMeshGetter or ILandscapeGetter
            or IDialogTopicGetter or IDialogResponsesGetter or IDialogBranchGetter or ISceneGetter or IFormListGetter
            || r is IQuestGetter q && q.EditorID?.StartsWith("DFTalk") == true
            || r is ILocationGetter l && l.EditorID != "DFLocIliacBay";
        var theirs = new Dictionary<(Type, string), FormKey>();
        foreach (var r in master.EnumerateMajorRecords())
            if (r.EditorID is { } e && r.FormKey.ModKey == master.ModKey && !Own(r))
                theirs.TryAdd((r.Registration.GetterType, e), r.FormKey);
        var remap = new Dictionary<FormKey, FormKey>();
        var drop = new List<(FormKey, Type)>();
        foreach (var r in mod.EnumerateMajorRecords())
            if (r.EditorID is { } e && !Own(r) && theirs.TryGetValue((r.Registration.GetterType, e), out var key))
            {
                remap[r.FormKey] = key;
                drop.Add((r.FormKey, r.Registration.GetterType));
            }
        foreach (var (key, type) in drop) mod.Remove(key, type);
        mod.RemapLinks(remap);
        return drop.Count;
    }
}
