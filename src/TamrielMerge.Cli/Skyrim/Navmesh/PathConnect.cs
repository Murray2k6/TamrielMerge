using Mutagen.Bethesda.Skyrim;
using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Skyrim.Navmesh;

/// <summary>
/// Whether NPCs can go where the source game's own AI went (user, 2026-10-21: "did we fix npc pathing so they move
/// normally", "it needs to be all 100 percent"): a built plugin's interiors against the game's pathgrids. Each pathgrid
/// point on the navmesh piece under it (its nearest triangle within 64; pieces by the triangles' own edge links), and each
/// pathgrid connection: whole (both ends on one piece), broken (two pieces: NPCs can't follow it), or with an end off the
/// navmesh. `mw-path-connect`, `ob-path-connect`.
/// </summary>
public static class PathConnect
{
    public sealed record Grid(List<Vector3> Points, List<(int A, int B)> Links);

    public static void Report(string what, ISkyrimModGetter m, IReadOnlyDictionary<string, Grid> gridsByEditorId, int worstCount)
    {
        long whole = 0, broken = 0, off = 0, points = 0, pointsOff = 0;
        var perCell = new List<(string Cell, int Links, int Broken, int Off, string Example, int Refs, int Points, int PointsOff)>();
        foreach (var cell in m.Cells.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
        {
            if (cell.EditorID is null || !gridsByEditorId.TryGetValue(cell.EditorID, out var grid)) continue;
            var tris = new List<(Vector3 A, Vector3 B, Vector3 C, int Piece)>();
            int pieceBase = 0;
            foreach (var nav in cell.NavigationMeshes.Select(n => n.Data).OfType<INavigationMeshDataGetter>())
            {
                int n = nav.Triangles.Count;
                var parent = Enumerable.Range(0, n).ToArray();
                int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
                for (int i = 0; i < n; i++)
                {
                    var t = nav.Triangles[i];
                    foreach (int e in new[] { (int)t.EdgeLink_0_1, t.EdgeLink_1_2, t.EdgeLink_2_0 })
                        if (e >= 0 && e < n) parent[Find(i)] = Find(e);
                }
                for (int i = 0; i < n; i++)
                {
                    var t = nav.Triangles[i];
                    Vector3 V(int k) => new(nav.Vertices[k].X, nav.Vertices[k].Y, nav.Vertices[k].Z);
                    tris.Add((V(t.Vertices.X), V(t.Vertices.Y), V(t.Vertices.Z), pieceBase + Find(i)));
                }
                pieceBase += n;
            }
            int PieceOf(Vector3 p)
            {
                float best = 64f * 64f; int piece = -1;
                foreach (var t in tris)
                {
                    float d = Vector3.DistanceSquared(p, NavmeshBuilder.ClosestOnTriangle(p, t.A, t.B, t.C));
                    if (d < best) { best = d; piece = t.Piece; }
                }
                return piece;
            }
            var pieceOfPoint = grid.Points.Select(PieceOf).ToArray();
            points += grid.Points.Count; pointsOff += pieceOfPoint.Count(p => p < 0);
            int cb = 0, co = 0;
            string example = "";
            foreach (var (a, b) in grid.Links)
            {
                if (a >= pieceOfPoint.Length || b >= pieceOfPoint.Length) continue;
                if (pieceOfPoint[a] < 0 || pieceOfPoint[b] < 0)
                {
                    off++; co++;
                    if (example.Length == 0) example = $"end off at {(pieceOfPoint[a] < 0 ? grid.Points[a] : grid.Points[b])}";
                }
                else if (pieceOfPoint[a] != pieceOfPoint[b])
                {
                    broken++; cb++;
                    if (example.Length == 0) example = $"broken {grid.Points[a]} - {grid.Points[b]}";
                }
                else whole++;
            }
            if (cb + co > 0) perCell.Add((cell.EditorID, grid.Links.Count, cb, co, example, cell.Temporary.Count + cell.Persistent.Count, grid.Points.Count, pieceOfPoint.Count(p => p < 0)));
        }
        long all = whole + broken + off;
        Console.WriteLine($"{what}: {points:N0} pathgrid points ({pointsOff:N0} more than 64 off the navmesh); {all:N0} connections: whole {whole:N0} ({whole * 100.0 / Math.Max(1, all):F1}%), broken across two pieces {broken:N0} ({broken * 100.0 / Math.Max(1, all):F1}%), an end off the navmesh {off:N0}");
        foreach (var c in perCell.OrderByDescending(c => c.Broken + c.Off).Take(worstCount))
            Console.WriteLine($"  {c.Cell} ({c.Refs} references, {c.Points} points, {c.PointsOff} off): {c.Links} connections, {c.Broken} broken, {c.Off} with an end off (e.g. {c.Example})");
    }
}
