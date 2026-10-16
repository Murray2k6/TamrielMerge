using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Skyrim.Distant;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Checks the built Daggerfall dungeons offline (user, 2026-10-13: "check for items floating, statue rotations, npc
/// placement in dungeons"): every interior cell of the Daggerfall plugins built from dungeon blocks (DFDunBlk*), against
/// its blocks' own geometry (their meshes, placed as the cell places them):
/// <list type="bullet">
/// <item>Floating: an item, piece of furniture or prop whose base is more than 12 units above whatever is under it (the
/// floor, or the top of another object), unless it hangs (a ceiling just over it) or is mounted (a wall beside it).</item>
/// <item>Statues facing a wall: a statue whose front (local -Y; +Y for StatueMara01/StatueTalos01, as ClutterPlacer)
/// meets a wall within 80 units while its back has more room.</item>
/// <item>NPCs and creatures: no floor under them, standing more than 32 units above it, inside a wall or inside a
/// solid object, or away from the cell's navmesh.</item>
/// </list>
/// </summary>
public static class DungeonCheck
{
    public sealed record Problem(string Kind, string Cell, string Ref, string Base, float X, float Y, float Z, string Detail);

    sealed record BaseInfo(string Type, string EditorId, string? Model, Vector3 Min, Vector3 Max);

    readonly record struct Tri(Vector3 A, Vector3 B, Vector3 C, Vector3 N);

    static readonly HashSet<string> Rests = ["MiscItem", "Weapon", "Armor", "Book", "Ingredient", "Ingestible", "Key", "Ammunition", "SoulGem", "Scroll",
        "Container", "Furniture", "Static", "MoveableStatic", "Flora"];
    static readonly HashSet<string> Solid = ["Container", "Furniture", "Static", "MoveableStatic"];
    static readonly HashSet<string> FrontPlusY = new(["StatueMara01", "StatueTalos01"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Things checked for floating, by base editor ID (the last run's).</summary>
    static readonly Dictionary<string, int> Checked = new(StringComparer.OrdinalIgnoreCase);

    public static List<Problem> Run(string dir, string skyrimData, Action<string> log, int limit = 0, IReadOnlyCollection<string>? onlyCells = null)
    {
        Checked.Clear();
        var pluginNames = new[] { "Daggerfall.esm", DaggerfallTowns.PluginName, DaggerfallDungeons.PluginName };
        var mods = new List<ISkyrimModDisposableGetter>();
        foreach (var m in new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }.Select(m => Path.Combine(skyrimData, m)).Where(File.Exists))
            mods.Add(SkyrimMod.CreateFromBinaryOverlay(m, SkyrimRelease.SkyrimSE));
        var ours = new List<ISkyrimModDisposableGetter>();
        foreach (var p in pluginNames.Select(p => Path.Combine(dir, p)).Where(File.Exists))
        {
            var m = SkyrimMod.CreateFromBinaryOverlay(p, SkyrimRelease.SkyrimSE);
            mods.Add(m); ours.Add(m);
        }
        var problems = new List<Problem>();
        try
        {
            var bases = new Dictionary<FormKey, BaseInfo>();
            foreach (var m in mods)
                foreach (var r in m.EnumerateMajorRecords())
                    if (r is IObjectBoundedGetter { ObjectBounds: { } ob } && r is IPlaceableObjectGetter)
                        bases[r.FormKey] = new BaseInfo(r.Registration.Name, r.EditorID ?? "", (r as IModeledGetter)?.Model?.File.DataRelativePath.Path,
                            new Vector3(ob.First.X, ob.First.Y, ob.First.Z), new Vector3(ob.Second.X, ob.Second.Y, ob.Second.Z));
            log($"{bases.Count} placeable bases");
            var files = new FileFinder(skyrimData, dir);
            var blockGeometry = new Dictionary<string, List<Tri>?>(StringComparer.OrdinalIgnoreCase);
            List<Tri>? Geometry(string model)
            {
                if (blockGeometry.TryGetValue(model, out var g)) return g;
                g = null;
                if (files.Find(model) is { } bytes && DistantModel.Load(bytes) is { } dm)
                {
                    g = [];
                    foreach (var part in dm.Parts)
                        for (int t = 0; t < part.T.Length; t += 3)
                        {
                            Vector3 a = part.P[part.T[t]], b = part.P[part.T[t + 1]], c = part.P[part.T[t + 2]];
                            var n = Vector3.Cross(b - a, c - a);
                            if (n.LengthSquared() < 1e-6f) continue;
                            // The side it is seen from, by the model's own vertex normals (meshes wind faces clockwise).
                            var shade = part.N[part.T[t]] + part.N[part.T[t + 1]] + part.N[part.T[t + 2]];
                            if (Vector3.Dot(n, shade) < 0) n = -n;
                            g.Add(new Tri(a, b, c, Vector3.Normalize(n)));
                        }
                }
                return blockGeometry[model] = g;
            }

            int cells = 0;
            var counts = new Dictionary<string, int>();
            foreach (var mod in ours)
                foreach (var cell in mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
                {
                    var refs = cell.Persistent.Concat(cell.Temporary).ToList();
                    var blocks = refs.OfType<IPlacedObjectGetter>().Where(r => bases.TryGetValue(r.Base.FormKey, out var b) && b.EditorId.StartsWith("DFDunBlk")).ToList();
                    if (blocks.Count == 0) continue;
                    if (onlyCells is { Count: > 0 } && !onlyCells.Contains(cell.EditorID ?? "", StringComparer.OrdinalIgnoreCase)) continue;
                    // Daggerfall's action models (lifts, platforms, moving floors and walls) are rooms' surfaces too.
                    blocks.AddRange(refs.OfType<IPlacedObjectGetter>().Where(r => bases.TryGetValue(r.Base.FormKey, out var b) && b.EditorId.StartsWith("DFAct") && b.Model is not null));
                    if (limit > 0 && cells >= limit) break;
                    cells++;
                    CheckCell(cell, refs, blocks, bases, Geometry, problems);
                    if (cells % 250 == 0) log($"  {cells} dungeon cells checked, {problems.Count} problems so far");
                }
            foreach (var g in problems.GroupBy(p => p.Kind)) counts[g.Key] = g.Count();
            log($"{cells} dungeon cells checked");
            foreach (var (kind, n) in counts.OrderByDescending(kv => kv.Value)) log($"  {kind}: {n}");
            // The things most often floating, against how many were checked.
            var floating = problems.Where(p => p.Kind.StartsWith("Floating")).GroupBy(p => p.Base).OrderByDescending(g => g.Count()).Take(12);
            foreach (var g in floating) log($"    {g.Key}: {g.Count()} of {Checked.GetValueOrDefault(g.Key)} floating");
            return problems;
        }
        finally { foreach (var m in mods) m.Dispose(); }
    }

    static Matrix4x4 Placement(P3Float rot, float scale) =>
        Matrix4x4.CreateRotationX(-rot.X) * Matrix4x4.CreateRotationY(-rot.Y) * Matrix4x4.CreateRotationZ(-rot.Z) * Matrix4x4.CreateScale(scale);

    sealed class Grid
    {
        const float Size = 128;
        readonly Dictionary<(int, int), List<int>> buckets = new();
        public readonly List<Tri> Tris = [];
        public void Add(Tri t)
        {
            int i = Tris.Count;
            Tris.Add(t);
            float x0 = MathF.Min(t.A.X, MathF.Min(t.B.X, t.C.X)), x1 = MathF.Max(t.A.X, MathF.Max(t.B.X, t.C.X));
            float y0 = MathF.Min(t.A.Y, MathF.Min(t.B.Y, t.C.Y)), y1 = MathF.Max(t.A.Y, MathF.Max(t.B.Y, t.C.Y));
            for (int bx = (int)MathF.Floor(x0 / Size); bx <= (int)MathF.Floor(x1 / Size); bx++)
                for (int by = (int)MathF.Floor(y0 / Size); by <= (int)MathF.Floor(y1 / Size); by++)
                {
                    if (!buckets.TryGetValue((bx, by), out var l)) buckets[(bx, by)] = l = [];
                    l.Add(i);
                }
        }
        public IEnumerable<Tri> Near(float x0, float y0, float x1, float y1)
        {
            var seen = new HashSet<int>();
            for (int bx = (int)MathF.Floor(MathF.Min(x0, x1) / Size); bx <= (int)MathF.Floor(MathF.Max(x0, x1) / Size); bx++)
                for (int by = (int)MathF.Floor(MathF.Min(y0, y1) / Size); by <= (int)MathF.Floor(MathF.Max(y0, y1) / Size); by++)
                    if (buckets.TryGetValue((bx, by), out var l))
                        foreach (int i in l) if (seen.Add(i)) yield return Tris[i];
        }
    }

    static float? HeightAt(Tri t, float x, float y)
    {
        Vector3 a = t.A, b = t.B, c = t.C;
        float den = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        if (MathF.Abs(den) < 1e-6f) return null;
        float w0 = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / den;
        float w1 = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / den;
        float w2 = 1 - w0 - w1;
        return w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f ? null : w0 * a.Z + w1 * b.Z + w2 * c.Z;
    }

    /// <summary>Distance along a ray to a triangle (Möller-Trumbore, both sides), or null.</summary>
    static float? Hit(Tri t, Vector3 o, Vector3 d)
    {
        var e1 = t.B - t.A; var e2 = t.C - t.A;
        var p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-7f) return null;
        float inv = 1 / det;
        var s = o - t.A;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1) return null;
        var q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, q) * inv;
        if (v < 0 || u + v > 1) return null;
        float dist = Vector3.Dot(e2, q) * inv;
        // A face the ray starts on counts (a wall torch's origin is on the wall's plane: rays from it met the wall at
        // distance 0 or a hair behind, and a quarter of the torches read as floating).
        return dist > -0.5f ? MathF.Max(dist, 0) : null;
    }

    static float Ray(Grid g, Vector3 o, Vector3 d, float max)
    {
        var end = o + d * max;
        float best = max;
        foreach (var t in g.Near(MathF.Min(o.X, end.X), MathF.Min(o.Y, end.Y), MathF.Max(o.X, end.X), MathF.Max(o.Y, end.Y)))
            if (Hit(t, o, d) is { } h && h < best) best = h;
        return best;
    }

    sealed record Box(FormKey Key, Vector3[] Corners, float Top, float Bottom);

    static void CheckCell(ICellGetter cell, List<IPlacedGetter> refs, List<IPlacedObjectGetter> blocks, Dictionary<FormKey, BaseInfo> bases,
        Func<string, List<Tri>?> geometry, List<Problem> problems)
    {
        string cellName = cell.EditorID ?? cell.FormKey.ToString();
        if (Environment.GetEnvironmentVariable("TM_CHECK_NEAR") is { Length: > 0 } near && near.Split(',') is [var nc, var nx, var ny] && nc == cellName)
            foreach (var r in refs.OfType<IPlacedObjectGetter>())
                if (r.Placement is { } q && MathF.Abs(q.Position.X - float.Parse(nx)) < 1200 && MathF.Abs(q.Position.Y - float.Parse(ny)) < 1200)
                    Console.WriteLine($"  near: {(bases.TryGetValue(r.Base.FormKey, out var nb) ? $"{nb.Type} {nb.EditorId} {nb.Model} {nb.Min}..{nb.Max}" : r.Base.FormKey.ToString())} at {q.Position} rot {q.Rotation} scale {r.Scale}");
        var floors = new Grid(); var ceilings = new Grid(); var walls = new Grid(); var all = new Grid();
        foreach (var b in blocks)
        {
            if (b.Placement is not { } pl || bases[b.Base.FormKey].Model is not { } model) continue;
            var g = geometry(model.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? model : Path.Combine("meshes", model));
            if (g is null) continue;
            var m = Placement(pl.Rotation, b.Scale ?? 1);
            var at = new Vector3(pl.Position.X, pl.Position.Y, pl.Position.Z);
            foreach (var t in g)
            {
                var w = new Tri(Vector3.Transform(t.A, m) + at, Vector3.Transform(t.B, m) + at, Vector3.Transform(t.C, m) + at, Vector3.Normalize(Vector3.TransformNormal(t.N, m)));
                all.Add(w);
                // Flat faces are floors below a thing and ceilings above it, whichever way they face.
                if (MathF.Abs(w.N.Z) >= 0.5f) { floors.Add(w); ceilings.Add(w); }
                else walls.Add(w);
            }
        }
        if (Environment.GetEnvironmentVariable("TM_CHECK_NEAR") is { Length: > 0 } near2 && near2.Split(',') is [var c2, var x2, var y2] && c2 == cellName)
        {
            float qx = float.Parse(x2), qy = float.Parse(y2);
            Console.WriteLine($"  geometry: {all.Tris.Count} triangles, {floors.Tris.Count} floors, {ceilings.Tris.Count} ceilings, {walls.Tris.Count} walls from {blocks.Count} blocks");
            foreach (var bl in blocks.Where(x => bases[x.Base.FormKey].EditorId.StartsWith("DFDunBlk")))
            {
                var gg = geometry(Path.Combine("meshes", bases[bl.Base.FormKey].Model!.Replace("Meshes\\", "", StringComparison.OrdinalIgnoreCase)));
                if (gg is null) { Console.WriteLine($"  block {bases[bl.Base.FormKey].EditorId}: no geometry"); continue; }
                var lo = gg.SelectMany(t => new[] { t.A, t.B, t.C }).Aggregate(Vector3.Min); var hi = gg.SelectMany(t => new[] { t.A, t.B, t.C }).Aggregate(Vector3.Max);
                Console.WriteLine($"  block {bases[bl.Base.FormKey].EditorId} at {bl.Placement?.Position}: local {lo} .. {hi}");
            }
            foreach (var n in refs.OfType<IPlacedNpcGetter>().Take(12)) Console.WriteLine($"  npc at {n.Placement?.Position}");
            foreach (var nm in cell.NavigationMeshes)
                if (nm.Data is { } nd && nd.Vertices.Count > 0)
                    Console.WriteLine($"  navmesh {nd.Vertices.Count} vertices: x {nd.Vertices.Min(v => v.X)}..{nd.Vertices.Max(v => v.X)} y {nd.Vertices.Min(v => v.Y)}..{nd.Vertices.Max(v => v.Y)} z {nd.Vertices.Min(v => v.Z)}..{nd.Vertices.Max(v => v.Z)}");
            var allBlocks = refs.OfType<IPlacedObjectGetter>().Where(r => bases.TryGetValue(r.Base.FormKey, out var bb) && bb.EditorId.StartsWith("DFDunBlk")).ToList();
            Console.WriteLine($"  all {allBlocks.Count} block refs x {allBlocks.Min(r => r.Placement!.Position.X)}..{allBlocks.Max(r => r.Placement!.Position.X)} y {allBlocks.Min(r => r.Placement!.Position.Y)}..{allBlocks.Max(r => r.Placement!.Position.Y)}");
            foreach (var t in floors.Near(qx - 64, qy - 64, qx + 64, qy + 64).Take(8)) Console.WriteLine($"  floor near: {t.A} {t.B} {t.C}");
            foreach (var t in all.Near(qx - 32, qy - 32, qx + 32, qy + 32).Take(8)) Console.WriteLine($"  any near: {t.A} {t.B} {t.C} n {t.N}");
        }
        // Solid objects' boxes (turned about z), for what things rest on and what people stand in.
        var boxes = new List<Box>();
        foreach (var r in refs.OfType<IPlacedObjectGetter>())
        {
            if (r.Placement is not { } pl || !bases.TryGetValue(r.Base.FormKey, out var bi) || !Solid.Contains(bi.Type) || bi.EditorId.StartsWith("DFDunBlk")) continue;
            float sc = r.Scale ?? 1, h = pl.Rotation.Z, c = MathF.Cos(h), s = MathF.Sin(h);
            var p = pl.Position;
            var corners = (from x in new[] { bi.Min.X * sc, bi.Max.X * sc } from y in new[] { bi.Min.Y * sc, bi.Max.Y * sc }
                           select new Vector3(p.X + x * c + y * s, p.Y - x * s + y * c, 0)).ToArray();
            boxes.Add(new Box(r.FormKey, corners, p.Z + bi.Max.Z * sc, p.Z + bi.Min.Z * sc));
        }
        static bool Inside(Box b, float x, float y, float shrink = 0)
        {
            // Convex quad (the corners in x-major order: 0,1,3,2 around).
            var q = new[] { b.Corners[0], b.Corners[1], b.Corners[3], b.Corners[2] };
            float sign = 0;
            for (int i = 0; i < 4; i++)
            {
                var a = q[i]; var c = q[(i + 1) % 4];
                float cross = (c.X - a.X) * (y - a.Y) - (c.Y - a.Y) * (x - a.X);
                float len = MathF.Max(MathF.Sqrt((c.X - a.X) * (c.X - a.X) + (c.Y - a.Y) * (c.Y - a.Y)), 1e-3f);
                float d = cross / len;
                if (sign == 0) sign = MathF.Sign(d);
                if (MathF.Sign(d) != sign || MathF.Abs(d) < shrink) return false;
            }
            return true;
        }
        float? SupportBelow(float x, float y, float z, FormKey self)
        {
            float? best = null;
            foreach (var t in floors.Near(x, y, x, y))
                if (HeightAt(t, x, y) is { } h && h <= z + 8 && (best is null || h > best)) best = h;
            foreach (var b in boxes)
                if (b.Key != self && b.Top <= z + 8 && (best is null || b.Top > best) && Inside(b, x, y)) best = b.Top;
            return best;
        }
        float? CeilingAbove(float x, float y, float z)
        {
            float? best = null;
            foreach (var t in ceilings.Near(x, y, x, y))
                if (HeightAt(t, x, y) is { } h && h >= z - 8 && (best is null || h < best)) best = h;
            return best;
        }
        float WallDistance(Vector3 at, float reach)
        {
            float best = reach;
            for (int k = 0; k < 8; k++)
            {
                float a = k * MathF.PI / 4;
                best = MathF.Min(best, Ray(walls, at, new Vector3(MathF.Cos(a), MathF.Sin(a), 0), reach));
            }
            return best;
        }

        // Moving parts (TMDfAction) and their trigger boxes (TMDfTrigger): each must have somewhere to go and its chain
        // and targets in this cell, or it does nothing in the game.
        var here = refs.Select(r => r.FormKey).ToHashSet();
        foreach (var r in refs.OfType<IPlacedObjectGetter>())
        {
            if (r.VirtualMachineAdapter is not { } vm || r.Placement is not { } at) continue;
            void Flag(string kind, string detail) => problems.Add(new Problem(kind, cellName, r.FormKey.ToString(), bases.TryGetValue(r.Base.FormKey, out var bb) ? bb.EditorId : "", at.Position.X, at.Position.Y, at.Position.Z, detail));
            foreach (var sc in vm.Scripts)
            {
                var props = sc.Properties.ToDictionary(pr => pr.Name, StringComparer.OrdinalIgnoreCase);
                if (sc.Name == "TMDfAction")
                {
                    int type = props.GetValueOrDefault("ActionType") is IScriptIntPropertyGetter ti ? ti.Data : 0;
                    bool isDoor = bases.TryGetValue(r.Base.FormKey, out var rb) && rb.Type == "Door";
                    // Action sprites don't move (Daggerfall Unity moves the billboard in no time): they pass the trigger on.
                    bool isFlat = rb?.EditorId.StartsWith("DFActionFlat") == true;
                    bool spins = props.GetValueOrDefault("SpinPoses") is IScriptFloatListPropertyGetter { Data.Count: >= 6 };
                    if (type == 1 && !isDoor && !isFlat && !spins)
                    {
                        if (props.GetValueOrDefault("EndPose") is not IScriptFloatListPropertyGetter pose || pose.Data.Count != 6) Flag("Moving part: nowhere to move", "");
                        else
                        {
                            var d = pose.Data;
                            float moved = new Vector3(d[0] - at.Position.X, d[1] - at.Position.Y, d[2] - at.Position.Z).Length();
                            float turned = MathF.Max(MathF.Abs(Wrap(d[3] - at.Rotation.X * 180 / MathF.PI)), MathF.Max(MathF.Abs(Wrap(d[4] - at.Rotation.Y * 180 / MathF.PI)), MathF.Abs(Wrap(d[5] - at.Rotation.Z * 180 / MathF.PI))));
                            if (moved < 1 && turned < 0.5f && !(rb?.EditorId.Contains("WHE") ?? false)) Flag("Moving part: ends where it starts", "");
                        }
                    }
                    if (props.GetValueOrDefault("NextObject") is IScriptObjectPropertyGetter next && !next.Object.IsNull && !here.Contains(next.Object.FormKey))
                        Flag("Moving part: its chain leads out of the cell", next.Object.FormKey.ToString());
                }
                else if (sc.Name == "TMDfTrigger"
                    && (props.GetValueOrDefault("Target") is not IScriptObjectPropertyGetter target || target.Object.IsNull || !here.Contains(target.Object.FormKey)))
                    Flag("Trigger box: no target", "");
            }
        }
        static float Wrap(float deg) { while (deg > 180) deg -= 360; while (deg < -180) deg += 360; return deg; }

        foreach (var r in refs)
        {
            if (r.Placement is not { } pl) continue;
            var p = new Vector3(pl.Position.X, pl.Position.Y, pl.Position.Z);
            bases.TryGetValue(r is IPlacedObjectGetter po ? po.Base.FormKey : FormKey.Null, out var bi);
            string baseName = bi?.EditorId ?? (r is IPlacedNpcGetter npc ? npc.Base.FormKey.ToString() : "");
            void Add(string kind, string detail) => problems.Add(new Problem(kind, cellName, r.FormKey.ToString(), baseName, p.X, p.Y, p.Z, detail));

            if (r is IPlacedNpcGetter)
            {
                var support = SupportBelow(p.X, p.Y, p.Z + 24, r.FormKey);
                if (support is null) Add("NPC: no floor under", "");
                else if (p.Z - support > 32) Add("NPC: standing in the air", $"{p.Z - support:F0} above the floor");
                var chest = p + new Vector3(0, 0, 48);
                if (WallDistance(chest, 12) < 12) Add("NPC: in a wall", "");
                if (boxes.Any(b => chest.Z > b.Bottom + 8 && chest.Z < b.Top - 8 && Inside(b, p.X, p.Y, 6))) Add("NPC: inside an object", "");
                if (!OnNavmesh(cell, p)) Add("NPC: off the navmesh", "");
                continue;
            }
            if (bi is null || !Rests.Contains(bi.Type) || bi.EditorId.StartsWith("DFDunBlk") || bi.EditorId.StartsWith("DFAct") || r is not IPlacedObjectGetter obj) continue;
            float sc = obj.Scale ?? 1;
            if ((bi.Max - bi.Min).Length() < 2 || bi.EditorId.Contains("Marker", StringComparison.OrdinalIgnoreCase) || bi.EditorId.StartsWith("FX", StringComparison.OrdinalIgnoreCase)) continue;   // markers, effects
            // A prison's evidence chests, hidden 2,000 units under it as Skyrim's are (CrimeFactions).
            if (bi.EditorId.StartsWith("EvidenceChest", StringComparison.OrdinalIgnoreCase)) continue;
            float bottom = p.Z + bi.Min.Z * sc, top = p.Z + bi.Max.Z * sc;

            // Statues: front into the room.
            if (bi.EditorId.Contains("Statue", StringComparison.OrdinalIgnoreCase))
            {
                float h = pl.Rotation.Z, sign = FrontPlusY.Contains(bi.EditorId) ? 1 : -1;
                var front = new Vector3(MathF.Sin(h), MathF.Cos(h), 0) * sign;
                var mid = new Vector3(p.X, p.Y, (bottom + top) / 2);
                float ahead = Ray(walls, mid, front, 400), behind = Ray(walls, mid, -front, 400);
                if (ahead < 80 && behind > ahead + 64) Add("Statue facing a wall", $"wall {ahead:F0} in front, {behind:F0} behind");
            }

            lock (Checked) Checked[bi.EditorId] = Checked.GetValueOrDefault(bi.EditorId) + 1;
            // A floor or top between its lowest point and its middle: standing in it, not floating above the one below.
            if (SupportBelow(p.X, p.Y, (bottom + top) / 2, obj.FormKey) is { } inside && inside - bottom > 12)
            {
                Add("Sunk into the floor", $"{inside - bottom:F0} units in");
                continue;
            }
            var under = SupportBelow(p.X, p.Y, bottom, obj.FormKey);
            float gap = under is { } u ? bottom - u : float.MaxValue;
            if (gap <= 12) continue;
            // Hung from a ceiling or fixed to a wall: not floating.
            if (CeilingAbove(p.X, p.Y, top) is { } ceil && ceil - top < 32) continue;
            float reach = MathF.Max(bi.Max.X - bi.Min.X, bi.Max.Y - bi.Min.Y) * sc + 32;
            if (WallDistance(new Vector3(p.X, p.Y, (bottom + top) / 2), reach) < reach) continue;
            Add(under is null ? "Floating: nothing under it" : "Floating", under is null ? "" : $"{gap:F0} units up");
        }
    }

    static bool OnNavmesh(ICellGetter cell, Vector3 p)
    {
        foreach (var navm in cell.NavigationMeshes)
        {
            if (navm.Data is not { } data) continue;
            var v = data.Vertices;
            foreach (var t in data.Triangles)
            {
                var a = v[t.Vertices.X]; var b = v[t.Vertices.Y]; var c = v[t.Vertices.Z];
                var tri = new Tri(new Vector3(a.X, a.Y, a.Z), new Vector3(b.X, b.Y, b.Z), new Vector3(c.X, c.Y, c.Z), Vector3.UnitZ);
                if (HeightAt(tri, p.X, p.Y) is { } h && MathF.Abs(h - p.Z) <= 64) return true;
            }
        }
        return false;
    }

    /// <summary>Files by Data-relative path: the converted output first, then Skyrim's Data and archives.</summary>
    sealed class FileFinder(string skyrimData, string dir)
    {
        readonly Dictionary<string, Mutagen.Bethesda.Archives.IArchiveFile> archived = Load(skyrimData);
        static Dictionary<string, Mutagen.Bethesda.Archives.IArchiveFile> Load(string data)
        {
            var d = new Dictionary<string, Mutagen.Bethesda.Archives.IArchiveFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Directory.GetFiles(data, "Skyrim - *.bsa"))
                foreach (var f in Mutagen.Bethesda.Archives.Archive.CreateReader(GameRelease.SkyrimSE, p).Files) d.TryAdd(f.Path.Replace('/', '\\'), f);
            return d;
        }
        public byte[]? Find(string rel)
        {
            rel = rel.Replace('/', '\\').TrimStart('\\');
            foreach (var root in new[] { dir, skyrimData })
                if (Path.Combine(root, rel) is var p && File.Exists(p)) return File.ReadAllBytes(p);
            return archived.TryGetValue(rel, out var f) ? f.GetBytes() : null;
        }
    }
}
