using System.Numerics;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Which way an object standing in a Daggerfall room should face: into the room. Daggerfall's statues are sprites
/// that always face the viewer (or models Skyrim's statues don't match), so they carry no heading of their own; turned
/// at random they faced walls and doorways (user, 2026-10-10: "some statues are rotated"). Level rays in
/// <see cref="Rays"/> directions are cast against the walls around the point, and the object faces where they reach
/// furthest on balance: the sum of the rays, each as long as it gets before a wall (out of an alcove, across a
/// corridor); standing against a wall, straight out from the nearest one. Daggerfall units, +Y up.
/// </summary>
public sealed class RoomFacing
{
    const int Rays = 32;
    const float Reach = 1024;   // Daggerfall units (2048 in Skyrim)
    const float AgainstWall = 120;
    readonly List<DfPlacedPolygon> walls;

    public RoomFacing(IEnumerable<DfPlacedPolygon> polygons) =>
        // Props replaced by Skyrim objects (the statue itself among them) aren't walls.
        walls = polygons.Where(p => MathF.Abs(p.Normal.Y) < 0.5f && p.Points.Length >= 3 && !PropReplacer.IsReplaced(p.ModelId)).ToList();

    /// <summary>The way to face from <paramref name="at"/> (a point a little above the floor), as a Skyrim horizontal
    /// direction (Daggerfall x, z); null in open space with no side more open than another.</summary>
    public Vector2? Toward(Vector3 at)
    {
        var near = walls.Where(w => w.Points.Any(p => MathF.Abs(p.X - at.X) < Reach && MathF.Abs(p.Z - at.Z) < Reach)
            && w.Points.Min(p => p.Y) <= at.Y && w.Points.Max(p => p.Y) >= at.Y).ToList();
        var sum = Vector2.Zero;
        float closest = float.MaxValue; var away = Vector2.Zero;
        for (int i = 0; i < Rays; i++)
        {
            float a = MathF.Tau * i / Rays;
            var d = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
            float hit = Hit(near, at, d);
            sum += new Vector2(d.X, d.Z) * hit;
            if (hit < closest) { closest = hit; away = -new Vector2(d.X, d.Z); }
        }
        // Standing against a wall (a statue flanking a doorway, in a corner): straight out from the nearest one.
        if (closest < AgainstWall) return away;
        return sum.Length() < Reach * 0.5f ? null : Vector2.Normalize(sum);
    }

    /// <summary>Dev aid: each ray's length from <paramref name="at"/> (direction in Daggerfall x, z).</summary>
    public IEnumerable<(Vector2 Dir, float Hit)> RaysFrom(Vector3 at)
    {
        var near = walls.Where(w => w.Points.Any(p => MathF.Abs(p.X - at.X) < Reach && MathF.Abs(p.Z - at.Z) < Reach)
            && w.Points.Min(p => p.Y) <= at.Y && w.Points.Max(p => p.Y) >= at.Y).ToList();
        for (int i = 0; i < Rays; i++)
        {
            float a = MathF.Tau * i / Rays;
            var d = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
            yield return (new Vector2(d.X, d.Z), Hit(near, at, d));
        }
    }

    /// <summary>How far along the level ray from <paramref name="o"/> the first wall is, up to <see cref="Reach"/>.</summary>
    static float Hit(List<DfPlacedPolygon> walls, Vector3 o, Vector3 d)
    {
        float best = Reach;
        foreach (var w in walls)
        {
            float denom = Vector3.Dot(w.Normal, d);
            if (MathF.Abs(denom) < 1e-4f) continue;
            float t = Vector3.Dot(w.Normal, w.Points[0] - o) / denom;
            if (t <= 0.5f || t >= best) continue;
            var h = o + d * t;
            bool inside = true; float sign = 0;
            for (int i = 0; i < w.Points.Length && inside; i++)
            {
                var e = w.Points[(i + 1) % w.Points.Length] - w.Points[i];
                float c = Vector3.Dot(Vector3.Cross(e, h - w.Points[i]), w.Normal);
                if (MathF.Abs(c) < 1e-3f) continue;
                if (sign == 0) sign = MathF.Sign(c); else if (MathF.Sign(c) != sign) inside = false;
            }
            if (inside) best = t;
        }
        return best;
    }

    /// <summary>The heading that turns a model whose front is local -Y (or +Y) to face <paramref name="toward"/>
    /// (Skyrim turns local (x, y) into (x cos h + y sin h, -x sin h + y cos h)).</summary>
    public static float Heading(Vector2 toward, bool frontPlusY) =>
        frontPlusY ? MathF.Atan2(toward.X, toward.Y) : MathF.Atan2(-toward.X, -toward.Y);
}
