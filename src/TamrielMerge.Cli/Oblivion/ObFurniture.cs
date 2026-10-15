using nifly;
using TamrielMerge.Skyrim.Meshes;
using V3 = System.Numerics.Vector3;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's furniture markers as Skyrim's. Oblivion marks where an actor stands to get in (the entry point, at floor
/// height, facing the furniture: orientation in thousandths of a radian, clockwise from +Y) with an animation reference
/// (beds 1-4, seats 11 left, 12 right, 13/14 front); its get-in animation then moves the actor about 55 units in.
/// Skyrim marks where the actor sits or lies (at the seat or mattress surface, heading the way a seated actor faces,
/// and for a bed from the head to the feet)
/// with the sides it may be entered from. So each front entry of a seat (and each side entry of a bed) gives a seat or
/// sleeping place 55 units in, at the surface height found in the model there; side entries of seats and the foot
/// entry of a bed become entry sides of the nearest place. The record's enabled markers follow Oblivion's.
/// </summary>
public sealed partial class ObMeshConverter
{
    /// <summary>A Skyrim furniture marker: position, heading, sit or lie, entry sides (Skyrim's bits: front 1,
    /// behind 2, right 4, left 8), and the Oblivion markers it comes from.</summary>
    public sealed record Seat(V3 At, float Heading, bool Lie, ushort Entries, List<int> Sources)
    {
        /// <summary>The entry side (Skyrim bit) each Oblivion marker of <see cref="Sources"/> gives.</summary>
        public List<ushort> SourceSides { get; init; } = [];
    }

    const float EntryDepth = 55;

    /// <summary>
    /// The model's seats or beds (<paramref name="sleep"/>), from its Oblivion markers and the converted model's
    /// geometry (already written to <paramref name="convertedPath"/>), written into that model as Skyrim's marker
    /// block. Empty when the model has no markers.
    /// </summary>
    public List<Seat> AddSeats(string model, string convertedPath, bool sleep)
    {
        // Once per converted model (a left and a right bed share theirs).
        if (seatsByModel.TryGetValue(convertedPath, out var done)) return done;
        return seatsByModel[convertedPath] = ReadSeats(model, convertedPath, sleep);
    }

    readonly Dictionary<string, List<Seat>> seatsByModel = new(StringComparer.OrdinalIgnoreCase);

    List<Seat> ReadSeats(string model, string convertedPath, bool sleep)
    {
        string source = model.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? model : @"meshes\" + model;
        var bytes = data.Read(source);
        if (bytes is null || IsOldNif(bytes) || Ob10Nif.IsUnreadableByNifly(bytes, out _)) return [];
        string tmp = Path.Combine(Path.GetTempPath(), $"tm_furn_{Guid.NewGuid():N}.nif");
        File.WriteAllBytes(tmp, bytes);
        try
        {
            using var src = new NifFile();
            if (src.Load(tmp) != 0) return [];
            var sh = src.GetHeader();
            BSFurnitureMarker? marker = null;
            for (uint i = 0; i < sh.GetNumBlocks() && marker is null; i++) marker = sh.GetBlockById(i) as BSFurnitureMarker;
            if (marker is null) return [];
            var entries = NiflyRaw.Elements<FurniturePosition>(marker.positions, 24)
                .Select(p => (At: new V3(p.offset.x, p.offset.y, p.offset.z), Facing: p.orientation / 1000f, Ref: (int)p.posRef1)).ToList();
            if (entries.Count == 0) return [];

            using var nif = new NifFile();
            if (nif.Load(convertedPath) != 0) return [];
            var tris = nif.GetShapes().SelectMany(s => Model(nif, s)).ToList();
            var verts = tris.SelectMany(t => t).ToList();
            var seats = sleep ? Beds(entries, verts, tris) : Chairs(entries, tris);
            if (seats.Count == 0) return [];

            // Skyrim's marker block: Oblivion's copied (one position per entry), each rewritten, the rest dropped.
            var node = BlockMeshBuilder.Owned(new BSFurnitureMarkerNode());
            node.name = new NiStringRef("FRN");
            node.positions = marker.positions;
            var slots = NiflyRaw.Elements<FurniturePosition>(node.positions, 24);
            for (int k = 0; k < seats.Count; k++)
            {
                var s = seats[k];
                slots[k].offset = new nifly.Vector3(s.At.X, s.At.Y, s.At.Z);
                slots[k].heading = s.Heading;
                slots[k].animationType = (ushort)(s.Lie ? 2 : 1);
                slots[k].entryPoints = s.Entries;
                slots[k].orientation = 0;
                slots[k].posRef1 = 0;
                slots[k].posRef2 = 0;
            }
            NiflyRaw.Truncate(node.positions, slots, seats.Count);
            nif.AssignExtraData(nif.GetRootNode(), node);
            if (nif.Save(convertedPath) != 0) return [];
            return seats;
        }
        finally { File.Delete(tmp); }
    }

    /// <summary>A shape's triangles in model space.</summary>
    static IEnumerable<V3[]> Model(NifFile nif, NiShape shape)
    {
        var chain = new List<MatTransform> { shape.transform };
        for (var n = nif.GetParentNode(shape); n is not null; n = nif.GetParentNode(n)) chain.Add(n.transform);
        var verts = (nif.GetVertsForShape(shape) ?? []).Select(v => { var g = v; foreach (var t in chain) g = t.ApplyTransform(g); return new V3(g.x, g.y, g.z); }).ToList();
        using var tris = new vectorTriangle();
        shape.GetTriangles(tris);
        foreach (var t in tris)
            if (t.p1 < verts.Count && t.p2 < verts.Count && t.p3 < verts.Count) yield return [verts[t.p1], verts[t.p2], verts[t.p3]];
    }

    static V3 Dir(float heading) => new(MathF.Sin(heading), MathF.Cos(heading), 0);
    static float Wrap(float a) { a %= MathF.Tau; if (a < 0) a += MathF.Tau; return a > MathF.Tau - 1e-3f ? 0 : a; }

    /// <summary>The highest surface straight below a point (a seat, a mattress: the model's triangles under it)
    /// between <paramref name="lo"/> and <paramref name="hi"/> above the floor, tried a little around it for seams;
    /// or null.</summary>
    static float? Surface(List<V3[]> tris, V3 at, float floor, float lo, float hi)
    {
        foreach (var (dx, dy) in new[] { (0f, 0f), (4f, 0f), (-4f, 0f), (0f, 4f), (0f, -4f), (8f, 8f), (-8f, -8f) })
        {
            float x = at.X + dx, y = at.Y + dy, best = float.MinValue;
            foreach (var t in tris)
            {
                var (a, b, c) = (t[0], t[1], t[2]);
                float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                if (MathF.Abs(d) < 1e-6f) continue;   // edge-on: a wall, not a surface
                float u = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / d;
                float v = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / d;
                if (u < 0 || v < 0 || u + v > 1) continue;
                float z = u * a.Z + v * b.Z + (1 - u - v) * c.Z;
                if (z >= floor + lo && z <= floor + hi && z > best) best = z;
            }
            if (best > float.MinValue) return best;
        }
        return null;
    }

    /// <summary>Which side of a place an entry is on, as Skyrim's entry bits (front 1, right 4, left 8).</summary>
    static ushort Side(V3 place, float heading, V3 entry)
    {
        var d = entry - place; d.Z = 0;
        var forward = Dir(heading);
        var right = new V3(forward.Y, -forward.X, 0);
        if (V3.Dot(d, forward) > MathF.Abs(V3.Dot(d, right))) return 1;
        return (ushort)(V3.Dot(d, right) > 0 ? 4 : 8);
    }

    static List<Seat> Chairs(List<(V3 At, float Facing, int Ref)> entries, List<V3[]> tris)
    {
        var seats = new List<Seat>();
        // Front entries (13, 14, and anything not a side entry) make the seats, facing back out the way in.
        for (int i = 0; i < entries.Count; i++)
        {
            var (at, facing, r) = entries[i];
            if (r is 11 or 12) continue;
            var spot = at + Dir(facing) * EntryDepth;
            float heading = Wrap(facing + MathF.PI);
            if (seats.FindIndex(s => V3.Distance(s.At with { Z = 0 }, spot with { Z = 0 }) < 20 && MathF.Abs(MathF.IEEERemainder(s.Heading - heading, MathF.Tau)) < 0.8f) is var same and >= 0)
            {
                seats[same].Sources.Add(i);
                seats[same].SourceSides.Add(1);
                continue;
            }
            spot.Z = Surface(tris, spot, at.Z, 10, 70) ?? at.Z + 34;
            seats.Add(new Seat(spot, heading, false, 1, [i]) { SourceSides = [1] });
        }
        // Side entries: a way into the nearest seat.
        for (int i = 0; i < entries.Count; i++)
        {
            var (at, facing, r) = entries[i];
            if (r is not (11 or 12)) continue;
            if (seats.Count == 0)
            {
                // A seat entered only from its sides (a stool): the seat 55 units in, facing as the entry turns right.
                var spot = at + Dir(facing) * EntryDepth;
                spot.Z = Surface(tris, spot, at.Z, 10, 70) ?? at.Z + 34;
                seats.Add(new Seat(spot, Wrap(facing + (r == 11 ? -MathF.PI / 2 : MathF.PI / 2)), false, 0, []));
            }
            int k = Enumerable.Range(0, seats.Count).MinBy(j => V3.Distance(seats[j].At with { Z = 0 }, at with { Z = 0 }));
            var s = seats[k];
            ushort side = Side(s.At, s.Heading, at);
            seats[k] = s with { Entries = (ushort)(s.Entries | side), Sources = [.. s.Sources, i], SourceSides = [.. s.SourceSides, side] };
        }
        return seats;
    }

    static List<Seat> Beds(List<(V3 At, float Facing, int Ref)> entries, List<V3> verts, List<V3[]> tris)
    {
        if (verts.Count == 0) return [];
        var lo = verts.Aggregate(V3.Min); var hi = verts.Aggregate(V3.Max);
        var centre = (lo + hi) / 2;
        // The bed's length: its longer side.
        var along = hi.X - lo.X > hi.Y - lo.Y ? V3.UnitX : V3.UnitY;
        var across = along == V3.UnitX ? V3.UnitY : V3.UnitX;
        // A narrow bed (a single, a bedroll) sleeps one, down its middle.
        bool single = V3.Dot(hi - lo, across) < 110;
        bool Foot(float facing) => MathF.Abs(V3.Dot(Dir(facing), along)) > 0.7f;
        var beds = new List<Seat>();
        for (int i = 0; i < entries.Count; i++)
        {
            var (at, facing, _) = entries[i];
            if (Foot(facing)) continue;
            var spot = at + Dir(facing) * EntryDepth;
            if (single) spot += across * V3.Dot(centre - spot, across);
            if (beds.FindIndex(b => V3.Distance(b.At with { Z = 0 }, spot with { Z = 0 }) < 40) is var same and >= 0)
            {
                // Both sides of a single bed: one sleeping place between them.
                var b = beds[same];
                var mid = (b.At + spot) / 2;
                ushort side = Side(mid, b.Heading, at);
                beds[same] = b with { At = mid with { Z = b.At.Z }, Entries = (ushort)(b.Entries | side), Sources = [.. b.Sources, i], SourceSides = [.. b.SourceSides, side] };
                continue;
            }
            // The head end: the higher end of the bed (its headboard), else the end the entry is nearer (Oblivion's
            // entries, like Skyrim's markers, sit a little towards the head).
            float Height(float sign) => verts.Where(v => V3.Dot(v - centre, along) * sign > V3.Dot(hi - lo, along) * 0.35f).Select(v => v.Z).DefaultIfEmpty(lo.Z).Max();
            float up = Height(1), down = Height(-1);
            float sign = MathF.Abs(up - down) > 5 ? MathF.Sign(up - down) : V3.Dot(at - centre, along) > 0 ? 1 : -1;
            // Skyrim heads a bed marker from the head to the feet (vanilla CommonBed01: headboard at -Y, heading 0).
            var feet = -along * sign;
            float heading = Wrap(MathF.Atan2(feet.X, feet.Y));
            spot.Z = Surface(tris, spot, at.Z, 5, 80) ?? at.Z + 37;
            ushort entry = Side(spot, heading, at);
            beds.Add(new Seat(spot, heading, true, entry, [i]) { SourceSides = [entry] });
        }
        for (int i = 0; i < entries.Count; i++)
        {
            var (at, facing, _) = entries[i];
            if (!Foot(facing)) continue;
            if (beds.Count == 0)
            {
                // Entered only from the foot (a bedroll): the sleeper's middle at the bed's centre, head away, so the
                // feet towards the entry.
                var spot = centre;
                spot.Z = Surface(tris, spot, at.Z, 0, 80) ?? at.Z + 10;
                beds.Add(new Seat(spot, Wrap(facing + MathF.PI), true, 1, [i]) { SourceSides = [1] });
                continue;
            }
            int k = Enumerable.Range(0, beds.Count).MinBy(j => V3.Distance(beds[j].At with { Z = 0 }, at with { Z = 0 }));
            beds[k] = beds[k] with { Entries = (ushort)(beds[k].Entries | 1), Sources = [.. beds[k].Sources, i], SourceSides = [.. beds[k].SourceSides, 1] };
        }
        return beds;
    }
}
