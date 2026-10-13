using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using nifly;
using TamrielMerge.Skyrim.Meshes;
using TamrielMerge.Util;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Distant terrain (terrain LOD) for a worldspace, in the form Skyrim reads, taken from vanilla files:
/// <list type="bullet">
/// <item>lodsettings\&lt;world&gt;.lod: south-west cell (2 x int16), stride in cells (int32, a power of two covering
/// the world), lowest and highest LOD level (4, 32).</item>
/// <item>meshes\terrain\&lt;world&gt;\&lt;world&gt;.&lt;level&gt;.&lt;x&gt;.&lt;y&gt;.btr per chunk of level x level cells
/// whose south-west cell is (x, y): a BSMultiBoundNode "chunk" holding the BSTriShape "land" (positions and UVs only;
/// local units 0-4096 scaled by the level, heights too; UV v runs north to south), shaded by the LOD landscape shader
/// (type 18) with the chunk's colour and model-space normal textures; and a BSMultiBoundNode "WATER" of unshaded
/// BSSubIndexTriShape quads where water covers the land, which the engine draws as distant water.</item>
/// <item>textures\terrain\&lt;world&gt;\&lt;world&gt;.&lt;level&gt;.&lt;x&gt;.&lt;y&gt;.dds and _n.dds.</item>
/// </list>
/// Colours come from each cell's landscape layers (each landscape texture's average colour, blended by the layers'
/// opacities) times its vertex colours; normals from its heights.
/// </summary>
public sealed class TerrainLod(SkyrimMod mod, string outDir, string skyrimData)
{
    public const int MinLevel = 4, MaxLevel = 32;
    const int Grid = 32;        // quads per chunk side
    const int TexSize = 256;
    const float Cell = 4096f;
    static readonly uint DefaultColor = Pack(110, 100, 80);

    readonly Dictionary<FormKey, uint> ltexColors = new();

    /// <summary>Land textures made elsewhere (the overland's Daggerfall ones, in its first part) and their diffuse.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<FormKey, string> KnownDiffuse = new();
    static readonly object NifLock = new();
    public int Chunks { get; private set; }
    public List<string> Problems { get; } = [];

    sealed class CellData
    {
        public readonly float[] H = new float[33 * 33];
        public readonly uint[] C = new uint[33 * 33];
        public float? Water;
        public float MinH = float.MaxValue;
    }

    /// <summary>Writes a worldspace's distant terrain settings (lodsettings\world.lod): its south-west cell, the stride
    /// (cells, a power of two) its chunks cover and the levels.</summary>
    public void WriteSettings(string world, int swX, int swY, int stride)
    {
        string lodDir = Path.Combine(outDir, "lodsettings");
        Directory.CreateDirectory(lodDir);
        using var w = new BinaryWriter(File.Create(Path.Combine(lodDir, world.ToLowerInvariant() + ".lod")));
        w.Write((short)swX); w.Write((short)swY); w.Write(stride); w.Write(MinLevel); w.Write(MaxLevel);
    }

    /// <summary>
    /// Builds terrain LOD for a worldspace from its exterior cells (grid -> cell). With <paramref name="region"/> (cells,
    /// inclusive, on the 32-cell grid of settings written by <see cref="WriteSettings"/>) only the chunks inside it are
    /// built and the settings are left as they are: a big worldspace is built a region at a time.
    /// </summary>
    public void Build<TCell>(string world, IReadOnlyDictionary<(int X, int Y), TCell> cells, Func<TCell, float?> waterOf, float defaultHeight,
        (int X0, int Y0, int X1, int Y1)? region = null) where TCell : ICellGetter
    {
        var data = new Dictionary<(int X, int Y), CellData>();
        foreach (var (key, cell) in cells)
            if (cell.Landscape is { } land && Decode(land, waterOf(cell)) is { } d) data[key] = d;
        if (data.Count == 0) return;
        int minX = data.Keys.Min(k => k.X), maxX = data.Keys.Max(k => k.X), minY = data.Keys.Min(k => k.Y), maxY = data.Keys.Max(k => k.Y);
        int swX = FloorTo(minX, MaxLevel), swY = FloorTo(minY, MaxLevel);
        int stride = MaxLevel;
        while (swX + stride <= maxX || swY + stride <= maxY) stride *= 2;
        int ex0 = swX, ey0 = swY, ex1 = swX + stride - 1, ey1 = swY + stride - 1;
        if (region is { } r) (ex0, ey0, ex1, ey1) = (FloorTo(r.X0, MaxLevel), FloorTo(r.Y0, MaxLevel), r.X1, r.Y1);
        else WriteSettings(world, swX, swY, stride);
        // The chunks' files are made on a solid-state drive when the output is on a hard disk (Util.Drives; 2026-10-12: a
        // dozen threads writing thousands of small files at once had it seeking, 15 minutes a region), then moved over one
        // at a time.
        writeRoot = StagingRoot(outDir);
        Directory.CreateDirectory(Path.Combine(outDir, "meshes", "terrain", world));
        Directory.CreateDirectory(Path.Combine(outDir, "textures", "terrain", world));
        Directory.CreateDirectory(Path.Combine(writeRoot, "meshes", "terrain", world));
        Directory.CreateDirectory(Path.Combine(writeRoot, "textures", "terrain", world));

        var keysByQuad = data.Keys.GroupBy(k => (FloorTo(k.X, MinLevel), FloorTo(k.Y, MinLevel))).Select(g => g.Key).ToHashSet();
        bool AnyIn(int x, int y, int level)
        {
            for (int qx = x; qx < x + level; qx += MinLevel)
                for (int qy = y; qy < y + level; qy += MinLevel)
                    if (keysByQuad.Contains((qx, qy))) return true;
            return false;
        }
        var jobs = new List<(int L, int X, int Y)>();
        for (int level = MinLevel; level <= MaxLevel; level *= 2)
            for (int x = ex0; x <= ex1; x += level)
                for (int y = ey0; y <= ey1; y += level)
                    if (AnyIn(x, y, level)) jobs.Add((level, x, y));
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(jobs, job =>
        {
            try { WriteChunk(world, job.L, job.X, job.Y, data, defaultHeight); }
            catch (Exception e) { errors.Add($"terrain LOD {world}.{job.L}.{job.X}.{job.Y}: {e.Message}"); }
        });
        Problems.AddRange(errors);
        Chunks += jobs.Count;
        if (writeRoot != outDir)
        {
            foreach (var sub in new[] { Path.Combine("meshes", "terrain", world), Path.Combine("textures", "terrain", world) })
                foreach (var file in Directory.GetFiles(Path.Combine(writeRoot, sub)))
                    File.Move(file, Path.Combine(outDir, sub, Path.GetFileName(file)), overwrite: true);
            try { Directory.Delete(writeRoot, true); } catch (IOException) { }
            writeRoot = outDir;
        }
    }

    /// <summary>Where chunks are made: a folder on the temporary drive when the output is on another, else the output.</summary>
    string writeRoot = outDir;

    static string StagingRoot(string outDir) =>
        Util.Drives.FastStaging(outDir, 4L << 30, Path.Combine("TamrielMergeLod", Guid.NewGuid().ToString("N"))) ?? outDir;

    static int FloorTo(int v, int m) => (int)Math.Floor((double)v / m) * m;

    static uint Pack(int r, int g, int b) => 0xFF000000u | (uint)Math.Clamp(r, 0, 255) << 16 | (uint)Math.Clamp(g, 0, 255) << 8 | (uint)Math.Clamp(b, 0, 255);

    // ---- Per-cell heights and colours ----

    CellData? Decode(ILandscapeGetter land, float? water)
    {
        if (land.VertexHeightMap is not { } vhgt) return null;
        var d = new CellData { Water = water };
        float rowStart = vhgt.Offset;
        for (int y = 0; y < 33; y++)
        {
            rowStart += vhgt.HeightMap[0, y];
            float v = rowStart;
            d.H[y * 33] = v * 8;
            for (int x = 1; x < 33; x++) { v += vhgt.HeightMap[x, y]; d.H[y * 33 + x] = v * 8; }
        }
        foreach (var h in d.H) d.MinH = MathF.Min(d.MinH, h);

        var r = new float[33 * 33]; var g = new float[33 * 33]; var b = new float[33 * 33];
        for (int i = 0; i < r.Length; i++) { r[i] = DefaultColor >> 16 & 255; g[i] = DefaultColor >> 8 & 255; b[i] = DefaultColor & 255; }
        var layers = land.Layers ?? [];
        for (int q = 0; q < 4; q++)
        {
            int qx = (q & 1) * 16, qy = (q >> 1) * 16;
            var inQuad = layers.Where(l => (int)l.Header!.Quadrant == q).ToList();
            if (inQuad.FirstOrDefault(l => l is not IAlphaLayerGetter) is { } baseLayer)
            {
                uint c = LtexColor(baseLayer.Header!.Texture.FormKey);
                for (int ly = 0; ly < 17; ly++)
                    for (int lx = 0; lx < 17; lx++)
                    {
                        int i = (qy + ly) * 33 + qx + lx;
                        r[i] = c >> 16 & 255; g[i] = c >> 8 & 255; b[i] = c & 255;
                    }
            }
            foreach (var layer in inQuad.OfType<IAlphaLayerGetter>().OrderBy(l => l.Header!.LayerNumber))
            {
                uint c = LtexColor(layer.Header!.Texture.FormKey);
                foreach (var a in layer.AlphaLayerData ?? [])
                {
                    if (a.Position >= 17 * 17) continue;
                    int i = (qy + a.Position / 17) * 33 + qx + a.Position % 17;
                    float t = Math.Clamp(a.Opacity, 0, 1);
                    r[i] += ((c >> 16 & 255) - r[i]) * t; g[i] += ((c >> 8 & 255) - g[i]) * t; b[i] += ((c & 255) - b[i]) * t;
                }
            }
        }
        var vc = land.VertexColors;
        for (int y = 0; y < 33; y++)
            for (int x = 0; x < 33; x++)
            {
                int i = y * 33 + x;
                float mr = 1, mg = 1, mb = 1;
                if (vc is not null) { var c = vc[x, y]; mr = c.X / 255f; mg = c.Y / 255f; mb = c.Z / 255f; }
                d.C[i] = Pack((int)(r[i] * mr), (int)(g[i] * mg), (int)(b[i] * mb));
            }
        return d;
    }

    /// <summary>A landscape texture's average colour: its texture set's diffuse, from our output or Skyrim's loose data.</summary>
    uint LtexColor(FormKey ltex)
    {
        if (ltexColors.TryGetValue(ltex, out var hit)) return hit;
        uint color = DefaultColor;
        string? diffuse = KnownDiffuse.GetValueOrDefault(ltex);
        if (diffuse is null && mod.LandscapeTextures.TryGetValue(ltex, out var lt) && lt.TextureSet.FormKey is var ts && mod.TextureSets.TryGetValue(ts, out var set)
            && set.Diffuse?.DataRelativePath.Path is { Length: > 0 } d)
            diffuse = d;
        if (diffuse is not null)
        {
            string rel = diffuse.StartsWith("textures", StringComparison.OrdinalIgnoreCase) ? diffuse : Path.Combine("textures", diffuse);
            foreach (var root in new[] { outDir, skyrimData })
            {
                string path = Path.Combine(root, rel);
                if (!File.Exists(path)) continue;
                if (DdsImage.Average(File.ReadAllBytes(path)) is { } avg) color = Pack(avg.R, avg.G, avg.B);
                break;
            }
        }
        return ltexColors[ltex] = color;
    }

    // ---- Sampling across cells ----

    static (CellData? D, float Fx, float Fy) Locate(Dictionary<(int X, int Y), CellData> data, double wx, double wy)
    {
        int cx = (int)Math.Floor(wx / Cell), cy = (int)Math.Floor(wy / Cell);
        return (data.GetValueOrDefault((cx, cy)), (float)((wx - cx * Cell) / 128), (float)((wy - cy * Cell) / 128));
    }

    static float Height(Dictionary<(int X, int Y), CellData> data, double wx, double wy, float fallback)
    {
        var (d, fx, fy) = Locate(data, wx, wy);
        if (d is null) return fallback;
        int ix = Math.Clamp((int)fx, 0, 31), iy = Math.Clamp((int)fy, 0, 31);
        float tx = Math.Clamp(fx - ix, 0, 1), ty = Math.Clamp(fy - iy, 0, 1);
        float h00 = d.H[iy * 33 + ix], h10 = d.H[iy * 33 + ix + 1], h01 = d.H[(iy + 1) * 33 + ix], h11 = d.H[(iy + 1) * 33 + ix + 1];
        return (h00 * (1 - tx) + h10 * tx) * (1 - ty) + (h01 * (1 - tx) + h11 * tx) * ty;
    }

    static uint Color(Dictionary<(int X, int Y), CellData> data, double wx, double wy)
    {
        var (d, fx, fy) = Locate(data, wx, wy);
        if (d is null) return Pack(60, 70, 80);
        int ix = Math.Clamp((int)fx, 0, 31), iy = Math.Clamp((int)fy, 0, 31);
        float tx = Math.Clamp(fx - ix, 0, 1), ty = Math.Clamp(fy - iy, 0, 1);
        uint c00 = d.C[iy * 33 + ix], c10 = d.C[iy * 33 + ix + 1], c01 = d.C[(iy + 1) * 33 + ix], c11 = d.C[(iy + 1) * 33 + ix + 1];
        int Ch(int shift) => (int)(((c00 >> shift & 255) * (1 - tx) + (c10 >> shift & 255) * tx) * (1 - ty) + ((c01 >> shift & 255) * (1 - tx) + (c11 >> shift & 255) * tx) * ty);
        return Pack(Ch(16), Ch(8), Ch(0));
    }

    // ---- One chunk ----

    void WriteChunk(string world, int level, int x0, int y0, Dictionary<(int X, int Y), CellData> data, float defaultHeight)
    {
        string name = $"{world}.{level}.{x0}.{y0}";
        double ox = x0 * Cell, oy = y0 * Cell, size = level * Cell, step = size / Grid;

        // Land: a 33 x 33 grid in local units (4096 across, scaled by the level).
        var heights = new float[(Grid + 1) * (Grid + 1)];
        for (int j = 0; j <= Grid; j++)
            for (int i = 0; i <= Grid; i++)
                heights[j * (Grid + 1) + i] = Height(data, ox + i * step, oy + j * step, defaultHeight);
        float minH = heights.Min(), maxH = heights.Max();

        // nifly isn't safe to drive from several threads at once (its string and block bookkeeping): one chunk's
        // mesh at a time; the textures below are built in parallel.
        lock (NifLock)
        {
            using var nif = new NifFile();
            nif.Create(NiVersion.getSSE());
            var hdr = nif.GetHeader();
            uint rootId = hdr.GetBlockID(nif.GetRootNode());
            hdr.ReplaceBlock(rootId, MultiBoundNode(hdr, "chunk", (float)size, minH, maxH));
            var root = nif.GetRootNode();

            using (var verts = new vectorVector3())
            using (var uvs = new vectorVector2())
            using (var tris = new vectorTriangle())
            {
                for (int j = 0; j <= Grid; j++)
                    for (int i = 0; i <= Grid; i++)
                    {
                        verts.Add(new Vector3(i * 4096f / Grid, j * 4096f / Grid, heights[j * (Grid + 1) + i] / level));
                        uvs.Add(new Vector2(i / (float)Grid, 1 - j / (float)Grid));
                    }
                for (int j = 0; j < Grid; j++)
                    for (int i = 0; i < Grid; i++)
                    {
                        ushort a = (ushort)(j * (Grid + 1) + i), b = (ushort)(a + 1), c = (ushort)(a + Grid + 1), d = (ushort)(c + 1);
                        tris.Add(new Triangle(a, b, d));
                        tris.Add(new Triangle(a, d, c));
                    }
                var land = nif.CreateShapeFromData("land", verts, tris, uvs);
                nif.SetTextureSlot(land, $"textures\\terrain\\{world}\\{name}.dds", 0);
                nif.SetTextureSlot(land, $"textures\\terrain\\{world}\\{name}_n.dds", 1);
                uint landId = hdr.GetBlockID(land);
                BlockMeshBuilder.EditBlock<BSTriShape>(nif, landId, t =>
                {
                    var tr = t.transform; tr.scale = level; t.transform = tr;
                    t.flags = 14;
                });
                BlockMeshBuilder.EditBlock<BSLightingShaderProperty>(nif, hdr.GetBlockID(nif.GetShader(land)), s =>
                {
                    s.SetShaderType(18);   // LOD landscape
                    s.shaderFlags1 = 0x80401000;
                    s.shaderFlags2 = 0x00000003;
                    s.glossiness = 1;
                    s.specularStrength = 1;
                });
            }

            // Distant water: a quad per run of neighbouring cells along a row with the same water height over land
            // below it.
            var runs = new List<(int X0, int X1, int Y, float W)>();
            for (int cy = y0; cy < y0 + level; cy++)
            {
                (int X0, int X1, float W)? run = null;
                for (int cx = x0; cx <= x0 + level; cx++)
                {
                    float? w = cx < x0 + level && data.TryGetValue((cx, cy), out var cd) && cd.Water is { } wh && cd.MinH < wh ? wh : null;
                    if (run is { } open && (w is null || w != open.W)) { runs.Add((open.X0, open.X1, cy, open.W)); run = null; }
                    if (w is { } nw) run = run is { } cur ? (cur.X0, cx, cur.W) : (cx, cx, nw);
                }
            }
            if (runs.Count > 0)
            {
                float wMin = runs.Min(r => r.W), wMax = runs.Max(r => r.W);
                var waterNode = nif.AddNode("WATER", new MatTransform(), root);
                uint waterId = hdr.GetBlockID(waterNode);
                hdr.ReplaceBlock(waterId, MultiBoundNode(hdr, "WATER", (float)size, wMin, wMax));
                var parent = (NiNode)hdr.GetBlockById(waterId);
                foreach (var (rx0, rx1, ry, w) in runs)
                {
                    float lx0 = (rx0 - x0) * 4096f / level, lx1 = (rx1 + 1 - x0) * 4096f / level, ly0 = (ry - y0) * 4096f / level, ly1 = (ry + 1 - y0) * 4096f / level, z = w / level;
                    using var qv = new vectorVector3 { new Vector3(lx0, ly0, z), new Vector3(lx1, ly0, z), new Vector3(lx0, ly1, z), new Vector3(lx1, ly1, z) };
                    using var qt = new vectorTriangle { new Triangle(0, 1, 3), new Triangle(0, 3, 2) };
                    using var quv = new vectorVector2 { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
                    // Vanilla uses BSSubIndexTriShape here, but nifly drops a hand-made one's geometry on save; a plain
                    // BSTriShape without a shader carries the same quad.
                    var quad = nif.CreateShapeFromData("", qv, qt, quv);
                    nif.DeleteShader(quad);
                    BlockMeshBuilder.EditBlock<BSTriShape>(nif, hdr.GetBlockID(quad), t =>
                    {
                        var tr = t.transform; tr.scale = level; t.transform = tr;
                        t.flags = 14;
                    });
                    nif.SetParentNode(hdr.GetBlockById(hdr.GetBlockID(quad)), parent);
                }
            }
            string meshPath = Path.Combine(writeRoot, "meshes", "terrain", world, name + ".btr");
            if (nif.Save(meshPath) != 0) throw new IOException($"nifly failed to save {meshPath}");
        }

        // Textures: colour, and model-space normals from the heights (X east, Y north, Z up as RGB).
        var color = new uint[TexSize * TexSize];
        var normal = new uint[TexSize * TexSize];
        double px = size / TexSize;
        for (int row = 0; row < TexSize; row++)
            for (int col = 0; col < TexSize; col++)
            {
                double wx = ox + (col + 0.5) * px, wy = oy + size - (row + 0.5) * px;
                color[row * TexSize + col] = Color(data, wx, wy);
                float dx = (Height(data, wx + px, wy, defaultHeight) - Height(data, wx - px, wy, defaultHeight)) / (float)(2 * px);
                float dy = (Height(data, wx, wy + px, defaultHeight) - Height(data, wx, wy - px, defaultHeight)) / (float)(2 * px);
                var n = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(-dx, -dy, 1));
                normal[row * TexSize + col] = Pack((int)((n.X * 0.5f + 0.5f) * 255), (int)((n.Y * 0.5f + 0.5f) * 255), (int)((n.Z * 0.5f + 0.5f) * 255));
            }
        string texDir = Path.Combine(writeRoot, "textures", "terrain", world);
        Morrowind.MwTextures.WriteBcDds(Path.Combine(texDir, name + ".dds"), color, TexSize, TexSize, false);
        Morrowind.MwTextures.WriteBcDds(Path.Combine(texDir, name + "_n.dds"), normal, TexSize, TexSize, false);
    }

    /// <summary>A BSMultiBoundNode with its axis-aligned bound (local to the chunk, in world units), as vanilla chunks have.</summary>
    static BSMultiBoundNode MultiBoundNode(NiHeader hdr, string name, float size, float zMin, float zMax)
    {
        var aabb = BlockMeshBuilder.Owned(new BSMultiBoundAABB());
        aabb.center = new Vector3(size / 2, size / 2, (zMin + zMax) / 2);
        aabb.halfExtent = new Vector3(size / 2, size / 2, MathF.Max((zMax - zMin) / 2, 1));
        uint aabbId = hdr.AddBlock(aabb);
        var bound = BlockMeshBuilder.Owned(new BSMultiBound());
        var dataRef = bound.dataRef; dataRef.index = aabbId; bound.dataRef = dataRef;
        uint boundId = hdr.AddBlock(bound);
        var node = BlockMeshBuilder.Owned(new BSMultiBoundNode());
        node.name = new NiStringRef(name);
        node.flags = 14;
        node.cullingMode = BSCPCullingType.BSCP_CULL_ALLPASS;
        var mbRef = node.multiBoundRef; mbRef.index = boundId; node.multiBoundRef = mbRef;
        return node;
    }
}
