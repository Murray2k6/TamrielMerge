using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Procedural creatures (the converter's option): one plugin, built after the games, from every converted game's
/// <see cref="CreatureCatalog"/>.
/// <list type="bullet">
/// <item>A leveled list per habitat (undead, daedra, cave, beast, water, cold, ash, construct) mixing the ordinary
/// creatures of all the converted games, placed through an actor templated on it.</item>
/// <item>Each game's ordinary creature placements are switched off (initially disabled) and a spawner (TMCreatureSpawner
/// on a hidden marker) stands in their place, drawing from the habitat's list.</item>
/// <item>New spawners: a few more in each dungeon that has creatures, at navmesh spots away from the others, and in some
/// wilderness cells with navmesh and nobody living nearby; their habitat is the nearest spot's.</item>
/// <item>Respawn as the creature's game does it (catalog hours), times the chosen scale.</item>
/// </list>
/// Bosses, uniques and quest creatures aren't in the catalogs, so they stay where their game put them.
/// </summary>
public static class CreatureSpawns
{
    public const string PluginName = "TamrielMerge - Creatures.esp";
    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);
    static readonly FormKey XMarkerHeading = Sk(0x34);
    static readonly string[] GameOrder = ["Daggerfall.esm", "Redguard.esm", "Morrowind.esm", "Oblivion.esm"];
    const uint InitiallyDisabled = 0x800;

    public sealed record Report(int Games, int Creatures, int Replaced, int NewPoints, IReadOnlyDictionary<string, int> PerHabitat, IReadOnlyList<string> Plugins);

    public static Report Build(string dir, string skyrimData, float respawnScale = 1, int seed = 1)
    {
        // The games in load order, Daggerfall's dungeons right after Daggerfall.esm.
        var order = GameOrder.SelectMany(p => p == "Daggerfall.esm"
            ? new[] { p, DaggerfallDungeons.PluginName }.Where(b => b == p || File.Exists(System.IO.Path.Combine(dir, b)))
            : [p]);
        var games = order.Select(p => (Path: System.IO.Path.Combine(dir, p), Catalog: CreatureCatalog.Load(System.IO.Path.Combine(dir, p))))
            .Where(g => g.Catalog is not null && File.Exists(g.Path)).Select(g => (g.Path, Catalog: g.Catalog!)).ToList();
        if (games.Count == 0) throw new InvalidOperationException($"no converted games with creature lists in {dir}");
        var mod = new SkyrimMod(ModKey.FromFileName(PluginName), SkyrimRelease.SkyrimSE);
        using var skyrim = SkyrimMod.CreateFromBinaryOverlay(System.IO.Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
        var listTemplate = skyrim.Npcs.First(n => n.EditorID == "LvlBanditMelee1H");

        // Habitat lists, mixed from every converted game.
        var all = games.SelectMany(g => g.Catalog.Creatures).ToList();
        var habitats = all.SelectMany(c => c.Habitats).Concat(games.SelectMany(g => g.Catalog.Spots.Select(s => s.Habitat))).Distinct().ToList();
        var actorFor = new Dictionary<string, Npc>();
        foreach (var h in habitats)
        {
            var members = all.Where(c => c.Habitats.Contains(h)).ToList();
            if (members.Count == 0) members = all.Where(c => c.Habitats.Contains("Beast")).ToList();
            if (members.Count == 0) members = all;
            if (members.Count == 0) continue;
            int lowest = members.Min(c => c.Level);
            var list = new LeveledNpc(mod) { EditorID = $"TMProc{h}List", Flags = LeveledNpc.Flag.CalculateFromAllLevelsLessThanOrEqualPlayer, Entries = [] };
            foreach (var c in members)
                // The weakest come from level 1, so a list always has something to offer.
                list.Entries.Add(new LeveledNpcEntry { Data = new LeveledNpcEntryData { Level = (short)Math.Clamp(c.Level == lowest ? 1 : c.Level, 1, 81), Count = 1, Reference = new FormLink<INpcSpawnGetter>(CreatureCatalog.Key(c.FormKey)) } });
            mod.LeveledNpcs.Add(list);
            var actor = listTemplate.Duplicate(mod.GetNextFormKey());
            actor.EditorID = $"TMProc{h}";
            actor.Template.SetTo(list);
            mod.Npcs.Add(actor);
            actorFor[h] = actor;
        }

        var rng = new Random(seed);
        int replaced = 0, added = 0;
        var perHabitat = new Dictionary<string, int>();
        float Scaled(float hours) => hours <= 0 ? hours : hours * respawnScale;
        PlacedObject Spawner(P3Float at, string habitat, float hours)
        {
            perHabitat[habitat] = perHabitat.GetValueOrDefault(habitat) + 1;
            var actor = actorFor.GetValueOrDefault(habitat) ?? actorFor.Values.First();
            return new PlacedObject(mod)
            {
                Base = new FormLinkNullable<IPlaceableObjectGetter>(XMarkerHeading),
                Placement = new Placement { Position = at, Rotation = new P3Float(0, 0, (float)(rng.NextDouble() * Math.PI * 2)) },
                VirtualMachineAdapter = new VirtualMachineAdapter
                {
                    Scripts =
                    [
                        new ScriptEntry
                        {
                            Name = "TMCreatureSpawner",
                            Properties =
                            [
                                new ScriptObjectProperty { Name = "Creature", Object = actor.ToLink<ISkyrimMajorRecordGetter>() },
                                new ScriptFloatProperty { Name = "RespawnHours", Data = Scaled(hours) },
                            ],
                        },
                    ],
                },
            };
        }

        foreach (var (path, catalog) in games)
        {
            using var plugin = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            var spots = catalog.Spots.GroupBy(s => s.Ref).ToDictionary(g => g.Key, g => g.First());
            var creatureBases = catalog.Creatures.Select(c => c.FormKey).ToHashSet();

            // Spots replaced, plus new points, in one cell: the cell overridden without its contents.
            void Fill(ICellGetter cell, Cell over, bool interior)
            {
                var here = cell.Temporary.OfType<IPlacedNpcGetter>().Where(r => spots.ContainsKey(CreatureCatalog.Key(r.FormKey))).ToList();
                foreach (var r in here)
                {
                    var spot = spots[CreatureCatalog.Key(r.FormKey)];
                    var off = r.DeepCopy();
                    off.SkyrimMajorRecordFlags |= (SkyrimMajorRecord.SkyrimMajorRecordFlag)InitiallyDisabled;
                    over.Temporary.Add(off);
                    over.Temporary.Add(Spawner(new P3Float(spot.X, spot.Y, spot.Z), spot.Habitat, spot.RespawnHours));
                    replaced++;
                }
                // New points on the navmesh, away from the spots and each other.
                int wanted = interior ? Math.Clamp(here.Count / 2 + 1, 1, 3) : 1;
                var taken = here.Select(r => spots[CreatureCatalog.Key(r.FormKey)]).Select(s => new Vector3(s.X, s.Y, s.Z)).ToList();
                foreach (var point in NavmeshPoints(cell, rng).Where(p => taken.All(t => Vector3.Distance(t, p) > 600)).Take(wanted))
                {
                    string habitat = Nearest(catalog, cell, point, interior) ?? (interior ? "Cave" : "Beast");
                    over.Temporary.Add(Spawner(new P3Float(point.X, point.Y, point.Z), habitat, interior ? catalog.InteriorRespawnHours : catalog.ExteriorRespawnHours));
                    taken.Add(point);
                    added++;
                }
            }

            // Dungeons: interiors where the game has ordinary creatures.
            foreach (var cell in plugin.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(s => s.Cells))
            {
                if (!cell.Temporary.OfType<IPlacedNpcGetter>().Any(r => spots.ContainsKey(CreatureCatalog.Key(r.FormKey)))) continue;
                var over = CellCopy(cell);
                AddInterior(mod, over);
                Fill(cell, over, interior: true);
            }

            // The wilderness: cells with spots, and some with navmesh where nobody lives within a cell.
            foreach (var ws in plugin.Worldspaces)
            {
                var cells = ws.SubCells.SelectMany(b => b.Items).SelectMany(s => s.Items).Where(c => c.Grid is not null).ToList();
                var lived = new HashSet<(int, int)>();
                foreach (var c in cells)
                    if (c.Temporary.Concat(c.Persistent).OfType<IPlacedNpcGetter>().Any(r => !spots.ContainsKey(CreatureCatalog.Key(r.FormKey)) && !creatureBases.Contains(CreatureCatalog.Key(r.Base.FormKey))))
                        for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) lived.Add((c.Grid!.Point.X + dx, c.Grid.Point.Y + dy));
                Worldspace? wsOver = null;
                foreach (var cell in cells)
                {
                    bool hasSpots = cell.Temporary.OfType<IPlacedNpcGetter>().Any(r => spots.ContainsKey(CreatureCatalog.Key(r.FormKey)));
                    bool wild = !lived.Contains((cell.Grid!.Point.X, cell.Grid.Point.Y)) && cell.NavigationMeshes.Count > 0 && rng.NextDouble() < 0.3;
                    if (!hasSpots && !wild) continue;
                    wsOver ??= WorldCopy(ws, mod);
                    var over = CellCopy(cell);
                    AddExterior(wsOver, over);
                    Fill(cell, over, interior: false);
                }
            }
        }

        var loadOrder = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }.Concat(games.Select(g => System.IO.Path.GetFileName(g.Path)))
            .Select(f => ModKey.FromFileName(f)).ToArray();
        TopicSubtypes.Fill(mod);
        mod.WriteToBinary(System.IO.Path.Combine(dir, PluginName), new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryWriteParameters
        {
            MastersListOrdering = new Mutagen.Bethesda.Plugins.Binary.Parameters.MastersListOrderingByLoadOrder(loadOrder),
        });
        return new Report(games.Count, all.Count, replaced, added, perHabitat, [.. games.Select(g => $"{g.Catalog.Game}: {g.Catalog.Creatures.Count} creatures, {g.Catalog.Spots.Count} spots")]);
    }

    /// <summary>The habitat of the catalog spot nearest a point in the same cell (interiors) or within three cells
    /// (outside), or null.</summary>
    static string? Nearest(CreatureCatalog catalog, ICellGetter cell, Vector3 p, bool interior)
    {
        string cellKey = CreatureCatalog.Key(cell.FormKey);
        var near = catalog.Spots.Where(s => interior ? s.Cell == cellKey : s.Cell.Length == 0)
            .Select(s => (s.Habitat, D: Vector3.Distance(new Vector3(s.X, s.Y, s.Z), p)))
            .Where(s => interior || s.D < 3 * 4096).OrderBy(s => s.D).FirstOrDefault();
        return near.Habitat;
    }

    /// <summary>Random navmesh triangle centres in a cell.</summary>
    static IEnumerable<Vector3> NavmeshPoints(ICellGetter cell, Random rng)
    {
        var points = new List<Vector3>();
        foreach (var nav in cell.NavigationMeshes)
        {
            if (nav.Data is not { } d) continue;
            var v = d.Vertices;
            foreach (var t in d.Triangles)
            {
                int a = t.Vertices.X, b = t.Vertices.Y, c = t.Vertices.Z;
                if (a >= v.Count || b >= v.Count || c >= v.Count) continue;
                points.Add(new Vector3((v[a].X + v[b].X + v[c].X) / 3, (v[a].Y + v[b].Y + v[c].Y) / 3, (v[a].Z + v[b].Z + v[c].Z) / 3 + 8));
            }
        }
        return points.OrderBy(_ => rng.Next());
    }

    /// <summary>A cell override without its contents (references, land, navmeshes stay the game plugin's).</summary>
    static Cell CellCopy(ICellGetter cell)
    {
        var copy = cell.DeepCopy(new Cell.TranslationMask(defaultOn: true) { Temporary = false, Persistent = false, Landscape = false, NavigationMeshes = false });
        copy.Temporary.Clear();
        copy.Persistent.Clear();
        return copy;
    }

    static Worldspace WorldCopy(IWorldspaceGetter ws, SkyrimMod mod)
    {
        var copy = ws.DeepCopy(new Worldspace.TranslationMask(defaultOn: true) { SubCells = false, TopCell = false });
        copy.SubCells.Clear();
        mod.Worldspaces.Add(copy);
        return copy;
    }

    static void AddInterior(SkyrimMod mod, Cell cell)
    {
        int blockNo = (int)(cell.FormKey.ID % 10), subNo = (int)(cell.FormKey.ID / 10 % 10);
        var block = mod.Cells.Records.FirstOrDefault(b => b.BlockNumber == blockNo);
        if (block is null) { block = new CellBlock { BlockNumber = blockNo, GroupType = GroupTypeEnum.InteriorCellBlock, SubBlocks = [] }; mod.Cells.Records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subNo);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = subNo, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = [] }; block.SubBlocks.Add(sub); }
        sub.Cells.Add(cell);
    }

    static void AddExterior(Worldspace ws, Cell cell)
    {
        int x = cell.Grid!.Point.X, y = cell.Grid.Point.Y;
        short bx = (short)(x >> 5), by = (short)(y >> 5), sx = (short)(x >> 3), sy = (short)(y >> 3);
        var block = ws.SubCells.FirstOrDefault(b => b.BlockNumberX == bx && b.BlockNumberY == by);
        if (block is null) { block = new WorldspaceBlock { BlockNumberX = bx, BlockNumberY = by, GroupType = GroupTypeEnum.ExteriorCellBlock }; ws.SubCells.Add(block); }
        var sub = block.Items.FirstOrDefault(s => s.BlockNumberX == sx && s.BlockNumberY == sy);
        if (sub is null) { sub = new WorldspaceSubBlock { BlockNumberX = sx, BlockNumberY = sy, GroupType = GroupTypeEnum.ExteriorCellSubBlock }; block.Items.Add(sub); }
        sub.Items.Add(cell);
    }
}
