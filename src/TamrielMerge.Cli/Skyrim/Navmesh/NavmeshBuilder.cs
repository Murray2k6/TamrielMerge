using DotRecast.Core.Numerics;
using DotRecast.Recast;
using DotRecast.Recast.Geom;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Skyrim.Navmesh;

/// <summary>
/// Generates Skyrim navmeshes with Recast (DotRecast) and writes NAVM records plus entries for the navmesh info
/// map (NAVI). Format facts were taken from vanilla data: triangles wound counter-clockwise seen from above,
/// every triangle flagged Found, a divisor x divisor lookup grid ordered y * divisor + x listing each triangle in
/// the cells its bounding box touches, door triangles tagged 0xE48B73F3, and the 0xA5E9A03C stamp in the NAVM
/// header and NAVI entries. The engine merges NAVI per entry (Dawnguard.esm overrides it with only its own), so
/// our override carries only our navmeshes.
/// </summary>
public sealed class NavmeshBuilder(SkyrimMod mod)
{
    const uint NavmeshVersion = 12;
    const uint Stamp = 0xA5E9A03C;
    const int DoorTag = unchecked((int)0xE48B73F3);
    static readonly FormKey InfoMapKey = new(ModKey.FromFileName("Skyrim.esm"), 0x12FB4);

    NavigationMeshInfoMap? infoMap;
    SkyrimMod Mod => mod;

    /// <summary>Recast settings in Skyrim units: actor ~128 tall, 24 radius, steps up to 32 (stairs arrive as ramps), slopes to 50 degrees (Daggerfall's steepest stairs are 45).</summary>
    static RcConfig Config() => new(
        RcPartition.WATERSHED,
        cellSize: Env("CS", 8f), cellHeight: Env("CH", 4f),
        agentMaxSlope: Env("SLOPE", 50f), agentHeight: Env("HEIGHT", 128f), agentRadius: Env("RADIUS", 24f), agentMaxClimb: Env("CLIMB", 32f),
        regionMinSize: 8, regionMergeSize: 20,
        edgeMaxLen: 600f, edgeMaxError: 2f,
        vertsPerPoly: 6,
        detailSampleDist: 6f, detailSampleMaxError: 1f,
        filterLowHangingObstacles: Env("LOWHANG", 1f) > 0, filterLedgeSpans: Env("LEDGE", 1f) > 0, filterWalkableLowHeightSpans: Env("LOWHEIGHT", 1f) > 0,
        walkableAreaMod: new RcAreaModification(0x3f), buildMeshDetail: false);

    /// <summary>Development override of a Recast setting (TM_NAV_name), for experiments.</summary>
    static float Env(string name, float fallback) =>
        float.TryParse(Environment.GetEnvironmentVariable("TM_NAV_" + name), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public sealed record Result(NavigationMesh Navmesh, int Triangles, int Vertices, int DoorTriangles);

    /// <summary>
    /// Builds the navmesh for an interior cell from its walkable geometry (Skyrim cell space, polygons wound
    /// counter-clockwise around their normals) and links load doors to the nearest triangle in front of them.
    /// </summary>
    public Result BuildInterior(Cell cell, IReadOnlyList<(Vector3[] Points, Vector3 Normal)> polygons, IEnumerable<IPlacedObjectGetter> loadDoors, P3Float? keepAround = null, IEnumerable<P3Float>? keepAlso = null,
        Func<P3Float, bool>? underwater = null) =>
        BeginInterior(cell, polygons, loadDoors, keepAround, keepAlso, underwater, background: false).Finish();

    /// <summary>
    /// An interior navmesh being made: the record is added at <see cref="Finish"/> (or, made in the background, at the
    /// start, so its form ID comes in the same order as a build made in the foreground), and its map entry at Finish.
    /// Finish the pending meshes in the order they were begun: the navmesh map lists them in that order.
    /// </summary>
    public sealed class PendingInterior
    {
        internal NavmeshBuilder Owner = null!;
        internal Cell Cell = null!;
        internal NavigationMesh? Navm;
        internal Func<Generated>? Run;
        internal Task<Generated>? Work;
        Result? result;
        /// <summary>Seconds the mesh took to make (on whichever thread made it).</summary>
        public double Seconds { get; internal set; }

        /// <summary>Move the cell's actors that don't stand on the finished mesh onto it (<see cref="SnapToMesh"/>).</summary>
        public bool SnapActors { get; init; }

        /// <summary>Actors moved onto the mesh at Finish.</summary>
        public int Snapped { get; private set; }

        public Result Finish()
        {
            if (result is not null) return result;
            Generated g;
            try { g = Work is not null ? Work.GetAwaiter().GetResult() : Run!(); }
            catch { if (Navm is not null) Cell.NavigationMeshes.Remove(Navm); throw; }
            var navm = Navm ?? new NavigationMesh(Owner.Mod);
            navm.Data = g.Data;
            if (Navm is null) Cell.NavigationMeshes.Add(navm);
            Owner.AddInfo(navm, Cell, g.Verts, g.Linked);
            if (SnapActors) Snapped = SnapToMesh(Cell, g.Verts, g.Data.Triangles);
            return result = new Result(navm, g.Triangles, g.Verts.Count, g.Linked.Count);
        }
    }

    /// <summary>
    /// Each actor in the cell not standing on the mesh (over a triangle and within 24 units of it) moved to the nearest
    /// point on it within 512 units, a fifth of the way in toward that triangle's middle, 4 units up (2026-10-16: 2,742
    /// of Daggerfall's dungeon creatures stood off their navmesh, 315 inside objects, 133 in walls and 73 in the air at
    /// their markers; the mesh keeps clear of walls and props, so on it they can move and fight).
    /// </summary>
    static int SnapToMesh(Cell cell, List<P3Float> verts, IReadOnlyList<INavmeshTriangleGetter> tris)
    {
        if (tris.Count == 0) return 0;
        static Vector3 V(P3Float p) => new(p.X, p.Y, p.Z);
        int moved = 0;
        foreach (var npc in cell.Temporary.Concat(cell.Persistent).OfType<PlacedNpc>())
        {
            if (npc.Placement is not { } pl) continue;
            var p = V(pl.Position);
            bool on = false;
            float best = 512f * 512f;
            Vector3? at = null;
            foreach (var t in tris)
            {
                Vector3 a = V(verts[t.Vertices.X]), b = V(verts[t.Vertices.Y]), c = V(verts[t.Vertices.Z]);
                // On it: over a triangle and within 64 (the mesh lies up to about 32 over the floor the actor stands on).
                if (HeightOver(a, b, c, p.X, p.Y) is { } h && MathF.Abs(h - p.Z) <= 64) { on = true; break; }
                var q = ClosestOnTriangle(p, a, b, c);
                // The same level only (at most 96 up or down), height counting four times: the nearest point in plain
                // distance was often on the floor above or below (2026-10-16: 360 actors 700-1,200 units off).
                float dz = q.Z - p.Z;
                if (MathF.Abs(dz) > 96) continue;
                float d = (q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y) + 16 * dz * dz;
                if (d < best) { best = d; at = q + ((a + b + c) / 3 - q) * 0.2f; }
            }
            if (on || at is not { } to) continue;
            // Its own height when the mesh is level with it (the mesh floats over the floor; set on it, an actor stood 36
            // units up), else the mesh's.
            pl.Position = new P3Float(to.X, to.Y, MathF.Abs(to.Z - p.Z) <= 48 ? p.Z : to.Z + 4);
            moved++;
        }
        return moved;
    }

    static float? HeightOver(Vector3 a, Vector3 b, Vector3 c, float x, float y)
    {
        float den = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        if (MathF.Abs(den) < 1e-6f) return null;
        float w0 = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / den;
        float w1 = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / den;
        float w2 = 1 - w0 - w1;
        return w0 < 0 || w1 < 0 || w2 < 0 ? null : w0 * a.Z + w1 * b.Z + w2 * c.Z;
    }

    /// <summary>The point of triangle abc nearest p (Ericson, Real-Time Collision Detection 5.1.5).</summary>
    static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return a;
        var bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return b;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) return a + ab * (d1 / (d1 - d3));
        var cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return c;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) return a + ac * (d2 / (d2 - d6));
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6)));
        float denom = 1 / (va + vb + vc);
        return a + ab * (vb * denom) + ac * (vc * denom);
    }

    internal sealed record Generated(NavigationMeshData Data, List<P3Float> Verts, List<IPlacedObjectGetter> Linked, int Triangles);

    /// <summary>
    /// <see cref="BuildInterior"/> begun: with <paramref name="background"/> the mesh is made on a worker thread (the
    /// inputs must not change until it is finished), else when <see cref="PendingInterior.Finish"/> is called.
    /// </summary>
    public PendingInterior BeginInterior(Cell cell, IReadOnlyList<(Vector3[] Points, Vector3 Normal)> polygons, IEnumerable<IPlacedObjectGetter> loadDoors, P3Float? keepAround, IEnumerable<P3Float>? keepAlso,
        Func<P3Float, bool>? underwater, bool background, bool snapActors = false)
    {
        var doors = loadDoors.ToList();
        var keep = keepAlso?.ToList();
        var pending = new PendingInterior { Owner = this, Cell = cell, SnapActors = snapActors };
        pending.Run = () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var g = Generate(cell, polygons, doors, keepAround, keep, underwater);
            pending.Seconds = sw.Elapsed.TotalSeconds;
            return g;
        };
        if (background)
        {
            pending.Navm = new NavigationMesh(mod);
            cell.NavigationMeshes.Add(pending.Navm);
            pending.Work = Task.Run(pending.Run);
        }
        return pending;
    }

    static Generated Generate(Cell cell, IReadOnlyList<(Vector3[] Points, Vector3 Normal)> polygons, List<IPlacedObjectGetter> doorList, P3Float? keepAround, IEnumerable<P3Float>? keepAlso,
        Func<P3Float, bool>? underwater)
    {
        // Recast's mesh, or the same input's from another part (SharedExteriors: a town part's building interiors are
        // the same buildings as other parts', 2026-10-15: a fifth of a town part's work).
        var (outVerts, tris) = SharedExteriors is { } dir ? SharedInterior(dir, polygons) : RecastInterior(polygons);
        (outVerts, tris) = PruneIslands(outVerts, tris, doorList.Select(d => d.Placement!.Position).Append(keepAround ?? default).Concat(keepAlso ?? []).ToList());
        if (tris.Count > short.MaxValue || outVerts.Count > short.MaxValue)
            throw new InvalidOperationException($"navmesh too large for one NAVM: {tris.Count} triangles, {outVerts.Count} vertices");

        var data = new NavigationMeshData
        {
            NavmeshVersion = NavmeshVersion,
            CrcHash = Stamp,
            Parent = new CellNavmeshParent { Parent = cell.ToLink<ICellGetter>() },
        };
        data.Vertices.AddRange(outVerts);
        var neighbours = Adjacency(tris);
        for (int i = 0; i < tris.Count; i++)
            data.Triangles.Add(new NavmeshTriangle
            {
                Vertices = new P3Int16((short)tris[i].A, (short)tris[i].B, (short)tris[i].C),
                EdgeLink_0_1 = (short)neighbours[i, 0],
                EdgeLink_1_2 = (short)neighbours[i, 1],
                EdgeLink_2_0 = (short)neighbours[i, 2],
                // Ground under water is swum over (the Water flag), so swimming creatures can use it.
                Flags = NavmeshTriangle.Flag.Found | (underwater?.Invoke(Centroid(outVerts, tris[i])) == true ? NavmeshTriangle.Flag.Water : 0),
            });

        // Load doors: tag the nearest triangle within 256 units of the door.
        var linked = new List<IPlacedObjectGetter>();
        foreach (var door in doorList)
        {
            var dp = door.Placement!.Position;
            int best = -1; float bestD = 256f * 256f;
            for (int i = 0; i < tris.Count; i++)
            {
                var c = Centroid(outVerts, tris[i]);
                float d = (c.X - dp.X) * (c.X - dp.X) + (c.Y - dp.Y) * (c.Y - dp.Y) + (c.Z - dp.Z) * (c.Z - dp.Z) / 4;
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best < 0) continue;
            data.DoorTriangles.Add(new DoorTriangle { TriangleBeforeDoor = (short)best, Unknown = DoorTag, Door = door.ToLink() });
            data.Triangles[best].Flags |= NavmeshTriangle.Flag.Door;
            linked.Add(door);
        }

        WriteGrid(data, outVerts, tris);
        return new Generated(data, outVerts, linked, tris.Count);
    }

    /// <summary>Interiors whose mesh another process had left in <see cref="SharedExteriors"/>.</summary>
    public static int InteriorsShared => interiorsShared;
    static int interiorsShared;

    /// <summary>An interior's Recast mesh from <see cref="SharedExteriors"/> when another part made one of the same
    /// polygons (to 1/16 unit, their normals to 1/1024), else made and left there.</summary>
    static (List<P3Float> Verts, List<(int A, int B, int C)> Tris) SharedInterior(string dir, IReadOnlyList<(Vector3[] Points, Vector3 Normal)> polygons)
    {
        var q = new List<int>();
        foreach (var (pts, n) in polygons)
        {
            q.Add(pts.Length);
            q.Add((int)MathF.Round(n.X * 1024)); q.Add((int)MathF.Round(n.Y * 1024)); q.Add((int)MathF.Round(n.Z * 1024));
            foreach (var p in pts) { q.Add((int)MathF.Round(p.X * 16)); q.Add((int)MathF.Round(p.Y * 16)); q.Add((int)MathF.Round(p.Z * 16)); }
        }
        var hash = System.Security.Cryptography.SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(q)));
        string file = Path.Combine(dir, $"i{BitConverter.ToUInt64(hash, 0):x16}{BitConverter.ToUInt64(hash, 8):x16}.bin");
        if (ReadShared(file, out var shared)) { Interlocked.Increment(ref interiorsShared); return shared is { } m ? (m.Item1.ToList(), m.Item2.ToList()) : ([], []); }
        var made = RecastInterior(polygons);
        WriteShared(file, (made.Verts.ToArray(), made.Tris.ToArray()));
        return made;
    }

    /// <summary>An interior's mesh as Recast makes it from its polygons, before its islands are pruned.</summary>
    static (List<P3Float> Verts, List<(int A, int B, int C)> Tris) RecastInterior(IReadOnlyList<(Vector3[] Points, Vector3 Normal)> polygons)
    {
        // Skyrim (x, y north, z up) -> Recast (x, y up, z) by rotation (x, z, -y), which keeps winding.
        var verts = new List<float>();
        var faces = new List<int>();
        foreach (var (pts, n) in polygons)
        {
            int start = verts.Count / 3;
            foreach (var p in pts) { verts.Add(p.X); verts.Add(p.Z); verts.Add(-p.Y); }
            for (int i = 1; i + 1 < pts.Length; i++)
            {
                bool ccw = Vector3.Dot(Vector3.Cross(pts[i] - pts[0], pts[i + 1] - pts[0]), n) >= 0;
                faces.AddRange(ccw ? [start, start + i, start + i + 1] : [start, start + i + 1, start + i]);
            }
        }
        var geom = new RcSampleInputGeomProvider(verts.ToArray(), faces.ToArray());
        var cfg = Config();
        var result = new RcBuilder().Build(geom, new RcBuilderConfig(cfg, geom.GetMeshBoundsMin(), geom.GetMeshBoundsMax()), false);
        var pmesh = result.Mesh;

        // Poly mesh vertices are voxel coordinates; convert back to Skyrim space.
        var outVerts = new List<P3Float>(pmesh.nverts);
        for (int i = 0; i < pmesh.nverts; i++)
        {
            float rx = pmesh.bmin.X + pmesh.verts[i * 3] * pmesh.cs;
            float ry = pmesh.bmin.Y + pmesh.verts[i * 3 + 1] * pmesh.ch;
            float rz = pmesh.bmin.Z + pmesh.verts[i * 3 + 2] * pmesh.cs;
            outVerts.Add(new P3Float(rx, -rz, ry));
        }
        var tris = new List<(int A, int B, int C)>();
        for (int p = 0; p < pmesh.npolys; p++)
        {
            int baseIdx = p * pmesh.nvp * 2;
            var poly = Enumerable.Range(0, pmesh.nvp).Select(k => pmesh.verts.Length > 0 ? pmesh.polys[baseIdx + k] : -1)
                .TakeWhile(v => v is >= 0 and < 0xffff).ToArray();
            for (int k = 1; k + 1 < poly.Length; k++)
            {
                var (a, b, c) = (poly[0], poly[k], poly[k + 1]);
                if (CrossZ(outVerts[a], outVerts[b], outVerts[c]) < 0) (b, c) = (c, b);
                tris.Add((a, b, c));
            }
        }
        if (Environment.GetEnvironmentVariable("TM_DEBUG_NAV") == "1")
        {
            // Pieces by Recast's own polygon adjacency, to compare with the vertex-sharing adjacency used below.
            var comp = new int[pmesh.npolys]; Array.Fill(comp, -1); int pieces = 0;
            for (int p0 = 0; p0 < pmesh.npolys; p0++)
            {
                if (comp[p0] >= 0) continue;
                var st = new Stack<int>([p0]); comp[p0] = pieces;
                while (st.Count > 0)
                {
                    int p = st.Pop(), b = p * pmesh.nvp * 2;
                    for (int k = 0; k < pmesh.nvp; k++)
                    {
                        int nb = pmesh.polys[b + pmesh.nvp + k];
                        if (pmesh.polys[b + k] == RcRecast.RC_MESH_NULL_IDX) break;
                        if ((nb & 0x8000) == 0 && nb != RcRecast.RC_MESH_NULL_IDX && nb < pmesh.npolys && comp[nb] < 0) { comp[nb] = pieces; st.Push(nb); }
                    }
                }
                pieces++;
            }
            var adj = Adjacency(tris);
            var tc = new int[tris.Count]; Array.Fill(tc, -1); int tpieces = 0;
            for (int i = 0; i < tris.Count; i++)
            {
                if (tc[i] >= 0) continue;
                var st = new Stack<int>([i]); tc[i] = tpieces;
                while (st.Count > 0) { int k = st.Pop(); for (int e = 0; e < 3; e++) if (adj[k, e] is var nn && nn >= 0 && tc[nn] < 0) { tc[nn] = tpieces; st.Push(nn); } }
                tpieces++;
            }
            var biggest = Enumerable.Range(0, tpieces).Select(c => tc.Count(x => x == c)).OrderDescending().Take(6);
            Console.WriteLine($"    navmesh debug: {pmesh.npolys} polys in {pieces} pieces by Recast adjacency; {tris.Count} triangles in {tpieces} pieces by shared vertices; largest {string.Join(",", biggest)}");
        }
        return (outVerts, tris);
    }

    // ---- Exterior cells ----

    /// <summary>Skyrim exterior cell size in units.</summary>
    public const float CellUnits = 4096f;

    /// <summary>An exterior cell's navmesh with its world-space geometry, kept for linking edges to neighbours.</summary>
    public sealed record ExteriorMesh(Worldspace Worldspace, int X, int Y, NavigationMesh Navmesh, List<P3Float> Verts, List<(int A, int B, int C)> Tris);

    readonly List<ExteriorMesh> exteriors = [];

    /// <summary>
    /// A Skyrim landscape's surface as world-space triangles (33 x 33 heights, 128 units apart; VHGT holds the first
    /// height then per-row and per-column deltas, all in units of 8). With <paramref name="water"/>, quads lying more
    /// than 64 units under the water are left out: actors swim there, they don't walk the bottom.
    /// </summary>
    public static void AddTerrain(List<Vector3[]> output, ILandscapeGetter land, int cx, int cy, float? water)
    {
        if (Heights(land) is not { } h) return;
        float ox = cx * CellUnits, oy = cy * CellUnits;
        Vector3 P(int x, int y) => new(ox + x * 128, oy + y * 128, h[x, y]);
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                if (water is { } w && MathF.Max(MathF.Max(h[x, y], h[x + 1, y]), MathF.Max(h[x, y + 1], h[x + 1, y + 1])) < w - 64) continue;
                output.Add([P(x, y), P(x + 1, y), P(x + 1, y + 1)]);
                output.Add([P(x, y), P(x + 1, y + 1), P(x, y + 1)]);
            }
    }

    /// <summary>A landscape's 33 x 33 heights (units), [x, y].</summary>
    static float[,]? Heights(ILandscapeGetter land)
    {
        if (land.VertexHeightMap is not { } vhgt) return null;
        var h = new float[33, 33];
        float rowStart = vhgt.Offset;
        for (int y = 0; y < 33; y++)
        {
            rowStart += vhgt.HeightMap[0, y];
            float v = rowStart;
            h[0, y] = v * 8;
            for (int x = 1; x < 33; x++) { v += vhgt.HeightMap[x, y]; h[x, y] = v * 8; }
        }
        return h;
    }

    /// <summary>
    /// An open-country cell with nothing on it but its land (Daggerfall's overland), meshed straight from its heights
    /// instead of by Recast: blocks of 16 quads (2048 units; Recast kept edges under 1600) split in four while they lie
    /// more than <see cref="GridTolerance"/> from the ground under them or cross from walkable ground into deep water or
    /// a slope too steep to walk, down to single 128-unit quads. Neighbouring blocks differ by one split at most, and a
    /// block's edge gets a midpoint where the block beside it is split, so the triangles share their corners.
    /// 2026-10-15 (user: "should be a 1 hour conversion"): Recast voxelised each of the overland's 400,000 cells for
    /// over 50 minutes on every core, and left about 3 triangles a cell, hundreds of units off the ground on Daggerfall's
    /// hills; an even grid per cell within 48 units took 7 minutes but made 99 million triangles.
    /// </summary>
    public static (List<P3Float> Verts, List<(int A, int B, int C)> Tris)? GenerateTerrainGrid(ILandscapeGetter land, int cx, int cy, float? water, List<P3Float> keep)
    {
        if (Heights(land) is not { } h) return null;
        // Each 128-unit quad: walkable, or left out (more than 64 units under the water, as AddTerrain leaves out, or
        // steeper than Recast's 50-degree limit).
        float cosMax = MathF.Cos(50f * MathF.PI / 180f);
        var open = new bool[32, 32];
        static float Up(float dx, float dy) => 1f / MathF.Sqrt(1 + dx * dx + dy * dy);
        bool any = false;
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                float top = MathF.Max(MathF.Max(h[x, y], h[x + 1, y]), MathF.Max(h[x, y + 1], h[x + 1, y + 1]));
                bool deep = water is { } w && top < w - 64;
                // The quad's two triangles' slopes (P00 P10 P11 and P00 P11 P01).
                bool steep = Up((h[x + 1, y] - h[x, y]) / 128, (h[x + 1, y + 1] - h[x + 1, y]) / 128) < cosMax
                          || Up((h[x + 1, y + 1] - h[x, y + 1]) / 128, (h[x, y + 1] - h[x, y]) / 128) < cosMax;
                open[x, y] = !deep && !steep;
                any |= open[x, y];
            }
        if (!any) return null;

        // The blocks: leaf[x, y] is the size of the block holding quad (x, y); blocks sit on multiples of their size.
        var leaf = new int[32, 32];
        bool Fits(int x0, int y0, int size)
        {
            bool first = open[x0, y0];
            for (int y = y0; y < y0 + size; y++)
                for (int x = x0; x < x0 + size; x++)
                    if (open[x, y] != first) return false;
            if (!first) return true;
            float a = h[x0, y0], b = h[x0 + size, y0], c = h[x0 + size, y0 + size], d = h[x0, y0 + size];
            for (int y = 0; y <= size; y++)
                for (int x = 0; x <= size; x++)
                {
                    float u = x / (float)size, v = y / (float)size;
                    float z = u >= v ? a + u * (b - a) + v * (c - b) : a + v * (d - a) + u * (c - d);
                    if (MathF.Abs(z - h[x0 + x, y0 + y]) > GridTolerance) return false;
                }
            return true;
        }
        void Set(int x0, int y0, int size) { for (int y = y0; y < y0 + size; y++) for (int x = x0; x < x0 + size; x++) leaf[x, y] = size; }
        void Split(int x0, int y0, int size)
        {
            if (size == 1 || Fits(x0, y0, size)) { Set(x0, y0, size); return; }
            int half = size / 2;
            Split(x0, y0, half); Split(x0 + half, y0, half); Split(x0, y0 + half, half); Split(x0 + half, y0 + half, half);
        }
        for (int y = 0; y < 32; y += 16) for (int x = 0; x < 32; x += 16) Split(x, y, 16);
        // Balance: a block twice as big as a neighbour's half is split, until none is.
        int Smallest(int x0, int y0, int size, int side)
        {
            int m = int.MaxValue;
            for (int i = 0; i < size; i++)
            {
                var (x, y) = side switch { 0 => (x0 - 1, y0 + i), 1 => (x0 + size, y0 + i), 2 => (x0 + i, y0 - 1), _ => (x0 + i, y0 + size) };
                if (x is >= 0 and < 32 && y is >= 0 and < 32) m = Math.Min(m, leaf[x, y]);
            }
            return m;
        }
        for (bool changed = true; changed;)
        {
            changed = false;
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                {
                    int size = leaf[x, y];
                    if (size == 1 || x % size != 0 || y % size != 0) continue;
                    if (Enumerable.Range(0, 4).Any(side => Smallest(x, y, size, side) < size / 2))
                    {
                        int half = size / 2;
                        Set(x, y, half);
                        changed = true;
                    }
                }
        }

        var verts = new List<P3Float>();
        var index = new Dictionary<(int, int), int>();
        int V(int x, int y)
        {
            if (!index.TryGetValue((x, y), out int i)) { i = verts.Count; verts.Add(new P3Float(cx * CellUnits + x * 128, cy * CellUnits + y * 128, h[x, y])); index[(x, y)] = i; }
            return i;
        }
        var tris = new List<(int A, int B, int C)>();
        for (int y0 = 0; y0 < 32; y0++)
            for (int x0 = 0; x0 < 32; x0++)
            {
                int size = leaf[x0, y0];
                if (x0 % size != 0 || y0 % size != 0 || !open[x0, y0]) continue;
                int half = size / 2, x1 = x0 + size, y1 = y0 + size;
                // Midpoints where the neighbour beside an edge is split (south, east, north, west).
                bool ms = size > 1 && Smallest(x0, y0, size, 2) < size, me = size > 1 && Smallest(x0, y0, size, 1) < size;
                bool mn = size > 1 && Smallest(x0, y0, size, 3) < size, mw = size > 1 && Smallest(x0, y0, size, 0) < size;
                if (!ms && !me && !mn && !mw)
                {
                    tris.Add((V(x0, y0), V(x1, y0), V(x1, y1)));
                    tris.Add((V(x0, y0), V(x1, y1), V(x0, y1)));
                    continue;
                }
                // A fan from the block's centre round its corners and midpoints, counter-clockwise.
                var ring = new List<int> { V(x0, y0) };
                if (ms) ring.Add(V(x0 + half, y0));
                ring.Add(V(x1, y0));
                if (me) ring.Add(V(x1, y0 + half));
                ring.Add(V(x1, y1));
                if (mn) ring.Add(V(x0 + half, y1));
                ring.Add(V(x0, y1));
                if (mw) ring.Add(V(x0, y0 + half));
                int centre = V(x0 + half, y0 + half);
                for (int i = 0; i < ring.Count; i++) tris.Add((centre, ring[i], ring[(i + 1) % ring.Count]));
            }
        if (tris.Count == 0) return null;
        (verts, tris) = PruneIslands(verts, tris, keep);
        return tris.Count == 0 ? null : (verts, tris);
    }

    /// <summary>How far (units) a terrain navmesh block may lie from the ground under it (2026-10-15, measured on 600
    /// overland cells: 48 units made 68 million triangles over the overland, 96 made 26, 128 made 17).</summary>
    const float GridTolerance = 96f;

    /// <summary>Recast settings for exteriors: 16-unit voxels (terrain vertices are 128 apart), one 4096-unit tile per
    /// cell with a border, so the agent radius doesn't erode the mesh back from the cell's edges.</summary>
    static RcConfig ExteriorConfig(bool open = false)
    {
        // Open country (open: Daggerfall's overland, smooth land and little on it) in coarser voxels and longer, looser
        // edges: a quarter of the triangles over 87,000 cells, as vanilla's wilds are meshed in big triangles too.
        float cs = open ? 32f : 16f, ch = open ? 8f : 4f, radius = 24f;
        int tile = (int)(CellUnits / cs);
        return new RcConfig(true, tile, tile, RcConfig.CalcBorder(radius, cs), RcPartition.WATERSHED,
            cs, ch, agentMaxSlope: 50f, agentHeight: 128f, agentRadius: radius, agentMaxClimb: open ? 48f : 32f,
            minRegionArea: 8 * cs * cs, mergeRegionArea: 20 * cs * cs, edgeMaxLen: open ? 1600f : 600f, edgeMaxError: open ? 4f : 2f, vertsPerPoly: 6,
            detailSampleDist: 6f, detailSampleMaxError: 1f, filterLowHangingObstacles: true, filterLedgeSpans: true, filterWalkableLowHeightSpans: true,
            walkableAreaMod: new RcAreaModification(0x3f), buildMeshDetail: false);
    }

    /// <summary>
    /// An exterior cell's navmesh geometry from world-space triangles (any winding; walkable faces are taken to point
    /// up), clipped to the cell. Thread-safe: it touches no records, so cells can be generated in parallel and written
    /// with <see cref="WriteExterior"/> afterwards. Null when nothing is walkable.
    /// </summary>
    public static (List<P3Float> Verts, List<(int A, int B, int C)> Tris)? GenerateExterior(int cx, int cy, List<Vector3[]> triangles, List<P3Float> keep, bool open = false) =>
        GenerateExteriorRaw(cx, cy, triangles, open) is { } raw ? Finish(raw.Verts, raw.Tris, keep) : null;

    /// <summary>The islands not reached from <paramref name="keep"/> pruned (<see cref="PruneIslands"/>); null when
    /// nothing is left or the mesh is too big for a navmesh's 16-bit indices.</summary>
    static (List<P3Float> Verts, List<(int A, int B, int C)> Tris)? Finish(List<P3Float> verts, List<(int A, int B, int C)> tris, List<P3Float> keep)
    {
        (verts, tris) = PruneIslands(verts, tris, keep);
        if (tris.Count == 0 || tris.Count > short.MaxValue || verts.Count > short.MaxValue) return null;
        return (verts, tris);
    }

    /// <summary><see cref="GenerateExterior"/>'s mesh as Recast makes it, before the islands are pruned.</summary>
    static (List<P3Float> Verts, List<(int A, int B, int C)> Tris)? GenerateExteriorRaw(int cx, int cy, List<Vector3[]> triangles, bool open)
    {
        if (triangles.Count == 0) return null;
        var verts = new float[triangles.Count * 9];
        var faces = new int[triangles.Count * 3];
        float minZ = float.MaxValue, maxZ = float.MinValue;
        for (int t = 0; t < triangles.Count; t++)
        {
            var p = triangles[t];
            bool up = Vector3.Cross(p[1] - p[0], p[2] - p[0]).Z >= 0;
            for (int k = 0; k < 3; k++)
            {
                var v = p[k];
                verts[t * 9 + k * 3] = v.X; verts[t * 9 + k * 3 + 1] = v.Z; verts[t * 9 + k * 3 + 2] = -v.Y;
                minZ = MathF.Min(minZ, v.Z); maxZ = MathF.Max(maxZ, v.Z);
            }
            faces[t * 3] = t * 3;
            faces[t * 3 + 1] = up ? t * 3 + 1 : t * 3 + 2;
            faces[t * 3 + 2] = up ? t * 3 + 2 : t * 3 + 1;
        }
        var geom = new RcSampleInputGeomProvider(verts, faces);
        var cfg = ExteriorConfig(open);
        var bmin = new RcVec3f(cx * CellUnits, minZ - 16, -(cy + 1) * CellUnits);
        var bmax = new RcVec3f((cx + 1) * CellUnits, maxZ + 256, -cy * CellUnits);
        var pmesh = new RcBuilder().Build(geom, new RcBuilderConfig(cfg, bmin, bmax, 0, 0), false).Mesh;
        if (pmesh is null || pmesh.npolys == 0) return null;
        var outVerts = new List<P3Float>(pmesh.nverts);
        for (int i = 0; i < pmesh.nverts; i++)
        {
            float rx = pmesh.bmin.X + pmesh.verts[i * 3] * pmesh.cs;
            float ry = pmesh.bmin.Y + pmesh.verts[i * 3 + 1] * pmesh.ch;
            float rz = pmesh.bmin.Z + pmesh.verts[i * 3 + 2] * pmesh.cs;
            outVerts.Add(new P3Float(rx, -rz, ry));
        }
        var tris = new List<(int A, int B, int C)>();
        for (int p = 0; p < pmesh.npolys; p++)
        {
            int baseIdx = p * pmesh.nvp * 2;
            var poly = Enumerable.Range(0, pmesh.nvp).Select(k => pmesh.polys[baseIdx + k]).TakeWhile(v => v is >= 0 and < 0xffff).ToArray();
            for (int k = 1; k + 1 < poly.Length; k++)
            {
                var (a, b, c) = (poly[0], poly[k], poly[k + 1]);
                if (CrossZ(outVerts[a], outVerts[b], outVerts[c]) < 0) (b, c) = (c, b);
                tris.Add((a, b, c));
            }
        }
        return (outVerts, tris);
    }

    /// <summary>
    /// Recast's meshes of town cells by what goes into them (user, 2026-10-15: "fix everything and the gaps"): a town
    /// part's 3,780 street cells had 1,194 different inputs (Daggerfall's towns are built of the same blocks, two cells
    /// a side each, and the open ground round them repeats), and Recast was four fifths of a part's work. The key is a
    /// hash of the cell's triangles in its own coordinates (to 1/16 unit); the mesh is kept in them too, and moved to
    /// each cell that has the same input, its islands then pruned for that cell's own people and doors.
    /// </summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<(ulong, ulong, bool), Lazy<(P3Float[] Verts, (int A, int B, int C)[] Tris)?>> exteriorCache = new();
    public int ExteriorReused => exteriorReused;
    /// <summary>Of the meshes made here, those another process had left in <see cref="SharedExteriors"/>.</summary>
    public int ExteriorShared => exteriorShared;
    int exteriorReused, exteriorShared;

    /// <summary>
    /// A folder the conversion's town parts share (TAMRIELMERGE_NAVMESHES, as the town meshes' TAMRIELMERGE_TOWN_MESHES):
    /// each mesh made here is left there as &lt;key&gt;.bin, and one another part made is read instead of being made again.
    /// </summary>
    public static string? SharedExteriors { get; set; } = Environment.GetEnvironmentVariable("TAMRIELMERGE_NAVMESHES") is { Length: > 0 } d ? d : null;

    // A mesh file: "TMNV", the vertex count (-1: nothing walkable), the vertices (x, y, z), the triangle count, the
    // triangles (a, b, c).
    static bool ReadShared(string file, out (P3Float[], (int, int, int)[])? mesh)
    {
        try
        {
            mesh = null;
            if (!File.Exists(file)) return false;
            using var r = new BinaryReader(File.OpenRead(file));
            if (r.ReadUInt32() != 0x564E4D54) return false;
            int nv = r.ReadInt32();
            if (nv < 0) return true;
            var verts = new P3Float[nv];
            for (int i = 0; i < nv; i++) verts[i] = new P3Float(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var tris = new (int, int, int)[r.ReadInt32()];
            for (int i = 0; i < tris.Length; i++) tris[i] = (r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
            mesh = (verts, tris);
            return true;
        }
        catch (IOException) { mesh = null; return false; }
    }

    static void WriteShared(string file, (P3Float[] Verts, (int A, int B, int C)[] Tris)? mesh)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string tmp = file + "." + Environment.ProcessId + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp)))
            {
                w.Write(0x564E4D54u);
                if (mesh is not { } m) w.Write(-1);
                else
                {
                    w.Write(m.Verts.Length);
                    foreach (var v in m.Verts) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
                    w.Write(m.Tris.Length);
                    foreach (var (a, b, c) in m.Tris) { w.Write(a); w.Write(b); w.Write(c); }
                }
            }
            // Renamed when whole; another part may have left the same one meanwhile (the same mesh).
            if (!File.Exists(file)) File.Move(tmp, file, overwrite: true); else File.Delete(tmp);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static (ulong, ulong) InputKey(List<Vector3[]> tris, int cx, int cy)
    {
        var q = new int[tris.Count * 9];
        int n = 0;
        foreach (var t in tris)
            foreach (var v in t)
            {
                q[n++] = (int)MathF.Round((v.X - cx * CellUnits) * 16);
                q[n++] = (int)MathF.Round((v.Y - cy * CellUnits) * 16);
                q[n++] = (int)MathF.Round(v.Z * 16);
            }
        var hash = System.Security.Cryptography.SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(q.AsSpan(0, n)));
        return (BitConverter.ToUInt64(hash, 0), BitConverter.ToUInt64(hash, 8));
    }

    (List<P3Float> Verts, List<(int A, int B, int C)> Tris)? GenerateExteriorReused(int cx, int cy, List<Vector3[]> triangles, List<P3Float> keep, bool open)
    {
        var (k1, k2) = InputKey(triangles, cx, cy);
        bool made = false;
        var local = exteriorCache.GetOrAdd((k1, k2, open), _ => new Lazy<(P3Float[], (int, int, int)[])?>(() =>
        {
            made = true;
            string? file = SharedExteriors is { } dir ? Path.Combine(dir, $"{k1:x16}{k2:x16}{(open ? "o" : "")}.bin") : null;
            if (file is not null && ReadShared(file, out var shared)) { Interlocked.Increment(ref exteriorShared); return shared; }
            (P3Float[], (int, int, int)[])? mesh = null;
            if (GenerateExteriorRaw(cx, cy, triangles, open) is { } raw)
            {
                float ox = cx * CellUnits, oy = cy * CellUnits;
                mesh = (raw.Verts.Select(v => new P3Float(v.X - ox, v.Y - oy, v.Z)).ToArray(), raw.Tris.ToArray());
            }
            if (file is not null) WriteShared(file, mesh);
            return mesh;
        })).Value;
        if (!made) Interlocked.Increment(ref exteriorReused);
        if (local is not { } m) return null;
        float wx = cx * CellUnits, wy = cy * CellUnits;
        return Finish(m.Verts.Select(v => new P3Float(v.X + wx, v.Y + wy, v.Z)).ToList(), [.. m.Tris], keep);
    }

    /// <summary>Writes an exterior cell's navmesh (generated by <see cref="GenerateExterior"/>), with its load doors.</summary>
    public ExteriorMesh WriteExterior(Worldspace ws, Cell cell, int cx, int cy, List<P3Float> verts, List<(int A, int B, int C)> tris, IEnumerable<IPlacedObjectGetter> loadDoors)
    {
        var data = new NavigationMeshData
        {
            NavmeshVersion = NavmeshVersion,
            CrcHash = Stamp,
            // Skyrim stores the cell's grid Y first, then X (2,914 of Skyrim.esm's exterior navmeshes, 2026-10-14); Mutagen's
            // P2Int16 writes X first, so Y goes in X. Swapped, a navmesh named a cell that doesn't exist in the long Iliac
            // Bay (Privateer's Hold at (-391, 91) became (91, -391)) and the game crashed stepping outside.
            Parent = new WorldspaceNavmeshParent { Parent = ws.ToLink<IWorldspaceGetter>(), Coordinates = new P2Int16((short)cy, (short)cx) },
        };
        data.Vertices.AddRange(verts);
        var neighbours = Adjacency(tris);
        for (int i = 0; i < tris.Count; i++)
            data.Triangles.Add(new NavmeshTriangle
            {
                Vertices = new P3Int16((short)tris[i].A, (short)tris[i].B, (short)tris[i].C),
                EdgeLink_0_1 = (short)neighbours[i, 0],
                EdgeLink_1_2 = (short)neighbours[i, 1],
                EdgeLink_2_0 = (short)neighbours[i, 2],
                Flags = NavmeshTriangle.Flag.Found,
            });
        var linked = new List<IPlacedObjectGetter>();
        foreach (var door in loadDoors)
        {
            var dp = door.Placement!.Position;
            int best = -1; float bestD = 256f * 256f;
            for (int i = 0; i < tris.Count; i++)
            {
                var c = Centroid(verts, tris[i]);
                float d = (c.X - dp.X) * (c.X - dp.X) + (c.Y - dp.Y) * (c.Y - dp.Y) + (c.Z - dp.Z) * (c.Z - dp.Z) / 4;
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best < 0) continue;
            data.DoorTriangles.Add(new DoorTriangle { TriangleBeforeDoor = (short)best, Unknown = DoorTag, Door = door.ToLink() });
            data.Triangles[best].Flags |= NavmeshTriangle.Flag.Door;
            linked.Add(door);
        }
        WriteGrid(data, verts, tris);
        var navm = new NavigationMesh(mod) { Data = data };
        cell.NavigationMeshes.Add(navm);
        // The navmesh map's entry stores the grid the same way: Y first.
        AddInfo(navm, new NavigationMapInfoWorldParent { ParentWorldspace = ws.ToLink<IWorldspaceGetter>(), ParentWorldspaceCoord = new P2Int16((short)cy, (short)cx) }, verts, linked);
        var mesh = new ExteriorMesh(ws, cx, cy, navm, verts, tris);
        exteriors.Add(mesh);
        return mesh;
    }

    /// <summary>
    /// Navmeshes for every exterior cell of a worldspace: terrain (each cell's landscape) and the collision of what is
    /// placed (<paramref name="solidsOf"/>: world-space triangles of the references placed in a cell), taken from the
    /// cell and its eight neighbours within reach of its border, generated in parallel and written in cell order.
    /// Load doors (persistent, so listed separately) link to the triangle in front of them. Edges are joined across
    /// cells later by <see cref="LinkExteriorEdges"/>. With <paramref name="only"/>, navmeshes only for the cells it
    /// picks (the rest are there as their neighbours: a big worldspace is built a piece at a time). Returns (navmeshes,
    /// triangles).
    /// </summary>
    /// <remarks>With <paramref name="reuse"/>, a cell whose input another cell of this builder had takes its mesh
    /// (<see cref="exteriorCache"/>; Daggerfall's towns). Not with <paramref name="open"/>.</remarks>
    public (int Meshes, int Triangles) BuildExteriorCells(Worldspace ws, IReadOnlyDictionary<(int X, int Y), Cell> cells,
        Func<int, int, IEnumerable<Vector3[]>> solidsOf, Func<Cell, float?> waterOf, IReadOnlyList<IPlacedObjectGetter> doors, List<string> problems,
        bool open = false, Func<int, int, bool>? only = null, bool reuse = false)
    {
        reuse &= !open;
        const float margin = 160f;
        var keys = cells.Keys.Where(k => only is null || only(k.X, k.Y)).OrderBy(k => k.Y).ThenBy(k => k.X).ToList();
        var results = new (List<P3Float> Verts, List<(int A, int B, int C)> Tris)?[keys.Count];
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        bool InCell(P3Float p, int x, int y) => p.X >= x * CellUnits && p.X < (x + 1) * CellUnits && p.Y >= y * CellUnits && p.Y < (y + 1) * CellUnits;
        Parallel.For(0, keys.Count, i =>
        {
            var (x, y) = keys[i];
            float x0 = x * CellUnits - margin, x1 = (x + 1) * CellUnits + margin, y0 = y * CellUnits - margin, y1 = (y + 1) * CellUnits + margin;
            bool Near(Vector3[] t) =>
                MathF.Max(t[0].X, MathF.Max(t[1].X, t[2].X)) >= x0 && MathF.Min(t[0].X, MathF.Min(t[1].X, t[2].X)) <= x1 &&
                MathF.Max(t[0].Y, MathF.Max(t[1].Y, t[2].Y)) >= y0 && MathF.Min(t[0].Y, MathF.Min(t[1].Y, t[2].Y)) <= y1;
            try
            {
                var cell = cells[(x, y)];
                var keep = cell.Temporary.OfType<IPlacedNpcGetter>().Select(n => n.Placement!.Position)
                    .Concat(doors.Select(d => d.Placement!.Position).Where(p => InCell(p, x, y))).ToList();
                // Open country with nothing standing on it near the cell: straight from its heights.
                if (open && cell.Landscape is { } bare && !Enumerable.Range(-1, 3).Any(dy => Enumerable.Range(-1, 3).Any(dx => solidsOf(x + dx, y + dy).Any())))
                {
                    results[i] = GenerateTerrainGrid(bare, x, y, waterOf(cell), keep);
                    return;
                }
                var tris = new List<Vector3[]>();
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (!cells.TryGetValue((x + dx, y + dy), out var c)) continue;
                        float? water = waterOf(c);
                        var local = new List<Vector3[]>();
                        if (c.Landscape is { } land) AddTerrain(local, land, x + dx, y + dy, water);
                        foreach (var t in solidsOf(x + dx, y + dy))
                            if (water is not { } w || MathF.Max(t[0].Z, MathF.Max(t[1].Z, t[2].Z)) >= w - 64) local.Add(t);
                        tris.AddRange(dx == 0 && dy == 0 ? local : local.Where(Near));
                    }
                // A cell Recast can't outline in the open-country setting is tried again in the fine one.
                if (reuse) results[i] = GenerateExteriorReused(x, y, tris, keep, open);
                else
                    try { results[i] = GenerateExterior(x, y, tris, keep, open); }
                    catch (Exception) when (open) { results[i] = GenerateExterior(x, y, tris, keep); }
            }
            catch (Exception e) { errors.Add($"navmesh {ws.EditorID} {x},{y}: {e.Message}"); }
        });
        problems.AddRange(errors);
        int meshes = 0, triangles = 0;
        for (int i = 0; i < keys.Count; i++)
        {
            if (results[i] is not { } r) continue;
            var (x, y) = keys[i];
            WriteExterior(ws, cells[keys[i]], x, y, r.Verts, r.Tris, doors.Where(d => InCell(d.Placement!.Position, x, y)));
            meshes++;
            triangles += r.Tris.Count;
        }
        return (meshes, triangles);
    }

    /// <summary>
    /// Joins exterior navmeshes across cell borders, as vanilla does: a triangle edge lying on its cell's border with
    /// no neighbour inside its mesh links to the triangle of the neighbouring cell's navmesh whose border edge
    /// overlaps it most (an EdgeLinks entry, whose index replaces the neighbour, and the edge's EdgeLink flag).
    /// Returns the number of links.
    /// </summary>
    public int LinkExteriorEdges()
    {
        var byCell = exteriors.GroupBy(m => (m.Worldspace, m.X, m.Y)).ToDictionary(g => g.Key, g => g.First());
        // Border edges per mesh and side (0 west, 1 east, 2 south, 3 north): (triangle, edge, span from, span to).
        var borders = new Dictionary<(ExteriorMesh, int), List<(int Tri, int Edge, float Lo, float Hi)>>();
        List<(int Tri, int Edge, float Lo, float Hi)> Borders(ExteriorMesh m, int side)
        {
            if (borders.TryGetValue((m, side), out var list)) return list;
            list = [];
            float line = side switch { 0 => m.X * CellUnits, 1 => (m.X + 1) * CellUnits, 2 => m.Y * CellUnits, _ => (m.Y + 1) * CellUnits };
            bool vertical = side < 2;
            for (int i = 0; i < m.Tris.Count; i++)
            {
                var t = m.Tris[i];
                int[] vs = [t.A, t.B, t.C];
                for (int e = 0; e < 3; e++)
                {
                    var a = m.Verts[vs[e]]; var b = m.Verts[vs[(e + 1) % 3]];
                    float ac = vertical ? a.X : a.Y, bc = vertical ? b.X : b.Y;
                    if (MathF.Abs(ac - line) > 0.5f || MathF.Abs(bc - line) > 0.5f) continue;
                    float sa = vertical ? a.Y : a.X, sb = vertical ? b.Y : b.X;
                    list.Add((i, e, MathF.Min(sa, sb), MathF.Max(sa, sb)));
                }
            }
            return borders[(m, side)] = list;
        }
        int links = 0;
        foreach (var m in exteriors)
        {
            var data = m.Navmesh.Data!;
            for (int side = 0; side < 4; side++)
            {
                var (nx, ny, nside) = side switch { 0 => (m.X - 1, m.Y, 1), 1 => (m.X + 1, m.Y, 0), 2 => (m.X, m.Y - 1, 3), _ => (m.X, m.Y + 1, 2) };
                if (!byCell.TryGetValue((m.Worldspace, nx, ny), out var other)) continue;
                var theirs = Borders(other, nside);
                if (theirs.Count == 0) continue;
                foreach (var (tri, edge, lo, hi) in Borders(m, side))
                {
                    var tr = data.Triangles[tri];
                    short current = edge switch { 0 => tr.EdgeLink_0_1, 1 => tr.EdgeLink_1_2, _ => tr.EdgeLink_2_0 };
                    if (current >= 0) continue;   // shared inside the mesh
                    int best = -1; float bestOverlap = 1f;
                    foreach (var o in theirs)
                    {
                        float overlap = MathF.Min(hi, o.Hi) - MathF.Max(lo, o.Lo);
                        if (overlap > bestOverlap) { bestOverlap = overlap; best = o.Tri; }
                    }
                    if (best < 0) continue;
                    short index = (short)data.EdgeLinks.Count;
                    data.EdgeLinks.Add(new EdgeLink { Unknown = 0, Mesh = other.Navmesh.ToLink<INavigationMeshGetter>(), TriangleIndex = (short)best });
                    switch (edge)
                    {
                        case 0: tr.EdgeLink_0_1 = index; tr.Flags |= NavmeshTriangle.Flag.EdgeLink_0_1; break;
                        case 1: tr.EdgeLink_1_2 = index; tr.Flags |= NavmeshTriangle.Flag.EdgeLink_1_2; break;
                        default: tr.EdgeLink_2_0 = index; tr.Flags |= NavmeshTriangle.Flag.EdgeLink_2_0; break;
                    }
                    links++;
                }
            }
        }
        return links;
    }

    /// <summary>
    /// Recast also finds standable patches on tables, pillar tops and ledges that nothing can reach; vanilla
    /// navmeshes have none. Keep the regions containing a kept point (start, doors) and any of at least 24 triangles.
    /// </summary>
    static (List<P3Float>, List<(int A, int B, int C)>) PruneIslands(List<P3Float> verts, List<(int A, int B, int C)> tris, List<P3Float> keep)
    {
        // A piece is kept if it has many triangles or covers a fair area (open land meshes in a few big triangles).
        const int minRegion = 24;
        const float minArea = 256f * 256f;
        var adj = Adjacency(tris);
        var comp = new int[tris.Count]; Array.Fill(comp, -1);
        var sizes = new List<int>();
        var areas = new List<float>();
        for (int i = 0; i < tris.Count; i++)
        {
            if (comp[i] >= 0) continue;
            int id = sizes.Count, size = 0;
            float area = 0;
            var stack = new Stack<int>([i]); comp[i] = id;
            while (stack.Count > 0)
            {
                int k = stack.Pop(); size++;
                area += MathF.Abs(CrossZ(verts[tris[k].A], verts[tris[k].B], verts[tris[k].C])) / 2;
                for (int e = 0; e < 3; e++)
                    if (adj[k, e] is var n && n >= 0 && comp[n] < 0) { comp[n] = id; stack.Push(n); }
            }
            sizes.Add(size);
            areas.Add(area);
        }
        var kept = new HashSet<int>(Enumerable.Range(0, sizes.Count).Where(c => sizes[c] >= minRegion || areas[c] >= minArea));
        foreach (var p in keep.Where(p => p != default))
            kept.Add(comp[Enumerable.Range(0, tris.Count).OrderBy(i => Dist2(Centroid(verts, tris[i]), p)).First()]);

        var remap = new Dictionary<int, int>();
        var newVerts = new List<P3Float>();
        var newTris = new List<(int, int, int)>();
        int Map(int v) { if (!remap.TryGetValue(v, out var m)) { m = newVerts.Count; newVerts.Add(verts[v]); remap[v] = m; } return m; }
        for (int i = 0; i < tris.Count; i++)
            if (kept.Contains(comp[i])) newTris.Add((Map(tris[i].A), Map(tris[i].B), Map(tris[i].C)));
        return (newVerts, newTris);
    }

    static float Dist2(P3Float a, P3Float b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z);

    static float CrossZ(P3Float a, P3Float b, P3Float c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    static P3Float Centroid(List<P3Float> v, (int A, int B, int C) t) =>
        new((v[t.A].X + v[t.B].X + v[t.C].X) / 3, (v[t.A].Y + v[t.B].Y + v[t.C].Y) / 3, (v[t.A].Z + v[t.B].Z + v[t.C].Z) / 3);

    /// <summary>For each triangle edge (0-1, 1-2, 2-0), the triangle sharing it, or -1.</summary>
    static int[,] Adjacency(List<(int A, int B, int C)> tris)
    {
        var result = new int[tris.Count, 3];
        var edges = new Dictionary<(int, int), List<(int Tri, int Edge)>>();
        for (int i = 0; i < tris.Count; i++)
        {
            var (a, b, c) = tris[i];
            int[][] es = [[a, b], [b, c], [c, a]];
            for (int e = 0; e < 3; e++)
            {
                result[i, e] = -1;
                var key = (Math.Min(es[e][0], es[e][1]), Math.Max(es[e][0], es[e][1]));
                if (!edges.TryGetValue(key, out var list)) edges[key] = list = [];
                list.Add((i, e));
            }
        }
        foreach (var list in edges.Values.Where(l => l.Count == 2))
        {
            result[list[0].Tri, list[0].Edge] = list[1].Tri;
            result[list[1].Tri, list[1].Edge] = list[0].Tri;
        }
        return result;
    }

    /// <summary>Lookup grid: divisor^2 cells (y-major), each an Int32 count then Int16 triangle indices.</summary>
    static void WriteGrid(NavigationMeshData data, List<P3Float> v, List<(int A, int B, int C)> tris)
    {
        int div = Math.Clamp((int)Math.Round(Math.Sqrt(tris.Count / 4.0)), 1, 12);
        var min = new P3Float(v.Min(p => p.X), v.Min(p => p.Y), v.Min(p => p.Z));
        var max = new P3Float(v.Max(p => p.X), v.Max(p => p.Y), v.Max(p => p.Z));
        float dx = Math.Max((max.X - min.X) / div, 1f), dy = Math.Max((max.Y - min.Y) / div, 1f);
        var cells = Enumerable.Range(0, div * div).Select(_ => new List<short>()).ToArray();
        for (int i = 0; i < tris.Count; i++)
        {
            var (a, b, c) = tris[i];
            float[] xs = [v[a].X, v[b].X, v[c].X], ys = [v[a].Y, v[b].Y, v[c].Y];
            int x0 = Math.Clamp((int)((xs.Min() - min.X) / dx), 0, div - 1), x1 = Math.Clamp((int)((xs.Max() - min.X) / dx), 0, div - 1);
            int y0 = Math.Clamp((int)((ys.Min() - min.Y) / dy), 0, div - 1), y1 = Math.Clamp((int)((ys.Max() - min.Y) / dy), 0, div - 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    cells[y * div + x].Add((short)i);
        }
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
            foreach (var cell in cells)
            {
                w.Write(cell.Count);
                foreach (var t in cell) w.Write(t);
            }
        data.NavmeshGridDivisor = (uint)div;
        data.MaxDistanceX = dx;
        data.MaxDistanceY = dy;
        data.Min = min;
        data.Max = max;
        data.NavmeshGrid = ms.ToArray();
    }

    /// <summary>The navmesh map entries made so far, taken out of the plugin: a plugin built in parts writes them once,
    /// all together (<see cref="InfoMapWith"/>), as its parts' merge keeps only one copy of the map.</summary>
    public List<NavigationMapInfo> TakeInfos()
    {
        if (infoMap is null) return [];
        var infos = infoMap.MapInfos.ToList();
        mod.NavigationMeshInfoMaps.Remove(infoMap);
        infoMap = null;
        return infos;
    }

    /// <summary>The navmesh map (an override of Skyrim's, with only these entries, as each DLC has) holding <paramref name="infos"/>.</summary>
    public static NavigationMeshInfoMap InfoMapWith(IEnumerable<NavigationMapInfo> infos)
    {
        var map = new NavigationMeshInfoMap(InfoMapKey, SkyrimRelease.SkyrimSE) { NavMeshVersion = NavmeshVersion, PreferredPathing = new PreferredPathing() };
        map.MapInfos.AddRange(infos);
        return map;
    }

    void AddInfo(NavigationMesh navm, Cell cell, List<P3Float> verts, List<IPlacedObjectGetter> doors) =>
        AddInfo(navm, new NavigationMapInfoCellParent { ParentCell = cell.ToLink<ICellGetter>(), Unused = 0 }, verts, doors);

    void AddInfo(NavigationMesh navm, ANavigationMapInfoParent parent, List<P3Float> verts, List<IPlacedObjectGetter> doors)
    {
        if (infoMap is null)
        {
            infoMap = new NavigationMeshInfoMap(InfoMapKey, SkyrimRelease.SkyrimSE) { NavMeshVersion = NavmeshVersion, PreferredPathing = new PreferredPathing() };
            mod.NavigationMeshInfoMaps.Add(infoMap);
        }
        var centre = new P3Float(verts.Average(p => p.X), verts.Average(p => p.Y), verts.Average(p => p.Z));
        var nearest = verts.OrderBy(p => (p.X - centre.X) * (p.X - centre.X) + (p.Y - centre.Y) * (p.Y - centre.Y) + (p.Z - centre.Z) * (p.Z - centre.Z)).First();
        var info = new NavigationMapInfo
        {
            NavigationMesh = navm.ToLink<INavigationMeshGetter>(),
            Unknown = 0,
            Point = nearest,
            PreferredMergesFlag = 0,
            Unknown2 = unchecked((int)Stamp),
            Parent = parent,
        };
        foreach (var d in doors) info.LinkedDoors.Add(new LinkedDoor { Unknown = DoorTag, Door = d.ToLink() });
        infoMap.MapInfos.Add(info);
    }
}
