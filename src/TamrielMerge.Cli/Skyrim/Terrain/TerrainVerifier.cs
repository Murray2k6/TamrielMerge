using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim.Terrain;

/// <summary>
/// Decodes LAND height data back to absolute heights to check (a) our [x, y] axis convention against
/// vanilla Skyrim, where neighbouring cells must share identical edge vertices, and (b) that our encoded
/// cells reproduce the generated height field.
/// </summary>
public static class TerrainVerifier
{
    const int N = HeightField.QuadsPerCell + 1;

    public static float[,] Decode(ILandscapeGetter land, bool transposed = false)
    {
        var hm = land.VertexHeightMap!;
        var h = new float[N, N];
        float row = hm.Offset;
        for (int y = 0; y < N; y++)
        {
            row += D(hm.HeightMap, 0, y, transposed);
            float cur = row;
            h[0, y] = cur * 8;
            for (int x = 1; x < N; x++)
            {
                cur += D(hm.HeightMap, x, y, transposed);
                h[x, y] = cur * 8;
            }
        }
        return h;
    }

    static sbyte D(IReadOnlyArray2d<sbyte> a, int x, int y, bool transposed) => transposed ? a[y, x] : a[x, y];

    static Dictionary<(int, int), ICellGetter> CellsByGrid(IWorldspaceGetter ws) =>
        ws.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items)
            .Where(c => c.Grid is not null && c.Landscape?.VertexHeightMap is not null)
            .ToDictionary(c => (c.Grid!.Point.X, c.Grid.Point.Y));

    /// <summary>Largest mismatch along shared east/west and north/south edges, for both axis conventions.</summary>
    public static (float asXY, float transposed) EdgeMismatch(IWorldspaceGetter ws, int sampleCells = 400)
    {
        var cells = CellsByGrid(ws);
        float best = 0, alt = 0;
        int checkedCells = 0;
        foreach (var ((x, y), cell) in cells)
        {
            if (!cells.TryGetValue((x + 1, y), out var east) || !cells.TryGetValue((x, y + 1), out var north)) continue;
            foreach (bool t in new[] { false, true })
            {
                var a = Decode(cell.Landscape!, t); var e = Decode(east.Landscape!, t); var n = Decode(north.Landscape!, t);
                float m = 0;
                for (int i = 0; i < N; i++)
                    m = Math.Max(m, Math.Max(Math.Abs(a[N - 1, i] - e[0, i]), Math.Abs(a[i, N - 1] - n[i, 0])));
                if (t) alt = Math.Max(alt, m); else best = Math.Max(best, m);
            }
            if (++checkedCells >= sampleCells) break;
        }
        return (best, alt);
    }

    /// <summary>Largest difference between decoded cell heights and the source height field.</summary>
    public static float SourceMismatch(IWorldspaceGetter ws, HeightField f)
    {
        float worst = 0;
        foreach (var ((gx, gy), cell) in CellsByGrid(ws))
        {
            var h = Decode(cell.Landscape!);
            int vx0 = (gx - f.MinCellX) * HeightField.QuadsPerCell, vy0 = (gy - f.MinCellY) * HeightField.QuadsPerCell;
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                    worst = Math.Max(worst, Math.Abs(h[x, y] - f.At(vx0 + x, vy0 + y)));
        }
        return worst;
    }
}
