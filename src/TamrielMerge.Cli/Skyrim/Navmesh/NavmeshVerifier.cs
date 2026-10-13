using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Util;

namespace TamrielMerge.Skyrim.Navmesh;

/// <summary>Reads NAVM records back and checks the invariants vanilla navmeshes satisfy, plus connectivity.</summary>
public static class NavmeshVerifier
{
    public static bool Verify(INavigationMeshGetter navm, string? pngPath, P3Float? start)
    {
        var d = navm.Data!;
        var v = d.Vertices;
        var t = d.Triangles;
        int cw = 0, badIndex = 0, asym = 0, unflagged = 0;
        for (int i = 0; i < t.Count; i++)
        {
            var tri = t[i];
            int[] idx = [tri.Vertices.X, tri.Vertices.Y, tri.Vertices.Z];
            if (idx.Any(k => k < 0 || k >= v.Count)) { badIndex++; continue; }
            var (a, b, c) = (v[idx[0]], v[idx[1]], v[idx[2]]);
            if ((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X) < 0) cw++;
            if (!tri.Flags.HasFlag(NavmeshTriangle.Flag.Found)) unflagged++;
            foreach (var n in new[] { tri.EdgeLink_0_1, tri.EdgeLink_1_2, tri.EdgeLink_2_0 }.Where(n => n >= 0))
            {
                var o = t[n];
                if (o.EdgeLink_0_1 != i && o.EdgeLink_1_2 != i && o.EdgeLink_2_0 != i) asym++;
            }
        }

        // Grid: divisor^2 cells of Int32 count + Int16 indices, nothing left over, every triangle listed.
        var grid = d.NavmeshGrid.ToArray();
        int pos = 0, cells = 0; var listed = new HashSet<int>();
        while (pos + 4 <= grid.Length)
        {
            int count = BitConverter.ToInt32(grid, pos); pos += 4;
            for (int k = 0; k < count; k++) listed.Add(BitConverter.ToInt16(grid, pos + k * 2));
            pos += count * 2; cells++;
        }
        bool gridOk = pos == grid.Length && cells == d.NavmeshGridDivisor * d.NavmeshGridDivisor && listed.Count == t.Count;

        // Connected components through edge links.
        var comp = new int[t.Count]; Array.Fill(comp, -1);
        var sizes = new List<int>();
        for (int i = 0; i < t.Count; i++)
        {
            if (comp[i] >= 0) continue;
            int id = sizes.Count, size = 0;
            var stack = new Stack<int>([i]); comp[i] = id;
            while (stack.Count > 0)
            {
                int k = stack.Pop(); size++;
                foreach (var n in new[] { t[k].EdgeLink_0_1, t[k].EdgeLink_1_2, t[k].EdgeLink_2_0 })
                    if (n >= 0 && comp[n] < 0) { comp[n] = id; stack.Push(n); }
            }
            sizes.Add(size);
        }
        int startComp = -1;
        if (start is { } s)
            startComp = comp[Enumerable.Range(0, t.Count).OrderBy(i => Dist2(Centroid(v, t[i]), s)).First()];
        var doorComps = d.DoorTriangles.Select(dt => comp[dt.TriangleBeforeDoor]).ToList();

        Console.WriteLine($"{navm.FormKey}: {t.Count} triangles, {v.Count} vertices, door triangles {d.DoorTriangles.Count} (flagged {t.Count(x => x.Flags.HasFlag(NavmeshTriangle.Flag.Door))})");
        Console.WriteLine($"  clockwise {cw}, bad indices {badIndex}, asymmetric links {asym}, missing Found {unflagged}, grid {(gridOk ? "ok" : "BAD")} ({cells} cells, {listed.Count} triangles listed)");
        Console.WriteLine($"  components {sizes.Count}: largest {string.Join(", ", sizes.OrderByDescending(x => x).Take(6))}; start in component of size {(startComp >= 0 ? sizes[startComp] : 0)}, door in {string.Join(",", doorComps.Select(c => sizes[c]))}; start-door connected {doorComps.Contains(startComp)}");

        if (pngPath is not null) Render(v, t, comp, startComp, pngPath);
        return cw == 0 && badIndex == 0 && asym == 0 && unflagged == 0 && gridOk;
    }

    static P3Float Centroid(IReadOnlyList<P3Float> v, INavmeshTriangleGetter t) =>
        new((v[t.Vertices.X].X + v[t.Vertices.Y].X + v[t.Vertices.Z].X) / 3, (v[t.Vertices.X].Y + v[t.Vertices.Y].Y + v[t.Vertices.Z].Y) / 3, (v[t.Vertices.X].Z + v[t.Vertices.Y].Z + v[t.Vertices.Z].Z) / 3);
    static float Dist2(P3Float a, P3Float b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z);

    /// <summary>Top-down render: the start's component green, others grey, north up.</summary>
    static void Render(IReadOnlyList<P3Float> v, IReadOnlyList<INavmeshTriangleGetter> t, int[] comp, int startComp, string path)
    {
        const float upp = 16;
        float minX = v.Min(p => p.X), minY = v.Min(p => p.Y), maxX = v.Max(p => p.X), maxY = v.Max(p => p.Y);
        int w = (int)((maxX - minX) / upp) + 1, h = (int)((maxY - minY) / upp) + 1;
        var rgb = new byte[w * h * 3];
        for (int i = 0; i < t.Count; i++)
        {
            var (a, b, c) = (v[t[i].Vertices.X], v[t[i].Vertices.Y], v[t[i].Vertices.Z]);
            byte shade = (byte)(80 + (i * 37 % 60));
            (byte r, byte g, byte bl) col = comp[i] == startComp ? ((byte)40, (byte)(140 + shade / 2), (byte)60) : (shade, shade, shade);
            float ax = (a.X - minX) / upp, ay = (a.Y - minY) / upp, bx = (b.X - minX) / upp, by = (b.Y - minY) / upp, cx = (c.X - minX) / upp, cy = (c.Y - minY) / upp;
            float area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
            if (MathF.Abs(area) < 1e-6f) continue;
            int x0 = Math.Max(0, (int)MathF.Min(ax, MathF.Min(bx, cx))), x1 = Math.Min(w - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
            int y0 = Math.Max(0, (int)MathF.Min(ay, MathF.Min(by, cy))), y1 = Math.Min(h - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float w0 = ((bx - px) * (cy - py) - (cx - px) * (by - py)) / area, w1 = ((cx - px) * (ay - py) - (ax - px) * (cy - py)) / area;
                    if (w0 < 0 || w1 < 0 || w0 + w1 > 1) continue;
                    int o = ((h - 1 - y) * w + x) * 3;
                    (rgb[o], rgb[o + 1], rgb[o + 2]) = col;
                }
        }
        Png.WriteRgb(path, w, h, rgb);
        Console.WriteLine($"  render: {Path.GetFullPath(path)}");
    }
}
