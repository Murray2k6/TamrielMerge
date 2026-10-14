using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall's dungeons outside its towns (4,228; Privateer's Hold and town dungeons are in Daggerfall.esm), in one
/// plugin "Daggerfall Dungeons.esm" with Daggerfall.esm as master, built in parts (one process each: all of them hold
/// ~2 million references and ~10 million navmesh triangles, more than one build can hold in memory) that
/// <see cref="PluginMerger"/> joins. A batch
/// adds each dungeon's interior cell and its entrance door on the Iliac Bay (overrides of Daggerfall.esm's overland tiles,
/// persistent cells only), and uses Daggerfall.esm's creatures, trap spells and block statics rather than copies (made
/// in a plugin that is thrown away, then matched by EditorID). Its ordinary creature spots go in its own
/// .creatures.json for procedural creatures.
/// </summary>
public static class DaggerfallDungeons
{
    public const string PluginName = "Daggerfall Dungeons.esm";
    public static string PartPath(string dir, int part) => Path.Combine(dir, $"Daggerfall Dungeons.part{part}.esm");
    /// <summary>40 parts of about 106 dungeons: 8 ran out of memory (2026-10-10), and a part holds up to 8 navmeshes being
    /// made at once (about 5 GB at its peak in 40 parts).</summary>
    public const int Parts = 40;

    /// <summary>The dungeons the batches cover, in a fixed order.</summary>
    public static List<DfLocation> All(string arena2) => DfLocations.LoadAll(arena2)
        .Where(l => l.DungeonBlocks.Count > 0 && l.Type is not (DfLocationType.TownCity or DfLocationType.TownHamlet or DfLocationType.TownVillage) && l.Name != "Privateer's Hold")
        .ToList();

    /// <summary>A part's build, with where its time went (seconds): setup, laying the dungeons out, their navmeshes (all
    /// threads' time added up) and writing.</summary>
    public sealed record Report(int Built, int Failed, long References, long NavmeshTriangles, string Path, double SetupSeconds = 0, double LayoutSeconds = 0, double NavmeshSeconds = 0, double WriteSeconds = 0);

    public static Report Build(string arena2, string skyrimData, string mainPlugin, int batch, int batches, Terrain.TerrainSettings terrain)
    {
        string outDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(mainPlugin))!;
        string outPath = PartPath(outDir, batch);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var skyrim = SkyrimMod.CreateFromBinaryOverlay(System.IO.Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
        using var master = SkyrimMod.CreateFromBinaryOverlay(mainPlugin, SkyrimRelease.SkyrimSE);
        var mod = new SkyrimMod(ModKey.FromFileName(PluginName), SkyrimRelease.SkyrimSE);
        mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
        PluginMerger.PreparePart(mod, batch, batches);

        // Entrance doors are placed on the whole Iliac Bay in global coordinates (a scratch worldspace), then moved into
        // the overland tiles they stand in: overrides of Daggerfall.esm's tiles, for their persistent cells only.
        var map0 = DfWorldMap.Load(arena2);
        var field = Terrain.HeightField.Shared(map0, terrain);
        var (world, moveIntoTiles) = new Terrain.OverlandTiles(field).OverrideTiles(mod, master);
        var iliacBay = master.Locations.FirstOrDefault(l => l.EditorID == "DFLocIliacBay")?.FormKey;

        // Daggerfall.esm's creatures and trap spells, by name (built once more into a plugin that is thrown away).
        var scratch = new SkyrimMod(ModKey.FromFileName("TMScratch.esp"), SkyrimRelease.SkyrimSE);
        var spells = new SpellBuilder(scratch, skyrim, DoorPlacer.BaseDoors);
        spells.Build(DfSpells.Load(arena2), new PlacedObject(scratch));
        spells.UseMaster(master);
        var creatures = new CreatureBuilder(scratch, skyrimData);
        creatures.Build();
        creatures.UseMaster(master);
        var statics = master.Statics.Where(s => s.EditorID?.StartsWith("DFDunBlk") == true).GroupBy(s => s.EditorID!).ToDictionary(g => g.Key, g => g.First().FormKey);

        var props = new PropReplacer(skyrim, skyrimData);
        var dungeons = new DungeonCellBuilder(mod, arena2, DfModel.LoadAll(arena2), new DfBsa(System.IO.Path.Combine(arena2, "BLOCKS.BSA")), outDir, new Navmesh.NavmeshBuilder(mod), props)
        {
            Spells = spells, Creatures = creatures, MasterStatics = statics, BackgroundNavmesh = true,
            QuestGlobal = n => DfQuestGlobals.For(mod, n, master),
            GroundAt = (x, y) => LocationPlacer.GroundAt(field, x, y),
            Clutter = new ClutterPlacer(skyrim, props, master.Books.Where(b => b.EditorID?.StartsWith("DF_Book") == true).Select(b => b.FormKey)),
        };
        dungeons.Clutter!.UseDaggerfallItems(DfItems.Lookup(master));   // Daggerfall's weapons and armor on its floors
        var all = All(arena2);
        int per = (all.Count + batches - 1) / batches;
        int built = 0, failed = 0;
        long refs = 0, tris = 0;
        double setup = clock.Elapsed.TotalSeconds, navSeconds = 0;
        // Navmeshes are made on worker threads while the next dungeons are laid out, a few at a time (each holds its
        // geometry until finished), and finished in build order so the plugin comes out the same as one made in turn.
        int window = Math.Clamp(Environment.ProcessorCount - 2, 1, 8);
        var inFlight = new Queue<(DfLocation Loc, DungeonCellBuilder.Built B)>();
        void FinishOldest()
        {
            var (loc, b) = inFlight.Dequeue();
            try
            {
                tris += b.Navmesh.Triangles;
                navSeconds += b.NavmeshWork.Seconds;
                refs += b.Cell.Temporary.Count + b.Cell.Persistent.Count;
                built++;
            }
            catch (Exception e) { failed++; Console.WriteLine($"  {loc.Name}: navmesh: {e.Message}"); }
        }
        foreach (var loc in all.Skip((batch - 1) * per).Take(per))
        {
            try
            {
                var b = dungeons.Build(loc, world, LocationPlacer.WorldPosition(field, loc.PixelX + 0.5, loc.PixelY + 0.5));
                if (iliacBay is { } l) b.Cell.Location.SetTo(l);
                inFlight.Enqueue((loc, b));
            }
            catch (Exception e) { failed++; Console.WriteLine($"  {loc.Name}: {e.Message}"); }
            while (inFlight.Count > window) FinishOldest();
        }
        double waitStart = clock.Elapsed.TotalSeconds;
        while (inFlight.Count > 0) FinishOldest();
        double layout = waitStart - setup, finishWait = clock.Elapsed.TotalSeconds - waitStart;

        moveIntoTiles();
        // This part's dungeons as quest sites for Daggerfall.esm's quest machine (TMDfQuestSites registers them in game).
        if (master.Quests.FirstOrDefault(q => q.EditorID == "DFQuestMachine")?.FormKey is { } machine)
        {
            var sites = QuestSites.Build(mod, $"D{batch}", machine, [], dungeons.BuiltDungeons, dungeons.QuestMarkers, [], QuestSites.MasterMarkers(master, arena2));
            Console.WriteLine($"Quest sites: {dungeons.BuiltDungeons.Count} dungeons in {sites.Count} site quests");
        }

        var catalog = new CreatureCatalog { Game = "Daggerfall", Plugin = PluginName, ExteriorRespawnHours = 0, InteriorRespawnHours = 0 };
        foreach (var (placed, cell, habitat) in dungeons.CreatureSpots)
        {
            var p = placed.Placement!.Position;
            catalog.Spots.Add(new(CreatureCatalog.Key(placed.FormKey), CreatureCatalog.Key(cell.FormKey), "", p.X, p.Y, p.Z, habitat, 1, 0));
        }
        mod.ModHeader.Description = DoorRefFix.Marker + "; " + StatueRefFix.Marker;   // doors and statues placed as the fixes make old ones
        PluginMerger.WritePart(mod, outPath, PluginMerger.Masters(System.IO.Path.GetFileName(mainPlugin)), batch, batches);
        catalog.Save(outPath);
        return new Report(built, failed, refs, tris, outPath, setup, layout + finishWait, navSeconds, clock.Elapsed.TotalSeconds - setup - layout - finishWait);
    }
}
