using Noggog;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// What Oblivion's faces say of their people: the age FaceGen gives a face. Oblivion's faces are FaceGen's 50 shape
/// and 50 texture coordinates (FGGS, FGTS on the NPC); its facegen\si.ctl (FRCTL001) holds FaceGen's controls over
/// them, among them the age: after the slider controls (56 shape, 26 asymmetric, 33 texture: 50, 30 and 50 numbers
/// and a label each), per race group four rows of 50 numbers and an offset: age by shape, age by texture, gender by
/// shape, gender by texture (measured in Oblivion's file 2026-11-14: offsets 29.715, 32.116, -0.196, -0.582, the
/// five groups alike). A face's age is the offset plus the row's product with its coordinates, in years.
/// </summary>
public sealed class ObFaces
{
    readonly float[]? ageShape, ageTexture, shade;
    readonly float shapeOffset, textureOffset;

    public ObFaces(ObData data)
    {
        if (data.Read(@"facegen\si.ctl") is not { Length: > 32 } b || System.Text.Encoding.ASCII.GetString(b, 0, 8) != "FRCTL001") return;
        int nGS = BitConverter.ToInt32(b, 16), nGA = BitConverter.ToInt32(b, 20), nTS = BitConverter.ToInt32(b, 24), nTA = BitConverter.ToInt32(b, 28);
        int p = 32, section = 0;
        foreach (int n in new[] { nGS, nGA, nTS, nTA })
        {
            int count = BitConverter.ToInt32(b, p); p += 4;
            for (int k = 0; k < count; k++)
            {
                int at = p;
                p += 4 * n;
                int length = BitConverter.ToInt32(b, p);
                string label = System.Text.Encoding.ASCII.GetString(b, p + 4, length);
                p += 4 + length;
                // The texture control "Skin Shade Dark / Light": how light the skin is (higher is lighter).
                if (section == 2 && label.StartsWith("Skin Shade", StringComparison.OrdinalIgnoreCase)) { int q = at; shade = Row(b, ref q, n); }
            }
            section++;
        }
        if (p + 4 * (nGS + 1 + nTS + 1) > b.Length) return;
        ageShape = Row(b, ref p, nGS); shapeOffset = BitConverter.ToSingle(b, p); p += 4;
        ageTexture = Row(b, ref p, nTS); textureOffset = BitConverter.ToSingle(b, p);
    }

    static float[] Row(byte[] b, ref int p, int n)
    {
        var row = new float[n];
        for (int i = 0; i < n; i++) row[i] = BitConverter.ToSingle(b, p + 4 * i);
        p += 4 * n;
        return row;
    }

    static float Dot(float[] row, ReadOnlyMemorySlice<byte>? coords)
    {
        if (coords is not { } c) return 0;
        var span = c.Span;
        float sum = 0;
        for (int i = 0; i < row.Length && 4 * i + 4 <= span.Length; i++) sum += row[i] * BitConverter.ToSingle(span.Slice(4 * i, 4));
        return sum;
    }

    /// <summary>The face's age in years by its shape and by its texture (skin), or null without Oblivion's controls
    /// or a face of its own.</summary>
    public (float Shape, float Texture)? Ages(Ob.INpcGetter n)
    {
        if (ageShape is null || ageTexture is null || n.FaceGenGeometrySymmetric is null) return null;
        return (shapeOffset + Dot(ageShape, n.FaceGenGeometrySymmetric), textureOffset + Dot(ageTexture, n.FaceGenTextureSymmetric));
    }

    /// <summary>How light the face's skin is among Oblivion's (FaceGen's "Skin Shade Dark / Light" control over its
    /// texture coordinates: 0 the race's own, positive lighter, negative darker), or null without the control.</summary>
    public float? Shade(Ob.INpcGetter n) => shade is null || n.FaceGenTextureSymmetric is null ? null : Dot(shade, n.FaceGenTextureSymmetric);

    /// <summary>One age for a face: the mean of the two (the skin's wrinkles and the shape's sag both show it).</summary>
    public float? Age(Ob.INpcGetter n) => Ages(n) is var (s, t) ? (s + t) / 2 : null;
}
