using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Skyrim.Navmesh;

namespace TamrielMerge.Skyrim.Terrain;

/// <summary>
/// The overland's load doors on its navmeshes (user, 2026-10-10: "do the gaps"): the town gates and dungeon entrances
/// that the Towns and Dungeons plugins stand on the overland tiles, each joined to the navmesh of the land cell it
/// stands in, so NPCs and followers can path to them and through them. The land plugin's navmeshes are built before
/// those plugins exist, so this is a light plugin of its own ("Daggerfall Overland Doors.esp", after them all, overrides
/// only): each touched navmesh with the triangle in front of each door tagged (DoorTriangles, the Door flag), and the
/// navmesh map's entries for those navmeshes with the doors listed (the game joins every plugin's map entries).
/// Triangles and edge links are left as they are, so the cells' joins with their neighbours stay valid.
/// </summary>
public static class OverlandDoorLinks
{
    public const string PluginName = "Daggerfall Overland Doors.esp";
    const int DoorTag = unchecked((int)0xE48B73F3);

    public static (int Doors, int Linked, int Navmeshes) Build(string landPlugin, IReadOnlyList<string> doorPlugins, string outPath, Action<string>? log = null)
    {
        using var land = SkyrimMod.CreateFromBinaryOverlay(landPlugin, SkyrimRelease.SkyrimSE);
        var others = doorPlugins.Select(p => SkyrimMod.CreateFromBinaryOverlay(p, SkyrimRelease.SkyrimSE)).ToList();
        try
        {
            // The overland's load doors, by tile worldspace.
            var tiles = land.Worldspaces.ToDictionary(w => w.FormKey);
            var doors = new List<(FormKey World, IPlacedObjectGetter Door)>();
            foreach (var mod in others)
                foreach (var w in mod.Worldspaces.Where(w => tiles.ContainsKey(w.FormKey)))
                {
                    var cells = w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items).ToList();
                    if (w.TopCell is { } top) cells.Add(top);
                    foreach (var c in cells)
                        foreach (var d in c.Persistent.Concat(c.Temporary).OfType<IPlacedObjectGetter>().Where(o => o.TeleportDestination is not null && o.Placement is not null))
                            doors.Add((w.FormKey, d));
                }

            var map = land.NavigationMeshInfoMaps.FirstOrDefault();
            var infos = map?.MapInfos.ToDictionary(i => i.NavigationMesh.FormKey) ?? [];
            var outMod = new SkyrimMod(ModKey.FromFileName(Path.GetFileName(outPath)), SkyrimRelease.SkyrimSE);
            outMod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
            var worldCopies = new Dictionary<FormKey, Worldspace>();
            var navCopies = new Dictionary<FormKey, (NavigationMesh Navm, NavigationMapInfo? Info)>();
            int linked = 0, noNavmesh = 0, noTriangle = 0;
            static (int X, int Y) GridOf(P3Float p) => ((int)Math.Floor(p.X / 4096), (int)Math.Floor(p.Y / 4096));
            // The doors' cells, found in one pass over each world's cells (2026-10-15: each door searched the whole
            // 1000 x 500 overland for its cell, 7,700 times, for over 20 minutes).
            var cellsAt = new Dictionary<(FormKey World, int X, int Y), ICellGetter>();
            foreach (var group in doors.GroupBy(d => d.World))
            {
                var wanted = group.Select(d => GridOf(d.Door.Placement!.Position)).ToHashSet();
                foreach (var c in tiles[group.Key].SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items))
                    if (c.Grid is { } g && wanted.Contains((g.Point.X, g.Point.Y))) cellsAt[(group.Key, g.Point.X, g.Point.Y)] = c;
            }
            foreach (var (worldKey, door) in doors)
            {
                var src = tiles[worldKey];
                var p = door.Placement!.Position;
                var (gx, gy) = GridOf(p);
                var cell = cellsAt.GetValueOrDefault((worldKey, gx, gy));
                var srcNavm = cell?.NavigationMeshes.FirstOrDefault();
                if (cell is null || srcNavm?.Data is null) { noNavmesh++; continue; }
                // The triangle the door stands on: the one under it, else the nearest edge, its height counted half,
                // within 384 units across. (Open ground's triangles run thousands of units across, so the nearest
                // centre is often far from the door standing on its triangle: that joined only half the doors.)
                int best = BestTriangle(srcNavm.Data, p);
                if (best < 0) { noTriangle++; continue; }
                if (!navCopies.TryGetValue(srcNavm.FormKey, out var entry))
                {
                    if (!worldCopies.TryGetValue(worldKey, out var ws))
                    {
                        ws = src.DeepCopy(new Worldspace.TranslationMask(defaultOn: true) { SubCells = false, TopCell = false });
                        ws.SubCells.Clear(); ws.TopCell = null;
                        outMod.Worldspaces.Add(ws);
                        worldCopies[worldKey] = ws;
                    }
                    var cellCopy = cell.DeepCopy(new Cell.TranslationMask(defaultOn: true) { Temporary = false, Persistent = false, NavigationMeshes = false, Landscape = false });
                    cellCopy.Temporary.Clear(); cellCopy.Persistent.Clear(); cellCopy.Landscape = null;
                    var navm = srcNavm.DeepCopy();
                    cellCopy.NavigationMeshes.Add(navm);
                    TerrainBuilder.AddCell(ws, cellCopy);
                    entry = (navm, infos.TryGetValue(srcNavm.FormKey, out var info) ? info.DeepCopy() : null);
                    navCopies[srcNavm.FormKey] = entry;
                }
                var data = entry.Navm.Data!;
                if (data.DoorTriangles.Any(t => t.Door.FormKey == door.FormKey)) continue;
                data.DoorTriangles.Add(new DoorTriangle { TriangleBeforeDoor = (short)best, Unknown = DoorTag, Door = door.ToLink() });
                data.Triangles[best].Flags |= NavmeshTriangle.Flag.Door;
                entry.Info?.LinkedDoors.Add(new LinkedDoor { Unknown = DoorTag, Door = door.ToLink() });
                linked++;
            }
            if (map is not null && navCopies.Count > 0)
            {
                var mapOverride = new NavigationMeshInfoMap(map.FormKey, SkyrimRelease.SkyrimSE) { NavMeshVersion = map.NavMeshVersion, PreferredPathing = new PreferredPathing() };
                foreach (var (_, (_, info)) in navCopies) if (info is not null) mapOverride.MapInfos.Add(info);
                outMod.NavigationMeshInfoMaps.Add(mapOverride);
            }
            // The load order: each input's own masters before it (Daggerfall.esm under the land), then the inputs.
            var masters = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }.Select(f => ModKey.FromFileName(f))
                .Concat(others.Prepend(land).SelectMany(m => m.ModHeader.MasterReferences.Select(r => r.Master).Append(m.ModKey))).Distinct().ToArray();
            outMod.WriteToBinary(outPath, new BinaryWriteParameters { MastersListOrdering = new MastersListOrderingByLoadOrder(masters) });
            log?.Invoke($"{doors.Count:N0} overland doors, {linked:N0} joined to {navCopies.Count:N0} navmeshes ({noNavmesh:N0} in cells without one, {noTriangle:N0} with no triangle within 384) -> {outPath}");
            return (doors.Count, linked, navCopies.Count);
        }
        finally { foreach (var o in others) o.Dispose(); }
    }

    /// <summary>The triangle a point stands on: the one under it in plan, else the one with the nearest edge (within 384
    /// units), whichever is closest with height counted half; -1 when none is near.</summary>
    static int BestTriangle(INavigationMeshDataGetter data, P3Float p)
    {
        int best = -1; float bestScore = float.MaxValue;
        for (int i = 0; i < data.Triangles.Count; i++)
        {
            var t = data.Triangles[i];
            var a = data.Vertices[t.Vertices.X]; var b = data.Vertices[t.Vertices.Y]; var c = data.Vertices[t.Vertices.Z];
            float across = DistanceInPlan(p, a, b, c);
            if (across > 384) continue;
            // The triangle's height under the point (its plane), else its mean height.
            float z = (a.Z + b.Z + c.Z) / 3;
            float den = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (MathF.Abs(den) > 1e-3f)
            {
                float w0 = ((b.Y - c.Y) * (p.X - c.X) + (c.X - b.X) * (p.Y - c.Y)) / den;
                float w1 = ((c.Y - a.Y) * (p.X - c.X) + (a.X - c.X) * (p.Y - c.Y)) / den;
                z = w0 * a.Z + w1 * b.Z + (1 - w0 - w1) * c.Z;
            }
            float score = across + MathF.Abs(z - p.Z) / 2;
            if (MathF.Abs(z - p.Z) < 512 && score < bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    /// <summary>How far a point is from a triangle seen from above: 0 inside it, else to its nearest edge.</summary>
    static float DistanceInPlan(P3Float p, P3Float a, P3Float b, P3Float c)
    {
        static float Cross(P3Float o, P3Float u, P3Float v) => (u.X - o.X) * (v.Y - o.Y) - (u.Y - o.Y) * (v.X - o.X);
        float d1 = Cross(a, b, p), d2 = Cross(b, c, p), d3 = Cross(c, a, p);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        if (!(neg && pos)) return 0;
        static float ToEdge(P3Float p, P3Float u, P3Float v)
        {
            float ex = v.X - u.X, ey = v.Y - u.Y, len = ex * ex + ey * ey;
            float s = len > 0 ? Math.Clamp(((p.X - u.X) * ex + (p.Y - u.Y) * ey) / len, 0, 1) : 0;
            float dx = u.X + s * ex - p.X, dy = u.Y + s * ey - p.Y;
            return MathF.Sqrt(dx * dx + dy * dy);
        }
        return MathF.Min(ToEdge(p, a, b), MathF.Min(ToEdge(p, b, c), ToEdge(p, c, a)));
    }
}
