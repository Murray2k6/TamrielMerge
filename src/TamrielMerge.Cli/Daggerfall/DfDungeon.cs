using System.Numerics;

namespace TamrielMerge.Daggerfall;

/// <summary>A placed model polygon in dungeon space (Daggerfall units, Y up, Z north).</summary>
public sealed record DfPlacedPolygon(Vector3[] Points, Vector3 Normal, int TextureArchive, int TextureRecord, uint ModelId);

/// <summary>A whole location dungeon: its RDB blocks laid out on the block grid.</summary>
public sealed class DfDungeon
{
    public required DfLocation Location { get; init; }
    public required IReadOnlyList<(DfDungeonBlock Block, DfDungeonBlockPlacement Placement)> Blocks { get; init; }

    public static DfDungeon Load(DfLocation location, DfBsa blocksBsa, Dictionary<string, DfDungeonBlock> cache) => new()
    {
        Location = location,
        Blocks = location.DungeonBlocks.Select(b =>
        {
            if (!cache.TryGetValue(b.BlockName, out var block)) cache[b.BlockName] = block = DfDungeonBlock.Load(blocksBsa, b.BlockName);
            return (block, b);
        }).ToList(),
    };

    public static Vector3 BlockOrigin(DfDungeonBlockPlacement p) => new(p.X * DfDungeonBlock.Side, 0, p.Z * DfDungeonBlock.Side);

    /// <summary>
    /// Model-to-dungeon transform. Daggerfall Unity applies Quaternion.Euler(-x, -y, -z) in Unity's frame
    /// (rotate about Z, then X, then Y); positions and vertices here are already in that frame.
    /// </summary>
    public static Matrix4x4 ObjectTransform(DfRdbObject o, Vector3 blockOrigin)
    {
        float rad = MathF.PI / 180f;
        var rot = UnityRotZ(-o.Rotation.Z * rad) * UnityRotX(-o.Rotation.X * rad) * UnityRotY(-o.Rotation.Y * rad);
        return rot * Matrix4x4.CreateTranslation(blockOrigin + o.Position);
    }

    // Unity's (left-handed) axis rotations written for System.Numerics' row-vector convention.
    public static Matrix4x4 UnityRotY(float a) => new(MathF.Cos(a), 0, -MathF.Sin(a), 0, 0, 1, 0, 0, MathF.Sin(a), 0, MathF.Cos(a), 0, 0, 0, 0, 1);
    public static Matrix4x4 UnityRotX(float a) => new(1, 0, 0, 0, 0, MathF.Cos(a), MathF.Sin(a), 0, 0, -MathF.Sin(a), MathF.Cos(a), 0, 0, 0, 0, 1);
    public static Matrix4x4 UnityRotZ(float a) => new(MathF.Cos(a), MathF.Sin(a), 0, 0, -MathF.Sin(a), MathF.Cos(a), 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);

    public IEnumerable<DfPlacedPolygon> Polygons(IReadOnlyDictionary<uint, DfModel> models)
    {
        foreach (var (block, placement) in Blocks)
        {
            var origin = BlockOrigin(placement);
            foreach (var o in block.Objects.Where(o => o.Type == DfRdbObjectType.Model))
            {
                if (!models.TryGetValue(o.ModelId, out var model)) continue;
                var m = ObjectTransform(o, origin);
                foreach (var poly in model.Polygons)
                    yield return new DfPlacedPolygon(poly.Points.Select(p => Vector3.Transform(p, m)).ToArray(),
                        Vector3.Normalize(Vector3.TransformNormal(poly.Normal, m)), poly.TextureArchive, poly.TextureRecord, o.ModelId);
            }
        }
    }
}
