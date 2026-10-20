using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Recast;
using DotRecast.Recast.Geom;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using System.Text;
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
    static RcConfig Config(RcPartition partition = RcPartition.WATERSHED, float coarse = 1, float outline = 1) => new(
        partition,
        cellSize: Env("CS", 8f) * coarse, cellHeight: Env("CH", 4f) * coarse,
        agentMaxSlope: Env("SLOPE", 50f), agentHeight: Env("HEIGHT", 128f), agentRadius: Env("RADIUS", 24f), agentMaxClimb: Env("CLIMB", 32f),
        regionMinSize: 8, regionMergeSize: 20,
        edgeMaxLen: 600f, edgeMaxError: 2f * outline,
        vertsPerPoly: 6,
        detailSampleDist: Env("DETAILDIST", 6f), detailSampleMaxError: Env("DETAILERR", 4f),
        filterLowHangingObstacles: Env("LOWHANG", 1f) > 0, filterLedgeSpans: Env("LEDGE", 1f) > 0, filterWalkableLowHeightSpans: Env("LOWHEIGHT", 1f) > 0,
        walkableAreaMod: new RcAreaModification(0x3f), buildMeshDetail: Env("DETAIL", 1f) > 0);

    /// <summary>Development override of a Recast setting (TM_NAV_name), for experiments.</summary>
    static float Env(string name, float fallback) =>
        float.TryParse(Environment.GetEnvironmentVariable("TM_NAV_" + name), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Meshes Recast's first try couldn't fully triangulate, made again (another partitioning, a finer outline or coarser voxels).</summary>
    public static int Retriangulated => retriangulated;
    static int retriangulated;

    /// <summary>Meshes still not fully triangulated after every retry (their Recast warnings are printed).</summary>
    public static int Untriangulated => untriangulated;
    static int untriangulated;

    /// <summary>The counts, for a build's log: the retries and the tries that made them whole.</summary>
    public static string RecastSummary =>
        $"navmeshes: {retriangulated} made again after Recast's first try, {untriangulated} still not whole after every retry, {turnedUpChosen} interiors from faces turned up (more actors on it)" +
        (retriangulated > 0 ? " (made whole by " + string.Join(", ", Enumerable.Range(1, Tries.Length - 1).Where(t => tryWins[t] > 0)
            .Select(t => $"{Tries[t].Partition.ToString().ToLowerInvariant()}{(Tries[t].Coarse > 1 ? " coarse" : "")}{(Tries[t].Error < 1 ? $" outline x{Tries[t].Error}" : "")}: {tryWins[t]}")) + ")" : "") +
        Environment.NewLine + "  " + FloorSummary;

    /// <summary>The tries in order: partitioning, voxel size (times the config's) and outline error (times the config's).</summary>
    static readonly (RcPartition Partition, float Coarse, float Error)[] Tries =
    [
        (RcPartition.WATERSHED, 1, 1), (RcPartition.LAYERS, 1, 1), (RcPartition.MONOTONE, 1, 1),
        (RcPartition.WATERSHED, 1, 0.5f), (RcPartition.LAYERS, 1, 0.5f), (RcPartition.MONOTONE, 1, 0.5f),
        (RcPartition.LAYERS, 1, 0), (RcPartition.MONOTONE, 1, 0),
        (RcPartition.WATERSHED, 2, 1), (RcPartition.LAYERS, 2, 1), (RcPartition.MONOTONE, 2, 1), (RcPartition.LAYERS, 2, 0), (RcPartition.MONOTONE, 2, 0),
    ];

    /// <summary>How many meshes each try made whole (by <see cref="Tries"/>' index; declared after it, as static fields are
    /// set in order).</summary>
    static readonly int[] tryWins = new int[Tries.Length];

    /// <summary>
    /// Recast's polygon mesh for the geometry, every region filled. Watershed partitioning (the best-shaped triangles)
    /// can leave a region's outline twisted ("rcBuildRegions: overlapping regions"), and simplifying an outline to
    /// within edgeMaxError voxels can fold it over itself where two walls of a region run close together; Recast then
    /// triangulates only part of it ("BuildPolyMesh: Bad triangulation Contour n", its own comment: "the contour is
    /// messed up. This sometimes happens if the contour simplification is too aggressive") and the rest of that floor
    /// gets no navmesh. A try is good when Recast warned of no bad triangulation or unmerged hole, no region lies over
    /// itself (<see cref="Overlapping"/>) and no walkable span of an outlined region lies under no polygon
    /// (<see cref="UncoveredFloor"/>, measured on the mesh itself); otherwise the mesh is made again: layer partitioning, then monotone (whose regions
    /// can't overlap), then the same with half the outline error and with none (the outline follows the voxels'
    /// edges, which can't cross). A retry can fail where watershed didn't (2026-10-16: layers threw "rcBuildContours:
    /// Multiple outlines for region 13" in The Abbey of Asmiameusessa), so with no good try the mesh covering the most
    /// is kept. Watershed can also throw ("rcBuildContours: Bad outline for region n, contour simplification is likely
    /// too aggressive": an Oblivion house and two exterior cells, 2026-10-17), and a column of more than 62 stacked
    /// surfaces can't be built at all ("Heightfield has too many layers"); last, the voxels are made twice as coarse,
    /// which joins surfaces closer than a voxel. DotRecast prints its warnings straight to the console, so each try's
    /// are kept (<see cref="RecastOutput"/>) and printed only for a mesh no try could make whole.
    /// </summary>
    static (RcPolyMesh Mesh, RcPolyMeshDetail? Detail)? BuildMesh(IRcInputGeomProvider geom, Func<RcPartition, float, float, RcBuilderConfig> config, Func<float, float, float?>? land = null, Lazy<WoundInput>? wound = null,
        float margin = 0, Hazards? hazards = null)
    {
        (RcPolyMesh Mesh, RcPolyMeshDetail? Detail)? best = null;
        FloorCheck bestFloor = default;
        // The spans each voxel size's tries make unwalkable (BuildOverLand), found by the first try of that size.
        var marks = new Dictionary<float, HashSet<long>>();
        int bestBad = int.MaxValue, bestOverlapping = 0, bestTry = -1;
        double bestCovered = 0;
        string bestWarnings = "";
        Exception? error = null;
        for (int t = 0; t < Tries.Length; t++)
        {
            var (partition, coarse, outline) = Tries[t];
            try
            {
                var bc = config(partition, coarse, outline);
                var r = RecastOutput.Keep(() => land is null && wound is null && hazards is null ? new RcBuilder().Build(geom, bc, true) : BuildOverLand(geom, bc, land, wound, marks, hazards), out var warnings);
                if (r.Mesh is null || r.ContourSet is null || r.CompactHeightfield is null) { if (t == 0) return r.Mesh is null ? null : (r.Mesh, r.MeshDetail); continue; }
                int overlapping = Overlapping(r.CompactHeightfield);
                if (overlapping > 0) warnings += $"{overlapping} regions lie over themselves ({partition}){Environment.NewLine}";
                int bad = RecastOutput.Bad(warnings) + overlapping;
                // Only the floor the mesh is kept for: not the margin round an exterior cell (clipped off).
                var floor = UncoveredFloor(r.CompactHeightfield, r.ContourSet, r.Mesh, r.MeshDetail, (int)MathF.Round(margin / r.CompactHeightfield.cs));
                if (Trace)
                {
                    Console.WriteLine($"  recast try {t} ({partition}, voxels x{coarse}, outline x{outline}) at {Where(r.Mesh)}: {bad} bad ({overlapping} over themselves); {floor}");
                    if (floor.None > 0) Console.Write(RegionDetail(r.CompactHeightfield, r.ContourSet, r.Mesh, floor.Where));
                }
                // Floor left without navmesh: under no polygon, in a region Recast made no outline for, or with only
                // another floor's polygon over or under it (2026-10-19, nine Bitter Coast cells of Morrowind: every one
                // of those was in a region with no outline, 0.05% of the floor, pockets on rock tops and under overhangs).
                long lost = floor.None + floor.Dropped + floor.Far;
                // A try in the config's own voxels with nothing wrong is the mesh. A coarser one is never taken as whole
                // on its own count: its voxels are four times the area, and its outline loses the narrow floor the agent's
                // radius takes off (the same nine cells: one whole coarse covered 914 spans of 32 units, 0.94 million
                // square units, where the fine tries covered 4,810 of 16, 1.23 million, short only one span).
                if (bad == 0 && lost == 0 && coarse == 1)
                {
                    if (t > 0) Interlocked.Increment(ref retriangulated);
                    Interlocked.Increment(ref tryWins[t]);
                    Record(floor, r.Mesh);
                    return (r.Mesh, r.MeshDetail);
                }
                // Otherwise the best of the tries: none of its regions over themselves, then the most floor covered (in
                // square units), then the fewest warnings.
                double covered = floor.On * (double)r.CompactHeightfield.cs * r.CompactHeightfield.cs;
                if (best is null || (overlapping == 0 && bestOverlapping > 0) || ((overlapping == 0) == (bestOverlapping == 0) && (covered > bestCovered || (covered == bestCovered && bad < bestBad))))
                    (bestFloor, bestBad, best, bestWarnings, bestOverlapping, bestCovered, bestTry) = (floor, bad, (r.Mesh, r.MeshDetail), warnings, overlapping, covered, t);
            }
            catch (Exception e) { error ??= e; }
        }
        if (best is null) throw error ?? new InvalidOperationException("Recast made no mesh");
        Record(bestFloor, best.Value.Mesh);
        long bestLost = bestFloor.None + bestFloor.Dropped + bestFloor.Far;
        // Whole only in coarser voxels, where no finer try could be made at all (one threw) or covered as much.
        if (bestLost == 0 && bestBad == 0)
        {
            Interlocked.Increment(ref retriangulated);
            Interlocked.Increment(ref tryWins[bestTry]);
            return best;
        }
        Interlocked.Increment(ref untriangulated);
        var (bp, bcoarse, boutline) = Tries[bestTry];
        Console.Write($"Recast, still after every retry at {Where(best.Value.Mesh)} ({bestLost} walkable spans with no polygon at their height; kept try {bestTry}, {bp} voxels x{bcoarse} outline x{boutline}): {bestFloor}" +
            $"{(bestFloor.DroppedAt.Length > 0 ? $"; in regions without an outline at {bestFloor.DroppedAt}" : "")}{(bestFloor.FarAt.Length > 0 ? $"; far at {bestFloor.FarAt}" : "")}{Environment.NewLine}{bestWarnings}");
        return best;
    }

    /// <summary>
    /// Recast's build with every voxel span whose top lies more than <see cref="BuriedDepth"/> under the land at its
    /// column (<paramref name="land"/>: Skyrim x, y to the land's height there, null where there is none) made
    /// unwalkable before the heightfield is filtered. The land is a height field, so nothing under it can be walked to;
    /// a placed object's triangle partly under it kept its buried part as floor (2026-10-19, Morrowind at -37376,-69952:
    /// a Velothi tomb entrance's base plate, 760 units across at 227, one corner where the land dips to 304, became
    /// navmesh 380 units under the land at 608; leaving out only triangles buried at all three corners left 0.39% of
    /// Morrowind's exterior navmesh more than 128 under the land). The spans stay as solid, so what stands on them
    /// keeps its headroom as it was.
    /// With <paramref name="wound"/> (the input as wound), a span shut inside something is made unwalkable too
    /// (<see cref="WoundInput.Shut"/>: from 64 units over it, the ray up and four level ones all meet faces from
    /// behind). Exteriors' faces are all turned up for Recast (their winding is either way), so the land inside an
    /// open-bottomed shell was floor (2026-10-19, measured over each navmesh against its input as wound, Morrowind's
    /// Bitter Coast: 0.74% of the navmesh shut in, the land inside the Bitter Coast's rocks, terrain_rock_bc_18 over
    /// -37952,-71232 with its top 224 units over the land and no bottom). Only a span with another above it in its
    /// column can be shut in, so the rays are cast for those alone.
    /// </summary>
    /// <remarks>The spans made unwalkable depend only on the voxels, the same for every try of one voxel size, so they
    /// are found once a size (<paramref name="marks"/>: by cell size, each span as column and top) and the later
    /// tries take them.</remarks>
    static RcBuilderResult BuildOverLand(IRcInputGeomProvider geom, RcBuilderConfig bc, Func<float, float, float?>? land, Lazy<WoundInput>? wound,
        Dictionary<float, HashSet<long>> marks, Hazards? hazards = null)
    {
        var ctx = new RcContext();
        var solid = RcVoxelizations.BuildSolidHeightfield(ctx, geom, bc);
        static long Key(int x, int z, int top) => x | ((long)z << 16) | ((long)top << 32);
        var marking = System.Diagnostics.Stopwatch.StartNew();
        if (marks.TryGetValue(solid.cs, out var known))
        {
            for (int z = 0; z < solid.height; z++)
                for (int x = 0; x < solid.width; x++)
                    for (var s = solid.spans[x + z * solid.width]; s is not null; s = s.next)
                        if (s.area != RcRecast.RC_NULL_AREA && known.Contains(Key(x, z, s.smax))) s.area = RcRecast.RC_NULL_AREA;
        }
        else
        {
            known = marks[solid.cs] = [];
            long cut = 0, shutIn = 0, tested = 0, hurt = 0;
            for (int z = 0; z < solid.height; z++)
                for (int x = 0; x < solid.width; x++)
                {
                    if (solid.spans[x + z * solid.width] is not { } first) continue;
                    // Recast's z is Skyrim's -y.
                    float wx = solid.bmin.X + (x + 0.5f) * solid.cs, wy = -(solid.bmin.Z + (z + 0.5f) * solid.cs);
                    float? ground = land?.Invoke(wx, wy);
                    for (var s = first; s is not null; s = s.next)
                    {
                        if (s.area == RcRecast.RC_NULL_AREA) continue;
                        float top = solid.bmin.Y + s.smax * solid.ch;
                        if (ground is { } g && top < g - BuriedDepth) { s.area = RcRecast.RC_NULL_AREA; cut++; known.Add(Key(x, z, s.smax)); }
                        else if (hazards is not null && hazards.Covers(wx, wy, top)) { s.area = RcRecast.RC_NULL_AREA; hurt++; known.Add(Key(x, z, s.smax)); }
                        else if (wound is not null && s.next is not null)
                        {
                            tested++;
                            if (wound.Value.Shut(new Vector3(wx, wy, top))) { s.area = RcRecast.RC_NULL_AREA; shutIn++; known.Add(Key(x, z, s.smax)); }
                        }
                    }
                }
            // TM_NAV_MARKS=x,y (dev aid): that column's spans, the land there, and what each shut-in ray met.
            if (Environment.GetEnvironmentVariable("TM_NAV_MARKS")?.Split(',') is [var mxs, var mys] && float.TryParse(mxs, out var mx) && float.TryParse(mys, out var my))
            {
                int qx = (int)MathF.Floor((mx - solid.bmin.X) / solid.cs), qz = (int)MathF.Floor((-my - solid.bmin.Z) / solid.cs);
                if (qx >= 0 && qz >= 0 && qx < solid.width && qz < solid.height)
                {
                    float wx = solid.bmin.X + (qx + 0.5f) * solid.cs, wy = -(solid.bmin.Z + (qz + 0.5f) * solid.cs);
                    var sb = new StringBuilder($"  marks at {wx:F0},{wy:F0} (voxels of {solid.cs}): land {land?.Invoke(wx, wy)?.ToString("F0") ?? "none"};");
                    for (var s = solid.spans[qx + qz * solid.width]; s is not null; s = s.next)
                    {
                        float top = solid.bmin.Y + s.smax * solid.ch;
                        sb.Append($" span {solid.bmin.Y + s.smin * solid.ch:F0}-{top:F0} area {s.area}{(known.Contains(Key(qx, qz, s.smax)) ? " made unwalkable" : "")}");
                        if (wound is not null && s.next is not null)
                        {
                            var o = new Vector3(wx, wy, top + 64);
                            string Ray(Vector3 d) => wound.Value.Cast(o, d, 4096) is { } h ? $"{h.T:F0}{(h.Back ? " back" : " front")}" : "none";
                            sb.Append($" (rays from {top + 64:F0}: up {Ray(Vector3.UnitZ)}, east {Ray(Vector3.UnitX)}, west {Ray(-Vector3.UnitX)}, north {Ray(Vector3.UnitY)}, south {Ray(-Vector3.UnitY)})");
                        }
                        sb.Append(';');
                    }
                    Console.WriteLine(sb.ToString());
                }
            }
            Interlocked.Add(ref buriedSpans, cut);
            Interlocked.Add(ref shutSpans, shutIn);
            Interlocked.Add(ref hazardSpans, hurt);
            Interlocked.Add(ref shutTested, tested);
            if (wound is { IsValueCreated: true }) wound.Value.Count();
        }
        Interlocked.Add(ref markingTicks, marking.ElapsedTicks);
        var recast = System.Diagnostics.Stopwatch.StartNew();
        var built = new RcBuilder().Build(ctx, bc.tileX, bc.tileZ, geom, bc.cfg, solid, true);
        Interlocked.Add(ref recastTicks, recast.ElapsedTicks);
        return built;
    }

    /// <summary>Spans tested for being shut in, and the time (all threads) spent marking spans and in Recast.</summary>
    static long shutTested, markingTicks, recastTicks;

    /// <summary>For the build's log: the shut-in test's work and where the time went.</summary>
    public static string MarkingSummary =>
        $"under-land and shut-in marking: {Interlocked.Read(ref shutTested):N0} spans tested, {WoundInput.Rays:N0} rays, {WoundInput.Tests:N0} triangles tested; " +
        $"{Interlocked.Read(ref markingTicks) / (double)System.Diagnostics.Stopwatch.Frequency:F0} s marking, {Interlocked.Read(ref recastTicks) / (double)System.Diagnostics.Stopwatch.Frequency:F0} s in Recast (all threads)";

    /// <summary>Walkable voxel spans made unwalkable as under the land (<see cref="BuildOverLand"/>), once a voxel size.</summary>
    public static long BuriedSpans => Interlocked.Read(ref buriedSpans);
    static long buriedSpans;

    /// <summary>Walkable voxel spans made unwalkable as shut inside something (<see cref="BuildOverLand"/>), once a voxel
    /// size.</summary>
    public static long ShutSpans => Interlocked.Read(ref shutSpans);
    static long shutSpans;

    /// <summary>Every kept mesh's floor check added up, and the meshes with the most floor without navmesh and the most
    /// far from it.</summary>
    static FloorCheck floorTotal;
    static readonly List<(long Count, string Where)> worstNone = [], worstFar = [], worstDropped = [], worstOff = [];
    static readonly object FloorGate = new();

    static void Record(FloorCheck floor, RcPolyMesh mesh)
    {
        lock (FloorGate)
        {
            floorTotal += floor;
            if (floor.None > 0) Keep(worstNone, (floor.None, Where(mesh)));
            if (floor.Off > 0) Keep(worstOff, (floor.Off, $"{Where(mesh)} (e.g. {floor.OffAt})"));
            if (floor.Dropped > 0) Keep(worstDropped, (floor.Dropped, $"{Where(mesh)} (e.g. {floor.DroppedAt})"));
            if (floor.Far > 0) Keep(worstFar, (floor.Far, $"{Where(mesh)} of {floor.Walkable} (e.g. {floor.FarAt})"));
        }
        static void Keep(List<(long Count, string Where)> list, (long, string) item)
        {
            list.Add(item);
            list.Sort((a, b) => b.Count.CompareTo(a.Count));
            if (list.Count > 5) list.RemoveAt(5);
        }
    }

    /// <summary>The floor check of every mesh kept so far, for a build's log.</summary>
    public static string FloorSummary
    {
        get
        {
            lock (FloorGate)
            {
                var f = floorTotal;
                string Pct(long n) => f.Walkable > 0 ? $"{100.0 * n / f.Walkable:F2}%" : "0%";
                return $"navmesh over the floor ({Interlocked.Read(ref trianglesMade):N0} triangles made, {BuriedLeftOut:N0} buried input triangles left out, {BuriedSpans:N0} voxel spans under the land and {ShutSpans:N0} shut inside something and {HazardSpans:N0} on or under a hazard made unwalkable): {f.Walkable:N0} walkable spans; on a polygon {Pct(f.On)}, off it by up to the agent's height {Pct(f.Off)} ({f.Off:N0}), " +
                    $"further {Pct(f.Far)} ({f.Far:N0}), under no polygon {f.None:N0} ({Pct(f.None)}), in regions too small for Recast to outline {f.Dropped:N0} ({Pct(f.Dropped)}); " +
                    $"polygons whose detail triangles fall short of them {f.LostPolygons:N0} ({f.LostArea:N0} voxels)" +
                    (worstNone.Count > 0 ? $"; most without: {string.Join(", ", worstNone.Select(w => $"{w.Count} at {w.Where}"))}" : "") +
                    (worstFar.Count > 0 ? $"; most far: {string.Join(", ", worstFar.Select(w => $"{w.Count} at {w.Where}"))}" : "") +
                    (worstOff.Count > 0 ? $"; most off: {string.Join(", ", worstOff.Select(w => $"{w.Count} at {w.Where}"))}" : "") +
                    (worstDropped.Count > 0 ? $"; most in regions without an outline: {string.Join(", ", worstDropped.Select(w => $"{w.Count} at {w.Where}"))}" : "") +
                    (ExteriorSurface.On ? Environment.NewLine + "  " + ExteriorSurface.Summary : "") +
                    (Interlocked.Read(ref markingTicks) > 0 ? Environment.NewLine + "  " + MarkingSummary : "");
            }
        }
    }

    /// <summary>For the trace: the regions named in a floor check's places ("r12"): their spans, contours and polygons.</summary>
    static string RegionDetail(RcCompactHeightfield chf, RcContourSet cset, RcPolyMesh mesh, string where)
    {
        var sb = new StringBuilder();
        foreach (int reg in System.Text.RegularExpressions.Regex.Matches(where, @" r(\d+)\)").Select(m => int.Parse(m.Groups[1].Value)).Distinct())
        {
            int spans = 0;
            foreach (var s in chf.spans) if (s.reg == reg) spans++;
            int polys = 0;
            for (int p = 0; p < mesh.npolys; p++) if (mesh.regs[p] == reg) polys++;
            var conts = cset.conts.Where(c => c.reg == reg).Select(c => $"{c.nverts} vertices ({c.nrverts} raw)").ToList();
            sb.AppendLine($"    region {reg}: {spans} spans, {polys} polygons, contours: {(conts.Count > 0 ? string.Join("; ", conts) : "none")}");
        }
        return sb.ToString();
    }

    /// <summary>TM_MEM=1: the memory in use as exterior navmeshes are made.</summary>
    public static readonly bool MemoryTrace = Environment.GetEnvironmentVariable("TM_MEM") == "1";

    /// <summary>TM_NAV_TRACE=1: every Recast try's coverage and warnings.</summary>
    static readonly bool Trace = Environment.GetEnvironmentVariable("TM_NAV_TRACE") == "1";

    /// <summary>Where a mesh lies, for the log: its middle in Skyrim units and (for an exterior) the cell.</summary>
    static string Where(RcPolyMesh mesh)
    {
        float x = (mesh.bmin.X + mesh.bmax.X) / 2, y = -(mesh.bmin.Z + mesh.bmax.Z) / 2;
        return $"{x:F0},{y:F0} (cell {MathF.Floor(x / CellUnits)},{MathF.Floor(y / CellUnits)})";
    }

    /// <summary>
    /// Console output that, on a thread building a Recast mesh, is kept for that try instead of written: DotRecast's
    /// RcContext writes its warnings with Console.WriteLine and its errors with Console.Error (the detail mesh's
    /// "delaunayHull: Removing dangling face", whose faces it drops itself) and can't be given another log. Installed
    /// over Console.Out and Console.Error the first time a mesh is built; other threads' output passes straight through.
    /// </summary>
    sealed class RecastOutput(TextWriter inner) : TextWriter
    {
        [ThreadStatic] static StringBuilder? capture;
        static readonly object Gate = new();
        static bool installed;

        public override Encoding Encoding => inner.Encoding;
        public override void Write(char value) { if (capture is { } c) c.Append(value); else inner.Write(value); }
        public override void Write(string? value) { if (capture is { } c) c.Append(value); else inner.Write(value); }
        public override void Write(char[] buffer, int index, int count) { if (capture is { } c) c.Append(buffer, index, count); else inner.Write(buffer, index, count); }
        public override void WriteLine(string? value) { if (capture is { } c) c.Append(value).Append(Environment.NewLine); else inner.WriteLine(value); }
        public override void Flush() => inner.Flush();

        /// <summary>Runs <paramref name="build"/> with this thread's console output kept in <paramref name="warnings"/>.</summary>
        public static T Keep<T>(Func<T> build, out string warnings)
        {
            lock (Gate)
                if (!installed) { Console.SetOut(new RecastOutput(Console.Out)); Console.SetError(new RecastOutput(Console.Error)); installed = true; }
            var outer = capture;
            var kept = capture = new StringBuilder();
            T result;
            try { result = build(); }
            finally { capture = outer; }
            warnings = kept.ToString();
            return result;
        }

        /// <summary>How many of Recast's warnings mean part of a floor got no polygons: a contour it triangulated only
        /// partly, or a hole it couldn't join to its outline.</summary>
        public static int Bad(string warnings)
        {
            int n = 0;
            foreach (var line in warnings.Split('\n'))
                if (line.Contains("Bad triangulation", StringComparison.Ordinal) || line.Contains("Failed to find merge points", StringComparison.Ordinal)) n++;
            return n;
        }
    }

    /// <summary>
    /// How many regions lie over themselves: a column of the heightfield with two of its spans in the same region, as
    /// where a stair turns back over its own foot or a ledge hangs over the floor it rose from. Recast's watershed
    /// only warns of these ("rcBuildRegions: n overlapping regions"; the split its comment promises isn't there) and
    /// monotone doesn't say, but the region's outline then runs over both floors and crosses itself, so a part of
    /// one of them gets no polygons or the outline can't be traced at all. Layer partitioning keeps such floors apart.
    /// </summary>
    static int Overlapping(RcCompactHeightfield chf)
    {
        HashSet<int>? over = null;
        foreach (var c in chf.cells)
            for (int i = c.index, end = c.index + c.count; i < end; i++)
            {
                int r = chf.spans[i].reg;
                if (r == 0 || (r & RcRecast.RC_BORDER_REG) != 0) continue;
                for (int j = i + 1; j < end; j++)
                    if (chf.spans[j].reg == r) (over ??= []).Add(r);
            }
        return over?.Count ?? 0;
    }

    /// <summary>
    /// The walkable floor no polygon of the mesh covers: each span of the heightfield in a region (the floor Recast found
    /// walkable, after the agent's radius was taken off) inside the tile is looked for under the polygons, at its own
    /// height (within a climb). An outline is simplified to within maxError voxels of the floor's edge, so a polygon
    /// within that (and a voxel) of the span covers it. Spans of a region Recast made no outline for (one whose outline
    /// simplifies to fewer than three corners: a few voxels against the tile's edge, measured 1 to 39 spans on
    /// Solstheim) are counted apart, as Dropped: Recast leaves them out by design.
    /// </summary>
    internal readonly record struct FloorCheck(long Walkable, long On, long Off, long Far, long None, long Dropped, int WorstOff, string Where, long LostPolygons = 0, double LostArea = 0, string FarAt = "", string DroppedAt = "", string OffAt = "")
    {
        public static FloorCheck operator +(FloorCheck a, FloorCheck b) =>
            new(a.Walkable + b.Walkable, a.On + b.On, a.Off + b.Off, a.Far + b.Far, a.None + b.None, a.Dropped + b.Dropped, Math.Max(a.WorstOff, b.WorstOff), (a.Where ?? "").Length > 0 ? a.Where! : b.Where ?? "", a.LostPolygons + b.LostPolygons, a.LostArea + b.LostArea, (a.FarAt ?? "").Length > 0 ? a.FarAt! : b.FarAt ?? "", (a.DroppedAt ?? "").Length > 0 ? a.DroppedAt! : b.DroppedAt ?? "", (a.OffAt ?? "").Length > 0 ? a.OffAt! : b.OffAt ?? "");

        public override string ToString() =>
            $"floor: {Walkable} walkable spans, {On} on a polygon, {Off} off it (at most {WorstOff} voxels), {Far} far from it, {None} under no polygon, {Dropped} in regions without an outline; detail triangles short of their polygon: {LostPolygons} ({LostArea:F0} voxels) {Where}";
    }

    static FloorCheck UncoveredFloor(RcCompactHeightfield chf, RcContourSet cset, RcPolyMesh mesh, RcPolyMeshDetail? detail, int skip = 0)
    {
        // The spans checked: inside the tile's border, and <paramref name="skip"/> more voxels in (the margin an exterior
        // cell's mesh is clipped off). The mesh's coordinates start at the border, so its faces are moved by skip.
        int b = chf.borderSize + skip, w = chf.width - 2 * b, h = chf.height - 2 * b, climb = chf.walkableClimb;
        double reach = Math.Ceiling(cset.maxError) + 1;
        var outlined = new HashSet<int>(cset.conts.Where(c => c.nverts >= 3).Select(c => c.reg));
        // The faces the navmesh is written from (SkyrimTriangles), in the tile's voxels (x, height, z): the detail
        // mesh's triangles (world space), or the polygons, whose fan from the first corner the navmesh's triangles are.
        var faces = new List<double[]>();
        // A polygon's detail triangles cover it whole unless Recast dropped some ("delaunayHull: Removing dangling
        // face"): their area on the x-z plane against the polygon's.
        long lostPolygons = 0; double lostArea = 0;
        if (detail is { nmeshes: > 0 })
            for (int m = 0; m < detail.nmeshes; m++)
            {
                int vb = detail.meshes[m * 4], tb = detail.meshes[m * 4 + 2], tc = detail.meshes[m * 4 + 3];
                double triArea = 0;
                for (int t = 0; t < tc; t++)
                {
                    var face = new double[9];
                    for (int k = 0; k < 3; k++)
                    {
                        int v = (vb + detail.tris[(tb + t) * 4 + k]) * 3;
                        face[k * 3] = (detail.verts[v] - mesh.bmin.X) / mesh.cs;
                        face[k * 3 + 1] = (detail.verts[v + 1] - mesh.bmin.Y) / mesh.ch;
                        face[k * 3 + 2] = (detail.verts[v + 2] - mesh.bmin.Z) / mesh.cs;
                    }
                    triArea += Math.Abs((face[3] - face[0]) * (face[8] - face[2]) - (face[6] - face[0]) * (face[5] - face[2])) / 2;
                    faces.Add(face);
                }
                if (m < mesh.npolys)
                {
                    int at = m * mesh.nvp * 2, n = 0;
                    while (n < mesh.nvp && mesh.polys[at + n] != 0xffff) n++;
                    double polyArea = 0;
                    for (int i = 0, j = n - 1; i < n; j = i++)
                    {
                        int vi = mesh.polys[at + i] * 3, vj = mesh.polys[at + j] * 3;
                        polyArea += (double)mesh.verts[vj] * mesh.verts[vi + 2] - (double)mesh.verts[vi] * mesh.verts[vj + 2];
                    }
                    polyArea = Math.Abs(polyArea) / 2;
                    if (triArea < polyArea - 0.01 - polyArea * 1e-4) { lostPolygons++; lostArea += polyArea - triArea; }
                }
            }
        else
            for (int p = 0; p < mesh.npolys; p++)
            {
                int at = p * mesh.nvp * 2, n = 0;
                while (n < mesh.nvp && mesh.polys[at + n] != 0xffff) n++;
                var face = new double[n * 3];
                for (int k = 0; k < n; k++)
                    for (int e = 0; e < 3; e++) face[k * 3 + e] = mesh.verts[mesh.polys[at + k] * 3 + e];
                faces.Add(face);
            }
        // Each column's surfaces: the face's height there (its fan of triangles from the first corner), or at the
        // nearest point of its edge for a column just outside it.
        var surfaces = new List<double>?[w * h];
        var xs = new double[Math.Max(mesh.nvp, 3)];
        var ys = new double[xs.Length];
        var zs = new double[xs.Length];
        foreach (var face in faces)
        {
            int n = face.Length / 3;
            double x0 = double.MaxValue, x1 = double.MinValue, z0 = double.MaxValue, z1 = double.MinValue;
            for (int k = 0; k < n; k++)
            {
                xs[k] = face[k * 3] - skip; ys[k] = face[k * 3 + 1]; zs[k] = face[k * 3 + 2] - skip;
                x0 = Math.Min(x0, xs[k]); x1 = Math.Max(x1, xs[k]); z0 = Math.Min(z0, zs[k]); z1 = Math.Max(z1, zs[k]);
            }
            for (int cz = Math.Max(0, (int)Math.Floor(z0 - reach)); cz < Math.Min(h, (int)Math.Ceiling(z1 + reach)); cz++)
                for (int cx = Math.Max(0, (int)Math.Floor(x0 - reach)); cx < Math.Min(w, (int)Math.Ceiling(x1 + reach)); cx++)
                {
                    double px = cx + 0.5, pz = cz + 0.5;
                    double? at2 = null;
                    for (int i = 1; i + 1 < n && at2 is null; i++)
                    {
                        // Barycentric weights of (px, pz) in the triangle 0, i, i + 1.
                        double d = (zs[i] - zs[i + 1]) * (xs[0] - xs[i + 1]) + (xs[i + 1] - xs[i]) * (zs[0] - zs[i + 1]);
                        if (Math.Abs(d) < 1e-9) continue;
                        double a = ((zs[i] - zs[i + 1]) * (px - xs[i + 1]) + (xs[i + 1] - xs[i]) * (pz - zs[i + 1])) / d;
                        double c = ((zs[i + 1] - zs[0]) * (px - xs[i + 1]) + (xs[0] - xs[i + 1]) * (pz - zs[i + 1])) / d;
                        if (a >= -1e-9 && c >= -1e-9 && a + c <= 1 + 1e-9) at2 = a * ys[0] + c * ys[i] + (1 - a - c) * ys[i + 1];
                    }
                    if (at2 is null)
                    {
                        double near = double.MaxValue, y = 0;
                        for (int i = 0, j = n - 1; i < n; j = i++)
                        {
                            double ex = xs[i] - xs[j], ez = zs[i] - zs[j], len = ex * ex + ez * ez;
                            double t = len > 0 ? Math.Clamp(((px - xs[j]) * ex + (pz - zs[j]) * ez) / len, 0, 1) : 0;
                            double dx = xs[j] + t * ex - px, dz = zs[j] + t * ez - pz;
                            if (dx * dx + dz * dz < near) { near = dx * dx + dz * dz; y = ys[j] + t * (ys[i] - ys[j]); }
                        }
                        if (near <= reach * reach) at2 = y;
                    }
                    if (at2 is { } s) (surfaces[cx + cz * w] ??= []).Add(s);
                }
        }
        // Each walkable span: on the floor (a surface within a climb), off it (the nearest surface within the agent's
        // height: the navmesh is there but above or below the ground), far (a polygon over or under it, but further than
        // that) or none (no polygon in its column at all: floor without navmesh).
        int walkable = 0, on = 0, off = 0, far = 0, none = 0, dropped = 0, worst = 0;
        var where = new List<string>();
        var farAt = new List<string>();
        var droppedAt = new List<string>();
        var offAt = new List<string>();
        for (int cz = 0; cz < h; cz++)
            for (int cx = 0; cx < w; cx++)
            {
                var c = chf.cells[cx + b + (cz + b) * chf.width];
                for (int i = c.index, end = c.index + c.count; i < end; i++)
                {
                    int reg = chf.spans[i].reg, y = chf.spans[i].y;
                    if (reg == 0 || (reg & RcRecast.RC_BORDER_REG) != 0) continue;
                    walkable++;
                    double gap = surfaces[cx + cz * w] is { } list ? list.Min(s => Math.Abs(s - y)) : double.MaxValue;
                    if (gap <= climb) on++;
                    else if (gap <= chf.walkableHeight)
                    {
                        off++; worst = Math.Max(worst, (int)Math.Round(gap));
                        if (offAt.Count < 3) offAt.Add($"{chf.bmin.X + (cx + b + 0.5f) * chf.cs:F0},{-(chf.bmin.Z + (cz + b + 0.5f) * chf.cs):F0},{chf.bmin.Y + y * chf.ch:F0} by {gap * chf.ch:F0}");
                    }
                    else if (gap < double.MaxValue)
                    {
                        far++;
                        // Where (Skyrim units), and how far off the nearest navmesh over or under it is.
                        if (farAt.Count < 3) farAt.Add($"{chf.bmin.X + (cx + b + 0.5f) * chf.cs:F0},{-(chf.bmin.Z + (cz + b + 0.5f) * chf.cs):F0},{chf.bmin.Y + y * chf.ch:F0} by {gap * chf.ch:F0}");
                        // TM_NAV_TRACE: the first such span's region, the polygons over its column and how near its own
                        // region's polygons come to it.
                        if (Trace && far == 1)
                        {
                            var sb = new StringBuilder($"    far span at voxel {cx},{cz} height {y} region {reg} (its region outlined: {outlined.Contains(reg)}):");
                            double nearOwn = double.MaxValue; int own = 0;
                            for (int p = 0; p < mesh.npolys; p++)
                            {
                                int at = p * mesh.nvp * 2, n = 0;
                                while (n < mesh.nvp && mesh.polys[at + n] != 0xffff) n++;
                                bool inside = false; double edge = double.MaxValue;
                                for (int k = 0, j = n - 1; k < n; j = k++)
                                {
                                    int vk = mesh.polys[at + k] * 3, vj = mesh.polys[at + j] * 3;
                                    double xk = mesh.verts[vk] - skip, zk = mesh.verts[vk + 2] - skip, xj = mesh.verts[vj] - skip, zj = mesh.verts[vj + 2] - skip;
                                    if ((zk > cz + 0.5) != (zj > cz + 0.5) && cx + 0.5 < (xj - xk) * (cz + 0.5 - zk) / (zj - zk) + xk) inside = !inside;
                                    double ex = xk - xj, ez = zk - zj, len = ex * ex + ez * ez;
                                    double tt = len > 0 ? Math.Clamp(((cx + 0.5 - xj) * ex + (cz + 0.5 - zj) * ez) / len, 0, 1) : 0;
                                    edge = Math.Min(edge, Math.Sqrt(Math.Pow(xj + tt * ex - cx - 0.5, 2) + Math.Pow(zj + tt * ez - cz - 0.5, 2)));
                                }
                                if (mesh.regs[p] == reg) { own++; nearOwn = Math.Min(nearOwn, inside ? 0 : edge); }
                                if (inside)
                                    sb.Append($" polygon {p} of region {mesh.regs[p]} heights {string.Join("/", Enumerable.Range(0, n).Select(k => mesh.verts[mesh.polys[at + k] * 3 + 1]))};");
                            }
                            sb.Append($" its region's {own} polygons come within {nearOwn:F1} voxels");
                            Console.WriteLine(sb.ToString());
                        }
                    }
                    else if (!outlined.Contains(reg))
                    {
                        dropped++;
                        if (droppedAt.Count < 3) droppedAt.Add($"{chf.bmin.X + (cx + b + 0.5f) * chf.cs:F0},{-(chf.bmin.Z + (cz + b + 0.5f) * chf.cs):F0},{chf.bmin.Y + y * chf.ch:F0} r{reg}");
                    }
                    else { none++; if (where.Count < 6) where.Add($"({cx},{cz},{y} r{reg})"); }
                }
            }
        return new(walkable, on, off, far, none, dropped, worst, string.Join(" ", where), lostPolygons, lostArea, string.Join(" ", farAt), string.Join(" ", droppedAt), string.Join(" ", offAt));
    }

    public sealed record Result(NavigationMesh Navmesh, int Triangles, int Vertices, int DoorTriangles);

    /// <summary>
    /// Builds the navmesh for an interior cell from its walkable geometry (Skyrim cell space, polygons wound
    /// counter-clockwise around their normals) and links load doors to the nearest triangle in front of them.
    /// </summary>
    public Result BuildInterior(Cell cell, IReadOnlyList<(Vector3[] Points, Vector3 Normal)> polygons, IEnumerable<IPlacedObjectGetter> loadDoors, P3Float? keepAround = null, IEnumerable<P3Float>? keepAlso = null,
        Func<P3Float, bool>? underwater = null, Hazards? hazards = null) =>
        BeginInterior(cell, polygons, loadDoors, keepAround, keepAlso, underwater, background: false, hazards: hazards).Finish();

    /// <summary>
    /// An interior's navmesh from its faces as wound (a ceiling or an underside is an obstacle), or, when that leaves an
    /// actor more than 64 units off it and every face turned up puts more of the cell's actors on it, from those: a
    /// model's collision can have a floor wound face down (2026-10-19, Morrowind's In_DE_shipwreckUL_LG: an actor 30 over
    /// a deck facing down), while turned up every ceiling under open space became a floor above it (Oblivion: 22 load
    /// doors' nearest navmesh lay on the ceiling they were set in; Morrowind's interiors: twice the walkable floor, 139
    /// million spans to 70, for five more actors near it). Load doors don't choose: a trapdoor is in the ceiling.
    /// </summary>
    public Result BuildInterior(Cell cell, IReadOnlyList<(Vector3[] Points, Vector3 Normal)> asWound, IReadOnlyList<(Vector3[] Points, Vector3 Normal)> turnedUp,
        IEnumerable<IPlacedObjectGetter> loadDoors, P3Float? keepAround, IReadOnlyList<P3Float> actors, Hazards? hazards = null, Func<P3Float, bool>? underwater = null)
    {
        var doors = loadDoors.ToList();
        var made = Generate(cell, asWound, doors, keepAround, actors, underwater, hazards);
        int On(Generated g) => actors.Count(a => Distance(g.Data, a) <= 64);
        int on = On(made);
        if (on < actors.Count)
        {
            Generated? alt = null;
            try { alt = Generate(cell, turnedUp, doors, keepAround, actors, underwater, hazards); }
            catch (Exception) { }
            if (alt is not null && On(alt) > on) { made = alt; Interlocked.Increment(ref turnedUpChosen); }
        }
        var chosen = made;
        return new PendingInterior { Owner = this, Cell = cell, Run = () => chosen }.Finish();
    }

    /// <summary>Interiors whose navmesh was made from every face turned up (the choice above).</summary>
    public static int TurnedUpChosen => turnedUpChosen;
    static int turnedUpChosen;

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
            if (on) continue;
            bool sameLevel = at is not null;
            // Nothing on its level (Daggerfall's flying monsters at their markers in mid-air, and actors on a lift or bridge
            // whose model stands elsewhere in Skyrim, 720 units over the floor): dropped onto the mesh under it, as it
            // would fall, the nearest across first.
            if (at is null)
            {
                float across = 256f * 256f;
                foreach (var t in tris)
                {
                    Vector3 a = V(verts[t.Vertices.X]), b = V(verts[t.Vertices.Y]), c = V(verts[t.Vertices.Z]);
                    var q = ClosestOnTriangle(new Vector3(p.X, p.Y, (a.Z + b.Z + c.Z) / 3), a, b, c);
                    if (q.Z > p.Z) continue;
                    float d = (q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y) + (p.Z - q.Z) * 0.01f;
                    if (d < across) { across = d; at = q + ((a + b + c) / 3 - q) * 0.2f; }
                }
            }
            // Nothing on its level or under it (in a passage too steep for the mesh): the nearest mesh, any level.
            if (at is null)
            {
                float any = 1024f * 1024f;
                foreach (var t in tris)
                {
                    Vector3 a = V(verts[t.Vertices.X]), b = V(verts[t.Vertices.Y]), c = V(verts[t.Vertices.Z]);
                    var q = ClosestOnTriangle(p, a, b, c);
                    float d = Vector3.DistanceSquared(p, q);
                    if (d < any) { any = d; at = q + ((a + b + c) / 3 - q) * 0.2f; }
                }
            }
            if (at is not { } to) continue;
            // On its level, its own height, or the mesh's where that is higher: never lower, as the mesh has no detail heights
            // and inside a big polygon lies up to 50 under the floor (2026-10-16: actors set 48 units into a floor). Fallen
            // or moved to another level, the mesh's.
            pl.Position = new P3Float(to.X, to.Y, sameLevel ? MathF.Max(p.Z, to.Z - 28) : to.Z + 4);
            if (MathF.Abs(to.Z - p.Z) > 48 && Environment.GetEnvironmentVariable("TM_SNAP_TRACE") is not null)
                Console.WriteLine($"  snap: {npc.FormKey} {p.X:F0},{p.Y:F0},{p.Z:F0} -> {to.X:F0},{to.Y:F0},{to.Z:F0}");
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
        Func<P3Float, bool>? underwater, bool background, bool snapActors = false, Hazards? hazards = null)
    {
        var doors = loadDoors.ToList();
        var keep = keepAlso?.ToList();
        var pending = new PendingInterior { Owner = this, Cell = cell, SnapActors = snapActors };
        pending.Run = () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var g = Generate(cell, polygons, doors, keepAround, keep, underwater, hazards);
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
        Func<P3Float, bool>? underwater, Hazards? hazards = null)
    {
        // Recast's mesh, or the same input's from another part (SharedExteriors: a town part's building interiors are
        // the same buildings as other parts', 2026-10-15: a fifth of a town part's work).
        // (Not shared with hazards: the shared files are keyed by the polygons alone.)
        var (outVerts, tris) = SharedExteriors is { } dir && hazards is null ? SharedInterior(dir, polygons) : RecastInterior(polygons, hazards);
        (outVerts, tris) = PruneIslands(outVerts, tris, doorList.Select(d => d.Placement!.Position).Append(keepAround ?? default).Concat(keepAlso ?? []).ToList());
        // Nothing walkable (an Oblivion test cell, a room of models the converter can't read): said plainly, not as the
        // grid's "Sequence contains no elements" (2026-10-17).
        if (tris.Count == 0) throw new InvalidOperationException("no floor to walk on (nothing for a navmesh)");
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
                // Water when wholly under it, its three corners, as vanilla's (WriteExterior).
                Flags = NavmeshTriangle.Flag.Found | (underwater is { } wet && wet(outVerts[tris[i].A]) && wet(outVerts[tris[i].B]) && wet(outVerts[tris[i].C]) ? NavmeshTriangle.Flag.Water : 0),
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

    /// <summary>How far a point is from a navmesh: the nearest point of its nearest triangle (units).</summary>
    public static float Distance(INavigationMeshDataGetter data, P3Float at)
    {
        var p = new Vector3(at.X, at.Y, at.Z);
        float best = float.MaxValue;
        foreach (var t in data.Triangles)
        {
            var a = data.Vertices[t.Vertices.X]; var b = data.Vertices[t.Vertices.Y]; var c = data.Vertices[t.Vertices.Z];
            best = MathF.Min(best, Vector3.Distance(p, ClosestOnTriangle(p, new(a.X, a.Y, a.Z), new(b.X, b.Y, b.Z), new(c.X, c.Y, c.Z))));
        }
        return best;
    }

    /// <summary>
    /// For the traces: how a model's collision triangles are wound. Edges shared by two triangles (corners welded to
    /// 1/16 unit) are consistent when the two run them opposite ways; a closed mesh (every edge shared) wound outward
    /// has a positive signed volume.
    /// </summary>
    public static string Winding(IEnumerable<Vector3[]> triangles)
    {
        var w = WindingOf(triangles);
        return $"{w.Count} triangles, {w.Consistent} edges shared oppositely, {w.Inconsistent} shared the same way, {w.Open} open, volume {w.Volume:F0}, its top facing {(w.TopUp > 0 ? "up" : w.TopUp < 0 ? "down" : "neither")}";
    }

    /// <summary>Whether a model's collision can go into an exterior navmesh as wound: every shared edge run oppositely,
    /// and wound outward (closed: a positive volume; open: the faces at its top facing up). Otherwise each of its faces
    /// is turned up (<see cref="BuildExteriorCells"/> with asWound).</summary>
    public static bool TrustedWinding(IEnumerable<Vector3[]> solid)
    {
        var w = WindingOf(solid);
        return w.Inconsistent == 0 && (w.Open == 0 ? w.Volume >= 0 : w.TopUp >= 0);
    }

    /// <summary>The kind of a model's winding, for the build's log: mixed; closed outward or inside out; open with its
    /// top facing up or down.</summary>
    public static string WindingKind(IEnumerable<Vector3[]> solid)
    {
        var w = WindingOf(solid);
        return w.Inconsistent > 0 ? "mixed" : w.Open == 0 ? (w.Volume >= 0 ? "closed, outward" : "closed, inside out") : (w.TopUp >= 0 ? "open, top facing up" : "open, top facing down");
    }

    /// <summary>A model's winding (<see cref="Winding"/>), and which way the faces at its top face: their area-weighted
    /// normals' z summed over the faces in the top tenth of its height (outward, the top of a thing faces up).</summary>
    public readonly record struct WindingInfo(int Count, int Consistent, int Inconsistent, int Open, double Volume, double TopUp);

    public static WindingInfo WindingOf(IEnumerable<Vector3[]> triangles)
    {
        var ids = new Dictionary<(int, int, int), int>();
        int Id(Vector3 v) { var k = ((int)MathF.Round(v.X * 16), (int)MathF.Round(v.Y * 16), (int)MathF.Round(v.Z * 16)); if (!ids.TryGetValue(k, out var i)) ids[k] = i = ids.Count; return i; }
        var directed = new Dictionary<(int, int), int>();
        double volume = 0;
        int count = 0;
        var list = triangles as IReadOnlyList<Vector3[]> ?? triangles.ToList();
        float zMin = float.MaxValue, zMax = float.MinValue;
        foreach (var t in list)
        {
            count++;
            int a = Id(t[0]), b = Id(t[1]), c = Id(t[2]);
            foreach (var e in new[] { (a, b), (b, c), (c, a) }) directed[e] = directed.GetValueOrDefault(e) + 1;
            volume += Vector3.Dot(t[0], Vector3.Cross(t[1], t[2])) / 6.0;
            foreach (var v in t) { zMin = MathF.Min(zMin, v.Z); zMax = MathF.Max(zMax, v.Z); }
        }
        int consistent = 0, inconsistent = 0, open = 0;
        foreach (var ((a, b), n) in directed)
        {
            if (directed.ContainsKey((b, a))) { if (a < b) consistent++; }
            else if (n > 1) inconsistent++;
            else open++;
        }
        // An edge run the same way by two triangles (counted on its own direction) is inconsistent; one with no partner is open.
        double topUp = 0;
        float topFrom = zMax - (zMax - zMin) * 0.1f;
        foreach (var t in list)
            if ((t[0].Z + t[1].Z + t[2].Z) / 3 >= topFrom) topUp += Vector3.Cross(t[1] - t[0], t[2] - t[0]).Z / 2;
        return new(count, consistent, inconsistent, open, volume, topUp);
    }

    /// <summary>TM_NAV_LAYERS=1, for "Heightfield has too many layers": the 32-unit column of the input with the most
    /// surfaces over each other (heights more than 8 units apart), and those heights.</summary>
    static string TallestStack(IReadOnlyList<(Vector3[] Points, Vector3 Normal)> polygons)
    {
        var columns = new Dictionary<(int, int), SortedSet<int>>();
        foreach (var (pts, _) in polygons)
            for (int i = 1; i + 1 < pts.Length; i++)
            {
                var (a, b, c) = (pts[0], pts[i], pts[i + 1]);
                float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                if (MathF.Abs(d) < 1e-3f) continue;
                for (float y = MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) / 32) * 32 + 16; y <= MathF.Max(a.Y, MathF.Max(b.Y, c.Y)); y += 32)
                    for (float x = MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / 32) * 32 + 16; x <= MathF.Max(a.X, MathF.Max(b.X, c.X)); x += 32)
                    {
                        float l1 = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / d, l2 = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / d;
                        if (l1 < 0 || l2 < 0 || l1 + l2 > 1) continue;
                        var key = ((int)MathF.Floor(x / 32), (int)MathF.Floor(y / 32));
                        if (!columns.TryGetValue(key, out var zs)) columns[key] = zs = [];
                        zs.Add((int)MathF.Round((l1 * a.Z + l2 * b.Z + (1 - l1 - l2) * c.Z) / 8));
                    }
            }
        if (columns.Count == 0) return "  layers: no input";
        static List<int> Apart(SortedSet<int> zs) { var l = new List<int>(); foreach (var z in zs) if (l.Count == 0 || z - l[^1] > 1) l.Add(z); return l; }
        var (at, apart) = columns.Select(kv => (kv.Key, Apart(kv.Value))).MaxBy(c => c.Item2.Count);
        return $"  layers: tallest column at {at.Item1 * 32 + 16},{at.Item2 * 32 + 16}: {apart.Count} surfaces over 8 units apart, heights " + string.Join(" ", apart.Select(z => z * 8));
    }

    /// <summary>
    /// What hurts to stand on or in, kept off the navmesh as vanilla keeps actors off its hazards (it has no navmesh over
    /// them): <paramref name="Faces"/>, the faces of objects that hurt whoever stands on or touches them (a span resting
    /// on one is no floor; Morrowind's lava pools, In_lava_*, whose script hurts a standing actor 20 a second), and
    /// <paramref name="Level"/>, the surface of a liquid that hurts (Oblivion's lava water, 50 a second, in its planes and
    /// 47 interiors), under which nothing is floor.
    /// </summary>
    public sealed record Hazards(WoundInput? Faces, Func<float, float, float?>? Level)
    {
        public bool Covers(float x, float y, float top) =>
            (Faces is { } f && f.Under(new Vector3(x, y, top), 12, 12) is not null) || (Level?.Invoke(x, y) is { } lv && top < lv);
    }

    /// <summary>Walkable voxel spans made unwalkable as on or under a hazard (<see cref="Hazards"/>), once a voxel size.</summary>
    public static long HazardSpans => Interlocked.Read(ref hazardSpans);
    static long hazardSpans;

    /// <summary>An interior's mesh as Recast makes it from its polygons, before its islands are pruned; with
    /// <paramref name="hazards"/>, nothing on or under them walkable.</summary>
    static (List<P3Float> Verts, List<(int A, int B, int C)> Tris) RecastInterior(IReadOnlyList<(Vector3[] Points, Vector3 Normal)> polygons, Hazards? hazards = null)
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
        if (Environment.GetEnvironmentVariable("TM_NAV_LAYERS") == "1") Console.WriteLine(TallestStack(polygons));
        var (pmesh, detail) = BuildMesh(geom, (p, coarse, outline) => new RcBuilderConfig(Config(p, coarse, outline), geom.GetMeshBoundsMin(), geom.GetMeshBoundsMax()), hazards: hazards)!.Value;
        var (outVerts, tris) = SkyrimTriangles(pmesh, detail);
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

    /// <summary>How far under the land a placed object's triangle must lie, all three corners, to be left out of an
    /// exterior navmesh's input (<see cref="BuildExteriorCells"/>).</summary>
    const float BuriedDepth = 32f;

    /// <summary>Placed objects' triangles left out of exterior navmeshes' input as buried (counted once for each cell
    /// whose input they were in).</summary>
    public static long BuriedLeftOut => Interlocked.Read(ref buriedLeftOut);
    static long buriedLeftOut;

    /// <summary>An exterior cell's navmesh with its world-space geometry, kept for linking edges to neighbours.</summary>
    /// <remarks>Only its triangles' edges on the cell's four sides are kept for linking (<see cref="BorderEdges"/>), not the
    /// mesh again: every exterior navmesh is held until the worldspaces are linked (2026-10-19, Oblivion's worldspaces:
    /// 35 million triangles, held by the builder, by these and by the records).</remarks>
    public sealed record ExteriorMesh(Worldspace Worldspace, int X, int Y, NavigationMesh Navmesh, List<(int Tri, int Edge, float Lo, float Hi, float ZLo, float ZHi)>[] Sides);

    /// <summary>A mesh's triangle edges on each side of its cell (0 west, 1 east, 2 south, 3 north) with no neighbour
    /// inside the mesh: (triangle, edge, span from, span to along the side, the heights there).</summary>
    static List<(int Tri, int Edge, float Lo, float Hi, float ZLo, float ZHi)>[] BorderEdges(int cx, int cy, List<P3Float> verts, List<(int A, int B, int C)> tris, int[,] neighbours)
    {
        var sides = new List<(int Tri, int Edge, float Lo, float Hi, float ZLo, float ZHi)>[4];
        for (int side = 0; side < 4; side++)
        {
            var list = sides[side] = [];
            float line = side switch { 0 => cx * CellUnits, 1 => (cx + 1) * CellUnits, 2 => cy * CellUnits, _ => (cy + 1) * CellUnits };
            bool vertical = side < 2;
            for (int i = 0; i < tris.Count; i++)
            {
                var t = tris[i];
                int[] vs = [t.A, t.B, t.C];
                for (int e = 0; e < 3; e++)
                {
                    if (neighbours[i, e] >= 0) continue;
                    var a = verts[vs[e]]; var b = verts[vs[(e + 1) % 3]];
                    float ac = vertical ? a.X : a.Y, bc = vertical ? b.X : b.Y;
                    if (MathF.Abs(ac - line) > 0.5f || MathF.Abs(bc - line) > 0.5f) continue;
                    float sa = vertical ? a.Y : a.X, sb = vertical ? b.Y : b.X;
                    list.Add(sa <= sb ? (i, e, sa, sb, a.Z, b.Z) : (i, e, sb, sa, b.Z, a.Z));
                }
            }
        }
        return sides;
    }

    readonly List<ExteriorMesh> exteriors = [];

    /// <summary>
    /// A Skyrim landscape's surface as world-space triangles (33 x 33 heights, 128 units apart; VHGT holds the first
    /// height then per-row and per-column deltas, all in units of 8), under water too: vanilla's navmesh covers the beds
    /// of its lakes and seas at every depth (2026-10-19, Skyrim.esm's Tamriel against its land: of the land's height
    /// points under water, 83% of those 0-128 deep had navmesh over them, 63% of those deeper than 2048), and its
    /// swimmers stand on it (77 of 133 actors more than 128 under water within 64 units of it); its triangles there are
    /// flagged Water (<see cref="WriteExterior"/>).
    /// </summary>
    public static void AddTerrain(List<Vector3[]> output, ILandscapeGetter land, int cx, int cy)
    {
        if (Heights(land) is not { } h) return;
        float ox = cx * CellUnits, oy = cy * CellUnits;
        Vector3 P(int x, int y) => new(ox + x * 128, oy + y * 128, h[x, y]);
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
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
    /// more than <see cref="GridTolerance"/> from the ground under them, cross the water's edge (a block is wholly under
    /// the water or not, so its triangles are flagged Water whole, as vanilla's are: <see cref="WriteExterior"/>) or
    /// cross into a slope too steep to walk, down to single 128-unit quads. Neighbouring blocks differ by one split at most, and a
    /// block's edge gets a midpoint where the block beside it is split, so the triangles share their corners.
    /// 2026-10-15 (user: "should be a 1 hour conversion"): Recast voxelised each of the overland's 400,000 cells for
    /// over 50 minutes on every core, and left about 3 triangles a cell, hundreds of units off the ground on Daggerfall's
    /// hills; an even grid per cell within 48 units took 7 minutes but made 99 million triangles.
    /// </summary>
    public static (List<P3Float> Verts, List<(int A, int B, int C)> Tris)? GenerateTerrainGrid(ILandscapeGetter land, int cx, int cy, float? water, List<P3Float> keep)
    {
        if (Heights(land) is not { } h) return null;
        // Each 128-unit quad: walkable, or left out (steeper than Recast's 50-degree limit); and wholly under the water
        // (its four corners) or not.
        float cosMax = MathF.Cos(50f * MathF.PI / 180f);
        var open = new bool[32, 32];
        var wet = new bool[32, 32];
        static float Up(float dx, float dy) => 1f / MathF.Sqrt(1 + dx * dx + dy * dy);
        bool any = false;
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                float top = MathF.Max(MathF.Max(h[x, y], h[x + 1, y]), MathF.Max(h[x, y + 1], h[x + 1, y + 1]));
                wet[x, y] = water is { } w && top < w;
                // The quad's two triangles' slopes (P00 P10 P11 and P00 P11 P01).
                bool steep = Up((h[x + 1, y] - h[x, y]) / 128, (h[x + 1, y + 1] - h[x + 1, y]) / 128) < cosMax
                          || Up((h[x + 1, y + 1] - h[x, y + 1]) / 128, (h[x, y + 1] - h[x, y]) / 128) < cosMax;
                open[x, y] = !steep;
                any |= open[x, y];
            }
        if (!any) return null;

        // The blocks: leaf[x, y] is the size of the block holding quad (x, y); blocks sit on multiples of their size.
        var leaf = new int[32, 32];
        bool Fits(int x0, int y0, int size)
        {
            bool first = open[x0, y0], firstWet = wet[x0, y0];
            for (int y = y0; y < y0 + size; y++)
                for (int x = x0; x < x0 + size; x++)
                    if (open[x, y] != first || wet[x, y] != firstWet) return false;
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

    /// <summary>How far past its cell an exterior cell's heightfield reaches (<see cref="GenerateExteriorRaw"/>): the agent's
    /// radius erodes the floor back from the heightfield's edge, out there, and a floor crossing the cell's edge is one
    /// region across it (2026-10-19, nine Bitter Coast cells of Morrowind: with Recast's tile border, a band round the
    /// tile of its own, a floor cut at the cell's edge a voxel wide got no outline, and its neighbour's navmesh met
    /// nothing there: 4 of 194 border edges facing a neighbour's navmesh unlinked). A multiple of every voxel size.</summary>
    const float ExteriorMargin = 128f;

    /// <summary>Recast settings for exteriors: 16-unit voxels (terrain vertices are 128 apart), the cell and
    /// <see cref="ExteriorMargin"/> round it in one heightfield. The detail mesh's heights every 24 units, within 16
    /// (measured on nine Bitter Coast cells of Morrowind against the floor, 2,182,021 walkable spans, those 32 to 128
    /// units from the navmesh over them: every 128 within 24, 0.39%, the navmesh 50-67 units over the dips in rocks'
    /// tops; 2026-10-20, counted: every 32 within 8, 1 span, 50,077 triangles; every 24 within 16, 2, 25,327; 16 within
    /// 16, 2, 26,108; 32 within 12, 8, 31,149; 32 within 16, 10, 23,373; 48 within 16, 24, 21,921; 64 within 12, 84,
    /// 26,475. Those left are 32 to 43 units off, at rocks' edges).</summary>
    static RcConfig ExteriorConfig(bool open = false, RcPartition partition = RcPartition.WATERSHED, float coarse = 1, float outline = 1)
    {
        // Open country (open: Daggerfall's overland, smooth land and little on it) in coarser voxels and longer, looser
        // edges: a quarter of the triangles over 87,000 cells, as vanilla's wilds are meshed in big triangles too.
        float cs = (open ? 32f : 16f) * coarse, ch = (open ? 8f : 4f) * coarse, radius = 24f;
        // Not tiled, no border: the heightfield reaches ExteriorMargin past the cell and the mesh is clipped to it.
        return new RcConfig(false, 0, 0, 0, partition,
            cs, ch, agentMaxSlope: 50f, agentHeight: 128f, agentRadius: radius, agentMaxClimb: open ? 48f : 32f,
            minRegionArea: 8 * cs * cs, mergeRegionArea: 20 * cs * cs, edgeMaxLen: open ? 1600f : 600f, edgeMaxError: (open ? 4f : 2f) * outline, vertsPerPoly: 6,
            detailSampleDist: Env("DETAILDIST", 24f) / cs, detailSampleMaxError: Env("DETAILERR", 16f) / ch, filterLowHangingObstacles: true, filterLedgeSpans: true, filterWalkableLowHeightSpans: true,
            walkableAreaMod: new RcAreaModification(0x3f), buildMeshDetail: Env("DETAIL", 1f) > 0);
    }

    /// <summary>
    /// An exterior cell's navmesh geometry from world-space triangles (any winding; walkable faces are taken to point
    /// up), clipped to the cell. Thread-safe: it touches no records, so cells can be generated in parallel and written
    /// with <see cref="WriteExterior"/> afterwards. Null when nothing is walkable.
    /// </summary>
    public static (List<P3Float> Verts, List<(int A, int B, int C)> Tris)? GenerateExterior(int cx, int cy, List<Vector3[]> triangles, List<P3Float> keep, bool open = false,
        Func<float, float, float?>? land = null, float[]? around = null, bool asWound = false, Hazards? hazards = null) =>
        GenerateExteriorRaw(cx, cy, triangles, open, land, around, asWound, hazards) is { } raw ? Finish(raw.Verts, raw.Tris, keep) : null;

    /// <summary>The islands not reached from <paramref name="keep"/> pruned (<see cref="PruneIslands"/>); null when
    /// nothing is left or the mesh is too big for a navmesh's 16-bit indices.</summary>
    static (List<P3Float> Verts, List<(int A, int B, int C)> Tris)? Finish(List<P3Float> verts, List<(int A, int B, int C)> tris, List<P3Float> keep)
    {
        (verts, tris) = PruneIslands(verts, tris, keep);
        if (tris.Count == 0 || tris.Count > short.MaxValue || verts.Count > short.MaxValue) return null;
        return (verts, tris);
    }

    /// <summary><see cref="GenerateExterior"/>'s mesh as Recast makes it, before the islands are pruned; with
    /// <paramref name="land"/>, nothing under the land is walkable, and with <paramref name="around"/> (the input of
    /// the cell and its neighbours as wound) nothing shut inside something (<see cref="BuildOverLand"/>).</summary>
    static (List<P3Float> Verts, List<(int A, int B, int C)> Tris)? GenerateExteriorRaw(int cx, int cy, List<Vector3[]> triangles, bool open, Func<float, float, float?>? land = null, float[]? around = null, bool asWound = false, Hazards? hazards = null)
    {
        if (triangles.Count == 0) return null;
        // TM_NAV_PROBE=x,y: every input triangle over that point, with its height there and which way it faces.
        if (Environment.GetEnvironmentVariable("TM_NAV_PROBE")?.Split(',') is [var sx, var sy] && float.TryParse(sx, out var qx) && float.TryParse(sy, out var qy)
            && MathF.Floor(qx / CellUnits) == cx && MathF.Floor(qy / CellUnits) == cy)
            foreach (var p in triangles)
            {
                float d = (p[1].Y - p[2].Y) * (p[0].X - p[2].X) + (p[2].X - p[1].X) * (p[0].Y - p[2].Y);
                if (MathF.Abs(d) < 1e-6f) continue;
                float a = ((p[1].Y - p[2].Y) * (qx - p[2].X) + (p[2].X - p[1].X) * (qy - p[2].Y)) / d, b = ((p[2].Y - p[0].Y) * (qx - p[2].X) + (p[0].X - p[2].X) * (qy - p[2].Y)) / d;
                if (a < 0 || b < 0 || a + b > 1) continue;
                var nz = Vector3.Normalize(Vector3.Cross(p[1] - p[0], p[2] - p[0])).Z;
                Console.WriteLine($"  probe {qx},{qy} (cell {cx},{cy}): triangle at height {a * p[0].Z + b * p[1].Z + (1 - a - b) * p[2].Z:F0}, normal z {nz:F2}, corners {string.Join(" ", p.Select(v => $"({v.X:F0},{v.Y:F0},{v.Z:F0})"))}");
            }
        var verts = new float[triangles.Count * 9];
        var faces = new int[triangles.Count * 3];
        float minZ = float.MaxValue, maxZ = float.MinValue;
        for (int t = 0; t < triangles.Count; t++)
        {
            var p = triangles[t];
            // Each face turned up (walkable faces are taken to point up, the winding either way), or as wound.
            bool up = asWound || Vector3.Cross(p[1] - p[0], p[2] - p[0]).Z >= 0;
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
        var bmin = new RcVec3f(cx * CellUnits - ExteriorMargin, minZ - 16, -(cy + 1) * CellUnits - ExteriorMargin);
        var bmax = new RcVec3f((cx + 1) * CellUnits + ExteriorMargin, maxZ + 256, -cy * CellUnits + ExteriorMargin);
        var wound = around is null ? null : new Lazy<WoundInput>(() => new WoundInput(around));
        if (BuildMesh(geom, (p, coarse, outline) => new RcBuilderConfig(ExteriorConfig(open, p, coarse, outline), bmin, bmax, 0, 0), land, wound, ExteriorMargin, hazards) is not { } built || built.Mesh.npolys == 0) return null;
        var (cv, ct) = SkyrimTriangles(built.Mesh, built.Detail);
        var clipped = ClipToCell(cv, ct, cx, cy);
        return clipped.Tris.Count == 0 ? null : clipped;
    }

    /// <summary>
    /// The navmesh's triangles in Skyrim space from Recast's meshes. The detail mesh's when there is one: Recast's
    /// polygons have heights only at their corners, so a polygon laid over a hill or a stair stood far off the floor
    /// inside it (2026-10-18, measured on the built plugins against the land: 8.7% of Redguard's exterior navmesh,
    /// 9.6% of Morrowind's and 7.7% of Oblivion's more than 128 units under it), and the detail mesh adds points
    /// where the floor leaves the polygon by more than detailSampleMaxError. Its submeshes repeat the corners and
    /// edge points they share (sampled the same way from both sides), so they are welded by position. Without one,
    /// each polygon's fan from its first corner.
    /// </summary>
    static (List<P3Float> Verts, List<(int A, int B, int C)> Tris) SkyrimTriangles(RcPolyMesh pmesh, RcPolyMeshDetail? detail)
    {
        var made = SkyrimTrianglesOf(pmesh, detail);
        Interlocked.Add(ref trianglesMade, made.Tris.Count);
        return made;
    }

    /// <summary>
    /// An exterior mesh's triangles clipped to its cell's square (Recast's mesh reaches <see cref="ExteriorMargin"/>
    /// past it): each triangle's part inside, fanned from its first corner, the corners on the cell's edges exactly on
    /// them (so <see cref="LinkExteriorEdges"/> finds them), corners welded at 1/8 unit, parts of no area left out.
    /// </summary>
    static (List<P3Float> Verts, List<(int A, int B, int C)> Tris) ClipToCell(List<P3Float> verts, List<(int A, int B, int C)> tris, int cx, int cy)
    {
        float x0 = cx * CellUnits, x1 = x0 + CellUnits, y0 = cy * CellUnits, y1 = y0 + CellUnits;
        var outVerts = new List<P3Float>();
        var outTris = new List<(int A, int B, int C)>();
        var index = new Dictionary<(long, long, long), int>();
        int Weld(Vector3 p)
        {
            var key = ((long)MathF.Round(p.X * 8), (long)MathF.Round(p.Y * 8), (long)MathF.Round(p.Z * 8));
            if (!index.TryGetValue(key, out int i)) { i = outVerts.Count; index[key] = i; outVerts.Add(new P3Float(p.X, p.Y, p.Z)); }
            return i;
        }
        // One side of the square: the polygon's part where the coordinate (0 x, 1 y) is on the kept side of the line,
        // the points made on the line set exactly on it.
        static List<Vector3> Clip(List<Vector3> poly, int axis, float line, bool keepAbove)
        {
            var kept = new List<Vector3>(poly.Count + 2);
            for (int i = 0; i < poly.Count; i++)
            {
                var p = poly[i]; var q = poly[(i + 1) % poly.Count];
                float fp = (axis == 0 ? p.X : p.Y) - line, fq = (axis == 0 ? q.X : q.Y) - line;
                if (!keepAbove) { fp = -fp; fq = -fq; }
                if (fp >= 0) kept.Add(p);
                if ((fp >= 0) != (fq >= 0))
                {
                    var m = p + (q - p) * (fp / (fp - fq));
                    kept.Add(axis == 0 ? new Vector3(line, m.Y, m.Z) : new Vector3(m.X, line, m.Z));
                }
            }
            return kept;
        }
        // A corner within 2 units of a cell's edge is put on it: just past it, its triangle's two crossings would be a
        // fraction of a unit apart, a border edge too short to link (measured: 268 of 5,571 on nine cells), and just
        // inside it, its edges to the line were slivers a unit long (with half a unit: 46 of 5,314).
        Vector3 Snap(P3Float v) => new(
            MathF.Abs(v.X - x0) < 2 ? x0 : MathF.Abs(v.X - x1) < 2 ? x1 : v.X,
            MathF.Abs(v.Y - y0) < 2 ? y0 : MathF.Abs(v.Y - y1) < 2 ? y1 : v.Y, v.Z);
        foreach (var (a, b, c) in tris)
        {
            var poly = new List<Vector3> { Snap(verts[a]), Snap(verts[b]), Snap(verts[c]) };
            if (poly.Any(p => p.X < x0 || p.X > x1 || p.Y < y0 || p.Y > y1))
            {
                poly = Clip(poly, 0, x0, true);
                if (poly.Count >= 3) poly = Clip(poly, 0, x1, false);
                if (poly.Count >= 3) poly = Clip(poly, 1, y0, true);
                if (poly.Count >= 3) poly = Clip(poly, 1, y1, false);
                if (poly.Count < 3) continue;
            }
            var ids = poly.Select(Weld).ToList();
            for (int k = 1; k + 1 < ids.Count; k++)
            {
                int ia = ids[0], ib = ids[k], ic = ids[k + 1];
                if (ia == ib || ib == ic || ia == ic) continue;
                float area = CrossZ(outVerts[ia], outVerts[ib], outVerts[ic]);
                // Under half a square unit (twice the area here): a sliver of the clipping.
                if (MathF.Abs(area) < 1f) continue;
                outTris.Add(area < 0 ? (ia, ic, ib) : (ia, ib, ic));
            }
        }
        return (outVerts, outTris);
    }

    /// <summary>Triangles Recast's meshes became (before islands are pruned), for the build's log.</summary>
    static long trianglesMade;

    static (List<P3Float> Verts, List<(int A, int B, int C)> Tris) SkyrimTrianglesOf(RcPolyMesh pmesh, RcPolyMeshDetail? detail)
    {
        var outVerts = new List<P3Float>();
        var tris = new List<(int A, int B, int C)>();
        if (detail is { nmeshes: > 0 })
        {
            var index = new Dictionary<(long, long, long), int>();
            int Weld(int v)
            {
                float x = detail.verts[v * 3], y = detail.verts[v * 3 + 1], z = detail.verts[v * 3 + 2];
                var key = ((long)MathF.Round(x * 8), (long)MathF.Round(y * 8), (long)MathF.Round(z * 8));
                if (!index.TryGetValue(key, out int i)) { i = outVerts.Count; index[key] = i; outVerts.Add(new P3Float(x, -z, y)); }
                return i;
            }
            for (int m = 0; m < detail.nmeshes; m++)
            {
                int vb = detail.meshes[m * 4], tb = detail.meshes[m * 4 + 2], tc = detail.meshes[m * 4 + 3];
                for (int t = 0; t < tc; t++)
                {
                    int k = (tb + t) * 4;
                    int a = Weld(vb + detail.tris[k]), b = Weld(vb + detail.tris[k + 1]), c = Weld(vb + detail.tris[k + 2]);
                    if (a == b || b == c || a == c) continue;
                    if (CrossZ(outVerts[a], outVerts[b], outVerts[c]) < 0) (b, c) = (c, b);
                    tris.Add((a, b, c));
                }
            }
            return (outVerts, tris);
        }
        // Poly mesh vertices are voxel coordinates; convert back to Skyrim space.
        for (int i = 0; i < pmesh.nverts; i++)
        {
            float rx = pmesh.bmin.X + pmesh.verts[i * 3] * pmesh.cs;
            float ry = pmesh.bmin.Y + pmesh.verts[i * 3 + 1] * pmesh.ch;
            float rz = pmesh.bmin.Z + pmesh.verts[i * 3 + 2] * pmesh.cs;
            outVerts.Add(new P3Float(rx, -rz, ry));
        }
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
            // Made by an older converter (a resumed run after an update): made again.
            if (Environment.ProcessPath is { } exe && File.GetLastWriteTimeUtc(file) < File.GetLastWriteTimeUtc(exe)) return false;
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
            // Renamed when whole; another part may have left the same one meanwhile (the same mesh), unless what is there
            // is an older converter's.
            if (!File.Exists(file) || (Environment.ProcessPath is { } exe && File.GetLastWriteTimeUtc(file) < File.GetLastWriteTimeUtc(exe))) File.Move(tmp, file, overwrite: true);
            else File.Delete(tmp);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The cell's input (its triangles in its own coordinates, to 1/16 unit) and, with <paramref name="land"/>,
    /// the land's heights at its 128-unit points from one beyond the cell's edges (the land is flat between them, and
    /// Recast's border reaches 80 units out), which decide what is under it (<see cref="BuildOverLand"/>).</summary>
    static (ulong, ulong) InputKey(List<Vector3[]> tris, int cx, int cy, Func<float, float, float?>? land = null)
    {
        var f = new float[tris.Count * 9];
        for (int i = 0; i < tris.Count; i++)
            for (int k = 0; k < 3; k++) { f[i * 9 + k * 3] = tris[i][k].X; f[i * 9 + k * 3 + 1] = tris[i][k].Y; f[i * 9 + k * 3 + 2] = tris[i][k].Z; }
        return InputKey(f, cx, cy, land);
    }

    /// <summary>The same over corners nine floats a triangle.</summary>
    static (ulong, ulong) InputKey(float[] coords, int cx, int cy, Func<float, float, float?>? land = null)
    {
        var q = new int[coords.Length + (land is null ? 0 : 35 * 35)];
        int n = 0;
        for (int k = 0; k + 2 < coords.Length; k += 3)
        {
            q[n++] = (int)MathF.Round((coords[k] - cx * CellUnits) * 16);
            q[n++] = (int)MathF.Round((coords[k + 1] - cy * CellUnits) * 16);
            q[n++] = (int)MathF.Round(coords[k + 2] * 16);
        }
        if (land is not null)
            for (int gy = -1; gy <= 33; gy++)
                for (int gx = -1; gx <= 33; gx++)
                    q[n++] = land(cx * CellUnits + gx * 128, cy * CellUnits + gy * 128) is { } g ? (int)MathF.Round(g * 16) : int.MinValue;
        var hash = System.Security.Cryptography.SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(q.AsSpan(0, n)));
        return (BitConverter.ToUInt64(hash, 0), BitConverter.ToUInt64(hash, 8));
    }

    (List<P3Float> Verts, List<(int A, int B, int C)> Tris)? GenerateExteriorReused(int cx, int cy, List<Vector3[]> triangles, List<P3Float> keep, bool open,
        Func<float, float, float?>? land = null, float[]? around = null, bool asWound = false)
    {
        // Keyed by the whole input round the cell when what is shut in is looked for beyond it (the cell's own input is
        // taken from that).
        var (k1, k2) = around is null ? InputKey(triangles, cx, cy, land) : InputKey(around, cx, cy, land);
        // Faces as wound make another mesh of the same input (and another shared file).
        if (asWound) k2 ^= 0x5A5A5A5A5A5A5A5AUL;
        bool made = false;
        var local = exteriorCache.GetOrAdd((k1, k2, open), _ => new Lazy<(P3Float[], (int, int, int)[])?>(() =>
        {
            made = true;
            string? file = SharedExteriors is { } dir ? Path.Combine(dir, $"{k1:x16}{k2:x16}{(open ? "o" : "")}.bin") : null;
            if (file is not null && ReadShared(file, out var shared)) { Interlocked.Increment(ref exteriorShared); return shared; }
            (P3Float[], (int, int, int)[])? mesh = null;
            if (GenerateExteriorRaw(cx, cy, triangles, open, land, around, asWound) is { } raw)
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

    /// <summary>Writes an exterior cell's navmesh (generated by <see cref="GenerateExterior"/>), with its load doors. A
    /// triangle wholly under the cell's <paramref name="water"/> (its three corners) is flagged Water, as vanilla's are
    /// (2026-10-19, Skyrim.esm's Tamriel: of its triangles with every corner under the water, 89-96% flagged once the
    /// shallowest corner is 8 units under, 65% when nearer; of those with a corner above it, 5%).</summary>
    public ExteriorMesh WriteExterior(Worldspace ws, Cell cell, int cx, int cy, List<P3Float> verts, List<(int A, int B, int C)> tris, IEnumerable<IPlacedObjectGetter> loadDoors, float? water = null)
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
                Flags = NavmeshTriangle.Flag.Found
                    | (water is { } w && verts[tris[i].A].Z < w && verts[tris[i].B].Z < w && verts[tris[i].C].Z < w ? NavmeshTriangle.Flag.Water : 0),
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
        var mesh = new ExteriorMesh(ws, cx, cy, navm, BorderEdges(cx, cy, verts, tris, neighbours));
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
    /// <remarks>A placed object's triangle wholly under the land by more than <see cref="BuriedDepth"/> is left out: the
    /// land is a height field, so nothing under it can be walked to, but the triangles are taken to face up (their
    /// winding is either way), so a buried underside became a floor with the land as its ceiling (2026-10-18, Solstheim's
    /// coast at -218880,185600: the land at 208, a rock's underside at -37 facing down, the navmesh at -19; 2,404 of
    /// 551,295 samples of 154 cells lay over 128 units under the land's lowest point round them).
    /// With <paramref name="reuse"/>, a cell whose input another cell of this builder had takes its mesh
    /// (<see cref="exteriorCache"/>; Daggerfall's towns). Not with <paramref name="open"/>.</remarks>
    public (int Meshes, int Triangles) BuildExteriorCells(Worldspace ws, IReadOnlyDictionary<(int X, int Y), Cell> cells,
        Func<int, int, IEnumerable<Vector3[]>> solidsOf, Func<Cell, float?> waterOf, IReadOnlyList<IPlacedObjectGetter> doors, List<string> problems,
        bool open = false, Func<int, int, bool>? only = null, bool reuse = false, bool asWound = false,
        Func<int, int, IEnumerable<Vector3[]>>? hazardsOf = null, Func<Cell, float?>? hazardLevelOf = null)
    {
        reuse &= !open;
        const float margin = 160f;
        var keys = cells.Keys.Where(k => only is null || only(k.X, k.Y)).OrderBy(k => k.Y).ThenBy(k => k.X).ToList();
        var results = new (List<P3Float> Verts, List<(int A, int B, int C)> Tris)?[keys.Count];
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        bool InCell(P3Float p, int x, int y) => p.X >= x * CellUnits && p.X < (x + 1) * CellUnits && p.Y >= y * CellUnits && p.Y < (y + 1) * CellUnits;
        int done = 0;
        // One cell a core: unbounded, the thread pool kept adding threads to the long iterations (2026-10-19: 26 to 152 in two
        // minutes of Morrowind's exteriors), each a whole Recast build at once with its own pooled buffers, and memory
        // rose with them to 16 GB.
        Parallel.For(0, keys.Count, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            // TM_MEM=1 (dev aid): the memory in use every 500 cells: allocated, live (after a full collection), private,
            // and the meshes made so far.
            if (MemoryTrace && Interlocked.Increment(ref done) is var started && started % 100 == 0)
            {
                // TM_MEM=2: live after a full collection too (it changes what is measured).
                long allocated = GC.GetTotalMemory(false), live = Environment.GetEnvironmentVariable("TM_MEM2") == "1" ? GC.GetTotalMemory(true) : -1;
                long meshTris = 0, meshVerts = 0;
                foreach (var r in results) if (r is { } made) { meshTris += made.Tris.Count; meshVerts += made.Verts.Count; }
                var gc = GC.GetGCMemoryInfo(GCKind.Any);
                static string G(long b) => $"{b / 1073741824.0:F1}";
                string gens = string.Join(", ", new[] { "gen0", "gen1", "gen2", "large", "pinned" }.Select((name, g) => g < gc.GenerationInfo.Length
                    ? $"{name} {G(gc.GenerationInfo[g].SizeAfterBytes)} (fragmented {G(gc.GenerationInfo[g].FragmentationAfterBytes)})" : ""));
                Console.WriteLine($"    navmesh {ws.EditorID}: {started:N0} of {keys.Count:N0} cells, {G(allocated)} GB allocated, {G(live)} GB live, " +
                    $"{G(System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64)} GB private; GC heap {G(gc.HeapSizeBytes)}, fragmented {G(gc.FragmentedBytes)}, committed {G(gc.TotalCommittedBytes)}, " +
                    $"by generation after the last collection: {gens}; meshes so far {meshTris:N0} triangles, {meshVerts:N0} vertices");
            }
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
                // The land's height under a point (its own triangles, split as AddTerrain splits them), where a cell
                // with land is there.
                var heights = new Dictionary<(int, int), float[,]?>();
                float? LandAt(float px, float py)
                {
                    int gx = (int)MathF.Floor(px / CellUnits), gy = (int)MathF.Floor(py / CellUnits);
                    if (!heights.TryGetValue((gx, gy), out var h))
                        heights[(gx, gy)] = h = cells.TryGetValue((gx, gy), out var hc) && hc.Landscape is { } l ? Heights(l) : null;
                    if (h is null) return null;
                    float fx = (px - gx * CellUnits) / 128, fy = (py - gy * CellUnits) / 128;
                    int ix = Math.Clamp((int)fx, 0, 31), iy = Math.Clamp((int)fy, 0, 31);
                    fx -= ix; fy -= iy;
                    return fx >= fy
                        ? h[ix, iy] + fx * (h[ix + 1, iy] - h[ix, iy]) + fy * (h[ix + 1, iy + 1] - h[ix + 1, iy])
                        : h[ix, iy] + fy * (h[ix, iy + 1] - h[ix, iy]) + fx * (h[ix + 1, iy + 1] - h[ix, iy + 1]);
                }
                bool Buried(Vector3[] t) => t.All(v => LandAt(v.X, v.Y) is { } g && v.Z < g - BuriedDepth);
                var tris = new List<Vector3[]>();
                // The cell's and its neighbours' whole input as wound, flat (WoundInput): what is shut inside something
                // is looked for in it.
                var aroundCoords = new List<float>();
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (!cells.TryGetValue((x + dx, y + dy), out var c)) continue;
                        var local = new List<Vector3[]>();
                        // Under water too (AddTerrain): what lies on the bed is walked as vanilla's is, flagged Water.
                        if (c.Landscape is { } land) AddTerrain(local, land, x + dx, y + dy);
                        foreach (var t in solidsOf(x + dx, y + dy))
                        {
                            if (Buried(t)) { Interlocked.Increment(ref buriedLeftOut); continue; }
                            local.Add(t);
                        }
                        tris.AddRange(dx == 0 && dy == 0 ? local : local.Where(Near));
                        foreach (var t in local)
                            foreach (var v in t) { aroundCoords.Add(v.X); aroundCoords.Add(v.Y); aroundCoords.Add(v.Z); }
                    }
                var around = aroundCoords.ToArray();
                aroundCoords = null;
                // What hurts round the cell (Hazards): the hazard faces of it and its neighbours, the hazard liquid's level
                // by the cell a point is in.
                Hazards? hazards = null;
                if (hazardsOf is not null || hazardLevelOf is not null)
                {
                    var hz = new List<float>();
                    if (hazardsOf is not null)
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                                foreach (var t in hazardsOf(x + dx, y + dy))
                                    foreach (var v in t) { hz.Add(v.X); hz.Add(v.Y); hz.Add(v.Z); }
                    float? LevelAt(float px, float py) => hazardLevelOf is not null && cells.TryGetValue(((int)MathF.Floor(px / CellUnits), (int)MathF.Floor(py / CellUnits)), out var hc) ? hazardLevelOf(hc) : null;
                    if (hz.Count > 0 || hazardLevelOf is not null) hazards = new Hazards(hz.Count > 0 ? new WoundInput(hz.ToArray()) : null, hazardLevelOf is null ? null : LevelAt);
                }
                // A cell Recast can't outline in the open-country setting is tried again in the fine one.
                // Shared meshes are keyed by the input alone: a cell with hazards is made on its own.
                if (reuse && hazards is null) results[i] = GenerateExteriorReused(x, y, tris, keep, open, LandAt, around, asWound);
                else
                    try { results[i] = GenerateExterior(x, y, tris, keep, open, LandAt, around, asWound, hazards); }
                    catch (Exception) when (open) { results[i] = GenerateExterior(x, y, tris, keep, land: LandAt, around: around, asWound: asWound, hazards: hazards); }
                if (ExteriorSurface.On && results[i] is { } made) ExteriorSurface.Check(ws.EditorID ?? "", x, y, around, made.Verts, made.Tris, LandAt);
            }
            catch (Exception e) { errors.Add($"navmesh {ws.EditorID} {x},{y}: {e.Message}"); }
        });
        problems.AddRange(errors);
        int meshes = 0, triangles = 0;
        for (int i = 0; i < keys.Count; i++)
        {
            if (results[i] is not { } r) continue;
            var (x, y) = keys[i];
            WriteExterior(ws, cells[keys[i]], x, y, r.Verts, r.Tris, doors.Where(d => InCell(d.Placement!.Position, x, y)), waterOf(cells[keys[i]]));
            meshes++;
            triangles += r.Tris.Count;
            results[i] = null;   // in the record now
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
        // Border edges per mesh and side (BorderEdges, kept when each was written).
        static List<(int Tri, int Edge, float Lo, float Hi, float ZLo, float ZHi)> Borders(ExteriorMesh m, int side) => m.Sides[side];
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
                foreach (var (tri, edge, lo, hi, zlo, zhi) in Borders(m, side))
                {
                    var tr = data.Triangles[tri];
                    short current = edge switch { 0 => tr.EdgeLink_0_1, 1 => tr.EdgeLink_1_2, _ => tr.EdgeLink_2_0 };
                    if (current >= 0) continue;   // shared inside the mesh
                    // The neighbour's edge overlapping it most whose floor is the same floor there: within 64 units of
                    // it at the middle of their overlap (not a bridge over it, or the ground under one).
                    static float At(float s, float lo2, float hi2, float z0, float z1) => hi2 > lo2 ? z0 + (z1 - z0) * Math.Clamp((s - lo2) / (hi2 - lo2), 0, 1) : (z0 + z1) / 2;
                    int best = -1; float bestOverlap = 0;
                    foreach (var o in theirs)
                    {
                        float from = MathF.Max(lo, o.Lo), to = MathF.Min(hi, o.Hi), overlap = to - from;
                        if (overlap <= bestOverlap) continue;
                        float mid = (from + to) / 2;
                        if (MathF.Abs(At(mid, lo, hi, zlo, zhi) - At(mid, o.Lo, o.Hi, o.ZLo, o.ZHi)) > 64) continue;
                        bestOverlap = overlap; best = o.Tri;
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
        // Nothing walkable: no triangle to be nearest a kept point (it threw "Sequence contains no elements" for three
        // Oblivion interiors, 2026-10-18), and the caller says there is no floor.
        if (tris.Count == 0) return (verts, tris);
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
