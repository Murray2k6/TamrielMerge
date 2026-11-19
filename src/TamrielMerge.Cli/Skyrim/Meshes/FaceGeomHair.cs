using System.Numerics;
using nifly;
using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Skyrim.Meshes;

/// <summary>
/// Another game's hair on Skyrim's head: the head a hair model was made for measured against Skyrim's
/// (<see cref="OntoHead"/>), and Skyrim's head as the surface the hair is then fitted outside of
/// (<see cref="HeadOf"/>, WornMesh.FitOutside). The hair is written as its own model and worn (Oblivion: ObHair).
/// Not cut into Skyrim's pre-made face files (facegendata\facegeom): their shapes are BSDynamicTriShape, and a plain
/// BSTriShape put among them crashed the game as the cell loaded (the Emperor's face, 2026-11-14).
/// </summary>
public static class FaceGeomHair
{
    /// <summary>The head shape of a face file or of the head model in the skeleton's bind space: the shapes drawn with
    /// the face tint shader (4); every shape when there is none.</summary>
    public static List<WornShape> HeadOf(IReadOnlyList<WornShape> shapes)
    {
        var head = shapes.Where(s => s.ShaderType == 4).ToList();
        return head.Count > 0 ? head : shapes.ToList();
    }

    /// <summary>
    /// Brings shapes made for one head onto another: the two heads' skulls (their vertices in the upper 55% of their
    /// height: brow, crown, ears, nape) box to box, each axis scaled and moved on its own. Returns the map's scale.
    /// </summary>
    public static Vector3 OntoHead(List<WornShape> shapes, IReadOnlyList<WornShape> fromHead, IReadOnlyList<WornShape> toHead)
    {
        static (Vector3 Min, Vector3 Max) Skull(IReadOnlyList<WornShape> head)
        {
            var all = head.SelectMany(s => s.V).ToList();
            if (all.Count == 0) throw new InvalidDataException("a head without vertices");
            float lo = all.Min(p => p.Z), hi = all.Max(p => p.Z), cut = lo + 0.45f * (hi - lo);
            var top = all.Where(p => p.Z >= cut).ToList();
            return (top.Aggregate(Vector3.Min), top.Aggregate(Vector3.Max));
        }
        var (aMin, aMax) = Skull(fromHead);
        var (bMin, bMax) = Skull(toHead);
        var size = aMax - aMin;
        if (size.X < 1e-3f || size.Y < 1e-3f || size.Z < 1e-3f) throw new InvalidDataException("a flat head");
        var scale = (bMax - bMin) / size;
        foreach (var s in shapes)
        {
            for (int i = 0; i < s.V.Length; i++) s.V[i] = bMin + (s.V[i] - aMin) * scale;
            // Normals under a non-uniform scale: by its inverse, then to unit length.
            for (int i = 0; i < s.N.Length; i++)
            {
                var n = s.N[i] / scale;
                s.N[i] = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitZ;
            }
        }
        return scale;
    }
}
