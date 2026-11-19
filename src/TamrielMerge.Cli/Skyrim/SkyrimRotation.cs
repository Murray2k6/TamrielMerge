using System.Numerics;

namespace TamrielMerge.Skyrim;

/// <summary>
/// How Skyrim turns a placed reference by its three angles (radians, each clockwise looking down its axis): about the
/// world's Z first, then Y, then X; for row vectors v * RotZ(-z) * RotY(-y) * RotX(-x). Measured on the game's own
/// data (2026-10-20, rot-order-vanilla: Skyrim.esm's 25233 tilted statics, their collision placed by each order
/// against the navmesh built on them; of the 26496 navmesh vertices the orders disagree on, Z-Y-X puts 45.7% on the
/// collision, X-Y-Z, which this code used before, 23.1%; against Z-X-Y, 1012 to 347). It is OpenMW's order for
/// Morrowind's references too, and Oblivion's (ob-rot-order: Oblivion.esm's 2880 tilted interior statics against their
/// pathgrids; of the 1273 points the orders disagree on, Z-Y-X 55.1%, X-Y-Z 37.3%; against Z-X-Y, 65 to 40), which is
/// why both games' angles carry over as they are.
/// </summary>
public static class SkyrimRotation
{
    /// <summary>The reference's rotation as a row-vector matrix (Vector3.Transform(v, m) turns model into world space).</summary>
    public static Matrix4x4 Matrix(Vector3 angles) =>
        Matrix4x4.CreateRotationZ(-angles.Z) * Matrix4x4.CreateRotationY(-angles.Y) * Matrix4x4.CreateRotationX(-angles.X);

    public static Matrix4x4 Matrix(Noggog.P3Float angles) => Matrix(new Vector3(angles.X, angles.Y, angles.Z));

    /// <summary>
    /// The angles (each in 0..2pi) that give a rotation, from its row-vector matrix (its rows are where the model's
    /// axes go). As column vectors that matrix is R = X(a) Y(b) Z(c) with a = -x, b = -y, c = -z.
    /// </summary>
    public static Vector3 Angles(Matrix4x4 m)
    {
        // R[i,j] = m[j,i].
        float b = MathF.Asin(Math.Clamp(m.M31, -1, 1));
        float a, c;
        // A quarter turn about the middle axis leaves the other two turning about one line: one of them is taken as 0.
        // Told by the matrix's own element, not by the angle's cosine: an exact quarter turn comes out of single
        // precision as 0.99999994, whose angle's cosine is 3.4e-4, and the two angles were then read from the rounding
        // noise of elements that should be 0 (2026-11-11, rg-euler-test with quarter turns: an error of 2, a full
        // reversal; Redguard's banners, laid flat in their models and hung by their placement, stood at odd angles).
        if (MathF.Abs(m.M31) < 0.999999f) { a = MathF.Atan2(-m.M32, m.M33); c = MathF.Atan2(-m.M21, m.M11); }
        else { b = MathF.CopySign(MathF.PI / 2, m.M31); a = 0; c = MathF.Atan2(m.M12, m.M22); }
        return new Vector3(Wrap(-a), Wrap(-b), Wrap(-c));
    }

    static float Wrap(float r) { r %= MathF.Tau; return r < 0 ? r + MathF.Tau : r; }
}
