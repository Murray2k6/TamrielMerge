using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Skyrim;

/// <summary>
/// `pack-test --dir DIR`: <see cref="PluginPack"/> proved on small plugins made here, with what a real set has and two
/// small files can't show at once: a navmesh of the later plugin linked to the earlier's and back, a door triangle, an
/// override of the master's navmesh and reference, FormIDs that collide, an interior cell that must change block, a
/// navmesh map in both, a third plugin mastering both; then the refusals: a cycle and a contested record's winner.
/// </summary>
public static class PluginPackTest
{
    static readonly FormKey Rock = FormKey.Factory("0DEAD0:Skyrim.esm");
    static readonly FormKey SkyrimStatic = FormKey.Factory("0DEAD1:Skyrim.esm");
    static readonly FormKey NaviKey = FormKey.Factory("012FB4:Skyrim.esm");

    static void Save(SkyrimMod mod, string dir, params ModKey[] order)
    {
        var masters = new[] { ModKey.FromFileName("Skyrim.esm") }.Concat(order).ToArray();
        foreach (var m in masters) if (!mod.ModHeader.MasterReferences.Any(r => r.Master == m)) mod.ModHeader.MasterReferences.Add(new MasterReference { Master = m });
        mod.WriteToBinary(Path.Combine(dir, mod.ModKey.FileName), new BinaryWriteParameters
        {
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = new MastersListOrderingByLoadOrder(masters),
            ModKey = ModKeyOption.NoCheck,
        });
    }

    static NavigationMesh Navmesh(FormKey key, Worldspace world)
    {
        var data = new NavigationMeshData
        {
            NavmeshVersion = 12, Parent = new WorldspaceNavmeshParent { Parent = world.ToLink<IWorldspaceGetter>(), Coordinates = new P2Int16(0, 0) },
            NavmeshGridDivisor = 1, MaxDistanceX = 1, MaxDistanceY = 1, Min = new P3Float(0, 0, 0), Max = new P3Float(128, 128, 0), NavmeshGrid = new byte[4],
        };
        data.Vertices.AddRange([new P3Float(0, 0, 0), new P3Float(128, 0, 0), new P3Float(0, 128, 0)]);
        data.Triangles.Add(new NavmeshTriangle { Vertices = new P3Int16(0, 1, 2), EdgeLink_0_1 = -1, EdgeLink_1_2 = -1, EdgeLink_2_0 = -1 });
        return new NavigationMesh(key, SkyrimRelease.SkyrimSE) { Data = data };
    }

    static void Place(Worldspace world, Cell cell)
    {
        var sub = new WorldspaceSubBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellSubBlock };
        sub.Items.Add(cell);
        var block = new WorldspaceBlock { BlockNumberX = 0, BlockNumberY = 0, GroupType = GroupTypeEnum.ExteriorCellBlock };
        block.Items.Add(sub);
        world.SubCells.Add(block);
    }

    static void Interior(SkyrimMod mod, Cell cell)
    {
        var sub = new CellSubBlock { BlockNumber = (int)(cell.FormKey.ID / 10 % 10), GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell);
        var block = new CellBlock { BlockNumber = (int)(cell.FormKey.ID % 10), GroupType = GroupTypeEnum.InteriorCellBlock };
        block.SubBlocks.Add(sub);
        mod.Cells.Records.Add(block);
    }

    static NavigationMapInfo Info(NavigationMesh n, Worldspace w) => new()
    {
        NavigationMesh = n.ToLink<INavigationMeshGetter>(), Parent = new NavigationMapInfoWorldParent { ParentWorldspace = w.ToLink<IWorldspaceGetter>(), ParentWorldspaceCoord = new P2Int16(0, 0) },
    };

    static NavigationMeshInfoMap Map(params NavigationMapInfo[] infos)
    {
        var map = new NavigationMeshInfoMap(NaviKey, SkyrimRelease.SkyrimSE) { NavMeshVersion = 12, PreferredPathing = new PreferredPathing() };
        map.MapInfos.AddRange(infos);
        return map;
    }

    static PlacedObject Ref(SkyrimMod mod, float x)
    {
        var r = new PlacedObject(mod) { Base = new FormLinkNullable<IPlaceableObjectGetter>(Rock), Placement = new Placement { Position = new P3Float(x, 0, 0) } };
        return r;
    }

    static Cell WorldCell(FormKey key)
    {
        var c = new Cell(key, SkyrimRelease.SkyrimSE) { Flags = 0, Grid = new CellGrid { Point = new P2Int(0, 0) }, MajorRecordFlagsRaw = 0 };
        return c;
    }

    /// <summary>Runs the proofs in <paramref name="root"/>; returns the failures (none: 0).</summary>
    public static int Run(string root, Action<string> log)
    {
        int failed = 0;
        void Check(bool ok, string what) { log($"{(ok ? "ok  " : "FAIL")} {what}"); if (!ok) failed++; }
        Directory.CreateDirectory(root);
        foreach (var name in new[] { "fold", "cycle", "winner" })
        {
            var d = Path.Combine(root, name);
            if (Directory.Exists(d)) Directory.Delete(d, true);
            Directory.CreateDirectory(d);
        }

        // The fold.
        string dir = Path.Combine(root, "fold");
        var keyA = ModKey.FromFileName("Daggerfall Navmesh.esm");
        var keyB = ModKey.FromFileName("Daggerfall Navmesh 1-2.esm");
        var keyC = ModKey.FromFileName("Daggerfall Overland Doors.esp");
        var a = new SkyrimMod(keyA, SkyrimRelease.SkyrimSE);
        var world = new Worldspace(a) { EditorID = "PackWorld" };
        var cell = new Cell(a) { Grid = new CellGrid { Point = new P2Int(0, 0) } };
        var n1 = Navmesh(a.GetNextFormKey(), world);
        var r1 = Ref(a, 100);
        cell.NavigationMeshes.Add(n1);
        cell.Temporary.Add(r1);
        Place(world, cell);
        a.Worldspaces.Add(world);
        var s1 = new Static(a) { EditorID = "PackA", Model = new Model { File = "a.nif" } };
        a.Statics.Add(s1);
        var i1 = new Cell(a) { EditorID = "PackInteriorA", Flags = Cell.Flag.IsInteriorCell };
        Interior(a, i1);
        a.NavigationMeshInfoMaps.Add(Map(Info(n1, world)));
        Save(a, dir);

        // The later plugin numbers from the same 0x800 up: all its FormIDs collide.
        var b = new SkyrimMod(keyB, SkyrimRelease.SkyrimSE);
        var worldB = world.DeepCopy();
        worldB.SubCells.Clear();
        var cellB = cell.DeepCopy();
        cellB.Temporary.Clear();
        cellB.NavigationMeshes.Clear();
        var n2 = Navmesh(b.GetNextFormKey(), worldB);
        n2.Data!.EdgeLinks.Add(new EdgeLink { Mesh = n1.ToLink<INavigationMeshGetter>(), TriangleIndex = 0 });
        n2.Data.DoorTriangles.Add(new DoorTriangle { TriangleBeforeDoor = 0, Unknown = 0, Door = r1.ToLink<IPlacedObjectGetter>() });
        var n1Over = n1.DeepCopy();
        n1Over.Data!.EdgeLinks.Add(new EdgeLink { Mesh = n2.ToLink<INavigationMeshGetter>(), TriangleIndex = 0 });
        var r1Over = r1.DeepCopy();
        r1Over.Placement!.Position = new P3Float(555, 0, 0);
        var r2 = Ref(b, 200);
        cellB.NavigationMeshes.Add(n1Over);
        cellB.NavigationMeshes.Add(n2);
        cellB.Temporary.Add(r1Over);
        cellB.Temporary.Add(r2);
        Place(worldB, cellB);
        b.Worldspaces.Add(worldB);
        var s2 = new Static(b) { EditorID = "PackB", Model = new Model { File = "b.nif" } };
        b.Statics.Add(s2);
        var i2 = new Cell(b) { EditorID = "PackInteriorB", Flags = Cell.Flag.IsInteriorCell };
        Interior(b, i2);
        b.NavigationMeshInfoMaps.Add(Map(Info(n2, worldB)));
        Save(b, dir, keyA);

        // A plugin mastering both, as Daggerfall Overland Doors.esp does: a list of both's records, an override of the
        // folded plugin's navmesh.
        var c = new SkyrimMod(keyC, SkyrimRelease.SkyrimSE);
        var worldC = worldB.DeepCopy();
        worldC.SubCells.Clear();
        var cellC = cellB.DeepCopy();
        cellC.Temporary.Clear();
        cellC.NavigationMeshes.Clear();
        var n2Over = n2.DeepCopy();
        cellC.NavigationMeshes.Add(n2Over);
        Place(worldC, cellC);
        c.Worldspaces.Add(worldC);
        var list = new FormList(c) { EditorID = "PackList" };
        list.Items.Add(s1);
        list.Items.Add(s2);
        c.FormLists.Add(list);
        Save(c, dir, keyA, keyB);
        LightPlugins.Flag(Path.Combine(dir, keyC.FileName), 0x200);

        var outcome = PluginPack.Run(dir, PluginPack.DefaultBudget, [keyA.FileName, keyB.FileName, keyC.FileName], log);
        Check(outcome.Folded.SequenceEqual([keyB.FileName.String]), "the dependent folded into its master");
        Check(!File.Exists(Path.Combine(dir, keyB.FileName)), "the folded plugin is gone");
        Check(PluginPack.OrderIn(dir) is { } names && names.SequenceEqual([keyA.FileName.String, keyC.FileName.String]), "the manifest names the two left, in order");
        Check(PluginPack.Packed(dir), "the manifest says packed");
        using (var mod = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(dir, keyA.FileName), SkyrimRelease.SkyrimSE))
        using (var doors = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(dir, keyC.FileName), SkyrimRelease.SkyrimSE))
        {
            var statics = mod.Statics.ToDictionary(x => x.EditorID!, x => x.FormKey);
            Check(statics["PackA"] == s1.FormKey, "the target's record keeps its FormID");
            Check(statics.TryGetValue("PackB", out var movedS2) && movedS2.ModKey == keyA && movedS2.ID != s1.FormKey.ID, "the folded record took a free FormID in the target");
            var navmeshes = mod.EnumerateMajorRecords<INavigationMeshGetter>().ToList();
            Check(navmeshes.Count == 2, "two navmeshes, the override applied over its original");
            var overN1 = navmeshes.Single(n => n.FormKey == n1.FormKey);
            var movedN2 = navmeshes.Single(n => n.FormKey != n1.FormKey);
            Check(overN1.Data!.EdgeLinks.Count == 1 && overN1.Data.EdgeLinks[0].Mesh.FormKey == movedN2.FormKey, "the master's navmesh links to the folded one's new FormID");
            Check(movedN2.Data!.EdgeLinks.Count == 1 && movedN2.Data.EdgeLinks[0].Mesh.FormKey == n1.FormKey, "the folded navmesh links back");
            Check(movedN2.Data.DoorTriangles.Count == 1 && movedN2.Data.DoorTriangles[0].Door.FormKey == r1.FormKey, "the door triangle's reference is right");
            Check(movedN2.Data.Parent is IWorldspaceNavmeshParentGetter p && p.Parent.FormKey == world.FormKey, "the navmesh's parent worldspace is right");
            var refs = mod.EnumerateMajorRecords<IPlacedObjectGetter>().ToList();
            Check(refs.Count == 2 && refs.Single(r => r.FormKey == r1.FormKey).Placement!.Position.X == 555, "the override's reference won, the new one is there");
            Check(mod.Worldspaces.Single().SubCells.SelectMany(x => x.Items).SelectMany(x => x.Items).Count() == 1, "one cell, its children joined");
            var infos = mod.NavigationMeshInfoMaps.Single().MapInfos;
            Check(infos.Count == 2 && infos.Any(i => i.NavigationMesh.FormKey == n1.FormKey) && infos.Any(i => i.NavigationMesh.FormKey == movedN2.FormKey), "the navmesh map joined, both entries, the new FormID");
            var interior = mod.Cells.SelectMany(x => x.SubBlocks.Select(s => (Block: x.BlockNumber, Sub: s))).SelectMany(x => x.Sub.Cells.Select(k => (x.Block, Sub: x.Sub.BlockNumber, Cell: k))).Single(x => x.Cell.EditorID == "PackInteriorB");
            Check(interior.Block == interior.Cell.FormKey.ID % 10 && interior.Sub == interior.Cell.FormKey.ID / 10 % 10, $"the interior with a new FormID is in its own block ({interior.Block},{interior.Sub})");
            Check(doors.ModHeader.MasterReferences.All(m => m.Master != keyB) && doors.ModHeader.MasterReferences.Any(m => m.Master == keyA), "the third plugin's masters rewritten");
            var items = doors.FormLists.Single().Items.Select(i => i.FormKey).ToList();
            Check(items.Contains(s1.FormKey) && items.Contains(movedS2), "the third plugin's links re-pointed");
            Check(doors.EnumerateMajorRecords<INavigationMeshGetter>().Single().FormKey == movedN2.FormKey, "the third plugin's override of the folded navmesh re-pointed");
            Check(((uint)doors.ModHeader.Flags & 0x200) != 0, "the third plugin stays light");
        }
        Check(!Directory.Exists(Path.Combine(dir, "_pack")), "the work folder is gone");
        Check(File.Exists(Path.Combine(dir, PluginPack.ReportName)), "pack-report.txt written");

        // A cycle: Land 2 masters Creatures, which masters Land: Land 2 can't join Land.
        dir = Path.Combine(root, "cycle");
        var land = new SkyrimMod(ModKey.FromFileName("Daggerfall Land.esm"), SkyrimRelease.SkyrimSE);
        land.Statics.Add(new Static(land) { EditorID = "PackLand", Model = new Model { File = "l.nif" } });
        Save(land, dir);
        var creatures = new SkyrimMod(ModKey.FromFileName("Daggerfall Creatures.esm"), SkyrimRelease.SkyrimSE);
        var listC = new FormList(creatures) { EditorID = "PackCycle" };
        listC.Items.Add(land.Statics.First());
        creatures.FormLists.Add(listC);
        Save(creatures, dir, land.ModKey);
        var land2 = new SkyrimMod(ModKey.FromFileName("Daggerfall Land 2.esm"), SkyrimRelease.SkyrimSE);
        var list2 = new FormList(land2) { EditorID = "PackCycle2" };
        list2.Items.Add(listC);
        land2.FormLists.Add(list2);
        Save(land2, dir, land.ModKey, creatures.ModKey);
        Check(PluginPack.Run(dir, PluginPack.DefaultBudget, [land.ModKey.FileName, creatures.ModKey.FileName, land2.ModKey.FileName], log, planOnly: true).Folded.Count == 0, "a cycle isn't joined");

        // A winner: Land and Land 2 both hold a Skyrim record, Creatures (mastering Land) between them holds it too and
        // loses to Land 2: joined before Creatures, Creatures would win.
        dir = Path.Combine(root, "winner");
        var over = new Static(SkyrimStatic, SkyrimRelease.SkyrimSE) { EditorID = "PackOver", Model = new Model { File = "o.nif" } };
        var w1 = new SkyrimMod(ModKey.FromFileName("Daggerfall Land.esm"), SkyrimRelease.SkyrimSE);
        w1.Statics.Add(over.DeepCopy());
        w1.Statics.Add(new Static(w1) { EditorID = "PackW1", Model = new Model { File = "w.nif" } });
        Save(w1, dir);
        var w2 = new SkyrimMod(ModKey.FromFileName("Daggerfall Creatures.esm"), SkyrimRelease.SkyrimSE);
        w2.Statics.Add(over.DeepCopy());
        var listW = new FormList(w2) { EditorID = "PackW2" };
        listW.Items.Add(w1.Statics.Single(s => s.EditorID == "PackW1"));
        w2.FormLists.Add(listW);
        Save(w2, dir, w1.ModKey);
        var w3 = new SkyrimMod(ModKey.FromFileName("Daggerfall Land 2.esm"), SkyrimRelease.SkyrimSE);
        w3.Statics.Add(over.DeepCopy());
        Save(w3, dir);
        Check(PluginPack.Run(dir, PluginPack.DefaultBudget, [w1.ModKey.FileName, w2.ModKey.FileName, w3.ModKey.FileName], log, planOnly: true).Folded.Count == 0, "a join that would change a winner isn't made");
        log(failed == 0 ? "All proofs passed" : $"{failed} proofs failed");
        return failed;
    }
}
