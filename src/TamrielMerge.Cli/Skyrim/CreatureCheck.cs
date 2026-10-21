using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Whether a built plugin's creatures can move as they should (user, 2026-10-21: "we need a tool for the creatures
/// too"): every placed actor whose race is not a person's (no ActorTypeNPC), in its interior, by size (its record's
/// object bounds by its scale; a leveled one by the largest it can be). For each: how far it stands from its navmesh;
/// the room over its spot (from the cell's collision, CollisionReader) against its height; the room across where it
/// stands on the navmesh (to the navmesh's edge) against half its width, as a big creature in a narrow way can't turn;
/// how much navmesh it has to roam (the piece it is on) and whether that piece reaches a load door (it can follow or
/// leave); and whether it has packages. `creature-check --plugin P [--worst N]`.
/// </summary>
public static class CreatureCheck
{
    sealed record Size(float Width, float Height);

    public static void Run(string pluginPath, string skyrimData, int worstCount)
    {
        using var m = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
        using var esm = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
        var npcKeyword = esm.Keywords.FirstOrDefault(k => k.EditorID == "ActorTypeNPC")?.FormKey;
        var races = esm.Races.Concat(m.Races).GroupBy(r => r.FormKey).ToDictionary(g => g.Key, g => g.Last());
        var npcs = esm.Npcs.Concat(m.Npcs).GroupBy(n => n.FormKey).ToDictionary(g => g.Key, g => g.Last());
        var leveled = esm.LeveledNpcs.Concat(m.LeveledNpcs).GroupBy(n => n.FormKey).ToDictionary(g => g.Key, g => g.Last());
        var finder = new DungeonCheck.FileFinder(skyrimData, Path.GetDirectoryName(Path.GetFullPath(pluginPath))!);
        var models = new Dictionary<FormKey, string>();
        foreach (var rec in m.EnumerateMajorRecords().Concat(esm.Statics).Concat(esm.Furniture).Concat(esm.Containers).Concat(esm.Doors).Concat(esm.MoveableStatics).Concat(esm.Activators))
            if (rec is IModeledGetter { Model: { } mo } && rec is not INpcGetter) models[rec.FormKey] = mo.File.DataRelativePath.Path;
        var collision = new Dictionary<string, List<Vector3[]>>(StringComparer.OrdinalIgnoreCase);
        List<Vector3[]> CollisionOf(string model)
        {
            if (collision.TryGetValue(model, out var c)) return c;
            var bytes = finder.Find(model);
            try { c = bytes is null ? [] : Meshes.CollisionReader.Read(bytes).Triangles.Where(t => t.Layer != 15 && t.Layer != 12).Select(t => t.Tri).ToList(); }
            catch (ArgumentOutOfRangeException) { c = []; }
            return collision[model] = c;
        }

        // A base's race and size (an NPC's bounds; a leveled list's largest entry, through nested lists).
        (IRaceGetter? Race, Size? Size, int Packages) BaseOf(FormKey key, int depth = 0)
        {
            if (npcs.TryGetValue(key, out var npc))
            {
                var b = npc.ObjectBounds;
                // Its width across (the narrower way: a long animal turns in what it is wide, as Skyrim paths it by a radius).
                var size = b is null ? null : new Size(MathF.Min(b.Second.X - b.First.X, b.Second.Y - b.First.Y), b.Second.Z - b.First.Z);
                // Bounds and packages from its template where it takes them from it.
                var flags = npc.Configuration.TemplateFlags;
                if (npc.Template.FormKeyNullable is { } t && depth < 4 && !t.IsNull)
                {
                    var from = BaseOf(t, depth + 1);
                    return (flags.HasFlag(NpcConfiguration.TemplateFlag.Traits) ? from.Race ?? races.GetValueOrDefault(npc.Race.FormKey) : races.GetValueOrDefault(npc.Race.FormKey),
                        size is { Height: > 0 } ? size : from.Size, flags.HasFlag(NpcConfiguration.TemplateFlag.AIPackages) ? from.Packages : npc.Packages.Count);
                }
                return (races.GetValueOrDefault(npc.Race.FormKey), size, npc.Packages.Count);
            }
            if (leveled.TryGetValue(key, out var list) && depth < 4)
            {
                (IRaceGetter?, Size?, int) best = (null, null, 0);
                foreach (var e in list.Entries ?? [])
                    if (e.Data?.Reference.FormKey is { } r && BaseOf(r, depth + 1) is var got && got.Size is { } s && (best.Item2 is null || s.Width * s.Height > best.Item2.Width * best.Item2.Height))
                        best = got;
                // A list with no size of its own: the first entry it has (its race), for at least its race and packages.
                if (best.Item1 is null && list.Entries?.FirstOrDefault()?.Data?.Reference.FormKey is { } first) best = BaseOf(first, depth + 1);
                return best;
            }
            return (null, null, 0);
        }
        bool IsCreature(IRaceGetter race) => npcKeyword is null || race.Keywords?.Any(k => k.FormKey == npcKeyword) != true;

        static float? ZAt(Vector3[] t, float x, float y)
        {
            float d = (t[1].Y - t[2].Y) * (t[0].X - t[2].X) + (t[2].X - t[1].X) * (t[0].Y - t[2].Y);
            if (MathF.Abs(d) < 1e-6f) return null;
            float a = ((t[1].Y - t[2].Y) * (x - t[2].X) + (t[2].X - t[1].X) * (y - t[2].Y)) / d;
            float b = ((t[2].Y - t[0].Y) * (x - t[2].X) + (t[0].X - t[2].X) * (y - t[2].Y)) / d;
            return a < 0 || b < 0 || a + b > 1 ? null : a * t[0].Z + b * t[1].Z + (1 - a - b) * t[2].Z;
        }

        int creatures = 0, off = 0, lowRoom = 0, narrow = 0, smallPiece = 0, noDoor = 0, noPackages = 0, unknown = 0;
        var issues = new List<(string Kind, float Badness, string What)>();
        var byRace = new Dictionary<string, (int Count, float Width, float Height)>();
        foreach (var cell in m.Cells.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
        {
            var placed = cell.Persistent.Concat(cell.Temporary).OfType<IPlacedNpcGetter>().Where(p => p.Placement is not null).ToList();
            if (placed.Count == 0) continue;
            var mine = new List<(IPlacedNpcGetter Ref, IRaceGetter Race, Size Size, int Packages)>();
            foreach (var p in placed)
            {
                var (race, size, packages) = BaseOf(p.Base.FormKey);
                if (race is null || !IsCreature(race)) continue;
                creatures++;
                if (size is null || size.Height <= 0) { unknown++; continue; }
                float sc = p.Scale ?? 1;
                mine.Add((p, race, new Size(size.Width * sc, size.Height * sc), packages));
                var cur = byRace.GetValueOrDefault(race.EditorID ?? "?");
                byRace[race.EditorID ?? "?"] = (cur.Count + 1, MathF.Max(cur.Width, size.Width * sc), MathF.Max(cur.Height, size.Height * sc));
            }
            if (mine.Count == 0) continue;
            // The cell's navmesh: triangles, their pieces, each piece's area and whether a door triangle is in it, and the
            // open edges (no neighbour).
            var tris = new List<(Vector3 A, Vector3 B, Vector3 C, int Piece)>();
            var open = new List<(Vector3 A, Vector3 B)>();
            var pieceArea = new Dictionary<int, float>();
            var pieceDoor = new HashSet<int>();
            int baseIndex = 0;
            foreach (var nav in cell.NavigationMeshes.Select(n => n.Data).OfType<INavigationMeshDataGetter>())
            {
                int n = nav.Triangles.Count;
                var parent = Enumerable.Range(0, n).ToArray();
                int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
                Vector3 V(int k) => new(nav.Vertices[k].X, nav.Vertices[k].Y, nav.Vertices[k].Z);
                for (int i = 0; i < n; i++)
                {
                    var t = nav.Triangles[i];
                    (int Link, int A, int B, NavmeshTriangle.Flag F)[] edges = [(t.EdgeLink_0_1, t.Vertices.X, t.Vertices.Y, NavmeshTriangle.Flag.EdgeLink_0_1), (t.EdgeLink_1_2, t.Vertices.Y, t.Vertices.Z, NavmeshTriangle.Flag.EdgeLink_1_2), (t.EdgeLink_2_0, t.Vertices.Z, t.Vertices.X, NavmeshTriangle.Flag.EdgeLink_2_0)];
                    foreach (var e in edges)
                    {
                        if (e.Link >= 0 && e.Link < n && !t.Flags.HasFlag(e.F)) parent[Find(i)] = Find(e.Link);
                        else if (e.Link < 0 && !t.Flags.HasFlag(e.F)) open.Add((V(e.A), V(e.B)));
                    }
                }
                var doorTris = nav.DoorTriangles.Select(d => (int)d.TriangleBeforeDoor).ToHashSet();
                for (int i = 0; i < n; i++)
                {
                    var t = nav.Triangles[i];
                    var (a, b, c) = (V(t.Vertices.X), V(t.Vertices.Y), V(t.Vertices.Z));
                    int piece = baseIndex + Find(i);
                    tris.Add((a, b, c, piece));
                    pieceArea[piece] = pieceArea.GetValueOrDefault(piece) + Vector3.Cross(b - a, c - a).Length() / 2;
                    if (doorTris.Contains(i)) pieceDoor.Add(piece);
                }
                baseIndex += n;
            }
            // The cell's collision, placed (for the room over each creature).
            var world = new List<Vector3[]>();
            foreach (var o in cell.Persistent.Concat(cell.Temporary).OfType<IPlacedObjectGetter>())
            {
                if (o.Placement is not { } pl || !models.TryGetValue(o.Base.FormKey, out var model)) continue;
                var rot = SkyrimRotation.Matrix(pl.Rotation);
                var pos = new Vector3(pl.Position.X, pl.Position.Y, pl.Position.Z);
                float sc = o.Scale ?? 1;
                foreach (var t in CollisionOf(model)) world.Add(t.Select(v => Vector3.Transform(v * sc, rot) + pos).ToArray());
            }
            foreach (var (r, race, size, packages) in mine)
            {
                var at = r.Placement!.Position;
                var p = new Vector3(at.X, at.Y, at.Z);
                string what = $"{race.EditorID} ({size.Width:F0} wide, {size.Height:F0} tall) in {cell.EditorID} at {at}";
                if (packages == 0) { noPackages++; issues.Add(("no packages", 1, what)); }
                // Its navmesh point and piece.
                (float D, Vector3 Q, int Piece) near = (float.MaxValue, default, -1);
                foreach (var t in tris)
                {
                    var q = Navmesh.NavmeshBuilder.ClosestOnTriangle(p, t.A, t.B, t.C);
                    float d = Vector3.Distance(p, q);
                    if (d < near.D) near = (d, q, t.Piece);
                }
                if (near.Piece < 0 || near.D > 64) { off++; issues.Add(("off its navmesh", near.D, $"{what}: navmesh {(near.Piece < 0 ? "none" : near.D.ToString("F0"))} away")); continue; }
                // Room over the navmesh where it stands, to the first collision over it.
                float? room = null;
                foreach (var t in world)
                    if (ZAt(t, near.Q.X, near.Q.Y) is { } z && z > near.Q.Z + 8 && (room is null || z - near.Q.Z < room)) room = z - near.Q.Z;
                if (room is { } rm && rm < size.Height) { lowRoom++; issues.Add(("too tall for its room", size.Height - rm, $"{what}: {rm:F0} over it")); }
                // Room across: to the navmesh's nearest open edge (in plan, on its level).
                float across = float.MaxValue;
                foreach (var (a, b) in open)
                {
                    if (MathF.Abs((a.Z + b.Z) / 2 - near.Q.Z) > 96) continue;
                    var ab = new System.Numerics.Vector2(b.X - a.X, b.Y - a.Y); var aq = new System.Numerics.Vector2(near.Q.X - a.X, near.Q.Y - a.Y);
                    float s = ab.LengthSquared() > 0 ? Math.Clamp(System.Numerics.Vector2.Dot(aq, ab) / ab.LengthSquared(), 0, 1) : 0;
                    across = MathF.Min(across, (aq - ab * s).Length());
                }
                // The navmesh keeps 24 (the agent's radius) off walls; a creature needs half its width past that.
                if (across < float.MaxValue && across + 24 < size.Width / 2) { narrow++; issues.Add(("too wide for its way", size.Width / 2 - across - 24, $"{what}: {across + 24:F0} to the walls")); }
                float area = pieceArea.GetValueOrDefault(near.Piece);
                if (area < 4 * size.Width * size.Width) { smallPiece++; issues.Add(("little room to roam", 4 * size.Width * size.Width - area, $"{what}: its navmesh piece {area:F0} sq units")); }
                if (!pieceDoor.Contains(near.Piece) && pieceDoor.Count > 0) { noDoor++; issues.Add(("no load door on its piece", area, $"{what}: piece {area:F0} sq units, apart from the doors")); }
            }
        }
        Console.WriteLine($"creature-check {Path.GetFileName(pluginPath)}: {creatures:N0} creatures placed in interiors ({unknown} of no known size)");
        Console.WriteLine($"  off their navmesh {off}, too tall for the room over them {lowRoom}, too wide for the way they stand in {narrow}, little navmesh to roam {smallPiece}, on a piece no load door is on {noDoor}, without packages {noPackages}");
        Console.WriteLine($"  races: {string.Join(", ", byRace.OrderByDescending(kv => kv.Value.Count).Take(25).Select(kv => $"{kv.Key} x{kv.Value.Count} (to {kv.Value.Width:F0} x {kv.Value.Height:F0})"))}");
        foreach (var g in issues.GroupBy(i => i.Kind))
        {
            Console.WriteLine($"  {g.Key}:");
            foreach (var i in g.OrderByDescending(i => i.Badness).Take(worstCount)) Console.WriteLine($"    {i.What}");
        }
    }
}
