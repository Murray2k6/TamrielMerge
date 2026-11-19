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
/// adds each dungeon's interior cell and its entrance door on the Iliac Bay, in the dungeon's outside that the town parts
/// build at its place in the layout (overrides of Daggerfall.esm's overland tiles, persistent cells only), and uses Daggerfall.esm's creatures, trap spells and block statics rather than copies (made
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

    /// <param name="buildDir">Where the conversion builds (the layout beside Daggerfall.esm): a part's Daggerfall.esm is a
    /// copy in a folder of its own. Unset: the main plugin's folder.</param>
    public static Report Build(string arena2, string skyrimData, string mainPlugin, int batch, int batches, Terrain.TerrainSettings terrain, string? buildDir = null)
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
        var field = Terrain.HeightField.Shared(map0, terrain, DfLocations.LoadAll(arena2));
        var (world, _, moveIntoTiles) = new Terrain.OverlandTiles(field).OverrideTiles(mod, master);
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
        // The people Daggerfall stands in dungeons (Scourg Barrow's necromancers and the King of Worms, castle courts).
        var people = new TownPeople(mod, new VanillaActors(skyrimData), DfFactions.Load(arena2), DfNames.Load(arena2));
        var dungeons = new DungeonCellBuilder(mod, arena2, DfModel.LoadAll(arena2), new DfBsa(System.IO.Path.Combine(arena2, "BLOCKS.BSA")), outDir, new Navmesh.NavmeshBuilder(mod), props)
        {
            Spells = spells, Creatures = creatures, MasterStatics = statics, BackgroundNavmesh = true, People = people,
            QuestGlobal = n => DfQuestGlobals.For(mod, n, master),
            GroundAt = (x, y) => LocationPlacer.GroundAt(field, x, y),
            Clutter = new ClutterPlacer(skyrim, props, master.Books.Where(b => b.EditorID?.StartsWith("DF_Book") == true).Select(b => b.FormKey)),
        };
        dungeons.Clutter!.UseDaggerfallItems(DfItems.Lookup(master));   // Daggerfall's weapons and armor on its floors
        // Each entrance in its location's outside on the overland (the town parts build it at its place in the layout):
        // the door set into its blocks' dungeon entrance surface; a location whose blocks have none, outside the south
        // side of its footprint, where travel arrives; one with no outside (no place in the layout), at its map pixel.
        var layout = Terrain.LocationLayout.In(buildDir ?? outDir) ?? throw new InvalidOperationException($"no {Terrain.LocationLayout.FileName} in {buildDir ?? outDir}: build Daggerfall.esm first");
        var outsides = new TownBuilder(mod, arena2, DfModel.LoadAll(arena2), new DfBsa(System.IO.Path.Combine(arena2, "BLOCKS.BSA")), map0, dungeons);
        int inSurface = 0, bySide = 0, atPixel = 0;
        DungeonCellBuilder.Entrance EntranceOf(DfLocation loc)
        {
            if (layout.Of(loc) is not { } site) { atPixel++; return dungeons.OpenGround(LocationPlacer.WorldPosition(field, loc.PixelX + 0.5, loc.PixelY + 0.5)); }
            var corner = Terrain.LocationLayout.Corner(site);
            if (outsides.DungeonSpot(loc, corner) is { } door) { inSurface++; return door; }
            bySide++;
            return dungeons.OpenGround(TownBuilder.ArrivalSpot(loc, corner, outsides.Block, (x, y) => LocationPlacer.GroundAt(field, x, y)));
        }
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
                // The slow ones named, as Oblivion's are: one dungeon's navmesh can hold up its whole part.
                if (b.NavmeshWork.Seconds >= 30) Console.WriteLine($"  navmesh {loc.Name}: {b.NavmeshWork.Seconds:F0} s ({b.Cell.NavigationMeshes.Count} navmesh, {b.Navmesh.Triangles:N0} triangles, {loc.DungeonBlocks.Count} blocks)");
                refs += b.Cell.Temporary.Count + b.Cell.Persistent.Count;
                built++;
            }
            catch (Exception e) { failed++; Console.WriteLine($"  {loc.Name}: navmesh: {e.Message}"); }
        }
        foreach (var loc in all.Skip((batch - 1) * per).Take(per))
        {
            try
            {
                var b = dungeons.Build(loc, world, EntranceOf(loc));
                if (iliacBay is { } l) b.Cell.Location.SetTo(l);
                inFlight.Enqueue((loc, b));
            }
            catch (Exception e)
            {
                failed++;
                // Where in the converter it failed (the innermost of its own frames).
                string? at = e.StackTrace?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("TamrielMerge."));
                Console.WriteLine($"  {loc.Name}: {e.Message}{(at is null ? "" : $" ({at})")}");
            }
            // Only the navmeshes still being made count against the window, and the finished ones are taken in build order as
            // they come: waiting on the oldest held the layout and every other thread while one dungeon tried Recast again and
            // again (2026-11-02, batch 15: The Hold of Copperham's 468 s took the batch from about 2 minutes to 11, with 4.6
            // of 16 cores busy over the whole run).
            while (inFlight.Count > 0 && inFlight.Peek().B.NavmeshWork.IsMade) FinishOldest();
            while (inFlight.Count(f => !f.B.NavmeshWork.IsMade) > window)
            {
                Navmesh.NavmeshBuilder.PendingInterior.WaitAny(inFlight.Select(f => f.B.NavmeshWork));
                while (inFlight.Count > 0 && inFlight.Peek().B.NavmeshWork.IsMade) FinishOldest();
            }
        }
        double waitStart = clock.Elapsed.TotalSeconds;
        while (inFlight.Count > 0) FinishOldest();
        double layoutSeconds = waitStart - setup, finishWait = clock.Elapsed.TotalSeconds - waitStart;
        Console.WriteLine($"Entrances: {inSurface} in their outside's entrance, {bySide} by its south side (no entrance surface), {atPixel} at their map pixel (no outside)");

        moveIntoTiles();
        // Its people in their Daggerfall factions and social groups: Daggerfall.esm's faction records (so their regard,
        // the quests' work topics and the guilds are the same as in town).
        if (dungeons.PlacedPeople.Count > 0)
        {
            new TownTalk(mod, DfText.Load(arena2)).Enlist(dungeons.PlacedPeople);
            Console.WriteLine($"People: {dungeons.PlacedPeople.Count} in dungeons; shared with {System.IO.Path.GetFileName(mainPlugin)}: "
                + DaggerfallTowns.UseMasterRecords(mod, master, r => r is IFactionGetter or IGlobalGetter) + " records");
        }
        // This part's dungeons as quest sites for Daggerfall.esm's quest machine (TMDfQuestSites registers them in game),
        // with the named individuals standing in them (the King of Worms).
        if (master.Quests.FirstOrDefault(q => q.EditorID == "DFQuestMachine")?.FormKey is { } machine)
        {
            var sites = QuestSites.Build(mod, $"D{batch}", machine, [], dungeons.BuiltDungeons, dungeons.QuestMarkers, QuestSites.Individuals(mod, dungeons.PlacedPeople), QuestSites.MasterMarkers(master, arena2));
            Console.WriteLine($"Quest sites: {dungeons.BuiltDungeons.Count} dungeons in {sites.Count} site quests");
        }

        var catalog = new CreatureCatalog { Game = "Daggerfall", Plugin = PluginName, ExteriorRespawnHours = 0, InteriorRespawnHours = 0 };
        foreach (var (placed, cell, habitat) in dungeons.CreatureSpots)
        {
            var p = placed.Placement!.Position;
            catalog.Spots.Add(new(CreatureCatalog.Key(placed.FormKey), CreatureCatalog.Key(cell.FormKey), "", p.X, p.Y, p.Z, habitat, 1, 0));
        }
        mod.ModHeader.Description = DoorRefFix.Marker + "; " + StatueRefFix.Marker;   // doors and statues placed as the fixes make old ones
        LetGoOfScratch(mod, scratch, master);
        PluginMerger.WritePart(mod, outPath, PluginMerger.Masters(System.IO.Path.GetFileName(mainPlugin)), batch, batches);
        catalog.Save(outPath);
        return new Report(built, failed, refs, tris, outPath, setup, layoutSeconds + finishWait, navSeconds, clock.Elapsed.TotalSeconds - setup - layoutSeconds - finishWait);
    }

    /// <summary>
    /// Links still to the thrown-away plugin the creatures and trap spells were built in (TMScratch.esp; their builders
    /// hand out Daggerfall.esm's records by EditorID, UseMaster): pointed at Daggerfall.esm's record of the same kind and
    /// EditorID, else the record (and what it links to there) copied into this part. 2026-10-25: part 24's Orsinium had
    /// script properties naming one, and the part couldn't be written ("Could not map FormKey to a master index").
    /// </summary>
    static void LetGoOfScratch(SkyrimMod mod, SkyrimMod scratch, ISkyrimModGetter master)
    {
        // Daggerfall.esm's records by EditorID, read a kind at a time and only the kinds a link needs (2026-11-02: read
        // whole, every record of the land and towns included, for parts that mostly had no link to let go of).
        var theirs = new Dictionary<(Type, string), FormKey>();
        var kindsRead = new HashSet<Type>();
        bool Theirs(Type kind, string editorId, out FormKey key)
        {
            if (kindsRead.Add(kind))
                foreach (var r in master.EnumerateMajorRecords(kind))
                    if (r.EditorID is { } e && r.FormKey.ModKey == master.ModKey) theirs.TryAdd((kind, e), r.FormKey);
            return theirs.TryGetValue((kind, editorId), out key);
        }
        var scratchRecords = scratch.EnumerateMajorRecords().ToDictionary(r => r.FormKey);
        var remap = new Dictionary<FormKey, FormKey>();
        int toMaster = 0, copied = 0;
        var named = new List<string>();
        for (int pass = 0; pass < 8; pass++)
        {
            var leaked = mod.EnumerateMajorRecords().SelectMany(r => r.EnumerateFormLinks()).Select(l => l.FormKey)
                .Where(k => k.ModKey == scratch.ModKey && !remap.ContainsKey(k)).Distinct().ToList();
            if (leaked.Count == 0) break;
            foreach (var k in leaked)
            {
                if (!scratchRecords.TryGetValue(k, out var rec)) throw new InvalidOperationException($"a link to {k}, which the scratch plugin hasn't");
                if (rec.EditorID is { } e && Theirs(rec.Registration.GetterType, e, out var key)) { remap[k] = key; toMaster++; }
                else
                {
                    var copy = (MajorRecord)rec.Duplicate(mod.GetNextFormKey());
                    mod.GetTopLevelGroup(rec.Registration.GetterType).AddUntyped(copy);
                    remap[k] = copy.FormKey; copied++;
                }
                if (named.Count < 8) named.Add($"{rec.Registration.Name} {rec.EditorID}");
            }
            mod.RemapLinks(remap);
        }
        if (toMaster + copied > 0)
            Console.WriteLine($"Scratch records still linked: {toMaster} pointed at {master.ModKey.FileName}'s, {copied} copied here ({string.Join(", ", named)})");
    }
}
