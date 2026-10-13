using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using nifly;
using Noggog;
using TamrielMerge.Daggerfall;
using TamrielMerge.Skyrim.Meshes;

namespace TamrielMerge.Skyrim.Terrain;

/// <summary>
/// Daggerfall's own grass on its overland (user, 2026-10-12: "the grass needs to be daggerfalls and the grass placement
/// on where its suppose to be"; "they need upscaled"): each climate's grass tufts (its nature archive's records 7 and 8)
/// as Skyrim grass (GRAS: three crossed quads with the upscaled sprite, set up as Skyrim's own grass meshes), on the
/// Daggerfall land textures (<see cref="TerrainBuilder.Ground"/>) as Daggerfall Unity lays its nature flats
/// (DaggerfallTerrain.LayoutNature): thick on grass, thin on dirt, almost none on stone, a quarter as thick in the
/// desert; the beach and sea floor are dirt and the steep slopes stone (TerrainBuilder.Weights).
/// </summary>
public sealed class DfGrass(SkyrimMod mod, ISkyrimModGetter? skyrim, string arena2, string outDir)
{
    /// <summary>The grass of a land texture (TerrainBuilder.Ground): its climate's nature archive, the grass records,
    /// how thick against Skyrim's field grass, and its name.</summary>
    static (int Archive, int[] Records, float Density, string Name) Spec(int tex) =>
        TerrainBuilder.Ground[tex] is var g ? (g.Nature, [7, 8], g.Grass, g.Name) : default;

    /// <summary>Every grass sprite, for the upscaler (df-upscale-export).</summary>
    public static IEnumerable<(int Archive, int Record)> Sprites =>
        TerrainBuilder.Ground.Select(g => g.Nature).Distinct().SelectMany(a => new[] { (a, 7), (a, 8) });

    /// <summary>LFieldGrass01's grass: the template for density, slopes, wave and colour ranges.</summary>
    static readonly FormKey FieldGrassLtex = new(ModKey.FromFileName("Skyrim.esm"), 0x13428);

    readonly DfTextureExport export = new(arena2, outDir);
    readonly Dictionary<(int, int, float), Grass> made = new();
    IGrassGetter? template;

    /// <summary>The grass for a land texture (Tex index): made once, in <c>mod</c>.</summary>
    public List<Grass> For(int tex)
    {
        var spec = Spec(tex);
        var sk = skyrim ?? throw new InvalidOperationException("DfGrass.For needs Skyrim.esm");
        template ??= sk.LandscapeTextures.First(t => t.FormKey == FieldGrassLtex).Grasses
            .Select(g => sk.Grasses.FirstOrDefault(x => x.FormKey == g.FormKey)).First(g => g is not null)!;
        var list = new List<Grass>();
        // The records share the ground: together as thick as the template where Daggerfall's grass is thickest.
        byte density = (byte)Math.Clamp(MathF.Round(template.Density * spec.Density / spec.Records.Length), 0, 255);
        if (density == 0) return list;   // too thin to show (Daggerfall's stone, the desert's dirt)
        foreach (int record in spec.Records)
        {
            if (made.TryGetValue((spec.Archive, record, spec.Density), out var hit)) { list.Add(hit); continue; }
            var (model, min, max) = Mesh(spec.Archive, record);
            var g = mod.Grasses.DuplicateInAsNewRecord(template);
            g.EditorID = $"DFGrass{spec.Name}{record}";
            g.Model = new Model { File = model };
            g.ObjectBounds = new ObjectBounds { First = new P3Int16((short)min.X, (short)min.Y, (short)min.Z), Second = new P3Int16((short)max.X, (short)max.Y, (short)max.Z) };
            g.Density = density;
            g.MaxSlope = Math.Min(template.MaxSlope, MaxSlopeDegrees);
            list.Add(made[(spec.Archive, record, spec.Density)] = g);
        }
        return list;
    }

    /// <summary>Daggerfall Unity rejects nature flats on ground steeper than about this.</summary>
    const byte MaxSlopeDegrees = 45;

    /// <summary>The grass mesh: three quads crossed at 60 degrees, the sprite's own size in the world (as its other
    /// sprites: BlockMeshBuilder.Scale), sunk a little; set up as Skyrim's grass (fieldgrass01.nif): alpha-tested,
    /// double-sided, vertex colours, normals up so it is lit as the ground it stands on.</summary>
    (string Model, System.Numerics.Vector3 Min, System.Numerics.Vector3 Max) Mesh(int archive, int record)
    {
        if (meshes.TryGetValue((archive, record), out var hit)) return hit;
        string name = $"DFGrass{archive}_{record}";
        var tex = DfTexture.Load(arena2, archive)!;
        var size = tex.Image(record).WorldSize;
        float w = size.Width * BlockMeshBuilder.Scale, h = size.Height * BlockMeshBuilder.Scale, sink = 2;
        string texture = export.EnsureSprite(archive, record).Path;
        string rel = $@"TamrielMerge\DF\Grass\{archive}_{record}.nif";
        lock (NiflyLock)
        {
            using var nif = new NifFile();
            nif.Create(NiVersion.getSSE());
            var hdr = nif.GetHeader();
            var fade = BlockMeshBuilder.Owned(new BSFadeNode());
            fade.name = new NiStringRef(name);
            hdr.ReplaceBlock(hdr.GetBlockID(nif.GetRootNode()), fade);
            using var verts = new vectorVector3(); using var uvs = new vectorVector2(); using var normals = new vectorVector3();
            using var tris = new vectorTriangle(); using var colours = new vectorColor4();
            for (int q = 0; q < 3; q++)
            {
                float a = q * MathF.PI / 3, c = MathF.Cos(a) * w / 2, s = MathF.Sin(a) * w / 2;
                int b = q * 4;
                verts.Add(new nifly.Vector3(-c, -s, -sink)); uvs.Add(new nifly.Vector2(0, 1));
                verts.Add(new nifly.Vector3(c, s, -sink)); uvs.Add(new nifly.Vector2(1, 1));
                verts.Add(new nifly.Vector3(c, s, h - sink)); uvs.Add(new nifly.Vector2(1, 0));
                verts.Add(new nifly.Vector3(-c, -s, h - sink)); uvs.Add(new nifly.Vector2(0, 0));
                for (int k = 0; k < 4; k++) { normals.Add(new nifly.Vector3(0, 0, 1)); colours.Add(new Color4(1, 1, 1, 1)); }
                tris.Add(new Triangle((ushort)b, (ushort)(b + 1), (ushort)(b + 2)));
                tris.Add(new Triangle((ushort)b, (ushort)(b + 2), (ushort)(b + 3)));
            }
            var shape = nif.CreateShapeFromData(name, verts, tris, uvs, normals);
            nif.SetColorsForShape(shape, colours);
            nif.SetTextureSlot(shape, texture, 0);
            nif.SetTextureSlot(shape, DfTextureExport.FlatNormal, 1);
            nif.CalcTangentsForShape(shape);
            nif.AssignAlphaProperty(shape, BlockMeshBuilder.Owned(new NiAlphaProperty { flags = 0x12EC, threshold = 128 }));
            BlockMeshBuilder.EditBlock<BSLightingShaderProperty>(nif, hdr.GetBlockID(nif.GetShader(shape)), s =>
            {
                s.shaderFlags1 = 0x82400308u;   // as fieldgrass01.nif: ZBuffer test, vertex alpha, cast + receive shadows
                s.shaderFlags2 = 0x00008031u;   // ZBuffer write, double-sided, vertex colours
                s.glossiness = 10;
                s.specularStrength = 0;
            });
            BlockMeshBuilder.EditBlock<BSTriShape>(nif, hdr.GetBlockID(shape), t => t.flags = 524302);   // as fieldgrass01.nif (last: replaces the block)
            string full = Path.Combine(outDir, "meshes", rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (nif.Save(full) != 0) throw new IOException($"nifly failed to save {full}");
        }
        float r = w / 2;
        return meshes[(archive, record)] = (rel, new(-r, -r, -sink), new(r, r, h - sink));
    }

    readonly Dictionary<(int, int), (string, System.Numerics.Vector3, System.Numerics.Vector3)> meshes = new();

    /// <summary>
    /// The grass sprites Daggerfall's town blocks place by hand (any climate's nature archive, 500-511, records 7 and 8)
    /// as statics of the same grass meshes, into <paramref name="clutter"/> (as models made from sprites), so they are
    /// Daggerfall's grass where Daggerfall put it instead of Skyrim's spiky grass.
    /// </summary>
    public void AddBlockGrass(ClutterPlacer clutter)
    {
        for (int archive = 500; archive <= 511; archive++)
        {
            if (DfTexture.Load(arena2, archive) is not { } tex) continue;
            foreach (int record in new[] { 7, 8 })
            {
                if (record >= tex.Count) continue;
                var (model, min, max) = Mesh(archive, record);
                var stat = new Static(mod)
                {
                    EditorID = $"DFGrassStatic{archive}_{record}",
                    Model = new Model { File = model },
                    ObjectBounds = new ObjectBounds { First = new P3Int16((short)min.X, (short)min.Y, (short)min.Z), Second = new P3Int16((short)max.X, (short)max.Y, (short)max.Z) },
                };
                mod.Statics.Add(stat);
                clutter.AddGenerated((archive, record), new Meshes.SpriteModelImporter.Made(stat.FormKey, min, max, SpriteModels.Kind.Plant));
            }
        }
    }

    static readonly object NiflyLock = new();
}
