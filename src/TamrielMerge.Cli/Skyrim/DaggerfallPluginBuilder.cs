using System.Net;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim;

/// <summary>Builds Daggerfall.esm: Daggerfall content rebuilt on top of Skyrim.esm records and assets.</summary>
public static class DaggerfallPluginBuilder
{
    public const string WorldEditorId = "DFIliacBay";

    public static int Build(string arena2, string skyrimData, string outPath, Terrain.TerrainSettings terrain, bool uncut = true, string towns = "Daggerfall@Daggerfall")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        var mod = new SkyrimMod(ModKey.FromFileName(Path.GetFileName(outPath)), SkyrimRelease.SkyrimSE);
        if (outPath.EndsWith(".esm", StringComparison.OrdinalIgnoreCase))
            mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
        using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);

        int books = AddBooks(mod, skyrim, arena2);
        Console.WriteLine($"Books: {books}");

        var map = DfWorldMap.Load(arena2);
        var field = Terrain.HeightField.Generate(map, terrain);
        // The overland, built in global coordinates and split into tiles at the end (OverlandTiles); its land is in
        // "Daggerfall Land N.esm", one plugin per tile.
        var world = Terrain.TerrainBuilder.NewWorldspace(mod, skyrim, field, WorldEditorId, "Iliac Bay");
        var layout = new Terrain.OverlandTiles(field);
        Console.WriteLine($"Overland: {field.CellsX}x{field.CellsY} cells ({terrain.PixelsPerCell} map pixels per cell), {layout.Columns}x{layout.Rows} tiles of {layout.CoreX}x{layout.CoreY}");

        var locations = DfLocations.LoadAll(arena2);
        var markers = LocationPlacer.PlaceMarkers(mod, world, field, locations);
        Console.WriteLine($"Map markers: {markers.Count}");
        // Daggerfall's opening dungeon, where a new game begins.
        string outDir = Path.GetDirectoryName(Path.GetFullPath(outPath))!;
        // Daggerfall's spells (and their tomes), before dungeons so spell traps cast the spell Daggerfall names.
        var spells = new SpellBuilder(mod, skyrim, DoorPlacer.BaseDoors);
        var recallAnchor = new PlacedObject(mod)
        {
            EditorID = "TMDfRecallAnchor",
            Base = new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x3B).ToNullableLink<IPlaceableObjectGetter>(), // XMarker
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
        };
        spells.Build(DfSpells.Load(arena2), recallAnchor);
        Console.WriteLine($"Spells: {spells.Spells.Count} spells with tomes, {spells.Traps.Count} trap variants, {mod.MagicEffects.Count} magic effects");
        var propReplacer = new PropReplacer(skyrim, skyrimData);
        // Daggerfall's ships (models 909, 910) stand only at its two "your ship" locations, which aren't built yet; when
        // they are, PropReplacer.SetShip(ShipModel.Build(...)) puts the tool's ship there. Not made here: a new record
        // early in Daggerfall.esm would renumber everything after it (the batch plugins and saves use those FormIDs).
        // Daggerfall's weapons and armor (fixed numbers from 0xD00000: nothing after them is renumbered).
        var itemMasters = new[] { "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }
            .Select(m => (ISkyrimModGetter)SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, m), SkyrimRelease.SkyrimSE)).Prepend(skyrim).ToList();
        var items = new DfItems(mod, itemMasters, skyrimData, outDir);
        items.Build();
        var clutter = new ClutterPlacer(skyrim, propReplacer, mod.Books.Where(b => b.EditorID?.StartsWith("DF_Book") == true).Select(b => b.FormKey));
        clutter.UseDaggerfallItems(DfItems.Lookup(mod));
        if (clutter.Missing.Count > 0) Console.WriteLine($"Clutter: vanilla forms not found: {string.Join(", ", clutter.Missing)}");
        ImportSpriteModels(mod, outDir, arena2, clutter);
        // Daggerfall's creatures (actors copied from vanilla/DLC creatures) and its encounter tables as leveled lists.
        var creatures = new CreatureBuilder(mod, skyrimData);
        creatures.Build();
        if (creatures.Missing.Count > 0) Console.WriteLine($"Creatures: templates not found: {string.Join(", ", creatures.Missing)}");
        Console.WriteLine($"Creatures: {mod.Npcs.Count} actors, {mod.LeveledNpcs.Count} encounter lists; stand-in bodies for {string.Join(", ", creatures.StandIns)}");
        // Daggerfall's people (town interiors and streets, and the residents of town dungeons such as castles).
        var people = new TownPeople(mod, new VanillaActors(skyrimData), DfFactions.Load(arena2), DfNames.Load(arena2));
        // Daggerfall's quest global variables (dungeon set-global actions and the quests), at fixed numbers.
        DfQuestGlobals.AddAll(mod);
        var dungeons = new DungeonCellBuilder(mod, arena2, DfModel.LoadAll(arena2), new DfBsa(Path.Combine(arena2, "BLOCKS.BSA")), outDir, new Navmesh.NavmeshBuilder(mod), propReplacer) { Spells = spells, Clutter = clutter, Creatures = creatures, People = people, FixedPropKeys = true, GroundAt = (x, y) => LocationPlacer.GroundAt(field, x, y) };
        var hold = locations.First(l => l.Name == "Privateer's Hold" && l.DungeonBlocks.Count > 0);
        var holdCell = dungeons.Build(hold, world, LocationPlacer.WorldPosition(field, hold.PixelX + 0.5, hold.PixelY + 0.5));
        Console.WriteLine($"Dungeon: {hold.Name} -> {holdCell.Cell.EditorID}, {holdCell.Cell.Temporary.Count} refs, navmesh {holdCell.Navmesh.Triangles} triangles / {holdCell.Navmesh.Vertices} vertices, {holdCell.Navmesh.DoorTriangles} door links");
        Console.WriteLine($"  placed: {string.Join(", ", holdCell.Fixtures.OrderByDescending(f => f.Value).Select(f => $"{f.Key} {f.Value}"))}");
        // Hidden below the start dungeon: holds the player's items while Skyrim's opening strips them.
        var start = holdCell.StartMarker.Placement!.Position;
        var stash = new PlacedObject(mod)
        {
            EditorID = "TMStoryStashChest",
            Base = new FormKey(ModKey.FromFileName("Skyrim.esm"), 0xF8478).ToNullableLink<IPlaceableObjectGetter>(), // TreasChestSmallEMPTYNoRespawn
            Placement = new Placement { Position = new Noggog.P3Float(start.X, start.Y, start.Z - 4000), Rotation = new Noggog.P3Float(0, 0, 0) },
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
        };
        holdCell.Cell.Persistent.Add(stash);
        recallAnchor.Placement = new Placement { Position = new Noggog.P3Float(start.X, start.Y, start.Z - 4100), Rotation = new Noggog.P3Float(0, 0, 0) };
        holdCell.Cell.Persistent.Add(recallAnchor);
        // Towns: each its own worldspace, entered from its spot on the overland map.
        var townBuilder = new TownBuilder(mod, arena2, DfModel.LoadAll(arena2), new DfBsa(Path.Combine(arena2, "BLOCKS.BSA")), map, dungeons)
        {
            People = people,
            Clutter = clutter,
            Creatures = creatures,
            Shops = new TownShops(mod, skyrim) { SpellTomes = [.. spells.Tomes.Values], DaggerfallSmith = DfItems.Lists(mod) },
        };
        var townWorlds = new List<Worldspace>();
        var builtTowns = new List<TownBuilder.Built>();
        var questTowns = new List<QuestSites.Town>();
        foreach (var town in SelectTowns(locations, towns))
        {
            var t = townBuilder.Build(town, skyrim, world, LocationPlacer.WorldPosition(field, town.PixelX + 0.5, town.PixelY + 0.5));
            townWorlds.Add(t.World);
            builtTowns.Add(t);
            questTowns.Add(new QuestSites.Town(town, t));
            Console.WriteLine($"Town: {town.Name} ({town.RegionName}) -> {t.World.EditorID}: {t.Buildings} buildings, {t.Interiors} interiors, {t.Doors} street doors ({t.Unlinked} without a way in)");
            Console.WriteLine($"  placed: {string.Join(", ", t.Fixtures.OrderByDescending(f => f.Value).Take(25).Select(f => $"{f.Key} {f.Value}"))}");
            var named = townBuilder.Placed.Where(p => p.Faction?.Type == DfFactionType.Individual).Select(p => p.Name).Distinct().ToList();
            Console.WriteLine($"  people: {townBuilder.Placed.Count}; named: {string.Join(", ", named.Take(20))}; e.g. {string.Join(", ", townBuilder.Placed.Take(8).Select(p => $"{p.Name} ({p.Building})"))}");
        }
        TownGuilds? guilds = null;
        var everyone = townBuilder.Placed.Concat(dungeons.PlacedPeople).ToList();
        if (townWorlds.Count > 0)
        {
            Console.WriteLine($"Town navmesh edge links: {dungeons.Navmeshes.LinkExteriorEdges()}");
            // Conversations: factions, greetings by reaction, "Where is...".
            var talk = new TownTalk(mod, DfText.Load(arena2), townBuilder.People!.Factions);
            talk.Enlist(everyone);
            talk.Build(builtTowns, townBuilder.TalkQuest, townBuilder.Shops?.Shopkeepers, townBuilder.Shops?.TavernKeepers, townBuilder.Shops?.Bankers);
            Console.WriteLine($"Talk: {talk.Responses} responses for {townBuilder.Placed.Count} townsfolk and {dungeons.PlacedPeople.Count} people in town dungeons");
            townBuilder.AttachDaytime();
            // Guilds, temples and knightly orders: joining, ranks, training, healing.
            guilds = new TownGuilds(mod, skyrim, DfText.Load(arena2), townBuilder.People!.Factions);
            guilds.Build(talk, townBuilder.TalkQuest, everyone, townBuilder.BreakIns);
            Console.WriteLine($"Guilds: {guilds.Members} guilds with members in town, {guilds.Trainers} trainers, {guilds.Responses} responses");
        }
        // Daggerfall's soundtrack (rendered once, then cached) and Skyrim weather/ambience by Daggerfall climate.
        var songs = DfMusic.RenderAll(arena2, Program.MusicTools(skyrimData), Path.Combine(outDir, "Music", "TamrielMerge"), Path.Combine(outDir, "..", "music-work"), uncut);
        var music = new MusicBuilder(mod);
        music.Build(uncut);
        world.Music = music.Explore.ToNullableLink<IMusicTypeGetter>();
        // The Daggerfall world as one location (the worldspace and every interior), so things meant only for this
        // world — its battle music — can test for it.
        var iliacBay = new Location(mod) { EditorID = "DFLocIliacBay", Name = "Iliac Bay" };
        mod.Locations.Add(iliacBay);
        world.Location = iliacBay.ToNullableLink<ILocationGetter>();
        // Towns are locations inside the Iliac Bay (their streets and buildings keep the town's location).
        foreach (var tw in townWorlds) tw.Music = world.Music;
        foreach (var townLoc in mod.Locations.Where(l => l.EditorID?.StartsWith("DFLoc") == true && l != iliacBay))
            townLoc.ParentLocation = iliacBay.ToNullableLink<ILocationGetter>();
        foreach (var cell in mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
            if (cell.Location.IsNull) cell.Location = iliacBay.ToNullableLink<ILocationGetter>();
        music.BuildCombat(skyrim, iliacBay);
        // Daggerfall's holidays, on the town conversation quest (when there are towns).
        if (townWorlds.Count > 0)
        {
            var festival = MerchantsFestivalPerk(mod, iliacBay);
            townBuilder.TalkQuest.VirtualMachineAdapter ??= new QuestAdapter();
            townBuilder.TalkQuest.VirtualMachineAdapter.Scripts.Add(new ScriptEntry
            {
                Name = "TMDfHolidays",
                Properties =
                [
                    new ScriptObjectProperty { Name = "IliacBay", Object = iliacBay.ToLink<ISkyrimMajorRecordGetter>() },
                    new ScriptObjectProperty { Name = "FestivalPrices", Object = festival.ToLink<ISkyrimMajorRecordGetter>() },
                    new ScriptObjectProperty { Name = "GameMonth", Object = new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x36).ToLink<ISkyrimMajorRecordGetter>() },
                    new ScriptObjectProperty { Name = "GameDay", Object = new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x37).ToLink<ISkyrimMajorRecordGetter>() },
                ],
            });
        }
        // Daggerfall's quests (DfQuestBuilder: the machine, reputation, all 232 quests from ARENA2) and the quest sites of
        // what this plugin built (Privateer's Hold, its towns and their dungeons); the town and dungeon plugins add theirs.
        var questBuilder = new DfQuestBuilder(mod, arena2, itemMasters, DfFactions.Load(arena2), DfNames.Load(arena2), new VanillaActors(skyrimData))
        {
            Foe = id => creatures.Spawn(id),
            Spells = spells.Spells,
            GuildsQuest = guilds?.Quest,
            Guilds = guilds?.Built ?? [],
            IliacBay = iliacBay.FormKey,
            Weapons = [.. mod.Weapons.Where(w => w.EditorID is { } e && (e.StartsWith("DFWeapIron") || e.StartsWith("DFWeapSteel"))).Select(w => w.FormKey)],
            Armor = [.. mod.Armors.Where(a => a.EditorID is { } e && (e.StartsWith("DFArmorLeather") || e.StartsWith("DFArmorChain") || e.StartsWith("DFArmorIron") || e.StartsWith("DFArmorSteel"))).Select(a => a.FormKey)],
            Books = [.. mod.Books.Where(b => b.EditorID?.StartsWith("DF_Book") == true).Select(b => b.FormKey)],
        };
        questBuilder.Build();
        var markerOf = markers.GroupBy(m => (m.Location.Region, m.Location.Index)).ToDictionary(g => g.Key, g => g.First().Marker.FormKey);
        var questSites = QuestSites.Build(mod, "Main", questBuilder.Machine!.FormKey, questTowns, dungeons.BuiltDungeons, dungeons.QuestMarkers,
            QuestSites.Individuals(mod, everyone), l => markerOf.TryGetValue((l.Region, l.Index), out var mk) ? mk : null);
        Console.WriteLine($"Quest sites: {questTowns.Count} towns, {dungeons.BuiltDungeons.Count} dungeons, {dungeons.QuestMarkers.Values.Sum(m => m.Count)} quest markers in {questSites.Count} site quests");
        AtmosphereBuilder.ApplyToDungeon(holdCell.Cell, music.Dungeon);
        // Everything on the overland is placed: split it into its tiles.
        var tiles = layout.Split(mod, world);
        AddOverlandQuest(mod, layout, tiles, markers, iliacBay, field.Settings.PixelsPerCell, map);
        var (regionAreas, regionOf) = AtmosphereBuilder.AddRegions(mod, skyrim, tiles, layout, field, map);
        Console.WriteLine($"Daggerfall light: {AtmosphereBuilder.ApplyDaggerfallLight(mod, skyrim, world)} weathers copied and relit for the Iliac Bay");
        foreach (var tile in tiles) tile.Climate = world.Climate;
        Console.WriteLine($"Music: {songs.Count} Daggerfall songs ({songs.Values.Sum() / 60:F0} min), {mod.MusicTypes.Count} music types, {mod.MusicTracks.Count} tracks");
        Console.WriteLine($"Regions: {string.Join(", ", regionAreas.Select(r => $"{r.Key} {r.Value} areas"))}");

        // Masters in load order (Mutagen's default is alphabetical, which would list the DLCs before Skyrim.esm).
        var loadOrder = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }.Select(f => ModKey.FromFileName(f)).ToArray();
        mod.ModHeader.Description = DoorRefFix.Marker + "; " + StatueRefFix.Marker;   // doors and statues placed as the fixes make old ones
        TopicSubtypes.Fill(mod);
        mod.WriteToBinary(outPath, new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryWriteParameters
        {
            MastersListOrdering = new Mutagen.Bethesda.Plugins.Binary.Parameters.MastersListOrderingByLoadOrder(loadOrder),
        });
        Console.WriteLine($"Wrote {Path.GetFullPath(outPath)}");
        // The tiles' land and distant terrain, a tile at a time.
        var lands = layout.LandPlugins(outPath, skyrimData, field, map, regionOf, line => Console.WriteLine("Land: " + line));
        Console.WriteLine($"Land plugins: {string.Join(", ", lands)}");
        // The overland's navmeshes, joined into the land plugin.
        foreach (var land in lands)
            Terrain.OverlandNavmesh.Build(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath))!, land), line => Console.WriteLine("Navmesh: " + line));
        // What procedural creatures may use from Daggerfall: its ordinary monsters and dungeon spots. Daggerfall resets a
        // dungeon when you leave it, so spots (and new points) respawn whenever you come back (0 hours).
        var catalog = new CreatureCatalog { Game = "Daggerfall", Plugin = Path.GetFileName(outPath), ExteriorRespawnHours = 0, InteriorRespawnHours = 0 };
        foreach (var (actor, name, level) in creatures.Ordinary())
            catalog.Creatures.Add(new(CreatureCatalog.Key(actor), name, level, CreatureCatalog.HabitatsOf(name)));
        foreach (var (placed, cell, habitat) in dungeons.CreatureSpots)
        {
            var p = placed.Placement!.Position;
            catalog.Spots.Add(new(CreatureCatalog.Key(placed.FormKey), CreatureCatalog.Key(cell.FormKey), "", p.X, p.Y, p.Z, habitat, 1, 0));
        }
        catalog.Save(outPath);

        // The start plugin finds Privateer's Hold's start marker at runtime; this plugin's own stash chest stays
        // (unused) so its FormIDs, and saves, don't shift.
        AltStartBuilder.Build(skyrimData, outDir, holdCell.StartMarker.FormKey.ID);
        AltStartBuilder.CompileScripts(skyrimData, Path.Combine(AppContext.BaseDirectory, "Papyrus"), outDir);
        AltStartBuilder.CopyEraPlugin(outDir);
        return 0;
    }

    /// <summary>
    /// "DFOverlandQuest" (TMDfOverland): the tiles' hand-over, with an origin marker in each tile to move to and the
    /// layout, and Daggerfall's travel: every town's map marker by region, for the Tamriel menu's Travel tab.
    /// </summary>
    public static void AddOverlandQuest(SkyrimMod mod, Terrain.OverlandTiles layout, List<Worldspace> tiles, List<(DfLocation Location, PlacedObject Marker)> markers,
        Location iliacBay, int pixelsPerCell, DfWorldMap map)
    {
        static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);
        const float u = Terrain.OverlandTiles.CellUnits;
        var origins = new List<FormKey>();
        for (int t = 0; t < tiles.Count; t++)
        {
            var marker = new PlacedObject(mod)
            {
                EditorID = "DFTileOrigin" + t,
                Base = Sk(0x3B).ToNullableLink<IPlaceableObjectGetter>(), // XMarker
                Placement = new Placement { Position = new Noggog.P3Float(0, 0, 0), Rotation = new Noggog.P3Float(0, 0, 0) },
                SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
            };
            tiles[t].TopCell!.Persistent.Add(marker);
            origins.Add(marker.FormKey);
        }
        var (x0, y0, x1, y1) = layout.Core(0);
        var (ox, oy) = layout.Origin(0);
        if (layout.Single) (x0, y0, x1, y1) = (layout.MinCellX - 1000, layout.MinCellY - 1000, layout.MinCellX + layout.CellsX + 1000, layout.MinCellY + layout.CellsY + 1000);

        // Towns by region, as Daggerfall's travel map lists them (towns are known from the start).
        var regions = markers.Where(m => m.Location.Type is DfLocationType.TownCity or DfLocationType.TownHamlet or DfLocationType.TownVillage)
            .GroupBy(m => m.Location.RegionName).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        var lists = new List<FormKey>();
        foreach (var g in regions)
        {
            var list = new FormList(mod) { EditorID = "DFTravel" + LocationPlacer.Sanitize(g.Key) };
            foreach (var m in g.OrderBy(m => m.Location.Name, StringComparer.Ordinal)) list.Items.Add(m.Marker.ToLink<ISkyrimMajorRecordGetter>());
            mod.FormLists.Add(list);
            lists.Add(list.FormKey);
        }
        ScriptObjectListProperty Objects(string name, IEnumerable<FormKey> keys)
        {
            var p = new ScriptObjectListProperty { Name = name };
            foreach (var k in keys) p.Objects.Add(new ScriptObjectProperty { Object = k.ToLink<ISkyrimMajorRecordGetter>() });
            return p;
        }
        ScriptStringListProperty Strings(string name, IEnumerable<string> values) => new() { Name = name, Data = [.. values] };
        var origin = Enumerable.Range(0, tiles.Count).Select(layout.Origin).ToList();
        var quest = new Quest(mod)
        {
            EditorID = "DFOverlandQuest",
            Name = "Iliac Bay",
            Flags = Quest.Flag.StartGameEnabled,
            Aliases = [new QuestAlias { ID = 0, Name = "Player", ForcedReference = new FormLinkNullable<IPlacedGetter>(Sk(0x14)) }],
        };
        quest.VirtualMachineAdapter = new QuestAdapter
        {
            Scripts =
            {
                new ScriptEntry
                {
                    Name = "TMDfOverland",
                    Properties =
                    [
                        new ScriptObjectProperty { Name = "PlayerRef", Object = Sk(0x14).ToLink<ISkyrimMajorRecordGetter>() },
                        new ScriptObjectProperty { Name = "GameHour", Object = Sk(0x38).ToLink<ISkyrimMajorRecordGetter>() },
                        new ScriptObjectProperty { Name = "IliacBay", Object = iliacBay.ToLink<ISkyrimMajorRecordGetter>() },
                        Objects("Tiles", tiles.Select(t => t.FormKey)),
                        Objects("Origins", origins),
                        new ScriptFloatListProperty { Name = "OriginX", Data = [.. origin.Select(o => o.X * u)] },
                        new ScriptFloatListProperty { Name = "OriginY", Data = [.. origin.Select(o => o.Y * u)] },
                        new ScriptIntProperty { Name = "Columns", Data = layout.Columns },
                        new ScriptFloatProperty { Name = "CoreMinX", Data = (x0 - ox) * u },
                        new ScriptFloatProperty { Name = "CoreMaxX", Data = (x1 + 1 - ox) * u },
                        new ScriptFloatProperty { Name = "CoreMinY", Data = (y0 - oy) * u },
                        new ScriptFloatProperty { Name = "CoreMaxY", Data = (y1 + 1 - oy) * u },
                        new ScriptFloatProperty { Name = "Hysteresis", Data = Terrain.OverlandTiles.Hysteresis },
                        new ScriptFloatProperty { Name = "UnitsPerPixel", Data = u / pixelsPerCell },
                        Strings("RegionNames", regions.Select(g => g.Key)),
                        Strings("RegionTowns", regions.Select(g => string.Join("|", g.OrderBy(m => m.Location.Name, StringComparer.Ordinal).Select(m => m.Location.Name.Replace("|", "/"))))),
                        Objects("RegionMarkers", lists),
                    ],
                },
            },
            Aliases =
            [
                new QuestFragmentAlias
                {
                    Property = new ScriptObjectProperty { Object = quest.ToLink<ISkyrimMajorRecordGetter>(), Alias = 0 },
                    Scripts = [new ScriptEntry { Name = "TMDfOverlandPlayer" }],
                },
            ],
        };
        mod.Quests.Add(quest);
    }

    /// <summary>A Daggerfall map pixel's climate as Daggerfall Unity's travel prices it (TravelTimeCalculator): 'O' the sea,
    /// else its terrain movement modifier's index (climateIndices: desert 0, mountain 1, rainforest 2, swamp 3,
    /// subtropical 4, the woods 5).</summary>
    static char TravelClass(DfClimate c) => c switch
    {
        DfClimate.Ocean => 'O',
        DfClimate.Desert or DfClimate.Desert2 => '0',
        DfClimate.Mountain => '1',
        DfClimate.Rainforest => '2',
        DfClimate.Swamp => '3',
        DfClimate.Subtropical => '4',
        _ => '5',
    };

    /// <summary>Squares of this many map pixels a side share one climate in the travel data (Daggerfall's 1000x500 map
    /// as 250x125: one quest's script data holds at most 64 KB).</summary>
    public const int TravelClimateStep = 4;

    /// <summary>
    /// "DFTravelClimate": a quest (never run) whose TMDfTravelClimate script holds Daggerfall's map climates for travel
    /// (user, 2026-10-12: "i need the travel like daggerfalls, like it can take days to get to a town"): one string per
    /// row of <see cref="TravelClimateStep"/>-pixel squares, north to south, a <see cref="TravelClass"/> character per
    /// square (the class most of its pixels have; the sea when it's half or more).
    /// </summary>
    static FormKey AddTravelClimate(SkyrimMod mod, DfWorldMap map)
    {
        const int k = TravelClimateStep;
        var rows = new List<string>();
        for (int y = 0; y < DfWorldMap.Height; y += k)
        {
            var row = new System.Text.StringBuilder();
            for (int x = 0; x < DfWorldMap.Width; x += k)
            {
                var counts = new Dictionary<char, int>();
                for (int j = 0; j < k; j++)
                    for (int i = 0; i < k; i++)
                    {
                        char c = TravelClass(map.ClimateAt(x + i, y + j));
                        counts[c] = counts.GetValueOrDefault(c) + 1;
                    }
                row.Append(counts.GetValueOrDefault('O') * 2 >= k * k ? 'O' : counts.Where(p => p.Key != 'O').MaxBy(p => p.Value).Key);
            }
            rows.Add(row.ToString());
        }
        var quest = new Quest(mod) { EditorID = "DFTravelClimate", Name = "Iliac Bay travel" };
        quest.VirtualMachineAdapter = new QuestAdapter
        {
            Scripts =
            {
                new ScriptEntry
                {
                    Name = "TMDfTravelClimate",
                    Properties =
                    [
                        new ScriptStringListProperty { Name = "Rows", Data = [.. rows] },
                        new ScriptIntProperty { Name = "Step", Data = k },
                    ],
                },
            },
        };
        mod.Quests.Add(quest);
        return quest.FormKey;
    }

    /// <summary>Models made from Daggerfall's sprites (tools/model3d/work, by sprite2mesh.py) into the clutter placer,
    /// when they have been made (without them those sprites keep their nearest vanilla stand-ins), and Daggerfall's grass
    /// for its grass sprites.</summary>
    public static void ImportSpriteModels(SkyrimMod mod, string outDir, string arena2, ClutterPlacer clutter)
    {
        // Daggerfall's grass where its blocks put grass sprites (Terrain/DfGrass).
        new Terrain.DfGrass(mod, null, arena2, outDir).AddBlockGrass(clutter);
        string work = Path.Combine(Setup.Dependencies.Root, "model3d", "work");
        if (!Directory.Exists(Path.Combine(work, "models"))) return;
        var problems = new List<string>();
        var models = new Meshes.SpriteModelImporter(mod, outDir, arena2).ImportAll(work, problems);
        foreach (var (key, made) in models) clutter.AddGenerated(key, made);
        Console.WriteLine($"Sprite models: {models.Count} of {SpriteModels.Targets.Count}{(problems.Count > 0 ? "; problems: " + string.Join("; ", problems.Take(5)) : "")}");
    }

    /// <summary>
    /// The sprite models and grass that <paramref name="master"/> (Daggerfall.esm, built first) already has, into the
    /// clutter placer as its own statics: a town or dungeon part places them straight from it instead of importing all
    /// 604 models again and writing their files once more (2026-10-14: that was most of each of the 84 town parts' first
    /// minutes, the same files every time; user: "should be a 1 hour conversion"). Returns how many; 0 when the master
    /// has none (then the part imports them itself).
    /// </summary>
    public static int SpriteModelsFromMaster(ISkyrimModGetter master, ClutterPlacer clutter)
    {
        var byName = SpriteModels.Targets.GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        int n = 0;
        foreach (var st in master.Statics)
        {
            if (st.EditorID is not { } e || st.ObjectBounds is not { } ob) continue;
            var min = new System.Numerics.Vector3(ob.First.X, ob.First.Y, ob.First.Z);
            var max = new System.Numerics.Vector3(ob.Second.X, ob.Second.Y, ob.Second.Z);
            if (e.StartsWith("DFSpriteModel", StringComparison.Ordinal) && byName.TryGetValue(e["DFSpriteModel".Length..], out var targets))
                foreach (var t in targets) { clutter.AddGenerated((t.Archive, t.Record), new Meshes.SpriteModelImporter.Made(st.FormKey, min, max, t.Kind)); n++; }
            else if (e.StartsWith("DFGrassStatic", StringComparison.Ordinal) && e["DFGrassStatic".Length..].Split('_') is [var a, var r]
                     && int.TryParse(a, out int archive) && int.TryParse(r, out int record))
            { clutter.AddGenerated((archive, record), new Meshes.SpriteModelImporter.Made(st.FormKey, min, max, SpriteModels.Kind.Plant)); n++; }
        }
        return n;
    }

    /// <summary>Towns named in <paramref name="spec"/> ("Name" or "Name@Region", comma-separated; "all"; "none").</summary>
    static IEnumerable<DfLocation> SelectTowns(List<DfLocation> locations, string spec)
    {
        var towns = locations.Where(l => l.ExteriorBlocks.Length > 0 && l.Type is DfLocationType.TownCity or DfLocationType.TownHamlet or DfLocationType.TownVillage);
        if (spec.Equals("none", StringComparison.OrdinalIgnoreCase)) return [];
        if (spec.Equals("all", StringComparison.OrdinalIgnoreCase)) return towns;
        var wanted = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.Split('@')).ToList();
        return towns.Where(l => wanted.Any(w => l.Name.Equals(w[0], StringComparison.OrdinalIgnoreCase) && (w.Length < 2 || l.RegionName.Equals(w[1], StringComparison.OrdinalIgnoreCase))));
    }

    static int AddBooks(SkyrimMod mod, ISkyrimModGetter skyrim, string arena2)
    {
        // Ordinary readable vanilla books (no skill/spell, not notes or journals), one per distinct model,
        // used as appearance templates so the Daggerfall books look varied on shelves. Only models shared by
        // many vanilla books qualify: unique covers belong to quest items (Oghma Infinium, Daedric books).
        var templates = skyrim.Books
            .Where(b => b.Type == Book.BookType.BookOrTome
                && b.Teaches is null or IBookTeachesNothingGetter
                && b.Model?.File.DataRelativePath.Path.Contains(@"Books\", StringComparison.OrdinalIgnoreCase) == true
                && b.EditorID is { } e && !e.Contains("Note") && !e.Contains("Journal") && !e.StartsWith("DLC"))
            .GroupBy(b => b.Model!.File.DataRelativePath.Path, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= 8 && Path.GetFileName(g.Key).StartsWith("B", StringComparison.OrdinalIgnoreCase)) // Basic*/Book*, not Note*/Journal*
            .Select(g => g.First())
            .OrderBy(b => b.EditorID, StringComparer.Ordinal)
            .ToList();
        if (templates.Count == 0) throw new InvalidOperationException("No vanilla book templates found in Skyrim.esm");

        int count = 0;
        foreach (var df in DfBook.LoadAll(arena2))
        {
            var template = templates[df.Id % templates.Count];
            var book = mod.Books.DuplicateInAsNewRecord(template);
            book.EditorID = $"DF_Book{df.Id:D5}";
            book.Name = df.Title;
            book.BookText = RenderText(df);
            book.Description = null;
            // Daggerfall book prices (300-800 in DFU, raw header up to ~1000) are far above Skyrim's 5-75 range.
            book.Value = (uint)Math.Clamp(df.Price / 20, 10, 60);
            count++;
        }
        return count;
    }

    /// <summary>Skyrim books use a Scaleform HTML subset: &lt;p align&gt; blocks, paginated by the engine.</summary>
    static string RenderText(DfBook book)
    {
        // Mirror vanilla layout: a centered title page, then left-aligned body text.
        var paras = book.Paragraphs;
        int i = 0;
        var title = new List<string>();
        for (; i < paras.Count && paras[i].Align == DfAlign.Center && title.Count < 4; i++)
            title.Add(paras[i].Text);
        if (title.Count == 0) title.Add(book.Title);

        var sb = new StringBuilder();
        sb.Append("[pagebreak]\n<p align=\"center\">\n\n\n\n");
        foreach (var t in title) sb.Append(WebUtility.HtmlEncode(t)).Append('\n');
        sb.Append("</p>\n\n[pagebreak]\n");
        for (; i < paras.Count; i++)
        {
            // Long centered blocks (whole jokes, verses) read badly centered in Skyrim's book UI.
            string align = paras[i].Align == DfAlign.Center && paras[i].Text.Length <= 60 ? "center" : "left";
            sb.Append($"<p align=\"{align}\">{WebUtility.HtmlEncode(paras[i].Text)}</p>\n\n");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Merchants' Festival (10 Sun's Height): the shops of the Iliac Bay sell at half price. A hidden perk the player is
    /// given (TMDfHolidays) halves buying prices only (Mod Buy Prices), on that day and in the Iliac Bay: its conditions
    /// read Skyrim's date and the player's location, so nothing needs re-applying after a saved game is loaded and the
    /// prices the player sells at are untouched. Fixed number (0xDF0100): made late in Daggerfall.esm's running order it
    /// would renumber what follows.
    /// </summary>
    static Perk MerchantsFestivalPerk(SkyrimMod mod, Location iliacBay)
    {
        var perk = new Perk(new FormKey(mod.ModKey, 0xDF0100), SkyrimRelease.SkyrimSE)
        {
            EditorID = "DFMerchantsFestivalPerk",
            Name = "Merchants' Festival",
            Playable = false,
            Hidden = true,
            NumRanks = 1,
        };
        var month = new GetGlobalValueConditionData(); month.Global = new FormLinkOrIndex<IGlobalGetter>(month, new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x36));
        var day = new GetGlobalValueConditionData(); day.Global = new FormLinkOrIndex<IGlobalGetter>(day, new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x37));
        var where = new GetInCurrentLocConditionData(); where.Location = new FormLinkOrIndex<ILocationGetter>(where, iliacBay.FormKey);
        var owner = new PerkCondition { RunOnTabIndex = 0 };
        owner.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 6, Data = month });   // Sun's Height
        owner.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 10, Data = day });
        owner.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1, Data = where });
        var half = new PerkEntryPointModifyValue
        {
            EntryPoint = APerkEntryPointEffect.EntryType.ModBuyPrices,
            PerkConditionTabCount = 3,
            Modification = PerkEntryPointModifyValue.ModificationType.Multiply,
            Value = 0.5f,
        };
        half.Conditions.Add(owner);
        perk.Effects.Add(half);
        mod.Perks.Add(perk);
        return perk;
    }
}
