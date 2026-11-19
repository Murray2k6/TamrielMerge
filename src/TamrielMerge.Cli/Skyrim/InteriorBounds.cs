using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Interiors kept where Skyrim leaves them alone. Loading an interior, Skyrim moves every reference with a model that
/// stands 30,000 units or more from the origin on x or y to where the player arrives: measured in the game on
/// 2026-11-12 (TMNative.DumpCell against the plugin's records): all 124 statics, activators and doors of Redguard's
/// Draggin Tale, built 30,700 units out on both axes, stood at one point and its people fell out of the world; with the
/// same cell moved about, every reference kept its place at 29,937 on either axis and none did at 30,121. Lights (no
/// model) and people are not moved. Skyrim's own interiors reach 23,593 at most.
/// <para>Checked just before each converted plugin is written. An interior whose body reaches past
/// <see cref="Reach"/> is moved as a whole so its middle is at the origin: its references, its navmeshes and the arrival
/// points of the doors that lead into it. A reference still past the reach after that (the games park hidden objects
/// hundreds of thousands of units away: Oblivion's Horn Cave has one 526,134 out) is held at the reach, off in its own
/// direction, rather than left for Skyrim to drop at the door.</para>
/// <para>Redguard's interiors are centred by its own builder (its scripts work in the scene's coordinates and must
/// know the move); this finds nothing left to do there.</para>
/// </summary>
public static class InteriorBounds
{
    /// <summary>How far from the origin, on x or y, anything is left indoors.</summary>
    public const float Reach = 28000;

    public sealed record Result(int CellsMoved, int ReferencesHeld, int ArrivalsMoved);

    static Placement? PlacementOf(IPlaced p) => p switch
    {
        PlacedObject o => o.Placement,
        PlacedNpc a => a.Placement,
        APlacedTrap t => t.Placement,
        _ => null,
    };

    public static Result Apply(SkyrimMod mod, Action<string>? log = null)
    {
        int cellsMoved = 0, held = 0, arrivals = 0;
        var shifts = new Dictionary<FormKey, P3Float>();   // a moved interior's references, each with the cell's move
        foreach (var cell in mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
        {
            var placed = cell.Persistent.Concat(cell.Temporary).Select(p => (Ref: p, At: PlacementOf(p))).Where(x => x.At is not null).ToList();
            if (placed.Count == 0 || placed.All(x => MathF.Abs(x.At!.Position.X) <= Reach && MathF.Abs(x.At.Position.Y) <= Reach)) continue;
            // The body of the cell: what stands within 40,000 of the median on both axes (parked objects left out).
            var xs = placed.Select(x => x.At!.Position.X).OrderBy(v => v).ToList(); var ys = placed.Select(x => x.At!.Position.Y).OrderBy(v => v).ToList();
            float mx = xs[xs.Count / 2], my = ys[ys.Count / 2];
            var body = placed.Where(x => MathF.Abs(x.At!.Position.X - mx) < 40000 && MathF.Abs(x.At.Position.Y - my) < 40000).ToList();
            float minX = body.Min(x => x.At!.Position.X), maxX = body.Max(x => x.At!.Position.X), minY = body.Min(x => x.At!.Position.Y), maxY = body.Max(x => x.At!.Position.Y);
            float dx = 0, dy = 0;
            if (minX < -Reach || maxX > Reach) dx = -MathF.Round((minX + maxX) / 2 / 64) * 64;
            if (minY < -Reach || maxY > Reach) dy = -MathF.Round((minY + maxY) / 2 / 64) * 64;
            if (dx != 0 || dy != 0)
            {
                foreach (var (r, at) in placed)
                {
                    at!.Position = new P3Float(at.Position.X + dx, at.Position.Y + dy, at.Position.Z);
                    shifts[r.FormKey] = new P3Float(dx, dy, 0);
                }
                foreach (var nav in cell.NavigationMeshes)
                {
                    if (nav.Data is not { } d) continue;
                    for (int i = 0; i < d.Vertices.Count; i++) d.Vertices[i] = new P3Float(d.Vertices[i].X + dx, d.Vertices[i].Y + dy, d.Vertices[i].Z);
                    d.Min = new P3Float(d.Min.X + dx, d.Min.Y + dy, d.Min.Z);
                    d.Max = new P3Float(d.Max.X + dx, d.Max.Y + dy, d.Max.Z);
                }
                cellsMoved++;
                log?.Invoke($"  interior {cell.EditorID}: reached {MathF.Max(MathF.Max(-minX, maxX), MathF.Max(-minY, maxY)):F0} from the origin; moved by {dx:F0}, {dy:F0} (Skyrim moves what stands 30,000 out)");
            }
            int heldHere = 0;
            foreach (var (_, at) in placed)
            {
                var p = at!.Position;
                if (MathF.Abs(p.X) <= Reach && MathF.Abs(p.Y) <= Reach) continue;
                at.Position = new P3Float(Math.Clamp(p.X, -Reach, Reach), Math.Clamp(p.Y, -Reach, Reach), p.Z);
                heldHere++;
            }
            if (heldHere > 0)
            {
                held += heldHere;
                log?.Invoke($"  interior {cell.EditorID}: {heldHere} reference{(heldHere == 1 ? "" : "s")} parked past {Reach:F0} held there");
            }
        }
        // The doors that lead into a moved interior arrive where it is now.
        if (shifts.Count > 0)
            foreach (var door in mod.EnumerateMajorRecords<IPlacedObject>())
                if (door.TeleportDestination is { } t && shifts.TryGetValue(t.Door.FormKey, out var s))
                {
                    t.Position = new P3Float(t.Position.X + s.X, t.Position.Y + s.Y, t.Position.Z);
                    arrivals++;
                }
        if (cellsMoved > 0 || held > 0)
            log?.Invoke($"  interiors ({mod.ModKey.FileName}): {cellsMoved} moved to the origin ({arrivals} door arrivals with them), {held} parked references held at {Reach:F0}");
        return new Result(cellsMoved, held, arrivals);
    }
}
