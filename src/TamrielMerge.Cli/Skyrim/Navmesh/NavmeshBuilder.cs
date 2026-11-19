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
    /// <summary>The plugin it was made for (kept as a field: <see cref="Target"/> starts from it too).</summary>
    readonly SkyrimMod mod = mod;
    SkyrimMod Mod => mod;
    /// <summary>The plugin new navmeshes are made in (<paramref name="mod"/> to begin with): a tile's navmeshes written
    /// into several plugins by size (OverlandNavmesh) switch it between pieces.</summary>
    public SkyrimMod Target { get; set; } = mod;

    /// <summary>Recast settings in Skyrim units: actor ~128 tall, 24 radius (12 in Oblivion's interiors: Config), steps up to 32 (stairs arrive as ramps), slopes to 50 degrees (Daggerfall's steepest stairs are 45).</summary>
    /// <summary>
    /// How near the floor an interior's navmesh must lie, in units, whatever the try's voxels (0: as the settings below
    /// give it, in voxels: 16 units, and 32 in the tries with voxels twice as coarse). Oblivion sets 8 (2026-11-15):
    /// the Imperial prison's mesh, kept from a coarse try, lay a median 5.6 off the floor along Captain Renault's way to
    /// her marker but 16 to 37 above it over long stretches, and at the 37 she turned back each time and never
    /// arrived (run r71, cell-walk; user: "fix the pathing"). The other games' meshes are left as they were.
    /// </summary>
    public float FloorFit { get; set; }

    static RcConfig Config(RcPartition partition = RcPartition.WATERSHED, float coarse = 1, float outline = 1, float floorFit = 0) => new(
        partition,
        cellSize: Env("CS", 8f) * coarse, cellHeight: Env("CH", 4f) * coarse,
        // Radius: how far the mesh keeps off walls. 24 for the other games; 12 with a floor fit (Oblivion's interiors), as
        // Skyrim's own keep theirs (open edges a median 13.5 to 29.7 off the wall in Morthal's guardhouse, Solitude's jail,
        // Helgen Keep, Bleak Falls Barrow: CLI nav-layout, 2026-11-16, where ours were a tight 28.5): Skyrim's jail guard
        // on a travel package walked out of the Imperial prison's cell clean in 4-5 s with 12 (probes p13, p14) where with
        // 24 it took 16-24 s, standing up to 10 s and set down once (p8, p9, p12).
        agentMaxSlope: Env("SLOPE", 50f), agentHeight: Env("HEIGHT", 128f), agentRadius: Env("RADIUS", floorFit > 0 ? 12f : 24f), agentMaxClimb: Env("CLIMB", 32f),
        regionMinSize: 8, regionMergeSize: 20,
        edgeMaxLen: 600f, edgeMaxError: 2f * outline,
        vertsPerPoly: 6,
        // (In voxels: Recast multiplies the distance by the cell size and the error by the cell height. With a floor
        // fit: heights every 24 units, never under 1.5 voxels, and the error that many units.)
        detailSampleDist: floorFit > 0 ? MathF.Max(1.5f, 24f / (Env("CS", 8f) * coarse)) : Env("DETAILDIST", 6f),
        detailSampleMaxError: floorFit > 0 ? floorFit / (Env("CH", 4f) * coarse) : Env("DETAILERR", 4f),
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
    /// <summary>Meshes Recast warned about (a bad triangulation, an unmerged hole) that measured whole all the same.</summary>
    public static int WarnedButWhole => warnedButWhole;
    static int warnedButWhole;
    /// <summary>Meshes made whole by cutting their regions into smaller blocks after every try had warned.</summary>
    public static int SmallerBlocks => smallerBlocks;
    static int smallerBlocks;
    [ThreadStatic] static int blockOverride;

    /// <summary>The counts, for a build's log: the retries and the tries that made them whole.</summary>
    public static string RecastSummary =>
        $"navmeshes: {retriangulated} made again after Recast's first try, {untriangulated} still not whole after every retry, {smallerBlocks} made clean by smaller blocks, {Interlocked.Read(ref outlinesTriangulated)} outlines Recast gave up on triangulated here ({Interlocked.Read(ref outlinesNotTriangulated)} neither could), {Interlocked.Read(ref contourCornersCleaned)} outline corners enclosing nothing taken out, {warnedButWhole} whole by measure though Recast warned, {turnedUpChosen} interiors from faces turned up (more actors on it), {actorsSnapped} actors moved onto their navmesh" +
        (retriangulated > 0 ? " (made whole by " + string.Join(", ", Enumerable.Range(1, Tries.Length - 1).Where(t => tryWins[t] > 0)
            .Select(t => $"{Tries[t].Partition.ToString().ToLowerInvariant()}{(Tries[t].Coarse > 1 ? " coarse" : "")}{(Tries[t].Error < 1 ? $" outline x{Tries[t].Error}" : "")}: {tryWins[t]}")) + ")" : "") +
        (SplitSummary.Length > 0 ? "; " + SplitSummary : "") +
        Environment.NewLine + "  " + FloorSummary;

    /// <summary>The tries in order: partitioning, voxel size (times the config's) and outline error (times the config's).</summary>
    static readonly (RcPartition Partition, float Coarse, float Error)[] Tries =
    [
        (RcPartition.WATERSHED, 1, 1), (RcPartition.LAYERS, 1, 1), (RcPartition.MONOTONE, 1, 1),
        (RcPartition.WATERSHED, 1, 0.5f), (RcPartition.LAYERS, 1, 0.5f), (RcPartition.MONOTONE, 1, 0.5f),
        (RcPartition.LAYERS, 1, 0), (RcPartition.MONOTONE, 1, 0),
        (RcPartition.WATERSHED, 2, 1), (RcPartition.LAYERS, 2, 1), (RcPartition.MONOTONE, 2, 1), (RcPartition.LAYERS, 2, 0), (RcPartition.MONOTONE, 2, 0),
    ];

    /// <summary>The order of <see cref="Tries"/> in the open-country setting: those that win it first (measured 2026-10-31
    /// on 7,673 cells of Daggerfall's overland: try 0 6,352, try 3 880, try 6 412, every other 30 in all; each win at 3 or
    /// 6 had paid for two or five failed builds first). A mesh is taken by the same tests in any order.</summary>
    static readonly int[] OpenTries = [0, 3, 6, 1, 2, 4, 5, 7, 8, 9, 10, 11, 12];

    /// <summary>Recast tries that threw, by what they threw. A try that throws is only the next try's cue, so nothing else
    /// shows it: measured 2026-10-31, every try of the overland's open setting threw, each cell falling back
    /// to the fine setting silently, at about seven times the time.</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> triesThrown = new();

    /// <summary>For the build's log: the tries that threw, most common first (empty when none did).</summary>
    public static string TriesThrownSummary => triesThrown.IsEmpty ? ""
        : $"Recast tries that threw: {triesThrown.Values.Sum():N0} ({string.Join("; ", triesThrown.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Value:N0} {kv.Key}"))})";

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
        float margin = 0, Hazards? hazards = null, bool open = false)
    {
        (RcPolyMesh Mesh, RcPolyMeshDetail? Detail)? best = null;
        FloorCheck bestFloor = default;
        // The spans each voxel size's tries make unwalkable (BuildOverLand), found by the first try of that size.
        var marks = new Dictionary<float, HashSet<long>>();
        int bestBad = int.MaxValue, bestOverlapping = 0, bestTry = -1;
        double bestCovered = 0;
        string bestWarnings = "";
        Exception? error = null;
        // One try: Recast's build in that try's settings and what it left out (nothing chosen here).
        TryOutcome Measure(int t)
        {
            var (partition, coarse, outline) = Tries[t];
            var tryWatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var bc = config(partition, coarse, outline);
                var r = RecastOutput.Keep(() => land is null && wound is null && hazards is null ? BuildSplit(geom, bc) : BuildOverLand(geom, bc, land, wound, marks, hazards), out var warnings);
                if (r.Mesh is null || r.ContourSet is null || r.CompactHeightfield is null) return new TryOutcome(t, r, warnings, 0, 0, default, null, Incomplete: true);
                int overlapping = Overlapping(r.CompactHeightfield);
                if (overlapping > 0) warnings += $"{overlapping} regions lie over themselves ({partition}){Environment.NewLine}";
                int bad = RecastOutput.Bad(warnings) + overlapping;
                // Only the floor the mesh is kept for: not the margin round an exterior cell (clipped off).
                var floor = UncoveredFloor(r.CompactHeightfield, r.ContourSet, r.Mesh, r.MeshDetail, (int)MathF.Round(margin / r.CompactHeightfield.cs));
                // TM_NAV_COLUMN=x,y (dev aid): the compact heightfield's spans at that point after Recast's filters: each
                // floor's height, the open space over it and its area (0 = not walkable), with its region.
                if (Environment.GetEnvironmentVariable("TM_NAV_COLUMN")?.Split(',') is [var cxs, var cys] && float.TryParse(cxs, out var colX) && float.TryParse(cys, out var colY))
                {
                    var chf = r.CompactHeightfield;
                    int qx = (int)MathF.Floor((colX - chf.bmin.X) / chf.cs), qz = (int)MathF.Floor((-colY - chf.bmin.Z) / chf.cs);
                    if (qx >= 0 && qz >= 0 && qx < chf.width && qz < chf.height)
                    {
                        var sb = new StringBuilder($"  column {colX:F0},{colY:F0} (try {t}):");
                        // The column and its four neighbours (west, north, east, south in Recast's x/z), each floor with the
                        // directions Recast connected it to.
                        (int Dx, int Dz, string Name)[] around = [(0, 0, "here"), (-1, 0, "x-1"), (0, 1, "z+1"), (1, 0, "x+1"), (0, -1, "z-1")];
                        foreach (var (dx, dz, nm) in around)
                        {
                            int cx = qx + dx, cz = qz + dz;
                            if (cx < 0 || cz < 0 || cx >= chf.width || cz >= chf.height) continue;
                            var cell = chf.cells[cx + cz * chf.width];
                            sb.Append($" [{nm}]");
                            for (int i = cell.index; i < cell.index + cell.count; i++)
                            {
                                var span = chf.spans[i];
                                string cons = string.Concat(Enumerable.Range(0, 4).Select(d => ((span.con >> (d * 6)) & 0x3f) != 0x3f ? "wnes"[d] : '-'));
                                sb.Append($" floor {chf.bmin.Y + span.y * chf.ch:F0} room {span.h * chf.ch:F0} area {chf.areas[i]} reg {span.reg} con {cons};");
                            }
                        }
                        Console.WriteLine(sb.ToString());
                        // The detail triangles over that point (the navmesh's faces) with their height there, and the
                        // polygon each belongs to with its corners' heights.
                        if (r.MeshDetail is { } dm)
                        {
                            var db = new StringBuilder($"  detail at {colX:F0},{colY:F0} (try {t}):");
                            float wx = colX, wz = -colY;
                            for (int m = 0; m < dm.nmeshes; m++)
                            {
                                int vb = dm.meshes[m * 4], tb = dm.meshes[m * 4 + 2], tc = dm.meshes[m * 4 + 3];
                                for (int k = 0; k < tc; k++)
                                {
                                    var p3 = Enumerable.Range(0, 3).Select(q => { int v = (vb + dm.tris[(tb + k) * 4 + q]) * 3; return (X: dm.verts[v], Y: dm.verts[v + 1], Z: dm.verts[v + 2]); }).ToArray();
                                    double den = (p3[1].Z - p3[2].Z) * (p3[0].X - p3[2].X) + (p3[2].X - p3[1].X) * (p3[0].Z - p3[2].Z);
                                    if (Math.Abs(den) < 1e-9) continue;
                                    double wa = ((p3[1].Z - p3[2].Z) * (wx - p3[2].X) + (p3[2].X - p3[1].X) * (wz - p3[2].Z)) / den;
                                    double wb = ((p3[2].Z - p3[0].Z) * (wx - p3[2].X) + (p3[0].X - p3[2].X) * (wz - p3[2].Z)) / den;
                                    if (wa < -0.02 || wb < -0.02 || wa + wb > 1.02) continue;
                                    double hy = wa * p3[0].Y + wb * p3[1].Y + (1 - wa - wb) * p3[2].Y;
                                    db.Append($" [poly {m} reg {(m < r.Mesh.npolys ? r.Mesh.regs[m] : -1)}: height {hy:F0}, corners {string.Join("/", p3.Select(c => c.Y.ToString("F0")))}]");
                                }
                            }
                            Console.WriteLine(db.ToString());
                        }
                    }
                }
                if (Trace)
                {
                    Console.WriteLine($"  recast try {t} ({partition}, voxels x{coarse}, outline x{outline}) at {Where(r.Mesh)}: {tryWatch.Elapsed.TotalSeconds:F1} s, {bad} bad ({overlapping} over themselves); {floor}{(floor.FarAt.Length > 0 ? $"; far at {floor.FarAt}" : "")}");
                    if (floor.None > 0) Console.Write(RegionDetail(r.CompactHeightfield, r.ContourSet, r.Mesh, floor.Where));
                }
                return new TryOutcome(t, r, warnings, overlapping, bad, floor, null);
            }
            catch (Exception e) { return new TryOutcome(t, null, "", 0, 0, default, e); }
        }
        // The tries in order. An interior's (no land, wound input or hazards: nothing shared between tries) after a first
        // that wasn't whole are built side by side on the idle cores, a wave at a time, the cheap ones first (2026-11-02:
        // Daggerfall's dungeons that needed a retry were made whole by tries 1, 2 and 5 only, 98 of them; the outline-0
        // tries took The Hold of Copperham 468 s one after another while its batch waited). They are taken in the same
        // order as one at a time, so the mesh is the same; a wave starts only when no try before it won.
        // Every cell with land takes the tries in the open country's order (2026-11-10): Oblivion's exterior cells were
        // made whole by try 3 in 4,353 cases and try 6 in 619, by tries 1 and 2 in 12 (Morrowind's: 1,583, 224 and 2),
        // each of those wins paying for two or five failed builds first, about a fifth of Oblivion's navmesh time.
        // TM_NAV_TRIES=n (dev aid): only the first n tries, to look at one quickly.
        var order = Enumerable.Range(0, Math.Clamp((int)Env("TRIES", Tries.Length), 1, Tries.Length)).Select(i => open || land is not null ? OpenTries[i] : i).ToList();
        bool sideBySide = land is null && wound is null && hazards is null;
        int[] waves = sideBySide ? [1, 4, 4, 4] : Enumerable.Repeat(1, order.Count).ToArray();
        int at = 0;
        foreach (int size in waves.Concat(Enumerable.Repeat(1, order.Count)))
        {
            if (at >= order.Count) break;
            var wave = order.Skip(at).Take(size).ToList();
            at += wave.Count;
            // The wave's tries side by side, each looked at in its turn as soon as it is made: a try that is whole is
            // taken without waiting for the later ones of its wave (their work is left to run out unused). Until
            // 2026-11-10 the whole wave was waited for: a Morrowind interior held its build over 30 minutes on one
            // core in a single try's RcMeshs.BuildPolyMesh (sampled on a running build), and one Oblivion interior
            // 31 minutes of every conversion.
            var making = wave.Count == 1 ? null : wave.Select(t => Task.Run(() => Measure(t))).ToArray();
            for (int member = 0; member < wave.Count; member++)
            {
                var o = making is null ? Measure(wave[0]) : making[member].Result;
                int t = o.Try;
                var (partition, coarse, outline) = Tries[t];
                if (o.Error is { } e)
                {
                    error ??= e;
                    triesThrown.AddOrUpdate($"{e.GetType().Name}: {e.Message.Split((char)10)[0]}", 1, (_, n) => n + 1);
                    continue;
                }
                var r = o.Result!;
                if (o.Incomplete) { if (t == 0) return r.Mesh is null ? null : (r.Mesh, r.MeshDetail); continue; }
                var (warnings, overlapping, bad, floor) = (o.Warnings, o.Overlapping, o.Bad, o.Floor);
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
        }
        if (best is null) throw error ?? new InvalidOperationException("Recast made no mesh");
        Record(bestFloor, best.Value.Mesh);
        long bestLost = bestFloor.None + bestFloor.Dropped + bestFloor.Far;
        // Whole only in coarser voxels, where no finer try could be made at all (one threw) or covered as much.
        // A mesh Recast still warned about after every try (a "Bad triangulation": it gave up on part of one region's
        // outline) is made again in the same settings with its regions cut into smaller blocks (SplitLarge at 24, then 12
        // voxels in place of 48): a block's outline is short and nearly convex, which Recast triangulates (user,
        // 2026-11-11, on Oblivion's six: "6 not whole and need fixed"; all six had every walkable span under a polygon).
        if (bestBad > 0 && bestOverlapping == 0 && bestTry >= 0)
            // The kept try's settings first, at each block size; then the other fine tries' (three of Oblivion's six
            // still warned at 24 and 12 in the kept try's settings alone).
            foreach (var (smaller, withTry) in new[] { 24, 12, 6, 4, 3 }.Select(bsz => (bsz, bestTry))
                .Concat(new[] { 12, 6, 3 }.SelectMany(bsz => Enumerable.Range(0, Tries.Length).Where(t => t != bestTry && Tries[t].Coarse == Tries[bestTry].Coarse).Select(t => (bsz, t)))))
            {
                TryOutcome o;
                blockOverride = smaller;
                try { o = Measure(withTry); } finally { blockOverride = 0; }
                if (o.Error is not null || o.Incomplete || o.Result?.Mesh is null) continue;
                long lostSmaller = o.Floor.None + o.Floor.Dropped + o.Floor.Far;
                if (o.Bad != 0 || lostSmaller > bestLost) continue;
                Interlocked.Increment(ref retriangulated);
                Interlocked.Increment(ref smallerBlocks);
                Record(o.Floor, o.Result.Mesh);
                if (lostSmaller == 0) return (o.Result.Mesh, o.Result.MeshDetail);
                (bestFloor, bestBad, best, bestWarnings, bestLost) = (o.Floor, 0, (o.Result.Mesh, o.Result.MeshDetail), o.Warnings, lostSmaller);
                break;
            }
        if (bestLost == 0 && bestBad > 0 && bestOverlapping == 0)
        {
            Interlocked.Increment(ref warnedButWhole);
            Console.Write($"Recast warned at {Where(best!.Value.Mesh)} in every setting and block size; every walkable span has a polygon (kept try {bestTry}):{Environment.NewLine}{bestWarnings}");
        }
        if (bestLost == 0 && bestOverlapping == 0)
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
    /// The floor of a doorway an inner door stands in (a gate, a room's door: no teleport), in Skyrim cell space: the
    /// door's foot as a convex outline (<paramref name="Corners"/>, x and y; its bounds across and a step through on
    /// either side) and the heights its floor lies between. Recast takes the agent's radius off every floor edge, so
    /// a doorway kept little more than its middle: the prisoner's cell gate in Oblivion's Imperial prison, 96 wide
    /// between its posts, stood over a neck of navmesh 40 wide with open edges either side (2026-11-16), and Skyrim's
    /// people never once went through it, whatever the door (its own gate, unowned, unscripted, or Skyrim's own
    /// FarmhouseJailDoor01 there): they stood before it and the game set them down on its far side. Skyrim's own
    /// navmesh runs out to the frame under its inner doors (Morthal's guardhouse jail door, 92 wide, over a strip 90
    /// wide). Inside these the floor is kept walkable as it was before the radius was taken off.
    /// </summary>
    public sealed record Doorway(System.Numerics.Vector2[] Corners, float Bottom, float Top);

    /// <summary>Recast's volumes standing for doorways (<see cref="Doorway"/>): kept walkable after the agent's radius
    /// comes off the floor, not given an area.</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RcConvexVolume, Doorway> DoorwayVolumes = new();

    /// <summary>Walkable spans kept in doorways that the agent's radius had taken off (<see cref="KeepDoorways"/>).</summary>
    public static long DoorwaySpans => Interlocked.Read(ref doorwaySpans);
    static long doorwaySpans;

    /// <summary>Inner doors' doorways added to interiors' navmesh input.</summary>
    public static long DoorwaysKept => Interlocked.Read(ref doorwaysKept);
    static long doorwaysKept;

    /// <summary>Each doorway's floor walkable again where it was before the agent's radius came off (<paramref name="walkable"/>:
    /// the areas as they were): every span over a voxel whose middle is inside the doorway's outline, between its heights.</summary>
    static void KeepDoorways(RcCompactHeightfield chf, int[] walkable, List<RcConvexVolume> doorways)
    {
        long kept = 0;
        foreach (var v in doorways)
        {
            int n = v.verts.Length / 3;
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (int k = 0; k < n; k++)
            {
                minX = MathF.Min(minX, v.verts[k * 3]); maxX = MathF.Max(maxX, v.verts[k * 3]);
                minZ = MathF.Min(minZ, v.verts[k * 3 + 2]); maxZ = MathF.Max(maxZ, v.verts[k * 3 + 2]);
            }
            int x0 = Math.Max(0, (int)MathF.Floor((minX - chf.bmin.X) / chf.cs)), x1 = Math.Min(chf.width - 1, (int)MathF.Floor((maxX - chf.bmin.X) / chf.cs));
            int z0 = Math.Max(0, (int)MathF.Floor((minZ - chf.bmin.Z) / chf.cs)), z1 = Math.Min(chf.height - 1, (int)MathF.Floor((maxZ - chf.bmin.Z) / chf.cs));
            int y0 = (int)MathF.Floor((v.hmin - chf.bmin.Y) / chf.ch), y1 = (int)MathF.Ceiling((v.hmax - chf.bmin.Y) / chf.ch);
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    float px = chf.bmin.X + (x + 0.5f) * chf.cs, pz = chf.bmin.Z + (z + 0.5f) * chf.cs;
                    if (!InOutline(v.verts, n, px, pz)) continue;
                    var c = chf.cells[x + z * chf.width];
                    for (int i = c.index; i < c.index + c.count; i++)
                    {
                        if (chf.areas[i] != 0 || walkable[i] == 0) continue;   // still walkable, or never was
                        if (chf.spans[i].y < y0 || chf.spans[i].y > y1) continue;
                        chf.areas[i] = walkable[i];
                        kept++;
                    }
                }
        }
        Interlocked.Add(ref doorwaySpans, kept);
    }

    /// <summary>Whether (x, z) of Recast's space is inside a convex volume's outline (its corners' x and z).</summary>
    static bool InOutline(float[] verts, int n, float x, float z)
    {
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            float xi = verts[i * 3], zi = verts[i * 3 + 2], xj = verts[j * 3], zj = verts[j * 3 + 2];
            if ((zi > z) != (zj > z) && x < (xj - xi) * (z - zi) / (zj - zi) + xi) inside = !inside;
        }
        return inside;
    }

    /// <summary>Recast's build of <paramref name="geom"/> (<see cref="BuildSplit(RcContext, int, int, IRcInputGeomProvider, RcConfig, RcHeightfield)"/>).</summary>
    static RcBuilderResult BuildSplit(IRcInputGeomProvider geom, RcBuilderConfig bc)
    {
        var ctx = new RcContext();
        return BuildSplit(ctx, bc.tileX, bc.tileZ, geom, bc.cfg, RcVoxelizations.BuildSolidHeightfield(ctx, geom, bc));
    }

    /// <summary>
    /// Recast's build from the solid heightfield on, step for step as RcBuilder.Build (DotRecast 2026.3.1), with the
    /// regions that lie over themselves split before their outlines are traced (<see cref="SplitOverlapping"/>; user,
    /// 2026-11-02: "the overlapping navmeshing needs fixed"). Recast finds them and only warns: its BuildRegions says
    /// "If overlapping regions were found during merging, split those regions" and then logs "rcBuildRegions: n overlapping
    /// regions". The outline of such a region runs over both floors and crosses itself, so part of it got no polygons and
    /// its detail mesh bridged the floors: The Hold of Copperham's region 475 held floors at 2,052 and 3,076 in the same
    /// columns and a navmesh triangle ran from one to the other (corners 3,080/3,080/2,056), 10,153 spans under no polygon
    /// and 6,550 far from it, and its retries took 469 s.
    /// </summary>
    static RcBuilderResult BuildSplit(RcContext ctx, int tileX, int tileZ, IRcInputGeomProvider geom, RcConfig cfg, RcHeightfield solid)
    {
        if (cfg.FilterLowHangingObstacles) RcFilters.FilterLowHangingWalkableObstacles(ctx, cfg.WalkableClimb, solid);
        if (cfg.FilterLedgeSpans) RcFilters.FilterLedgeSpans(ctx, cfg.WalkableHeight, cfg.WalkableClimb, solid);
        if (cfg.FilterWalkableLowHeightSpans) RcFilters.FilterWalkableLowHeightSpans(ctx, cfg.WalkableHeight, solid);
        var chf = RcCompacts.BuildCompactHeightfield(ctx, cfg.WalkableHeight, cfg.WalkableClimb, solid);
        var volumes = geom.ConvexVolumes();
        var doorways = volumes.Where(v => DoorwayVolumes.TryGetValue(v, out _)).ToList();
        int[]? walkable = doorways.Count > 0 ? (int[])chf.areas.Clone() : null;
        RcAreas.ErodeWalkableArea(ctx, cfg.WalkableRadius, chf);
        foreach (var vol in volumes) if (!DoorwayVolumes.TryGetValue(vol, out _)) RcAreas.MarkConvexPolyArea(ctx, vol.verts, vol.hmin, vol.hmax, vol.areaMod, chf);
        if (walkable is not null) KeepDoorways(chf, walkable, doorways);
        if (cfg.Partition == RcPartitionType.WATERSHED.Value)
        {
            RcRegions.BuildDistanceField(ctx, chf);
            RcRegions.BuildRegions(ctx, chf, cfg.MinRegionArea, cfg.MergeRegionArea);
        }
        else if (cfg.Partition == RcPartitionType.MONOTONE.Value) RcRegions.BuildRegionsMonotone(ctx, chf, cfg.MinRegionArea, cfg.MergeRegionArea);
        else RcRegions.BuildLayerRegions(ctx, chf, cfg.MinRegionArea);
        SplitOverlapping(chf, cfg.MinRegionArea);
        SplitLarge(chf, cfg.MinRegionArea);
        var cset = RcContours.BuildContours(ctx, chf, cfg.MaxSimplificationError, cfg.MaxEdgeLen, RcBuildContoursFlags.RC_CONTOUR_TESS_WALL_EDGES);
        CleanContours(cset);
        var (pmesh, dmesh) = MeshInParts(ctx, cset, chf, cfg);
        return new RcBuilderResult(tileX, tileZ, solid, chf, cset, pmesh, dmesh, ctx);
    }

    /// <summary>Meshes made in parts on several cores (<see cref="MeshInParts"/>), and the parts (for the build's log).</summary>
    public static long MeshesInParts => Interlocked.Read(ref meshesInParts);
    static long meshesInParts, meshParts;
    public static string PartsSummary => meshesInParts == 0 ? "" : $"meshes made in parts on several cores: {meshesInParts:N0}, in {Interlocked.Read(ref meshParts):N0} parts";

    /// <summary>
    /// The polygon mesh and its detail from the outlines, on several cores (user, 2026-11-10: "it needs to use more
    /// than one"): one mesh's outlines dealt into as many lots as there are cores (the biggest first, each to the
    /// lightest lot by its points squared), each lot's polygons and detail made by itself, and the lots joined as
    /// Recast joins the tiles of a tiled build (RcMeshs.MergePolyMeshes, which joins the shared corners and works out
    /// the neighbours again, and RcMeshDetails.MergePolyMeshDetails, in the same order). An outline's polygons depend
    /// on that outline alone, so the mesh is the same as made in one piece but for the order of its polygons. Before,
    /// a mesh was made on one core however big: a Morrowind interior over 30 minutes (sampled in
    /// RcMeshs.BuildPolyMesh). Each lot's warnings are written again on the calling thread, where a try counts them.
    /// TM_NAV_PARTS=1 (dev aid): in one piece, to compare.
    /// </summary>
    /// <summary>Outlines Recast could not triangulate that were triangulated here (<see cref="PolyMesh"/>), and those
    /// neither could.</summary>
    public static long OutlinesTriangulatedHere => Interlocked.Read(ref outlinesTriangulated);
    static long outlinesTriangulated, outlinesNotTriangulated;

    /// <summary>
    /// Recast's polygon mesh of an outline set, with every outline it gives up on ("BuildPolyMesh: Bad triangulation
    /// Contour n": its ear clipping finds no ear and keeps only the part done) triangulated here instead and given
    /// back to it as one small outline a triangle, so its mesh has the whole outline (user, 2026-11-11: "fix all bad
    /// trianglation when found"). The outline set's list is replaced, not changed: other lots and the checks after
    /// keep the outlines as Recast made them.
    /// </summary>
    static RcPolyMesh PolyMesh(RcContext ctx, RcContourSet cset, int maxVertsPerPoly)
    {
        for (int round = 0; ; round++)
        {
            var mesh = RecastOutput.Keep(() => RcMeshs.BuildPolyMesh(ctx, cset, maxVertsPerPoly), out string said);
            var bad = BadContour.Matches(said).Select(m => int.Parse(m.Groups[1].Value)).Distinct().Where(k => k >= 0 && k < cset.conts.Count).OrderByDescending(k => k).ToList();
            var replaced = new List<int>();
            if (round < 3 && bad.Count > 0)
            {
                var conts = new List<RcContour>(cset.conts);
                foreach (int k in bad)
                {
                    if (Triangles(conts[k]) is not { } tris) { Interlocked.Increment(ref outlinesNotTriangulated); continue; }
                    conts.RemoveAt(k);
                    conts.AddRange(tris);
                    replaced.Add(k);
                    Interlocked.Increment(ref outlinesTriangulated);
                }
                if (replaced.Count > 0) { cset.conts = conts; continue; }
            }
            // What Recast said that stands: written where the caller's own keeping hears it.
            if (said.Length > 0) Console.Write(said);
            return mesh;
        }
    }

    static readonly System.Text.RegularExpressions.Regex BadContour = new(@"Bad triangulation Contour (\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// An outline as triangles, each an outline of its own (in the outline's winding, its region and area): ear clipping
    /// in whole numbers. A corner is an ear when it turns the outline's way and no other corner of the outline lies in
    /// or on its triangle (corners at the same spot as one of the triangle's own don't count: Recast joins a hole to its
    /// outline through a way in and out along one line, so its corners come twice); a corner that turns nowhere is
    /// dropped. Null when no ear is left before the outline is done (an outline that crosses itself).
    /// </summary>
    static List<RcContour>? Triangles(RcContour c)
    {
        int n = c.nverts;
        if (n < 3) return [];
        long X(int k) => c.verts[k * 4];
        long Z(int k) => c.verts[k * 4 + 2];
        long Cross(int a, int b, int d) => (X(b) - X(a)) * (Z(d) - Z(a)) - (Z(b) - Z(a)) * (X(d) - X(a));
        long area2 = 0;
        for (int k = 0; k < n; k++) { int m = (k + 1) % n; area2 += X(k) * Z(m) - X(m) * Z(k); }
        if (area2 == 0) return [];
        int way = Math.Sign(area2);
        var left = Enumerable.Range(0, n).ToList();
        var tris = new List<(int A, int B, int D)>();
        while (left.Count > 3)
        {
            bool clipped = false;
            for (int k = 0; k < left.Count && !clipped; k++)
            {
                int a = left[(k + left.Count - 1) % left.Count], b = left[k], d = left[(k + 1) % left.Count];
                long turn = Cross(a, b, d) * way;
                if (turn < 0) continue;
                if (turn == 0) { left.RemoveAt(k); clipped = true; break; }
                bool blocked = false;
                foreach (int e in left)
                {
                    if (e == a || e == b || e == d) continue;
                    if ((X(e) == X(a) && Z(e) == Z(a)) || (X(e) == X(b) && Z(e) == Z(b)) || (X(e) == X(d) && Z(e) == Z(d))) continue;
                    if (Cross(a, b, e) * way >= 0 && Cross(b, d, e) * way >= 0 && Cross(d, a, e) * way >= 0) { blocked = true; break; }
                }
                if (blocked) continue;
                tris.Add((a, b, d));
                left.RemoveAt(k);
                clipped = true;
            }
            if (!clipped) return null;
        }
        if (left.Count == 3 && Cross(left[0], left[1], left[2]) * way > 0) tris.Add((left[0], left[1], left[2]));
        var made = new List<RcContour>(tris.Count);
        foreach (var (a, b, d) in tris)
        {
            var v = new int[12];
            int at = 0;
            foreach (int k in new[] { a, b, d }) { Array.Copy(c.verts, k * 4, v, at, 4); at += 4; }
            made.Add(new RcContour { verts = v, nverts = 3, rverts = (int[])v.Clone(), nrverts = 3, reg = c.reg, area = c.area });
        }
        return made;
    }

    static (RcPolyMesh Mesh, RcPolyMeshDetail? Detail) MeshInParts(RcContext ctx, RcContourSet cset, RcCompactHeightfield chf, RcConfig cfg)
    {
        RcPolyMeshDetail? Detail(RcContext c, RcPolyMesh m) => cfg.BuildMeshDetail ? RcMeshDetails.BuildPolyMeshDetail(c, m, chf, cfg.DetailSampleDist, cfg.DetailSampleMaxError) : null;
        int most = (int)Env("PARTS", Environment.ProcessorCount);
        int parts = Math.Min(most, cset.conts.Count / 4);
        if (parts < 2)
        {
            var whole = PolyMesh(ctx, cset, cfg.MaxVertsPerPoly);
            return (whole, Detail(ctx, whole));
        }
        var lots = Enumerable.Range(0, parts).Select(_ => new List<RcContour>()).ToArray();
        var weight = new double[parts];
        foreach (var c in cset.conts.OrderByDescending(c => c.nverts))
        {
            int to = Array.IndexOf(weight, weight.Min());
            lots[to].Add(c);
            weight[to] += (double)c.nverts * c.nverts + 1;
        }
        var meshes = new RcPolyMesh?[parts];
        var details = new RcPolyMeshDetail?[parts];
        var said = new string[parts];
        Parallel.For(0, parts, new ParallelOptions { MaxDegreeOfParallelism = parts }, i =>
        {
            meshes[i] = RecastOutput.Keep(() =>
            {
                var c = new RcContext();
                var lot = new RcContourSet
                {
                    conts = lots[i], bmin = cset.bmin, bmax = cset.bmax, cs = cset.cs, ch = cset.ch,
                    width = cset.width, height = cset.height, borderSize = cset.borderSize, maxError = cset.maxError,
                };
                var m = PolyMesh(c, lot, cfg.MaxVertsPerPoly);
                if (m.npolys > 0) details[i] = Detail(c, m);
                return m;
            }, out said[i]);
        });
        foreach (var s in said) if (s.Length > 0) Console.Write(s);
        var keep = Enumerable.Range(0, parts).Where(i => meshes[i] is { npolys: > 0 }).ToArray();
        if (keep.Length == 0)
        {
            var whole = PolyMesh(ctx, cset, cfg.MaxVertsPerPoly);
            return (whole, Detail(ctx, whole));
        }
        Interlocked.Increment(ref meshesInParts);
        Interlocked.Add(ref meshParts, keep.Length);
        if (keep.Length == 1) return (meshes[keep[0]]!, details[keep[0]]);
        var joined = RcMeshs.MergePolyMeshes(ctx, keep.Select(i => meshes[i]!).ToArray(), keep.Length);
        var joinedDetail = cfg.BuildMeshDetail ? RcMeshDetails.MergePolyMeshDetails(ctx, keep.Select(i => details[i]!).ToArray(), keep.Length) : null;
        return (joined, joinedDetail);
    }

    /// <summary>Regions split for lying over themselves (<see cref="SplitOverlapping"/>), and the pieces made of them.</summary>
    public static long RegionsSplit => Interlocked.Read(ref regionsSplit);
    static long regionsSplit, regionPieces;

    /// <summary>For the build's log.</summary>
    public static string SplitSummary => regionsSplit == 0 ? "" : $"regions over themselves split: {regionsSplit:N0}, into {Interlocked.Read(ref regionPieces):N0} pieces";

    // Recast's directions: west, north, east, south in its x/z (rcGetDirOffsetX/Y).
    static readonly int[] DirX = [-1, 0, 1, 0], DirZ = [0, 1, 0, -1];

    /// <summary>
    /// Each region with two spans in one column (<see cref="Overlapping"/>) made into pieces that have none: grown from
    /// its lowest span over its connected spans, a piece never taking a second span in a column it has, then the next
    /// piece from the lowest span left, and so on (a stair that turns back over its foot: the flight under, then the one
    /// over it). Pieces smaller than Recast's smallest region join a piece of the same region beside them where their
    /// columns don't clash, as Recast would merge them. Returns the regions split.
    /// </summary>
    static int SplitOverlapping(RcCompactHeightfield chf, int minArea)
    {
        int w = chf.width, h = chf.height, n = chf.spanCount;
        var over = new HashSet<int>();
        int maxReg = 0;
        var spanX = new int[n];
        var spanZ = new int[n];
        for (int z = 0; z < h; z++)
            for (int x = 0; x < w; x++)
            {
                var c = chf.cells[x + z * w];
                for (int i = c.index, end = c.index + c.count; i < end; i++)
                {
                    spanX[i] = x; spanZ[i] = z;
                    int r = chf.spans[i].reg;
                    if (r == 0 || (r & RcRecast.RC_BORDER_REG) != 0) continue;
                    maxReg = Math.Max(maxReg, r);
                    for (int j = i + 1; j < end; j++)
                        if (chf.spans[j].reg == r) over.Add(r);
                }
            }
        if (over.Count == 0) return 0;
        int Col(int i) => spanX[i] + spanZ[i] * w;
        int Neighbour(int i, int dir)
        {
            int con = (chf.spans[i].con >> (dir * 6)) & 0x3f;
            if (con == 0x3f) return -1;
            int nx = spanX[i] + DirX[dir], nz = spanZ[i] + DirZ[dir];
            return chf.cells[nx + nz * w].index + con;
        }
        var piece = new int[n];
        Array.Fill(piece, -1);
        var columns = new Dictionary<int, HashSet<int>>();
        var members = new Dictionary<int, List<int>>();
        var original = new Dictionary<int, int>();
        int next = maxReg + 1;
        // The lowest spans first: a region's spans by height, so each piece starts on the floor beneath.
        var order = Enumerable.Range(0, n).Where(i => over.Contains(chf.spans[i].reg)).OrderBy(i => chf.spans[i].y).ToList();
        var queue = new Queue<int>();
        foreach (int seed in order)
        {
            if (piece[seed] >= 0) continue;
            int id = next++, reg = chf.spans[seed].reg;
            var taken = new HashSet<int> { Col(seed) };
            var list = new List<int> { seed };
            piece[seed] = id;
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                int s = queue.Dequeue();
                for (int dir = 0; dir < 4; dir++)
                {
                    int ni = Neighbour(s, dir);
                    if (ni < 0 || piece[ni] >= 0 || chf.spans[ni].reg != reg || !taken.Add(Col(ni))) continue;
                    piece[ni] = id;
                    list.Add(ni);
                    queue.Enqueue(ni);
                }
            }
            columns[id] = taken; members[id] = list; original[id] = reg;
        }
        // Small pieces into a piece of the same region beside them (the biggest whose columns they don't share).
        foreach (int id in members.Keys.OrderBy(k => members[k].Count).ToList())
        {
            if (!members.TryGetValue(id, out var list) || list.Count >= minArea) continue;
            int best = -1;
            foreach (int s in list)
                for (int dir = 0; dir < 4; dir++)
                {
                    int ni = Neighbour(s, dir);
                    if (ni < 0 || piece[ni] < 0 || piece[ni] == id || original[piece[ni]] != original[id]) continue;
                    int other = piece[ni];
                    if ((best < 0 || members[other].Count > members[best].Count) && !columns[other].Overlaps(columns[id])) best = other;
                }
            if (best < 0) continue;
            foreach (int s in list) piece[s] = best;
            members[best].AddRange(list);
            columns[best].UnionWith(columns[id]);
            members.Remove(id); columns.Remove(id);
        }
        // Ids in order from after the last region (Recast's border flag is the top bit: none may reach it).
        var ids = members.Keys.Order().Select((k, i) => (k, i)).ToDictionary(t => t.k, t => maxReg + 1 + t.i);
        if (maxReg + 1 + ids.Count >= RcRecast.RC_BORDER_REG) return 0;
        foreach (var (id, list) in members)
            foreach (int s in list)
                chf.spans[s] = RcCompactSpanBuilder.NewBuilder(ref chf.spans[s]).WithReg(ids[id]).Build();
        chf.maxRegions = maxReg + 1 + ids.Count;
        Interlocked.Add(ref regionsSplit, over.Count);
        Interlocked.Add(ref regionPieces, ids.Count);
        return over.Count;
    }

    /// <summary>Outline corners taken out as doubled or as spikes (<see cref="CleanContours"/>).</summary>
    public static long ContourCornersCleaned => Interlocked.Read(ref contourCornersCleaned);
    static long contourCornersCleaned;

    /// <summary>
    /// An outline's corners that enclose nothing, taken out before Recast triangulates it: the same corner twice
    /// running, and a spike (out to a corner and straight back to the one before). Recast's ear clipping finds no ear
    /// at such a corner and gives the outline up part-way ("BuildPolyMesh: Bad triangulation Contour n"); with them out
    /// the outline is the same area. One of Oblivion's exterior cells warned so in every partitioning, outline error
    /// and block size (2026-11-11, at 4743,5555, every walkable span under a polygon all the same).
    /// </summary>
    static void CleanContours(RcContourSet cset)
    {
        foreach (var c in cset.conts)
        {
            if (c.nverts < 3) continue;
            var v = new List<(int X, int Y, int Z, int F)>(c.nverts);
            for (int i = 0; i < c.nverts; i++) v.Add((c.verts[i * 4], c.verts[i * 4 + 1], c.verts[i * 4 + 2], c.verts[i * 4 + 3]));
            int before = v.Count;
            bool changed = true;
            while (changed && v.Count >= 3)
            {
                changed = false;
                for (int i = 0; i < v.Count && v.Count >= 3; i++)
                {
                    var p = v[(i + v.Count - 1) % v.Count]; var q = v[i]; var n = v[(i + 1) % v.Count];
                    if ((q.X == p.X && q.Z == p.Z) || (p.X == n.X && p.Z == n.Z)) { v.RemoveAt(i); i--; changed = true; }
                }
            }
            if (v.Count != before)
            {
                Interlocked.Add(ref contourCornersCleaned, before - v.Count);
                if (v.Count < 3) { c.nverts = 0; continue; }
                Store(c, v);
            }
        }
        // Not done: an outline that comes back to a corner it has been through is not split in two. Recast joins a
        // region's holes to its outline by such a way in and out, and split there the hole becomes an outline wound
        // the wrong way (tried 2026-11-11 on Oblivion's OBDarkFissure: 51 outlines split, ten bad triangulations where
        // there had been one).
    }

    static void Store(RcContour c, List<(int X, int Y, int Z, int F)> v)
    {
        var flat = new int[v.Count * 4];
        for (int i = 0; i < v.Count; i++) { flat[i * 4] = v[i].X; flat[i * 4 + 1] = v[i].Y; flat[i * 4 + 2] = v[i].Z; flat[i * 4 + 3] = v[i].F; }
        c.verts = flat;
        c.nverts = v.Count;
    }

    /// <summary>Regions cut for their size (<see cref="SplitLarge"/>), and the pieces made of them (for the build's log).</summary>
    public static string LargeSummary => largeSplit == 0 ? "" : $"regions cut for their size: {Interlocked.Read(ref largeSplit):N0}, into {Interlocked.Read(ref largePieces):N0} pieces";
    static long largeSplit, largePieces;

    /// <summary>
    /// Regions wider than a block and a half (a block: 48 voxels, TM_NAV_BLOCK) cut along the block grid into connected
    /// pieces, each a region of its own. Recast merges an outline's triangles into polygons by looking at every pair
    /// again after each merge, which for one region covering a great hall or cavern floor is the cube of its triangles
    /// on a single core: a Morrowind interior's last navmesh took 4,777 s of its build's 5,231 (2026-11-10), with the
    /// outlines dealt over the cores (<see cref="MeshInParts"/>) still one core for the one great outline. Cut up, no
    /// outline is big and the lots are even. Pieces smaller than Recast's smallest region join a piece of their
    /// region beside them. The floor covered is the same; the polygons meet along the cuts.
    /// </summary>
    static int SplitLarge(RcCompactHeightfield chf, int minArea)
    {
        int block = blockOverride > 0 ? blockOverride : (int)Env("BLOCK", 48);
        if (block <= 0) return 0;
        int w = chf.width, h = chf.height, n = chf.spanCount;
        var spanX = new int[n];
        var spanZ = new int[n];
        var box = new Dictionary<int, (int X0, int Z0, int X1, int Z1)>();
        int maxReg = 0;
        for (int z = 0; z < h; z++)
            for (int x = 0; x < w; x++)
            {
                var c = chf.cells[x + z * w];
                for (int i = c.index, end = c.index + c.count; i < end; i++)
                {
                    spanX[i] = x; spanZ[i] = z;
                    int r = chf.spans[i].reg;
                    if (r == 0 || (r & RcRecast.RC_BORDER_REG) != 0) continue;
                    maxReg = Math.Max(maxReg, r);
                    box[r] = box.TryGetValue(r, out var b) ? (Math.Min(b.X0, x), Math.Min(b.Z0, z), Math.Max(b.X1, x), Math.Max(b.Z1, z)) : (x, z, x, z);
                }
            }
        var large = box.Where(kv => kv.Value.X1 - kv.Value.X0 > block * 3 / 2 || kv.Value.Z1 - kv.Value.Z0 > block * 3 / 2).Select(kv => kv.Key).ToHashSet();
        if (large.Count == 0) return 0;
        int Neighbour(int i, int dir)
        {
            int con = (chf.spans[i].con >> (dir * 6)) & 0x3f;
            if (con == 0x3f) return -1;
            int nx = spanX[i] + DirX[dir], nz = spanZ[i] + DirZ[dir];
            return chf.cells[nx + nz * w].index + con;
        }
        var piece = new int[n];
        Array.Fill(piece, -1);
        var members = new Dictionary<int, List<int>>();
        var original = new Dictionary<int, int>();
        int next = 0;
        var queue = new Queue<int>();
        for (int seed = 0; seed < n; seed++)
        {
            int reg = chf.spans[seed].reg;
            if (piece[seed] >= 0 || !large.Contains(reg)) continue;
            int bx = spanX[seed] / block, bz = spanZ[seed] / block, id = next++;
            var list = new List<int> { seed };
            piece[seed] = id;
            queue.Enqueue(seed);
            while (queue.Count > 0)
            {
                int sp = queue.Dequeue();
                for (int dir = 0; dir < 4; dir++)
                {
                    int ni = Neighbour(sp, dir);
                    if (ni < 0 || piece[ni] >= 0 || chf.spans[ni].reg != reg || spanX[ni] / block != bx || spanZ[ni] / block != bz) continue;
                    piece[ni] = id;
                    list.Add(ni);
                    queue.Enqueue(ni);
                }
            }
            members[id] = list; original[id] = reg;
        }
        // Small pieces into the biggest piece of the same region beside them.
        foreach (int id in members.Keys.OrderBy(k => members[k].Count).ToList())
        {
            if (!members.TryGetValue(id, out var list) || list.Count >= Math.Max(minArea, 8)) continue;
            int best = -1;
            foreach (int sp in list)
                for (int dir = 0; dir < 4; dir++)
                {
                    int ni = Neighbour(sp, dir);
                    if (ni < 0 || piece[ni] < 0 || piece[ni] == id || original[piece[ni]] != original[id]) continue;
                    if (best < 0 || members[piece[ni]].Count > members[best].Count) best = piece[ni];
                }
            if (best < 0) continue;
            foreach (int sp in list) piece[sp] = best;
            members[best].AddRange(list);
            members.Remove(id);
        }
        // Ids from after the last region (Recast's border flag is the top bit: none may reach it).
        if (maxReg + 1 + members.Count >= RcRecast.RC_BORDER_REG) return 0;
        var ids = members.Keys.Order().Select((k, i) => (k, i)).ToDictionary(t => t.k, t => maxReg + 1 + t.i);
        foreach (var (id, list) in members)
            foreach (int sp in list)
                chf.spans[sp] = RcCompactSpanBuilder.NewBuilder(ref chf.spans[sp]).WithReg(ids[id]).Build();
        chf.maxRegions = maxReg + 1 + ids.Count;
        Interlocked.Add(ref largeSplit, large.Count);
        Interlocked.Add(ref largePieces, ids.Count);
        return large.Count;
    }

    /// <summary>One Recast try as <see cref="BuildMesh"/> measured it: its build (null when it threw), Recast's warnings, the
    /// regions over themselves, the bad count and the floor left out.</summary>
    sealed record TryOutcome(int Try, RcBuilderResult? Result, string Warnings, int Overlapping, int Bad, FloorCheck Floor, Exception? Error, bool Incomplete = false);

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
        // (x is a span's column, never below 0: taken as unsigned, so its sign can't spill into z's and top's bits.)
        static long Key(int x, int z, int top) => (uint)x | ((long)z << 16) | ((long)top << 32);
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
                    RecastOutput.Say(sb.ToString());
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
        var built = BuildSplit(ctx, bc.tileX, bc.tileZ, geom, bc.cfg, solid);
        Interlocked.Add(ref recastTicks, recast.ElapsedTicks);
        return built;
    }

    /// <summary>Spans tested for being shut in, and the time (all threads) spent marking spans and in Recast.</summary>
    static long shutTested, markingTicks, recastTicks;

    /// <summary>For the build's log: the shut-in test's work and where the time went.</summary>
    public static string MarkingSummary =>
        $"under-land and shut-in marking: {Interlocked.Read(ref shutTested):N0} spans tested, {WoundInput.Rays:N0} rays, {WoundInput.Tests:N0} triangles tested; " +
        $"{Interlocked.Read(ref markingTicks) / (double)System.Diagnostics.Stopwatch.Frequency:F0} s marking, {Interlocked.Read(ref recastTicks) / (double)System.Diagnostics.Stopwatch.Frequency:F0} s in Recast (all threads)" + (WoundInput.CheckSummary.Length > 0 ? "; " + WoundInput.CheckSummary : "");

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
    /// <summary>The interior being built (its EditorID), for the messages: it goes with the work onto the worker threads.</summary>
    static readonly AsyncLocal<string?> Building = new();

    static string Where(RcPolyMesh mesh)
    {
        float x = (mesh.bmin.X + mesh.bmax.X) / 2, y = -(mesh.bmin.Z + mesh.bmax.Z) / 2;
        if (Building.Value is { Length: > 0 } interior) return $"{x:F0},{y:F0} in {interior}";
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
        static TextWriter? original;

        /// <summary>A line to the console as it was, past any capture (the dev traces run inside a build).</summary>
        public static void Say(string line) { lock (Gate) (original ?? Console.Out).WriteLine(line); }

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
                if (!installed) { original = Console.Out; Console.SetOut(new RecastOutput(Console.Out)); Console.SetError(new RecastOutput(Console.Error)); installed = true; }
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
        Func<P3Float, bool>? underwater = null, Hazards? hazards = null, bool snapActors = false) =>
        BeginInterior(cell, polygons, loadDoors, keepAround, keepAlso, underwater, background: false, snapActors: snapActors, hazards: hazards).Finish();

    /// <summary>
    /// An interior's navmesh from its faces as wound (a ceiling or an underside is an obstacle), or, when that leaves an
    /// actor more than 64 units off it and every face turned up puts more of the cell's actors on it, from those: a
    /// model's collision can have a floor wound face down (2026-10-19, Morrowind's In_DE_shipwreckUL_LG: an actor 30 over
    /// a deck facing down), while turned up every ceiling under open space became a floor above it (Oblivion: 22 load
    /// doors' nearest navmesh lay on the ceiling they were set in; Morrowind's interiors: twice the walkable floor, 139
    /// million spans to 70, for five more actors near it). Load doors don't choose: a trapdoor is in the ceiling.
    /// </summary>
    public Result BuildInterior(Cell cell, IReadOnlyList<(Vector3[] Points, Vector3 Normal)> asWound, IReadOnlyList<(Vector3[] Points, Vector3 Normal)> turnedUp,
        IEnumerable<IPlacedObjectGetter> loadDoors, P3Float? keepAround, IReadOnlyList<P3Float> actors, Hazards? hazards = null, Func<P3Float, bool>? underwater = null) =>
        BeginInterior(cell, asWound, turnedUp, loadDoors, keepAround, actors, hazards, underwater, background: false).Finish();

    /// <summary>
    /// <see cref="BuildInterior(Cell, IReadOnlyList{ValueTuple{Vector3[], Vector3}}, IReadOnlyList{ValueTuple{Vector3[], Vector3}}, IEnumerable{IPlacedObjectGetter}, P3Float?, IReadOnlyList{P3Float}, Hazards?, Func{P3Float, bool}?)"/>
    /// begun: with <paramref name="background"/> both meshes are made on a worker thread (the record reserved now, in
    /// the cell's order; the inputs and the cell left alone until <see cref="PendingInterior.Finish"/>), so a game's
    /// interiors are made on every core (2026-10-22: Oblivion's 1,916 one at a time took 46 minutes on one of eight).
    /// </summary>
    public PendingInterior BeginInterior(Cell cell, IReadOnlyList<(Vector3[] Points, Vector3 Normal)> asWound, IReadOnlyList<(Vector3[] Points, Vector3 Normal)> turnedUp,
        IEnumerable<IPlacedObjectGetter> loadDoors, P3Float? keepAround, IReadOnlyList<P3Float> actors, Hazards? hazards, Func<P3Float, bool>? underwater, bool background,
        IReadOnlyList<Doorway>? doorways = null)
    {
        Building.Value = cell.EditorID;
        float fit = FloorFit;
        var doors = loadDoors.ToList();
        NoteDoors(doors);
        var actorList = actors.ToList();
        // Actors off it moved onto it (SnapToMesh: Morrowind's Prelude Shipwreck has a skeleton and a rat 85 under its
        // deck, and Mortrag Glacier's werewolves stand 90-170 over their floor); those in water stay, as they swim.
        var pending = new PendingInterior { Owner = this, Cell = cell, SnapActors = true, StaysPut = underwater };
        pending.Run = () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var made = Generate(cell, asWound, doors, keepAround, actorList, underwater, hazards, floorFit: fit, doorways: doorways);
            pending.Timing = "as wound: " + made.Timing;
            int On(Generated g) => actorList.Count(a => Distance(g.Data, a) <= 64);
            int on = On(made);
            if (on < actorList.Count)
            {
                Generated? alt = null;
                var altWatch = System.Diagnostics.Stopwatch.StartNew();
                try { alt = Generate(cell, turnedUp, doors, keepAround, actorList, underwater, hazards, floorFit: fit, doorways: doorways); }
                catch (Exception) { }
                pending.Timing += $"; turned up ({on} of {actorList.Count} actors on it as wound): {(alt is null ? $"failed after {altWatch.Elapsed.TotalSeconds:F1} s" : alt.Timing)}";
                if (alt is not null && On(alt) > on) { made = alt; Interlocked.Increment(ref turnedUpChosen); }
            }
            pending.Seconds = sw.Elapsed.TotalSeconds;
            return made;
        };
        if (background)
        {
            pending.Navm = new NavigationMesh(Target);
            cell.NavigationMeshes.Add(pending.Navm);
            pending.Work = Task.Run(pending.Run);
        }
        return pending;
    }

    /// <summary>
    /// An interior's mesh in navmeshes: each piece of it (triangles joined by edges) that holds a load door, the biggest
    /// piece apart, in a navmesh of its own; the rest together, first. Skyrim's people go through a load door from one
    /// navmesh to another, as Skyrim's own cells join theirs: the Imperial prison's Mythic Dawn assassins walked their
    /// room for twelve minutes and never took its door, whose other side is in the same cell on the same navmesh (runs
    /// r103 and r104, 2026-11-16; Oblivion's people take such a door: its path grid's doors join wherever they lead).
    /// </summary>
    static List<(NavigationMeshData Data, List<P3Float> Verts)> DoorPieces(NavigationMeshData data, List<P3Float> verts)
    {
        var tris = data.Triangles;
        int n = tris.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int i = 0; i < n; i++)
            foreach (short e in new[] { tris[i].EdgeLink_0_1, tris[i].EdgeLink_1_2, tris[i].EdgeLink_2_0 })
                if (e >= 0 && e < n) parent[Find(i)] = Find(e);
        var size = new Dictionary<int, int>();
        for (int i = 0; i < n; i++) size[Find(i)] = size.GetValueOrDefault(Find(i)) + 1;
        if (size.Count < 2) return [(data, verts)];
        int biggest = size.MaxBy(kv => kv.Value).Key;
        var own = data.DoorTriangles.Select(d => Find(d.TriangleBeforeDoor)).Where(p => p != biggest).Distinct().ToList();
        if (own.Count == 0) return [(data, verts)];
        // Pieces in order: the rest together, then each door piece.
        var groups = new List<Func<int, bool>> { i => !own.Contains(Find(i)) };
        foreach (int p in own) groups.Add(i => Find(i) == p);
        var list = new List<(NavigationMeshData, List<P3Float>)>();
        foreach (var inGroup in groups)
        {
            var keep = Enumerable.Range(0, n).Where(inGroup).ToList();
            var triIndex = new Dictionary<int, int>();
            for (int k = 0; k < keep.Count; k++) triIndex[keep[k]] = k;
            var vertIndex = new Dictionary<int, int>();
            var pv = new List<P3Float>();
            int Vert(int v) { if (!vertIndex.TryGetValue(v, out int at)) { at = vertIndex[v] = pv.Count; pv.Add(verts[v]); } return at; }
            var piece = new NavigationMeshData { NavmeshVersion = data.NavmeshVersion, CrcHash = data.CrcHash, Parent = data.Parent.DeepCopy() };
            var ptris = new List<(int A, int B, int C)>();
            short Link(short e) => e >= 0 && triIndex.TryGetValue(e, out int to) ? (short)to : (short)-1;
            foreach (int i in keep)
            {
                var t = tris[i];
                var corners = (Vert(t.Vertices.X), Vert(t.Vertices.Y), Vert(t.Vertices.Z));
                ptris.Add(corners);
                piece.Triangles.Add(new NavmeshTriangle
                {
                    Vertices = new P3Int16((short)corners.Item1, (short)corners.Item2, (short)corners.Item3),
                    EdgeLink_0_1 = Link(t.EdgeLink_0_1), EdgeLink_1_2 = Link(t.EdgeLink_1_2), EdgeLink_2_0 = Link(t.EdgeLink_2_0),
                    Flags = t.Flags, CoverFlags = t.CoverFlags,
                });
            }
            piece.Vertices.AddRange(pv);
            foreach (var d in data.DoorTriangles)
                if (triIndex.TryGetValue(d.TriangleBeforeDoor, out int at))
                    piece.DoorTriangles.Add(new DoorTriangle { TriangleBeforeDoor = (short)at, Unknown = d.Unknown, Door = new FormLink<IPlacedObjectGetter>(d.Door.FormKey) });
            WriteGrid(piece, pv, ptris);
            list.Add((piece, pv));
        }
        return list;
    }

    /// <summary>The load doors <paramref name="data"/>'s door triangles name, of <paramref name="linked"/>.</summary>
    static List<IPlacedObjectGetter> LinkedOf(NavigationMeshData data, List<IPlacedObjectGetter> linked)
    {
        var keys = data.DoorTriangles.Select(d => d.Door.FormKey).ToHashSet();
        return linked.Where(d => keys.Contains(d.FormKey)).ToList();
    }

    /// <summary>Navmeshes made for an interior's pieces reached only through a load door (<see cref="DoorPieces"/>).</summary>
    public static long DoorPieceMeshes => Interlocked.Read(ref doorPieceMeshes);
    static long doorPieceMeshes;

    /// <summary>Interiors whose navmesh was made from every face turned up (the choice above).</summary>
    public static int TurnedUpChosen => turnedUpChosen;
    static int turnedUpChosen;

    /// <summary>Actors moved onto their interior's navmesh (SnapToMesh), over every builder.</summary>
    public static int ActorsSnapped => actorsSnapped;
    static int actorsSnapped;

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
        /// <summary>Whether the mesh is made (on its worker thread; one made in the foreground, once finished).</summary>
        public bool IsMade => result is not null || Work is { IsCompleted: true };

        /// <summary>Waits until one of <paramref name="pending"/> made in the background is made.</summary>
        public static void WaitAny(IEnumerable<PendingInterior> pending)
        {
            var work = pending.Where(p => !p.IsMade && p.Work is not null).Select(p => (Task)p.Work!).ToArray();
            if (work.Length > 0) Task.WaitAny(work);
        }

        /// <summary>Seconds the mesh took to make (on whichever thread made it).</summary>
        public double Seconds { get; internal set; }
        /// <summary>Where that time went (each mesh tried: as wound, then turned up).</summary>
        public string Timing { get; internal set; } = "";

        /// <summary>Move the cell's actors that don't stand on the finished mesh onto it (<see cref="SnapToMesh"/>).</summary>
        public bool SnapActors { get; init; }

        /// <summary>Actors moved onto the mesh at Finish.</summary>
        public int Snapped { get; private set; }

        /// <summary>Actors left where they are by SnapActors (by position: those in water, who swim).</summary>
        public Func<P3Float, bool>? StaysPut { get; init; }

        public Result Finish()
        {
            if (result is not null) return result;
            Generated g;
            try { g = Work is not null ? Work.GetAwaiter().GetResult() : Run!(); }
            catch { if (Navm is not null) Cell.NavigationMeshes.Remove(Navm); throw; }
            if (SnapActors) { Snapped = SnapToMesh(Cell, g.Verts, g.Data.Triangles, StaysPut); Interlocked.Add(ref actorsSnapped, Snapped); }
            var navm = Navm ?? new NavigationMesh(Owner.Mod);
            if (Navm is null) Cell.NavigationMeshes.Add(navm);
            // FloorFit's interiors (Oblivion's): a piece reached only through a load door its own navmesh (DoorPieces).
            var pieces = Owner.FloorFit > 0 ? DoorPieces(g.Data, g.Verts) : [(g.Data, g.Verts)];
            navm.Data = pieces[0].Data;
            Owner.AddInfo(navm, Cell, pieces[0].Verts, LinkedOf(pieces[0].Data, g.Linked));
            foreach (var (data, verts) in pieces.Skip(1))
            {
                var more = new NavigationMesh(Owner.Target) { Data = data };
                Cell.NavigationMeshes.Add(more);
                Owner.AddInfo(more, Cell, verts, LinkedOf(data, g.Linked));
                Interlocked.Increment(ref doorPieceMeshes);
            }
            return result = new Result(navm, g.Triangles, g.Verts.Count, g.Linked.Count);
        }
    }

    /// <summary>
    /// Each actor in the cell not standing on the mesh (over a triangle and within 24 units of it) moved to the nearest
    /// point on it within 512 units, a fifth of the way in toward that triangle's middle, 4 units up (2026-10-16: 2,742
    /// of Daggerfall's dungeon creatures stood off their navmesh, 315 inside objects, 133 in walls and 73 in the air at
    /// their markers; the mesh keeps clear of walls and props, so on it they can move and fight).
    /// </summary>
    static int SnapToMesh(Cell cell, List<P3Float> verts, IReadOnlyList<INavmeshTriangleGetter> tris, Func<P3Float, bool>? staysPut = null)
    {
        if (tris.Count == 0) return 0;
        static Vector3 V(P3Float p) => new(p.X, p.Y, p.Z);
        int moved = 0;
        foreach (var npc in cell.Temporary.Concat(cell.Persistent).OfType<PlacedNpc>())
        {
            if (npc.Placement is not { } pl) continue;
            var p = V(pl.Position);
            // In water with its navmesh under it, it swims there: left as it is. In water with none under it (Paradise's
            // prisoners, in their cages under the grotto's water), moved as any other.
            if (staysPut?.Invoke(pl.Position) == true && tris.Any(t => HeightOver(V(verts[t.Vertices.X]), V(verts[t.Vertices.Y]), V(verts[t.Vertices.Z]), p.X, p.Y) is { } under && under < p.Z)) continue;
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
                // Any distance: with nothing on its level or under it, it would fall for ever (Xeddefen's three, 1,316 under
                // everything in the cell, 2026-10-21).
                float any = float.MaxValue;
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
            // or moved to another level, the mesh's. High over it (Mortrag Glacier's werewolves, 70-170 over their floor), let
            // down to 56 over it: still over the floor where the mesh lies 50 under it, and no fall.
            float z = sameLevel ? MathF.Max(p.Z, to.Z - 28) : to.Z + 4;
            if (sameLevel && z > to.Z + 64) z = to.Z + 56;
            pl.Position = new P3Float(to.X, to.Y, z);
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
    internal static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
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

    internal sealed record Generated(NavigationMeshData Data, List<P3Float> Verts, List<IPlacedObjectGetter> Linked, int Triangles)
    {
        /// <summary>Where its time went (seconds): Recast, the islands pruned, the rest; and its input's triangles.</summary>
        public string Timing { get; init; } = "";
    }

    // (No walk surface over rough floor. From 2026-11-16 an invisible stair-helper surface was laid over the navmesh's
    // rough triangles in Oblivion's interiors, the mesh raised onto it; measured that day with Skyrim's own jail guard on
    // travel packages (probes p10, p11): our navmesh for Skyrim's Dragonsreach with it stalled the climb to the upper
    // floor 40 and 62 s where Skyrim's own navmesh took 13 s, and without it the climb ran clean twice in 13-15 s. In
    // the Imperial prison it made no walk better (builds 17 and 18). Taken out.)

    /// <summary>
    /// <see cref="BuildInterior"/> begun: with <paramref name="background"/> the mesh is made on a worker thread (the
    /// inputs must not change until it is finished), else when <see cref="PendingInterior.Finish"/> is called.
    /// </summary>
    public PendingInterior BeginInterior(Cell cell, IReadOnlyList<(Vector3[] Points, Vector3 Normal)> polygons, IEnumerable<IPlacedObjectGetter> loadDoors, P3Float? keepAround, IEnumerable<P3Float>? keepAlso,
        Func<P3Float, bool>? underwater, bool background, bool snapActors = false, Hazards? hazards = null, bool bridgeGaps = false)
    {
        Building.Value = cell.EditorID;
        float fit = FloorFit;
        var doors = loadDoors.ToList();
        NoteDoors(doors);
        var keep = keepAlso?.ToList();
        var pending = new PendingInterior { Owner = this, Cell = cell, SnapActors = snapActors };
        pending.Run = () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var g = Generate(cell, polygons, doors, keepAround, keep, underwater, hazards, bridgeGaps, fit);
            pending.Seconds = sw.Elapsed.TotalSeconds;
            return g;
        };
        if (background)
        {
            pending.Navm = new NavigationMesh(Target);
            cell.NavigationMeshes.Add(pending.Navm);
            pending.Work = Task.Run(pending.Run);
        }
        return pending;
    }

    static Generated Generate(Cell cell, IReadOnlyList<(Vector3[] Points, Vector3 Normal)> polygons, List<IPlacedObjectGetter> doorList, P3Float? keepAround, IEnumerable<P3Float>? keepAlso,
        Func<P3Float, bool>? underwater, Hazards? hazards = null, bool bridgeGaps = false, float floorFit = 0, IReadOnlyList<Doorway>? doorways = null)
    {
        // Recast's mesh, or the same input's from another part (SharedExteriors: a town part's building interiors are
        // the same buildings as other parts', 2026-10-15: a fifth of a town part's work).
        // (Not shared with hazards or doorways: the shared files are keyed by the polygons alone.)
        var stage = System.Diagnostics.Stopwatch.StartNew();
        var (outVerts, tris) = SharedExteriors is { } dir && hazards is null && floorFit == 0 && doorways is not { Count: > 0 } ? SharedInterior(dir, polygons) : RecastInterior(polygons, hazards, floorFit, doorways);
        double recastSeconds = stage.Elapsed.TotalSeconds; stage.Restart();
        int recastTris = tris.Count;
        (outVerts, tris) = PruneIslands(outVerts, tris, doorList.Select(d => d.Placement!.Position).Append(keepAround ?? default).Concat(keepAlso ?? []).ToList());
        // Pieces joined across short gaps nothing stands in, once the islands are gone (2026-11-06: joined first, the
        // borders went to table tops and ledges, and Redguard's palace kept 34 pieces where 21 joined after).
        int bridged = 0;
        if (bridgeGaps)
        {
            // TM_NAVIN=DIR (dev aid): each interior's input polygons kept as <cell>.bin (navmesh-bridge-test --polys).
            if (Environment.GetEnvironmentVariable("TM_NAVIN") is { Length: > 0 } navIn) WritePolygons(Path.Combine(navIn, (cell.EditorID ?? "cell") + ".bin"), polygons);
            var why = new Dictionary<string, int>();
            bridged = BridgeGaps(outVerts, tris, polygons, why: why);
            lock (BridgeReasons) foreach (var (k, v) in why) BridgeReasons[k] = BridgeReasons.GetValueOrDefault(k) + v;
        }
        // Ways down off ledges to the floor below (FloorFit's interiors: Oblivion's).
        if (floorFit > 0) DropLinks(outVerts, tris, polygons);
        double pruneSeconds = stage.Elapsed.TotalSeconds; stage.Restart();
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

        // Load doors: tag the triangle before each (DoorTriangleFor).
        var linked = new List<IPlacedObjectGetter>();
        foreach (var door in doorList)
        {
            int best = DoorTriangleFor(tris.Count, i => Corners(outVerts, tris[i]), door);
            if (best < 0) continue;
            data.DoorTriangles.Add(new DoorTriangle { TriangleBeforeDoor = (short)best, Unknown = DoorTag, Door = door.ToLink() });
            data.Triangles[best].Flags |= NavmeshTriangle.Flag.Door;
            linked.Add(door);
        }

        WriteGrid(data, outVerts, tris);
        return new Generated(data, outVerts, linked, tris.Count)
        {
            Timing = $"Recast {recastSeconds:F1} s ({polygons.Count:N0} input polygons, {recastTris:N0} triangles{(bridgeGaps ? $", {bridged} gaps joined" : "")}), islands pruned {pruneSeconds:F1} s ({tris.Count:N0} kept), the rest {stage.Elapsed.TotalSeconds:F1} s",
        };
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
    /// <paramref name="Level"/>, the surface of a liquid that hurts that is not to be swum, under which nothing is floor
    /// (Oblivion's lava is swum as water is, its bed flagged Water: user, 2026-10-20, "lava is swimmable though and its
    /// hazard in case you fall in").
    /// </summary>
    public sealed record Hazards(WoundInput? Faces, Func<float, float, float?>? Level)
    {
        public bool Covers(float x, float y, float top) =>
            (Faces is { } f && f.Under(new Vector3(x, y, top), 12, 12) is not null) || (Level?.Invoke(x, y) is { } lv && top < lv);
    }

    /// <summary>Walkable voxel spans made unwalkable as on or under a hazard (<see cref="Hazards"/>), once a voxel size.</summary>
    public static long HazardSpans => Interlocked.Read(ref hazardSpans);
    static long hazardSpans;

    /// <summary>
    /// Each navmesh corner set on the floor under it (<see cref="FloorFit"/>'s interiors): the highest face facing up
    /// from 48 under it to 4 over it. Recast's polygon corners take their height from the outline of a region, which
    /// can be a raised span beside them (a frame's foot, a sill): in Oblivion's Imperial prison the corners at the
    /// prisoner's cell doorway lay 14 to 19 over the corridor floor, and Skyrim's own people (a vanilla jail guard sent
    /// there with PathToReference, 2026-11-16) walked up to that spot and no further, the way failed, whatever stood in
    /// the doorway or nothing; elsewhere the mesh lay about 8 over the floor and was walked. Skyrim's own navmesh has its
    /// corners on the floor.
    /// </summary>
    static void SetOnFloor(List<P3Float> verts, IReadOnlyList<(Vector3[] Points, Vector3 Normal)> polygons)
    {
        var up = new List<Vector3[]>();
        foreach (var (pts, n) in polygons)
        {
            if (n.Z < 0.5f) continue;
            for (int i = 1; i + 1 < pts.Length; i++) up.Add([pts[0], pts[i], pts[i + 1]]);
        }
        if (up.Count == 0) return;
        var floor = new WoundInput(up);
        long moved = 0; double lift = 0;
        for (int i = 0; i < verts.Count; i++)
        {
            var v = verts[i];
            if (floor.FloorUnder(new Vector3(v.X, v.Y, v.Z), 48, 4) is not { } z || MathF.Abs(v.Z - z) < 1) continue;
            lift += v.Z - z;
            verts[i] = new P3Float(v.X, v.Y, z);
            moved++;
        }
        Interlocked.Add(ref cornersOnFloor, moved);
        lock (liftLock) cornerLift += lift;
    }

    /// <summary>Navmesh corners set on the floor (<see cref="SetOnFloor"/>), and how far they were moved down in all.</summary>
    public static long CornersOnFloor => Interlocked.Read(ref cornersOnFloor);
    public static double CornerLift { get { lock (liftLock) return cornerLift; } }
    static long cornersOnFloor;
    static double cornerLift;
    static readonly object liftLock = new();

    /// <summary>An interior's mesh as Recast makes it from its polygons, before its islands are pruned; with
    /// <paramref name="hazards"/>, nothing on or under them walkable.</summary>
    static (List<P3Float> Verts, List<(int A, int B, int C)> Tris) RecastInterior(IReadOnlyList<(Vector3[] Points, Vector3 Normal)> polygons, Hazards? hazards = null, float floorFit = 0,
        IReadOnlyList<Doorway>? doorways = null)
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
        // Inner doors' doorways, floor kept to their frame (Doorway), in Recast's space as the polygons are.
        foreach (var d in doorways ?? [])
        {
            var vol = new RcConvexVolume { verts = d.Corners.SelectMany(c => new[] { c.X, 0f, -c.Y }).ToArray(), hmin = d.Bottom, hmax = d.Top };
            DoorwayVolumes.Add(vol, d);
            geom.AddConvexVolume(vol);
            Interlocked.Increment(ref doorwaysKept);
        }
        if (Environment.GetEnvironmentVariable("TM_NAV_LAYERS") == "1") Console.WriteLine(TallestStack(polygons));
        // TM_NAV_COLUMN=x,y (dev aid): the input triangles within 4 units of that point across, with their height there
        // (or span) and which way they face, as Recast gets them.
        if (Environment.GetEnvironmentVariable("TM_NAV_COLUMN")?.Split(',') is [var ixs, var iys] && float.TryParse(ixs, out var inX) && float.TryParse(iys, out var inY))
        {
            // Each triangle clipped to the voxel column Recast puts the point in (its height range there), as rasterized.
            var bmin = geom.GetMeshBoundsMin();
            float cs = Env("CS", 8f);
            int vx = (int)MathF.Floor((inX - bmin.X) / cs), vz = (int)MathF.Floor((-inY - bmin.Z) / cs);
            float cx0 = bmin.X + vx * cs, cx1 = cx0 + cs, cz0 = bmin.Z + vz * cs, cz1 = cz0 + cs;
            var near = new List<(float Lo, float Hi, float Nz)>();
            for (int f = 0; f + 2 < faces.Count; f += 3)
            {
                // Recast space (x, y up, z): clip the triangle to x in [cx0, cx1] and z in [cz0, cz1].
                var poly = new List<Vector3>();
                for (int i = 0; i < 3; i++) poly.Add(new Vector3(verts[faces[f + i] * 3], verts[faces[f + i] * 3 + 1], verts[faces[f + i] * 3 + 2]));
                List<Vector3> Clip(List<Vector3> pin, Func<Vector3, float> side)
                {
                    var o = new List<Vector3>();
                    for (int i = 0; i < pin.Count; i++)
                    {
                        var a = pin[i]; var b = pin[(i + 1) % pin.Count];
                        float da = side(a), db = side(b);
                        if (da >= 0) o.Add(a);
                        if ((da >= 0) != (db >= 0)) o.Add(a + (b - a) * (da / (da - db)));
                    }
                    return o;
                }
                poly = Clip(poly, v => v.X - cx0); if (poly.Count < 3) continue;
                poly = Clip(poly, v => cx1 - v.X); if (poly.Count < 3) continue;
                poly = Clip(poly, v => v.Z - cz0); if (poly.Count < 3) continue;
                poly = Clip(poly, v => cz1 - v.Z); if (poly.Count < 3) continue;
                Vector3 V(int i) => new(verts[faces[f + i] * 3], verts[faces[f + i] * 3 + 1], verts[faces[f + i] * 3 + 2]);
                var nrm = Vector3.Cross(V(1) - V(0), V(2) - V(0));   // Recast's own (y up)
                near.Add((poly.Min(v => v.Y), poly.Max(v => v.Y), nrm.LengthSquared() > 0 ? Vector3.Normalize(nrm).Y : 0));
            }
            Console.WriteLine($"  input at {inX:F0},{inY:F0}: {string.Join("; ", near.OrderBy(t => t.Hi).Select(t => $"{t.Lo:F0}..{t.Hi:F0} nz {t.Nz:F2}"))}");
        }
        var (pmesh, detail) = BuildMesh(geom, (p, coarse, outline) => new RcBuilderConfig(Config(p, coarse, outline, floorFit), geom.GetMeshBoundsMin(), geom.GetMeshBoundsMax()), hazards: hazards)!.Value;
        var (outVerts, tris) = SkyrimTriangles(pmesh, detail);
        if (floorFit > 0) SetOnFloor(outVerts, polygons);
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
        // A block is walkable by its own slope at its own size (its two triangles'), not its quads': judged a quad at a
        // time, hills with steep quads scattered through them split to single quads everywhere (2026-10-27, measured on
        // four 4,096-cell pieces of the Iliac Bay's hills: about 760 triangles a cell whatever the tolerance, 96 or 256).
        var leaf = new int[32, 32];
        var leafOpen = new bool[32, 32];
        bool BlockOpen(int x0, int y0, int size)
        {
            float a = h[x0, y0], b = h[x0 + size, y0], c = h[x0 + size, y0 + size], d = h[x0, y0 + size], s = size * 128f;
            return Up((b - a) / s, (c - b) / s) >= cosMax && Up((c - d) / s, (d - a) / s) >= cosMax;
        }
        bool Fits(int x0, int y0, int size)
        {
            bool firstWet = wet[x0, y0];
            for (int y = y0; y < y0 + size; y++)
                for (int x = x0; x < x0 + size; x++)
                    if (wet[x, y] != firstWet) return false;
            if (!BlockOpen(x0, y0, size)) return true;
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
        void Set(int x0, int y0, int size)
        {
            bool walkable = BlockOpen(x0, y0, size);
            for (int y = y0; y < y0 + size; y++) for (int x = x0; x < x0 + size; x++) { leaf[x, y] = size; leafOpen[x, y] = walkable; }
        }
        void Split(int x0, int y0, int size)
        {
            if (size <= GridMinBlock || Fits(x0, y0, size)) { Set(x0, y0, size); return; }
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
                        // All four quarters (2026-10-27: one quarter set left the other three unaligned and unwritten).
                        int half = size / 2;
                        Set(x, y, half); Set(x + half, y, half); Set(x, y + half, half); Set(x + half, y + half, half);
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
                if (x0 % size != 0 || y0 % size != 0 || !leafOpen[x0, y0]) continue;
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
    /// overland cells: 48 units made 68 million triangles over the overland, 96 made 26, 128 made 17; 2026-10-27, four
    /// hill pieces of 4,096 cells with blocks judged by their own slope: 96 made 4.98 million, 128 made 3.69, against
    /// 10.36 a quad at a time, and a tile at that came to 2.8 GB, past what Mutagen reads). TM_NAV_GRIDTOL overrides it
    /// for measuring.</summary>
    static readonly float GridTolerance = Env("GRIDTOL", 128f);
    /// <summary>The smallest terrain block, in 128-unit quads (1, 2, 4 ...; TM_NAV_GRIDMIN for measuring).</summary>
    static readonly int GridMinBlock = (int)Env("GRIDMIN", 1f);

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
        // The detail mesh's height samples, in voxels: never under 1.5. Below Recast's 0.9 it samples nothing, and
        // DotRecast's RcMeshDetails.TriangulateHull then throws (index out of range). 24 units in the open setting's
        // 32-unit voxels is 0.75: measured 2026-10-31 on a 4,096-cell piece of Daggerfall's overland, every try of
        // all 2,189 cells with something standing near threw, 13 tries a cell, and each cell then began again in the
        // fine setting: 2,329 cell-seconds, 31,153 tries, 370,713 triangles. With 1.5 voxels: 352, 3,026, 214,841, none
        // thrown. Unchanged in the fine setting (24 units in 16-unit voxels is 1.5).
        float detailDist = MathF.Max(Env("DETAILDIST", 24f), cs * 1.5f);
        // Not tiled, no border: the heightfield reaches ExteriorMargin past the cell and the mesh is clipped to it.
        return new RcConfig(false, 0, 0, 0, partition,
            cs, ch, agentMaxSlope: 50f, agentHeight: 128f, agentRadius: radius, agentMaxClimb: open ? 48f : 32f,
            minRegionArea: 8 * cs * cs, mergeRegionArea: 20 * cs * cs, edgeMaxLen: open ? 1600f : 600f, edgeMaxError: (open ? 4f : 2f) * outline, vertsPerPoly: 6,
            detailSampleDist: detailDist / cs, detailSampleMaxError: Env("DETAILERR", 16f) / ch, filterLowHangingObstacles: true, filterLedgeSpans: true, filterWalkableLowHeightSpans: true,
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
        if (BuildMesh(geom, (p, coarse, outline) => new RcBuilderConfig(ExteriorConfig(open, p, coarse, outline), bmin, bmax, 0, 0), land, wound, ExteriorMargin, hazards, open) is not { } built || built.Mesh.npolys == 0) return null;
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
    static (ulong, ulong, float) InputKey(List<Vector3[]> tris, int cx, int cy, Func<float, float, float?>? land = null)
    {
        var f = new float[tris.Count * 9];
        for (int i = 0; i < tris.Count; i++)
            for (int k = 0; k < 3; k++) { f[i * 9 + k * 3] = tris[i][k].X; f[i * 9 + k * 3 + 1] = tris[i][k].Y; f[i * 9 + k * 3 + 2] = tris[i][k].Z; }
        return InputKey(f, cx, cy, land);
    }

    /// <summary>The same over corners nine floats a triangle. Heights count from the input's lowest (to the whole unit,
    /// returned as the base): the same blocks stand at every height on the overland (each location on the ground the
    /// land was levelled to for it), and keyed by their heights as they are, none of their cells' meshes was reused.</summary>
    static (ulong, ulong, float) InputKey(float[] coords, int cx, int cy, Func<float, float, float?>? land = null)
    {
        var q = new int[coords.Length + (land is null ? 0 : 35 * 35)];
        float low = float.MaxValue;
        for (int k = 2; k < coords.Length; k += 3) low = MathF.Min(low, coords[k]);
        var heights = new float?[land is null ? 0 : 35 * 35];
        if (land is not null)
            for (int gy = -1, i = 0; gy <= 33; gy++)
                for (int gx = -1; gx <= 33; gx++, i++)
                    if ((heights[i] = land(cx * CellUnits + gx * 128, cy * CellUnits + gy * 128)) is { } g) low = MathF.Min(low, g);
        float baseZ = low == float.MaxValue ? 0 : MathF.Floor(low);
        int n = 0;
        for (int k = 0; k + 2 < coords.Length; k += 3)
        {
            q[n++] = (int)MathF.Round((coords[k] - cx * CellUnits) * 16);
            q[n++] = (int)MathF.Round((coords[k + 1] - cy * CellUnits) * 16);
            q[n++] = (int)MathF.Round((coords[k + 2] - baseZ) * 16);
        }
        foreach (var g in heights) q[n++] = g is { } h ? (int)MathF.Round((h - baseZ) * 16) : int.MinValue;
        var hash = System.Security.Cryptography.SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(q.AsSpan(0, n)));
        return (BitConverter.ToUInt64(hash, 0), BitConverter.ToUInt64(hash, 8), baseZ);
    }

    (List<P3Float> Verts, List<(int A, int B, int C)> Tris)? GenerateExteriorReused(int cx, int cy, List<Vector3[]> triangles, List<P3Float> keep, bool open,
        Func<float, float, float?>? land = null, float[]? around = null, bool asWound = false)
    {
        // Keyed by the whole input round the cell when what is shut in is looked for beyond it (the cell's own input is
        // taken from that).
        var (k1, k2, baseZ) = around is null ? InputKey(triangles, cx, cy, land) : InputKey(around, cx, cy, land);
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
                mesh = (raw.Verts.Select(v => new P3Float(v.X - ox, v.Y - oy, v.Z - baseZ)).ToArray(), raw.Tris.ToArray());
            }
            if (file is not null) WriteShared(file, mesh);
            return mesh;
        })).Value;
        if (!made) Interlocked.Increment(ref exteriorReused);
        if (local is not { } m) return null;
        float wx = cx * CellUnits, wy = cy * CellUnits;
        return Finish(m.Verts.Select(v => new P3Float(v.X + wx, v.Y + wy, v.Z + baseZ)).ToList(), [.. m.Tris], keep);
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
        var doorList = loadDoors.ToList();
        NoteDoors(doorList);
        foreach (var door in doorList)
        {
            int best = DoorTriangleFor(tris.Count, i => Corners(verts, tris[i]), door);
            if (best < 0) continue;
            data.DoorTriangles.Add(new DoorTriangle { TriangleBeforeDoor = (short)best, Unknown = DoorTag, Door = door.ToLink() });
            data.Triangles[best].Flags |= NavmeshTriangle.Flag.Door;
            linked.Add(door);
        }
        WriteGrid(data, verts, tris);
        var navm = new NavigationMesh(Target) { Data = data };
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
    /// (<see cref="exteriorCache"/>; Daggerfall's towns). Not with <paramref name="open"/>, unless
    /// <paramref name="fineWhereStanding"/>: then open country's cells with something standing in or round them are
    /// made in the fine setting (and reused by input with <paramref name="reuse"/>), the bare ones from their heights:
    /// Daggerfall's overland, its towns' streets and doorways on it.</remarks>
    public (int Meshes, int Triangles) BuildExteriorCells(Worldspace ws, IReadOnlyDictionary<(int X, int Y), Cell> cells,
        Func<int, int, IEnumerable<Vector3[]>> solidsOf, Func<Cell, float?> waterOf, IReadOnlyList<IPlacedObjectGetter> doors, List<string> problems,
        bool open = false, Func<int, int, bool>? only = null, bool reuse = false, bool asWound = false,
        Func<int, int, IEnumerable<Vector3[]>>? hazardsOf = null, Func<Cell, float?>? hazardLevelOf = null, bool fineWhereStanding = false, Func<int, int, bool>? fineAt = null)
    {
        Building.Value = null;
        var job = BeginExteriorCells(ws, cells, solidsOf, waterOf, doors, problems, open, only, reuse, asWound, hazardsOf, hazardLevelOf, fineWhereStanding, fineAt);
        GenerateExteriorCells([job]);
        return job.Finish();
    }

    /// <summary>A worldspace's exterior navmeshes to make (<see cref="BeginExteriorCells"/>): its cells, how to make
    /// one, and how to write them all once made.</summary>
    public sealed class ExteriorJob
    {
        public required string Name { get; init; }
        public required IReadOnlyList<(int X, int Y)> Keys { get; init; }
        public required Action<int> Make { get; init; }
        public required Func<int, int> Busy { get; init; }
        /// <summary>Writes the meshes made, in cell order; returns (navmeshes, triangles).</summary>
        public required Func<(int Meshes, int Triangles)> Finish { get; init; }
    }

    /// <summary>
    /// Makes the navmeshes of several worldspaces' jobs in one parallel loop, the busiest cells (what stands in and
    /// around each) first, handed out one at a time. Worldspace by worldspace, in cell order, the last long cells of
    /// each ran alone at its end (2026-10-22, Oblivion: 32 of its 99 minutes of exterior navmeshes on 3.6 cores, the
    /// small worldspaces after Tamriel each waiting on its slowest cell). Prints the slowest cells (over 30 s).
    /// </summary>
    public static void GenerateExteriorCells(IReadOnlyList<ExteriorJob> jobs)
    {
        var work = jobs.SelectMany(j => Enumerable.Range(0, j.Keys.Count).Select(i => (Job: j, Index: i, Busy: j.Busy(i))))
            .OrderByDescending(w => w.Busy).ToList();
        var slow = new System.Collections.Concurrent.ConcurrentBag<(double Seconds, string Where)>();
        Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(work, System.Collections.Concurrent.EnumerablePartitionerOptions.NoBuffering),
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, w =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try { w.Job.Make(w.Index); }
            finally
            {
                if (watch.Elapsed.TotalSeconds > 30)
                    slow.Add((watch.Elapsed.TotalSeconds, $"{w.Job.Name} {w.Job.Keys[w.Index].X},{w.Job.Keys[w.Index].Y}: {watch.Elapsed.TotalSeconds:F0} s ({w.Busy} references around)"));
            }
        });
        foreach (var s in slow.OrderByDescending(s => s.Seconds).Take(10)) Console.WriteLine($"    slow navmesh cell {s.Where}");
    }

    /// <summary>The job of <see cref="BuildExteriorCells"/> (the same parameters), made by
    /// <see cref="GenerateExteriorCells"/> and written by its <see cref="ExteriorJob.Finish"/>: several worldspaces'
    /// cells made together.</summary>
    public ExteriorJob BeginExteriorCells(Worldspace ws, IReadOnlyDictionary<(int X, int Y), Cell> cells,
        Func<int, int, IEnumerable<Vector3[]>> solidsOf, Func<Cell, float?> waterOf, IReadOnlyList<IPlacedObjectGetter> doors, List<string> problems,
        bool open = false, Func<int, int, bool>? only = null, bool reuse = false, bool asWound = false,
        Func<int, int, IEnumerable<Vector3[]>>? hazardsOf = null, Func<Cell, float?>? hazardLevelOf = null, bool fineWhereStanding = false, Func<int, int, bool>? fineAt = null)
    {
        // Cells that get past open country's bare-ground shortcut are made in the fine setting.
        bool cellOpen = open && !fineWhereStanding;
        reuse &= !cellOpen;
        const float margin = 160f;
        var keys = cells.Keys.Where(k => only is null || only(k.X, k.Y)).OrderBy(k => k.Y).ThenBy(k => k.X).ToList();
        var results = new (List<P3Float> Verts, List<(int A, int B, int C)> Tris)?[keys.Count];
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        bool InCell(P3Float p, int x, int y) => p.X >= x * CellUnits && p.X < (x + 1) * CellUnits && p.Y >= y * CellUnits && p.Y < (y + 1) * CellUnits;
        // The worldspace's persistent people by the cell they stand in: their pieces are kept too (2026-11-06: Redguard's
        // Brennan, Yaelii and Avik on their boats' decks, persistent, had theirs pruned as islands: 80-204 units off).
        var persistentAt = (ws.TopCell?.Persistent ?? []).OfType<IPlacedNpcGetter>().Where(n => n.Placement is not null)
            .GroupBy(n => ((int)MathF.Floor(n.Placement!.Position.X / CellUnits), (int)MathF.Floor(n.Placement.Position.Y / CellUnits)))
            .ToDictionary(g => g.Key, g => g.Select(n => n.Placement!.Position).ToList());
        int done = 0;
        // One cell a core: unbounded, the thread pool kept adding threads to the long iterations (2026-10-19: 26 to 152 in two
        // minutes of Morrowind's exteriors), each a whole Recast build at once with its own pooled buffers, and memory
        // rose with them to 16 GB.
        // (GenerateExteriorCells: the busiest first, by what stands in and around each cell; the results stay in index
        // order, as written.)
        int Busy(int i) { var (cx, cy) = keys[i]; int n = 0; for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) if (cells.TryGetValue((cx + dx, cy + dy), out var c)) n += c.Temporary.Count + (c.Landscape is null ? 0 : 1); return n; }
        return new ExteriorJob { Name = ws.EditorID ?? "", Keys = keys, Make = ExteriorCell, Busy = Busy, Finish = Finish };
        void ExteriorCell(int i)
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
                var keep = cell.Temporary.Concat(cell.Persistent).OfType<IPlacedNpcGetter>().Where(n => n.Placement is not null).Select(n => n.Placement!.Position)
                    .Concat(persistentAt.GetValueOrDefault((x, y)) ?? [])
                    .Concat(doors.Select(d => d.Placement!.Position).Where(p => InCell(p, x, y))).ToList();
                // Open country with nothing standing on it near the cell (in it, or in a neighbour within the margin the
                // mesh takes in: what Recast would be given): straight from its heights. (2026-10-26: any neighbour with
                // something on it sent the cell to Recast, and the bare land round every town was meshed in hundreds of
                // triangles a cell.)
                if (open && cell.Landscape is { } bare && !solidsOf(x, y).Any()
                    && !Enumerable.Range(-1, 3).Any(dy => Enumerable.Range(-1, 3).Any(dx => (dx != 0 || dy != 0) && solidsOf(x + dx, y + dy).Any(Near))))
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
                // TM_NAVCELL=WORLD,X,Y,DIR (dev aid): that cell's input (its triangles and the shut-in test's) kept in DIR as
                // <world>_<x>_<y>.bin and _around.bin (navmesh-cell-test).
                if (Environment.GetEnvironmentVariable("TM_NAVCELL")?.Split(',') is [var nw, var nx, var ny, var ndir] && nw == ws.EditorID && int.Parse(nx) == x && int.Parse(ny) == y)
                {
                    WritePolygons(Path.Combine(ndir, $"{nw}_{x}_{y}.bin"), tris.Select(t => (t, Vector3.UnitZ)).ToList());
                    File.WriteAllBytes(Path.Combine(ndir, $"{nw}_{x}_{y}_around.bin"), System.Runtime.InteropServices.MemoryMarshal.AsBytes(around.AsSpan()).ToArray());
                }
                // A cell Recast can't outline in the open-country setting is tried again in the fine one.
                // Shared meshes are keyed by the input alone: a cell with hazards is made on its own.
                if (reuse && hazards is null) results[i] = GenerateExteriorReused(x, y, tris, keep, cellOpen, LandAt, around, asWound);
                else
                    // fineAt: cells that must keep the fine setting however coarse the country round them (a door's).
                    try { results[i] = GenerateExterior(x, y, tris, keep, cellOpen && fineAt?.Invoke(x, y) != true, LandAt, around, asWound, hazards); }
                    catch (Exception) when (cellOpen && fineAt?.Invoke(x, y) != true) { results[i] = GenerateExterior(x, y, tris, keep, land: LandAt, around: around, asWound: asWound, hazards: hazards); }
                if (ExteriorSurface.On && results[i] is { } made) ExteriorSurface.Check(ws.EditorID ?? "", x, y, around, made.Verts, made.Tris, LandAt);
            }
            catch (Exception e) { errors.Add($"navmesh {ws.EditorID} {x},{y}: {e.Message}"); }
        }
        (int, int) Finish()
        {
            problems.AddRange(errors.OrderBy(e => e, StringComparer.Ordinal));
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
    }

    /// <summary>
    /// Joins exterior navmeshes across cell borders, as vanilla does: a triangle edge lying on its cell's border with
    /// no neighbour inside its mesh links to the triangle of the neighbouring cell's navmesh whose border edge
    /// overlaps it most (an EdgeLinks entry, whose index replaces the neighbour, and the edge's EdgeLink flag).
    /// Returns the number of links.
    /// </summary>
    /// <summary>
    /// Lets go of exterior navmeshes already written out, every neighbour of which is made and linked to them (a big
    /// worldspace's are written a piece at a time: OverlandNavmesh). <see cref="LinkExteriorEdges"/> after it links the
    /// rest as before: an edge linked once is left as it is, so it may be called again as more cells are made.
    /// </summary>
    public int ForgetExteriors(ISet<NavigationMesh> written) => exteriors.RemoveAll(e => written.Contains(e.Navmesh));

    /// <summary>Exterior navmeshes held for linking, and the meshes kept for reuse by input (entries, triangles).</summary>
    public (int Held, int Cached, long CachedTriangles) ExteriorHeld()
    {
        long tris = 0;
        foreach (var v in exteriorCache.Values)
            if (v.IsValueCreated && v.Value is { } m) tris += m.Tris.Length;
        return (exteriors.Count, exteriorCache.Count, tris);
    }

    public int LinkExteriorEdges()
    {
        var byCell = exteriors.GroupBy(m => (m.Worldspace.FormKey, m.X, m.Y)).ToDictionary(g => g.Key, g => g.First());
        // Border edges per mesh and side (BorderEdges, kept when each was written).
        static List<(int Tri, int Edge, float Lo, float Hi, float ZLo, float ZHi)> Borders(ExteriorMesh m, int side) => m.Sides[side];
        int links = 0;
        foreach (var m in exteriors)
        {
            var data = m.Navmesh.Data!;
            for (int side = 0; side < 4; side++)
            {
                var (nx, ny, nside) = side switch { 0 => (m.X - 1, m.Y, 1), 1 => (m.X + 1, m.Y, 0), 2 => (m.X, m.Y - 1, 3), _ => (m.X, m.Y + 1, 2) };
                if (!byCell.TryGetValue((m.Worldspace.FormKey, nx, ny), out var other)) continue;
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
        if (Trace)
        {
            // Each piece: its triangles, area, middle, and whether it is kept.
            for (int c = 0; c < sizes.Count; c++)
            {
                var mine = Enumerable.Range(0, tris.Count).Where(i => comp[i] == c).Select(i => Centroid(verts, tris[i])).ToList();
                Console.WriteLine($"  piece {c}: {sizes[c]} triangles, {areas[c]:F0} sq units, around ({mine.Average(q => q.X):F0},{mine.Average(q => q.Y):F0},{mine.Average(q => q.Z):F0}){(kept.Contains(c) ? ", kept" : ", left out")}");
            }
        }

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
    /// <summary>Dev aid: navmesh input polygons to a file (count; each: point count, points, normal) and back.</summary>
    internal static void WritePolygons(string path, IReadOnlyList<(Vector3[] Points, Vector3 Normal)> polygons)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var w = new BinaryWriter(File.Create(path));
        w.Write(polygons.Count);
        foreach (var (pts, n) in polygons)
        {
            w.Write(pts.Length);
            foreach (var p in pts) { w.Write(p.X); w.Write(p.Y); w.Write(p.Z); }
            w.Write(n.X); w.Write(n.Y); w.Write(n.Z);
        }
    }

    internal static List<(Vector3[] Points, Vector3 Normal)> ReadPolygons(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        int count = r.ReadInt32();
        var list = new List<(Vector3[] Points, Vector3 Normal)>(count);
        for (int i = 0; i < count; i++)
        {
            var pts = new Vector3[r.ReadInt32()];
            for (int k = 0; k < pts.Length; k++) pts[k] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            list.Add((pts, new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle())));
        }
        return list;
    }

    /// <summary>What <see cref="BridgeGaps"/> found in every interior it joined (border vertices with nothing near, ways
    /// blocked, runs joined...), all builds together.</summary>
    public static readonly Dictionary<string, int> BridgeReasons = [];

    /// <summary>Navmesh pieces joined across short gaps (<see cref="BridgeGaps"/>), all builds together.</summary>
    public static int GapsBridged => gapsBridged;
    static int gapsBridged;

    /// <summary>
    /// Pieces of a navmesh joined across short level gaps that Recast leaves where nothing stops a walker (2026-11-06,
    /// Redguard's catacombs in 204 pieces: a groove 12 wide and 21 deep across a corridor, a 12-unit sill, each kept clear
    /// by the actor's radius on both sides, 47-80 units of nothing between floors at one height). Two border edges of
    /// different pieces are joined by a quad of two triangles when: their nearest points are at most
    /// <paramref name="maxGap"/> apart across and <paramref name="climb"/> up; they run within 37 degrees of each other
    /// and overlap by at least half the shorter; no solid face crosses the way at 40, 80 or 120 units over the floor
    /// (no wall, door or gate between); and a solid surface lies within a step under every point of the way (no pit). The
    /// quad's corners are the edges' own vertices, so the triangles link to both pieces. Each border edge joins once.
    /// </summary>
    /// <summary>
    /// Border edges facing another piece within <paramref name="maxGap"/> and <paramref name="climb"/> (by their middle's
    /// distance to its nearest border edge of another piece) cut into pieces of at most 16 units, for
    /// <see cref="BridgeGaps"/> to zip vertex to vertex: Recast's borders are straight with vertices only at their corners,
    /// and across a doorway 80 units wide in Redguard's catacombs the nearest corners were 107-115 apart. A triangle with
    /// a cut edge is fanned from its middle (its other edges, and so its links, as they were). Stops before the mesh would
    /// pass 30,000 triangles (a NAVM holds 32,767). Returns the edges cut.
    /// </summary>
    static int SubdivideFacingBorders(List<P3Float> verts, List<(int A, int B, int C)> tris, float maxGap, float climb)
    {
        Vector3 V(int i) => new(verts[i].X, verts[i].Y, verts[i].Z);
        var adj = Adjacency(tris);
        var parent = Enumerable.Range(0, tris.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int i = 0; i < tris.Count; i++) for (int e = 0; e < 3; e++) if (adj[i, e] >= 0) parent[Find(i)] = Find(adj[i, e]);
        var edges = new List<(int Tri, int E, int A, int B, int Piece)>();
        for (int i = 0; i < tris.Count; i++)
        {
            var (a, b, c) = tris[i];
            int[][] es = [[a, b], [b, c], [c, a]];
            for (int e = 0; e < 3; e++) if (adj[i, e] < 0) edges.Add((i, e, es[e][0], es[e][1], Find(i)));
        }
        const float Cell = 64;
        var byCell = new Dictionary<(int, int), List<int>>();
        for (int k = 0; k < edges.Count; k++)
        {
            Vector3 a = V(edges[k].A), b = V(edges[k].B);
            int steps = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(a, b) / Cell));
            var seen = new HashSet<(int, int)>();
            for (int s = 0; s <= steps; s++)
            {
                var p = Vector3.Lerp(a, b, s / (float)steps);
                var key = ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell));
                if (seen.Add(key)) (byCell.TryGetValue(key, out var l) ? l : byCell[key] = []).Add(k);
            }
        }
        // The nearest point of a segment to a point, across (2D); its height there.
        static (float Across, float Z) Nearest(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = new System.Numerics.Vector2(b.X - a.X, b.Y - a.Y);
            float len2 = ab.LengthSquared();
            float t = len2 < 1e-6f ? 0 : Math.Clamp(System.Numerics.Vector2.Dot(new System.Numerics.Vector2(p.X - a.X, p.Y - a.Y), ab) / len2, 0, 1);
            var q = Vector3.Lerp(a, b, t);
            return (new System.Numerics.Vector2(q.X - p.X, q.Y - p.Y).Length(), q.Z);
        }
        var cut = new Dictionary<int, List<int>>();   // triangle -> its edges (0-2) to cut
        int reach = (int)MathF.Ceiling(maxGap / Cell), added = 0;
        for (int k = 0; k < edges.Count; k++)
        {
            Vector3 a = V(edges[k].A), b = V(edges[k].B);
            float len = new System.Numerics.Vector2(b.X - a.X, b.Y - a.Y).Length();
            if (len <= 24) continue;
            var mid = (a + b) / 2;
            int cx = (int)MathF.Floor(mid.X / Cell), cy = (int)MathF.Floor(mid.Y / Cell);
            bool facing = false;
            for (int gx = cx - reach; gx <= cx + reach && !facing; gx++)
                for (int gy = cy - reach; gy <= cy + reach && !facing; gy++)
                    if (byCell.TryGetValue((gx, gy), out var list))
                        foreach (int o in list)
                        {
                            if (edges[o].Piece == edges[k].Piece) continue;
                            var (across, z) = Nearest(mid, V(edges[o].A), V(edges[o].B));
                            if (across <= maxGap && MathF.Abs(z - mid.Z) <= climb) { facing = true; break; }
                        }
            if (!facing) continue;
            int pieces = (int)MathF.Ceiling(len / 16);
            if (tris.Count + added + pieces + 2 > 30000) break;
            (cut.TryGetValue(edges[k].Tri, out var l) ? l : cut[edges[k].Tri] = []).Add(edges[k].E);
            added += pieces + 2;
        }
        if (cut.Count == 0) return 0;
        int edgesCut = 0;
        var keep = new List<(int A, int B, int C)>();
        var fans = new List<(int A, int B, int C)>();
        for (int i = 0; i < tris.Count; i++)
        {
            if (!cut.TryGetValue(i, out var which)) { keep.Add(tris[i]); continue; }
            var (a, b, c) = tris[i];
            int[] corners = [a, b, c];
            // The triangle's outline, its cut edges with the points between their ends.
            var ring = new List<int>();
            for (int e = 0; e < 3; e++)
            {
                int from = corners[e], to = corners[(e + 1) % 3];
                ring.Add(from);
                if (!which.Contains(e)) continue;
                edgesCut++;
                Vector3 pf = V(from), pt = V(to);
                int n = (int)MathF.Ceiling(new System.Numerics.Vector2(pt.X - pf.X, pt.Y - pf.Y).Length() / 16);
                for (int s = 1; s < n; s++)
                {
                    var p = Vector3.Lerp(pf, pt, s / (float)n);
                    verts.Add(new P3Float(p.X, p.Y, p.Z));
                    ring.Add(verts.Count - 1);
                }
            }
            var centre = (V(a) + V(b) + V(c)) / 3;
            verts.Add(new P3Float(centre.X, centre.Y, centre.Z));
            int m = verts.Count - 1;
            // The fan keeps the triangle's winding: ring order is its own.
            for (int r = 0; r < ring.Count; r++) fans.Add((m, ring[r], ring[(r + 1) % ring.Count]));
        }
        tris.Clear();
        tris.AddRange(keep);
        tris.AddRange(fans);
        return edgesCut;
    }

    /// <summary>
    /// Drops from ledges (<see cref="FloorFit"/>'s interiors: Oblivion's): each piece of the mesh not joined to the
    /// biggest is given ways down off its edge to another piece below it, each a steep strip of two triangles from one
    /// of its border edges to a border edge 48 to 320 lower and at most 160 across, with nothing in the way off the edge
    /// or on the way down; people walk off and fall to it. As Skyrim's own interiors drop: 98 triangles steeper than 60
    /// degrees falling 96 or more join a level to the one below in 52 of Skyrim.esm's cells (Saarthal's: 0.1 of upright,
    /// 174 down, joined both sides). Oblivion's people need it: the Imperial prison's Mythic Dawn assassins come out of
    /// their door onto a ledge 190 over the first ambush (Oblivion's own path grid there is a closed group too: its AI
    /// goes straight at the target where the grid has no way), and with no way down they never reached it (r102, r103).
    /// </summary>
    internal static int DropLinks(List<P3Float> verts, List<(int A, int B, int C)> tris, IReadOnlyList<(Vector3[] Points, Vector3 Normal)> polygons,
        float reach = 160, float minDrop = 48, float maxDrop = 320)
    {
        if (tris.Count == 0) return 0;
        Vector3 V(int i) => new(verts[i].X, verts[i].Y, verts[i].Z);
        var adj = Adjacency(tris);
        var parent = Enumerable.Range(0, tris.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int i = 0; i < tris.Count; i++) for (int e = 0; e < 3; e++) if (adj[i, e] >= 0) parent[Find(i)] = Find(adj[i, e]);
        var size = new Dictionary<int, int>();
        for (int i = 0; i < tris.Count; i++) size[Find(i)] = size.GetValueOrDefault(Find(i)) + 1;
        if (size.Count < 2) return 0;
        int up = 0;
        foreach (var (a, b, c) in tris) up += Vector3.Cross(V(b) - V(a), V(c) - V(a)).Z >= 0 ? 1 : -1;
        bool ccw = up >= 0;
        var border = new List<(int A, int B, int Tri)>();
        for (int i = 0; i < tris.Count; i++)
        {
            var (a, b, c) = tris[i];
            int[][] es = [[a, b], [b, c], [c, a]];
            for (int e = 0; e < 3; e++) if (adj[i, e] < 0) border.Add((es[e][0], es[e][1], i));
        }
        // Solid faces by 128-unit squares, and a segment against them (Moller-Trumbore over each polygon's fan).
        const float SCell = 128;
        var solidBy = new Dictionary<(int, int), List<int>>();
        for (int s = 0; s < polygons.Count; s++)
        {
            var pts = polygons[s].Points;
            for (int gx = (int)MathF.Floor(pts.Min(p => p.X) / SCell); gx <= (int)MathF.Floor(pts.Max(p => p.X) / SCell); gx++)
                for (int gy = (int)MathF.Floor(pts.Min(p => p.Y) / SCell); gy <= (int)MathF.Floor(pts.Max(p => p.Y) / SCell); gy++)
                    (solidBy.TryGetValue((gx, gy), out var l) ? l : solidBy[(gx, gy)] = []).Add(s);
        }
        bool Blocked(Vector3 p, Vector3 q)
        {
            var dir = q - p;
            var seen = new HashSet<int>();
            for (int gx = (int)MathF.Floor(MathF.Min(p.X, q.X) / SCell); gx <= (int)MathF.Floor(MathF.Max(p.X, q.X) / SCell); gx++)
                for (int gy = (int)MathF.Floor(MathF.Min(p.Y, q.Y) / SCell); gy <= (int)MathF.Floor(MathF.Max(p.Y, q.Y) / SCell); gy++)
                    if (solidBy.TryGetValue((gx, gy), out var l))
                        foreach (int s in l)
                        {
                            if (!seen.Add(s)) continue;
                            var poly = polygons[s].Points;
                            for (int k = 1; k + 1 < poly.Length; k++)
                            {
                                Vector3 e1 = poly[k] - poly[0], e2 = poly[k + 1] - poly[0], h = Vector3.Cross(dir, e2);
                                float det = Vector3.Dot(e1, h);
                                if (MathF.Abs(det) < 1e-6f) continue;
                                float f = 1 / det;
                                var sv = p - poly[0];
                                float u = f * Vector3.Dot(sv, h);
                                if (u < 0 || u > 1) continue;
                                var qv = Vector3.Cross(sv, e1);
                                float v = f * Vector3.Dot(dir, qv);
                                if (v < 0 || u + v > 1) continue;
                                float t = f * Vector3.Dot(e2, qv);
                                if (t > 0.02f && t < 0.98f) return true;
                            }
                        }
            return false;
        }
        int added = 0;
        var lift = new Vector3(0, 0, 40);
        var byBigness = size.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        int main = byBigness[0];
        foreach (var piece in byBigness.Skip(1))
        {
            // Already joined to the biggest piece by a drop made before.
            if (Find(piece) == Find(main)) continue;
            // For each of its border edges, the best edge below to drop to (nearest, the drop counted a quarter); edges of
            // 32 or more on both sides first (a person is about that wide: build 21's one drop, from the ledge's corner to
            // an edge 18 long, was never taken, r104), the nearest of any length if no long pair can be had.
            var pairs = new List<(int A, int B, int U, int W, float Score, Vector3 Mid, bool Wide)>();
            foreach (var (a, b, ta) in border)
            {
                if (Find(ta) != Find(piece)) continue;
                var m = (V(a) + V(b)) / 2;
                float upperLong = new System.Numerics.Vector2(V(b).X - V(a).X, V(b).Y - V(a).Y).Length();
                (int U, int W, float Score, bool Wide)? best = null;
                foreach (var (u, w, tu) in border)
                {
                    if (Find(tu) == Find(piece)) continue;
                    var n = (V(u) + V(w)) / 2;
                    float across = new System.Numerics.Vector2(n.X - m.X, n.Y - m.Y).Length(), drop = m.Z - n.Z;
                    if (across > reach || drop < minDrop || drop > maxDrop) continue;
                    bool wide = upperLong >= 32 && new System.Numerics.Vector2(V(w).X - V(u).X, V(w).Y - V(u).Y).Length() >= 32;
                    float score = across + drop / 4;
                    if (best is { } bb && (bb.Wide && !wide || bb.Wide == wide && bb.Score <= score)) continue;
                    // Off the edge at knee height, then straight down to the floor below: nothing in the way.
                    var over = new Vector3(n.X, n.Y, m.Z) + lift;
                    if (Blocked(m + lift, over) || Blocked(over, n + lift)) continue;
                    best = (u, w, score, wide);
                }
                if (best is { } got) pairs.Add((a, b, got.U, got.W, got.Score, m, got.Wide));
            }
            if (pairs.Count == 0) continue;
            // Wide ones if any, else the single nearest; several along a ledge (128 apart at least, six at most), as
            // Skyrim's own ledges drop at more than one place.
            var chosen = new List<(int A, int B, int U, int W, float Score, Vector3 Mid, bool Wide)>();
            var pool = pairs.Any(x => x.Wide) ? pairs.Where(x => x.Wide).OrderBy(x => x.Score).ToList() : [pairs.MinBy(x => x.Score)];
            var usedLower = new HashSet<(int, int)>();
            foreach (var c in pool)
            {
                if (chosen.Count >= 6) break;
                if (chosen.Any(x => Vector3.Distance(x.Mid, c.Mid) < 128) || !usedLower.Add((c.U, c.W))) continue;
                chosen.Add(c);
            }
            int lower = -1;
            foreach (var d in chosen)
            {
                // The lower edge's ends paired with the upper's nearest them, so the strip doesn't twist.
                int lu = d.U, lw = d.W;
                if (Vector3.Distance(V(d.B), V(lu)) + Vector3.Distance(V(d.A), V(lw)) > Vector3.Distance(V(d.B), V(lw)) + Vector3.Distance(V(d.A), V(lu))) (lu, lw) = (lw, lu);
                lower = Find(border.First(x => x.A == d.U && x.B == d.W).Tri);
                foreach (var (p, q, r) in new[] { (d.A, d.B, lu), (d.A, lu, lw) })
                {
                    bool turns = Vector3.Cross(V(q) - V(p), V(r) - V(p)).Z >= 0;
                    tris.Add(turns == ccw ? (p, q, r) : (p, r, q));
                    parent = [.. parent, lower];
                }
                added++;
            }
            parent[Find(piece)] = lower;
        }
        Interlocked.Add(ref dropsAdded, added);
        return added;
    }
    static long dropsAdded;
    /// <summary>Ways down off ledges added (<see cref="DropLinks"/>).</summary>
    public static long DropsAdded => Interlocked.Read(ref dropsAdded);

    /// <summary>Dev aid: border vertices near here have their partner search printed (navmesh-bridge-test --trace).</summary>
    internal static Vector3? BridgeTraceAt;
    internal static int BridgeShowUnfloored;

    internal static int BridgeGaps(List<P3Float> verts, List<(int A, int B, int C)> tris, IReadOnlyList<(Vector3[] Points, Vector3 Normal)> polygons,
        float maxGap = 96, float climb = 32, Dictionary<string, int>? why = null)
    {
        void Why(string reason) { if (why is not null) why[reason] = why.GetValueOrDefault(reason) + 1; }
        int split = SubdivideFacingBorders(verts, tris, maxGap, climb);
        if (why is not null) why["border edges split"] = split;
        if (tris.Count == 0) return 0;
        Vector3 V(int i) => new(verts[i].X, verts[i].Y, verts[i].Z);
        var adj = Adjacency(tris);
        var parent = Enumerable.Range(0, tris.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        for (int i = 0; i < tris.Count; i++) for (int e = 0; e < 3; e++) if (adj[i, e] >= 0) parent[Find(i)] = Find(adj[i, e]);
        // The mesh's winding (most triangles'), for the joining triangles to match.
        int up = 0;
        foreach (var (a, b, c) in tris) up += Vector3.Cross(V(b) - V(a), V(c) - V(a)).Z >= 0 ? 1 : -1;
        bool ccw = up >= 0;

        // Border edges (no neighbour), each as its triangle runs it, chained into loops round each piece (the next edge
        // starts where one ends), and their vertices by the 64-unit square they are in.
        var border = new List<(int A, int B, int Piece)>();
        for (int i = 0; i < tris.Count; i++)
        {
            var (a, b, c) = tris[i];
            int[][] es = [[a, b], [b, c], [c, a]];
            for (int e = 0; e < 3; e++) if (adj[i, e] < 0) border.Add((es[e][0], es[e][1], Find(i)));
        }
        var startsAt = new Dictionary<int, List<int>>();
        for (int k = 0; k < border.Count; k++) (startsAt.TryGetValue(border[k].A, out var l) ? l : startsAt[border[k].A] = []).Add(k);
        var loops = new List<List<int>>();   // vertex indices in order round the loop
        var loopPiece = new List<int>();
        var seenEdge = new bool[border.Count];
        for (int k = 0; k < border.Count; k++)
        {
            if (seenEdge[k]) continue;
            var loop = new List<int>();
            int cur = k;
            while (cur >= 0 && !seenEdge[cur])
            {
                seenEdge[cur] = true;
                loop.Add(border[cur].A);
                int next = -1;
                if (startsAt.TryGetValue(border[cur].B, out var outs)) foreach (int o in outs) if (!seenEdge[o] && border[o].Piece == border[cur].Piece) { next = o; break; }
                cur = next;
            }
            if (loop.Count >= 3) { loops.Add(loop); loopPiece.Add(border[k].Piece); }
        }
        var placeOf = new Dictionary<int, (int Loop, int At)>();
        for (int li = 0; li < loops.Count; li++) for (int at = 0; at < loops[li].Count; at++) placeOf.TryAdd(loops[li][at], (li, at));
        const float Cell = 64;
        (int, int) Key(Vector3 p) => ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell));
        var byCell = new Dictionary<(int, int), List<int>>();
        foreach (int v in placeOf.Keys) { var key = Key(V(v)); (byCell.TryGetValue(key, out var l) ? l : byCell[key] = []).Add(v); }
        // The solid faces by the 128-unit squares they cover.
        const float SCell = 128;
        var solidBy = new Dictionary<(int, int), List<int>>();
        for (int s = 0; s < polygons.Count; s++)
        {
            var pts = polygons[s].Points;
            float x0 = pts.Min(p => p.X), x1 = pts.Max(p => p.X), y0 = pts.Min(p => p.Y), y1 = pts.Max(p => p.Y);
            for (int gx = (int)MathF.Floor(x0 / SCell); gx <= (int)MathF.Floor(x1 / SCell); gx++)
                for (int gy = (int)MathF.Floor(y0 / SCell); gy <= (int)MathF.Floor(y1 / SCell); gy++)
                    (solidBy.TryGetValue((gx, gy), out var l) ? l : solidBy[(gx, gy)] = []).Add(s);
        }
        IEnumerable<int> SolidsNear(Vector3 a, Vector3 b)
        {
            var seen = new HashSet<int>();
            for (int gx = (int)MathF.Floor(MathF.Min(a.X, b.X) / SCell); gx <= (int)MathF.Floor(MathF.Max(a.X, b.X) / SCell); gx++)
                for (int gy = (int)MathF.Floor(MathF.Min(a.Y, b.Y) / SCell); gy <= (int)MathF.Floor(MathF.Max(a.Y, b.Y) / SCell); gy++)
                    if (solidBy.TryGetValue((gx, gy), out var l)) foreach (int s in l) if (seen.Add(s)) yield return s;
        }
        // A segment against a solid polygon (fanned into triangles): Moller-Trumbore, within the segment.
        static bool Hits(Vector3 p, Vector3 q, Vector3[] poly)
        {
            var dir = q - p;
            for (int k = 1; k + 1 < poly.Length; k++)
            {
                Vector3 e1 = poly[k] - poly[0], e2 = poly[k + 1] - poly[0], h = Vector3.Cross(dir, e2);
                float det = Vector3.Dot(e1, h);
                if (MathF.Abs(det) < 1e-6f) continue;
                float f = 1 / det;
                var s = p - poly[0];
                float u = f * Vector3.Dot(s, h);
                if (u < 0 || u > 1) continue;
                var qv = Vector3.Cross(s, e1);
                float v = f * Vector3.Dot(dir, qv);
                if (v < 0 || u + v > 1) continue;
                float t = f * Vector3.Dot(e2, qv);
                if (t >= 0 && t <= 1) return true;
            }
            return false;
        }
        bool Blocked(Vector3 p, Vector3 q)
        {
            foreach (float h in new[] { 40f, 80f, 120f })
            {
                var lift = new Vector3(0, 0, h);
                foreach (int s in SolidsNear(p, q)) if (Hits(p + lift, q + lift, polygons[s].Points)) return true;
            }
            return false;
        }
        bool Supported(Vector3 p)
        {
            var top = p + new Vector3(0, 0, climb); var bottom = p - new Vector3(0, 0, climb + 16);
            foreach (int s in SolidsNear(p, p)) if (Hits(top, bottom, polygons[s].Points)) return true;
            return false;
        }

        // Each border vertex's partner: the nearest border vertex of another piece within the gap and a step, with the
        // way between clear and floored.
        var partner = new Dictionary<int, int>();
        int PartnerOf(int v)
        {
            if (partner.TryGetValue(v, out int got)) return got;
            var pv = V(v);
            int piece = loopPiece[placeOf[v].Loop], reach = (int)MathF.Ceiling(maxGap / Cell), best = -1;
            float bestD = float.MaxValue;
            bool tooHigh = false;
            var (cx, cy) = Key(pv);
            for (int gx = cx - reach; gx <= cx + reach; gx++)
                for (int gy = cy - reach; gy <= cy + reach; gy++)
                    if (byCell.TryGetValue((gx, gy), out var list))
                        foreach (int u in list)
                        {
                            if (loopPiece[placeOf[u].Loop] == piece) continue;
                            var pu = V(u);
                            float across = new System.Numerics.Vector2(pu.X - pv.X, pu.Y - pv.Y).Length(), rise = MathF.Abs(pu.Z - pv.Z);
                            if (across > maxGap) continue;
                            if (rise > climb) { tooHigh = true; continue; }
                            if (across + rise < bestD) { bestD = across + rise; best = u; }
                        }
            bool traced = BridgeTraceAt is { } at && new System.Numerics.Vector2(pv.X - at.X, pv.Y - at.Y).Length() < 160 && MathF.Abs(pv.Z - at.Z) < 96;
            if (traced)
            {
                var near = new List<string>();
                for (int gx = cx - reach; gx <= cx + reach; gx++)
                    for (int gy = cy - reach; gy <= cy + reach; gy++)
                        if (byCell.TryGetValue((gx, gy), out var list))
                            foreach (int u in list)
                            {
                                var pu = V(u);
                                float across = new System.Numerics.Vector2(pu.X - pv.X, pu.Y - pv.Y).Length();
                                if (across <= maxGap && u != v) near.Add($"{pu} piece {loopPiece[placeOf[u].Loop]}{(loopPiece[placeOf[u].Loop] == piece ? " (own)" : "")} across {across:F0}");
                            }
                Console.WriteLine($"    trace {pv} piece {piece}: {near.Count} border vertices within reach: {string.Join("; ", near.Take(6))}");
            }
            if (best >= 0)
            {
                var pu = V(best);
                bool blocked = Blocked(pv, pu), floored = true;
                for (int s = 1; s <= 3 && floored; s++) floored = Supported(Vector3.Lerp(pv, pu, s / 4f));
                if (traced) Console.WriteLine($"    trace {pv} -> {pu}: {(blocked ? "blocked" : "clear")}, {(floored ? "floored" : "no floor")}");
                if (!blocked && !floored && BridgeShowUnfloored > 0) { BridgeShowUnfloored--; Console.WriteLine($"    no floor: {pv} -> {pu}"); }
                if (blocked || !floored) { Why(blocked ? "vertex: way blocked" : "vertex: no floor under the way"); best = -1; }
            }
            else
            {
                if (traced) Console.WriteLine($"    trace {pv}: {(tooHigh ? "only higher or lower" : "nothing near")}");
                Why(tooHigh ? "vertex: only higher or lower" : "vertex: nothing near");
            }
            return partner[v] = best;
        }

        var usedEdge = new HashSet<(int, int)>();
        (int, int) E(int x, int y) => (Math.Min(x, y), Math.Max(x, y));
        int joined = 0;
        for (int li = 0; li < loops.Count; li++)
        {
            var loop = loops[li];
            int n = loop.Count;
            // Runs of neighbouring vertices whose partners are on one other loop (cyclic: started after a break).
            var partnerLoop = loop.Select(v => PartnerOf(v) is int u and >= 0 ? placeOf[u].Loop : -1).ToArray();
            int start = Array.FindIndex(partnerLoop, x => x < 0);
            if (start < 0) start = 0;
            int idx = 0;
            while (idx < n)
            {
                int q = partnerLoop[(start + idx) % n];
                if (q < 0) { idx++; continue; }
                var run = new List<int>();
                while (idx < n && partnerLoop[(start + idx) % n] == q) { run.Add(loop[(start + idx) % n]); idx++; }
                if (run.Count < 2) continue;
                // The other piece's border from the first vertex's partner to the last's, the shorter way round its loop.
                var qLoop = loops[q];
                int u0 = placeOf[partner[run[0]]].At, u1 = placeOf[partner[run[^1]]].At, m = qLoop.Count;
                int back = ((u0 - u1) % m + m) % m, fwd = ((u1 - u0) % m + m) % m;
                int steps = Math.Min(back, fwd), dir = back <= fwd ? -1 : 1;
                if (steps > 3 * run.Count + 4) { Why("run: other border too long"); continue; }
                var other = Enumerable.Range(0, steps + 1).Select(st => qLoop[((u0 + dir * st) % m + m) % m]).ToList();
                bool clash = false;
                for (int r = 0; r + 1 < run.Count && !clash; r++) clash = usedEdge.Contains(E(run[r], run[r + 1]));
                for (int r = 0; r + 1 < other.Count && !clash; r++) clash = usedEdge.Contains(E(other[r], other[r + 1]));
                if (clash) { Why("run: border already joined"); continue; }
                // Zip the two borders together, each step to the nearer of the next two vertices; every new crossing clear.
                var made = new List<(int, int, int)>();
                int i = 0, j = 0;
                bool ok = true;
                while (ok && (i < run.Count - 1 || j < other.Count - 1))
                {
                    bool takeA = j >= other.Count - 1 || (i < run.Count - 1 && Vector3.Distance(V(run[i + 1]), V(other[j])) <= Vector3.Distance(V(run[i]), V(other[j + 1])));
                    if (takeA)
                    {
                        if (Blocked(V(run[i + 1]), V(other[j]))) ok = false;
                        made.Add((run[i], run[i + 1], other[j]));
                        i++;
                    }
                    else
                    {
                        if (Blocked(V(run[i]), V(other[j + 1]))) ok = false;
                        made.Add((run[i], other[j + 1], other[j]));
                        j++;
                    }
                }
                if (!ok) { Why("run: a crossing blocked"); continue; }
                foreach (var (x, y, z) in made)
                {
                    bool isCcw = Vector3.Cross(V(y) - V(x), V(z) - V(x)).Z >= 0;
                    tris.Add(isCcw == ccw ? (x, y, z) : (x, z, y));
                }
                for (int r = 0; r + 1 < run.Count; r++) usedEdge.Add(E(run[r], run[r + 1]));
                for (int r = 0; r + 1 < other.Count; r++) usedEdge.Add(E(other[r], other[r + 1]));
                Why("run: joined");
                joined++;
            }
        }
        Interlocked.Add(ref gapsBridged, joined);
        return joined;
    }

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

    /// <summary>
    /// The triangle before a load door (-1 none): the nearest on the door's own floor, within 256 across and 150 above
    /// or below the door's origin (which sits up in the doorway: Morrowind's and Oblivion's doors a median 90 over the
    /// floor, 2026-10-20, against vanilla's 64); else the nearest under it, within 256 across and down to 700 below (a
    /// trapdoor in a ceiling, reached by a ladder from the floor under it: Maar Gan's and Ald'ruhn's guard towers, 507
    /// below, had none).
    /// </summary>
    internal static int DoorTriangleFor(int count, Func<int, (P3Float A, P3Float B, P3Float C)> corners, IPlacedObjectGetter door)
    {
        int best = -1, under = -1;
        float bestD = float.MaxValue, underD = float.MaxValue;
        var at = door.Placement!.Position;
        var p = new Vector3(at.X, at.Y, at.Z);
        // The door model's own height about its origin (its object bounds, by its scale): a floor anywhere along it is the
        // door's floor too (Legge's dark door, 1010 tall about its middle, stands 177 under the tunnel floor it closes).
        var (low, high) = doorSpans.TryGetValue(door.Base.FormKey, out var span) ? (span.Low * (door.Scale ?? 1), span.High * (door.Scale ?? 1)) : (0f, 0f);
        // Last: the nearest within 384 across and 600 over or under (a door just past 256 from its floor, Anvil's castle
        // tower door 269 and a Bravil house's 313; a trapdoor in a floor, under the navmesh it is reached from: Oblivion's
        // Paradise exit 441 under, Morrowind's Mournhold pavilion ladder 182), or straight under it to 2000 (the Imperial
        // sewers' hearth grate, 1865 over its floor, 2026-10-21).
        int far = -1, deep = -1;
        float farD = float.MaxValue, deepD = float.MaxValue;
        for (int i = 0; i < count; i++)
        {
            // Measured to the triangle's nearest point, not its middle: a hall's floor triangles can be large, their
            // middles further than 256 across from a door beside them (Oblivion's TestingHall, 2026-10-20).
            var (a, b, c) = corners(i);
            var q = ClosestOnTriangle(p, new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, b.Y, b.Z), new Vector3(c.X, c.Y, c.Z));
            float across = (q.X - p.X) * (q.X - p.X) + (q.Y - p.Y) * (q.Y - p.Y);
            float up = p.Z - q.Z;
            if (across <= 384f * 384f && MathF.Abs(up) <= 600 && across + up * up / 4 < farD) { farD = across + up * up / 4; far = i; }
            if (across <= 64f * 64f && up > 0 && up <= 2000 && up < deepD) { deepD = up; deep = i; }
            if (across > 256f * 256f) continue;
            if (MathF.Abs(up) <= 150 || (q.Z >= p.Z + low && q.Z <= p.Z + high - 32))
            {
                float d = across + up * up / 4;
                if (d < bestD) { bestD = d; best = i; }
            }
            else if (up > 0 && up <= 700 && across < underD) { underD = across; under = i; }
        }
        return best >= 0 ? best : under >= 0 ? under : far >= 0 ? far : deep;
    }

    static (P3Float A, P3Float B, P3Float C) Corners(List<P3Float> v, (int A, int B, int C) t) => (v[t.A], v[t.B], v[t.C]);

    /// <summary>Door models' heights about their origins (object bounds' Z), by base door, for DoorTriangleFor.</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<FormKey, (float Low, float High)> doorSpans = new();

    void NoteDoors(IEnumerable<IPlacedObjectGetter> doors)
    {
        foreach (var d in doors)
            if (!doorSpans.ContainsKey(d.Base.FormKey) && mod.Doors.TryGetValue(d.Base.FormKey, out var b) && b.ObjectBounds is { } ob)
                doorSpans[d.Base.FormKey] = (MathF.Min(ob.First.Z, ob.Second.Z), MathF.Max(ob.First.Z, ob.Second.Z));
    }

    /// <summary>Load doors given a door triangle after the navmeshes were made (a door placed later: the back of a
    /// one-way door; or whose partner was linked later), on the cells' own navmeshes; returns how many.</summary>
    public int AddMissingDoorTriangles(IEnumerable<Cell> cells)
    {
        int added = 0;
        foreach (var cell in cells)
        {
            var navs = cell.NavigationMeshes.Where(n => n.Data is not null).ToList();
            if (navs.Count == 0) continue;
            var have = navs.SelectMany(n => n.Data!.DoorTriangles.Select(d => d.Door.FormKey)).ToHashSet();
            foreach (var door in cell.Persistent.Concat(cell.Temporary).OfType<PlacedObject>().Where(d => d.TeleportDestination is not null && d.Placement is not null))
            {
                if (have.Contains(door.FormKey)) continue;
                NoteDoors([door]);
                (NavigationMesh Nav, int Tri, float D) pick = (navs[0], -1, float.MaxValue);
                foreach (var nav in navs)
                {
                    var d = nav.Data!;
                    (P3Float, P3Float, P3Float) Of(int i) { var t = d.Triangles[i].Vertices; return (d.Vertices[t.X], d.Vertices[t.Y], d.Vertices[t.Z]); }
                    int tri = DoorTriangleFor(d.Triangles.Count, Of, door);
                    if (tri < 0) continue;
                    var (ta, tb, tc) = Of(tri);
                    // (Only doors with a placement were taken.)
                    var at = new Vector3(door.Placement!.Position.X, door.Placement.Position.Y, door.Placement.Position.Z);
                    float dist = Vector3.Distance(at, ClosestOnTriangle(at, new Vector3(ta.X, ta.Y, ta.Z), new Vector3(tb.X, tb.Y, tb.Z), new Vector3(tc.X, tc.Y, tc.Z)));
                    if (dist < pick.D) pick = (nav, tri, dist);
                }
                if (pick.Tri < 0) continue;
                pick.Nav.Data!.DoorTriangles.Add(new DoorTriangle { TriangleBeforeDoor = (short)pick.Tri, Unknown = DoorTag, Door = door.ToLink<IPlacedObjectGetter>() });
                pick.Nav.Data.Triangles[pick.Tri].Flags |= NavmeshTriangle.Flag.Door;
                if (infoMap?.MapInfos.FirstOrDefault(i => i.NavigationMesh.FormKey == pick.Nav.FormKey) is { } info)
                    info.LinkedDoors.Add(new LinkedDoor { Unknown = DoorTag, Door = door.ToLink<IPlacedObjectGetter>() });
                have.Add(door.FormKey);
                added++;
            }
        }
        return added;
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
