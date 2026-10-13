using System.Numerics;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using nifly;
using TamrielMerge.Skyrim.Meshes;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Skyrim.Render;

/// <summary>
/// The main menu, Tamriel's (tools/menu, docs/menu.md): Skyrim's own 3D emblem in the red stone of the Tamriel logo,
/// in front of an intro video looping over the converted games, as Oblivion's menu flies over Cyrodiil.
///
/// The menu's 3D scene is the vanilla logo mesh (meshes\interface\logo\logo.nif, and logo01ae*.nif on Anniversary
/// Edition), so each is rebuilt with the video behind the emblem. The video is a flipbook: frames packed COLS x ROWS to
/// a DXT1 sheet (tools/menu/intro.py), one shape per sheet shown in turn (NiVisController), its frame chosen by
/// stepping the effect shader's UV offset (BSEffectShaderPropertyFloatController, constant keys). Each shape is a flat,
/// unlit screen: the game places the menu camera in code, so it was photographed (tools/menu/calibration.py, solved
/// into tools/menu/menu_camera.json) and the screen stands on the plane measured behind the emblem, its corners where
/// the camera's view corners meet that plane, a little oversized, with a dark apron past them for wider screens.
/// </summary>
public static class MenuBuilder
{
    public const string TextureDir = @"textures\tamrielmerge\menu";
    /// <summary>
    /// The emblem's size in the menu against the vanilla logo's, about its own middle (user: at full size it would hide
    /// the video). It stands left of the screen's middle, the menu's items on the right.
    /// </summary>
    public const float EmblemScale = 0.6f;
    /// <summary>The video screen past the camera's view on each side (the solved camera is good to about a pixel).</summary>
    const float Overscan = 1.03f;
    const string FogMesh = @"meshes\interface\intmenufogparticles.nif";
    static readonly string[] LogoMeshes = [@"meshes\interface\logo\logo.nif", @"meshes\interface\logo\logo01ae.nif", @"meshes\interface\logo\logo01ae_animated.nif"];
    static readonly string[] LogoTextures = ["logo02_d", "logo01_d", "logo01ae_d"];

    sealed record Flyby(int frames, float fps, int cols, int rows, int sheet, int sheetWidth, int sheetHeight, int frameWidth, int frameHeight, int sheets)
    {
        public int SheetW => sheetWidth > 0 ? sheetWidth : sheet;     // older flyby.json: square sheets of `sheet`
        public int SheetH => sheetHeight > 0 ? sheetHeight : sheet;
    }
    sealed record Banner(float u0, float v0, float u1, float v1, int width, int height);
    /// <summary>The banner's layer in front of the video screen, in the logo mesh's units.</summary>
    const float BannerAhead = 20f;
    sealed record MenuCamera(float[] eye, float[] forward, float[] up, float hfov, float vfov, float wall_y);

    /// <summary>
    /// Writes the menu into <paramref name="outDir"/> from <paramref name="artDir"/> (flyby.json and its sheets, and the
    /// red logo textures from tools/menu/red_logo.py), the screen placed for <paramref name="cameraJson"/> (the menu
    /// camera solved by tools/menu/calibration.py).
    /// </summary>
    public static void Build(string skyrimData, string artDir, string outDir, string cameraJson)
    {
        var flyby = JsonSerializer.Deserialize<Flyby>(File.ReadAllText(Path.Combine(artDir, "flyby.json")))!;
        var cam = JsonSerializer.Deserialize<MenuCamera>(File.ReadAllText(cameraJson))!;
        string texOut = Path.Combine(outDir, TextureDir);
        Directory.CreateDirectory(texOut);
        foreach (var old in Directory.GetFiles(texOut, "flyby_*.dds")) File.Delete(old);
        for (int s = 0; s < flyby.sheets; s++) File.Copy(Path.Combine(artDir, $"flyby_{s:00}.dds"), Path.Combine(texOut, $"flyby_{s:00}.dds"), true);
        // The emblem on its banner (intro.py banner), a layer of its own in front of the video.
        Banner? banner = null;
        if (File.Exists(Path.Combine(artDir, "banner.json")))
        {
            banner = JsonSerializer.Deserialize<Banner>(File.ReadAllText(Path.Combine(artDir, "banner.json")))!;
            File.Copy(Path.Combine(artDir, "banner.dds"), Path.Combine(texOut, "banner.dds"), true);
        }
        else File.Delete(Path.Combine(texOut, "banner.dds"));
        // The emblem's own textures, recoloured (the menu is all that uses them).
        string objOut = Path.Combine(outDir, "textures", "interface", "objects");
        Directory.CreateDirectory(objOut);
        foreach (var t in LogoTextures)
            if (File.Exists(Path.Combine(artDir, t + ".dds"))) File.Copy(Path.Combine(artDir, t + ".dds"), Path.Combine(objOut, t + ".dds"), true);

        var archives = Directory.GetFiles(skyrimData, "Skyrim - Meshes*.bsa").Select(p => Archive.CreateReader(GameRelease.SkyrimSE, p)).ToList();

        // No menu fog: Skyrim's drifting particles (intmenufogparticles.nif) would cloud the flyby (user, 2026-10-08).
        // The menu drives that mesh (its controller manager and behaviour graph), so the mesh stays whole and only what
        // it draws is hidden; an empty mesh in its place crashes the game at the menu. That is the particle systems and
        // the shape they are emitted from ("EmitGEo:0", 200 triangles of animated thin steam at 45% over the flyby; user,
        // 2026-10-10: "see if the menu fog layer is messing with it"): hiding only the particles left it drawn.
        if (archives.SelectMany(a => a.Files).FirstOrDefault(f => f.Path.Equals(FogMesh, StringComparison.OrdinalIgnoreCase)) is { } fogFile)
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"tm_fog_{Guid.NewGuid():N}.nif");
            File.WriteAllBytes(tmp, fogFile.GetBytes());
            try
            {
                using var nif = new NifFile();
                if (nif.Load(tmp) == 0)
                {
                    var hdr = nif.GetHeader();
                    int hidden = 0;
                    for (uint id = 0; id < hdr.GetNumBlocks(); id++)
                    {
                        string type = hdr.GetBlockTypeStringById(id);
                        if (type is not ("NiParticleSystem" or "BSStripParticleSystem" or "NiMeshParticleSystem" or "BSTriShape" or "BSSubIndexTriShape"
                            or "BSDynamicTriShape" or "NiTriShape" or "NiTriStrips")) continue;
                        BlockMeshBuilder.EditBlock<NiAVObject>(nif, id, o => o.flags |= 1);
                        hidden++;
                    }
                    string fog = Path.Combine(outDir, FogMesh);
                    Directory.CreateDirectory(Path.GetDirectoryName(fog)!);
                    nif.Save(fog);
                    Console.WriteLine($"  {FogMesh}: {hidden} particle systems and shapes hidden (no menu fog over the flyby)");
                }
            }
            finally { File.Delete(tmp); }
        }
        EachLogo(archives, outDir, nif =>
        {
            AddFlyby(nif, flyby, cam, banner);
            return $"the intro video ({flyby.frames} frames on {flyby.sheets} sheets, {flyby.frames / flyby.fps:F1} s), "
                + (banner is null ? "the 3D emblem hidden, no banner (no banner.json)" : "the emblem on its banner in front, the 3D emblem hidden");
        });
    }

    /// <summary>
    /// Writes the logo meshes with the red emblem and, around it, the camera calibration markers of
    /// tools/menu/calibration.py (calibration.json and calibration.dds in <paramref name="calDir"/>) in place of the
    /// flyby; the emblem's middle goes to centre.json for the solve.
    /// </summary>
    public static void BuildCalibration(string skyrimData, string calDir, string outDir)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(calDir, "calibration.json")));
        string texOut = Path.Combine(outDir, TextureDir);
        Directory.CreateDirectory(texOut);
        File.Copy(Path.Combine(calDir, "calibration.dds"), Path.Combine(texOut, "calibration.dds"), true);
        var archives = Directory.GetFiles(skyrimData, "Skyrim - Meshes*.bsa").Select(p => Archive.CreateReader(GameRelease.SkyrimSE, p)).ToList();
        EachLogo(archives, outDir, nif =>
        {
            var centre = EmblemCentre(nif);
            File.WriteAllText(Path.Combine(calDir, "centre.json"), JsonSerializer.Serialize(new { centre = new[] { centre.X, centre.Y, centre.Z } }));
            if (!doc.RootElement.TryGetProperty("shrink", out var shrink) || shrink.GetBoolean()) ShrinkEmblem(nif);
            var verts = new vectorVector3(); var uvs = new vectorVector2(); var tris = new vectorTriangle();
            foreach (var q in doc.RootElement.GetProperty("quads").EnumerateArray())
            {
                int start = (int)verts.Count;
                var corners = q.GetProperty("corners").EnumerateArray().ToList();
                var quv = q.GetProperty("uvs").EnumerateArray().ToList();
                for (int k = 0; k < 4; k++)
                {
                    var p = corners[k].EnumerateArray().Select(e => e.GetSingle()).ToArray();
                    var t = quv[k].EnumerateArray().Select(e => e.GetSingle()).ToArray();
                    verts.Add(new nifly.Vector3(centre.X + p[0], centre.Y + p[1], centre.Z + p[2]));
                    uvs.Add(new nifly.Vector2(t[0], t[1]));
                }
                tris.Add(new Triangle((ushort)start, (ushort)(start + 1), (ushort)(start + 2)));
                tris.Add(new Triangle((ushort)start, (ushort)(start + 2), (ushort)(start + 3)));
            }
            AddUnlitShape(nif, "TamrielCalibration", verts, tris, uvs, null, $@"{TextureDir}\calibration.dds");
            return $"emblem with {doc.RootElement.GetProperty("quads").GetArrayLength()} calibration markers around it";
        });
    }

    /// <summary>Loads each vanilla logo mesh from Skyrim's archives, lets <paramref name="edit"/> change it, saves it to <paramref name="outDir"/>.</summary>
    static void EachLogo(List<IArchiveReader> archives, string outDir, Func<NifFile, string> edit)
    {
        foreach (var mesh in LogoMeshes)
        {
            var file = archives.SelectMany(a => a.Files).FirstOrDefault(f => f.Path.Equals(mesh, StringComparison.OrdinalIgnoreCase));
            if (file is null) { Console.WriteLine($"  {mesh}: not in Skyrim's archives, skipped"); continue; }
            string tmp = Path.Combine(Path.GetTempPath(), $"tm_logo_{Guid.NewGuid():N}.nif");
            File.WriteAllBytes(tmp, file.GetBytes());
            try
            {
                using var nif = new NifFile();
                if (nif.Load(tmp) != 0) { Console.WriteLine($"  {mesh}: unreadable"); continue; }
                string what = edit(nif);
                string dest = Path.Combine(outDir, mesh);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                nif.Save(dest);
                Console.WriteLine($"  {mesh}: {what}");
            }
            finally { File.Delete(tmp); }
        }
    }

    /// <summary>The emblem's middle: its shapes' placement (the vanilla logo stands at about (23.8, 15.6, 0)).</summary>
    static Vector3 EmblemCentre(NifFile nif)
    {
        var centre = Vector3.Zero; int n = 0;
        foreach (var shape in nif.GetShapes()) { var t = shape.transform.translation; centre += new Vector3(t.x, t.y, t.z); n++; }
        return n > 0 ? centre / n : centre;
    }

    /// <summary>The emblem out of the video's way: shrunk to EmblemScale about its own middle.</summary>
    static void ShrinkEmblem(NifFile nif)
    {
        var hdr = nif.GetHeader();
        foreach (var shape in nif.GetShapes().ToList())
        {
            var vs = nif.GetVertsForShape(shape);
            if (vs is null || vs.Count == 0) continue;
            float mid = (vs.Max(v => v.z) + vs.Min(v => v.z)) / 2;
            BlockMeshBuilder.EditBlock<BSTriShape>(nif, hdr.GetBlockID(shape), t =>
            {
                var tr = t.transform;
                float oldMid = tr.translation.z + tr.scale * mid;
                tr.scale *= EmblemScale;
                tr.translation = new nifly.Vector3(tr.translation.x, tr.translation.y, oldMid - tr.scale * mid);
                t.transform = tr;
            });
        }
    }

    /// <summary>An unlit, double-sided textured shape under the root (BSEffectShaderProperty); returns (shape, shader) block IDs.</summary>
    static (uint shape, uint shader) AddUnlitShape(NifFile nif, string name, vectorVector3 verts, vectorTriangle tris, vectorVector2 uvs, List<Color4>? colours, string texture)
    {
        var hdr = nif.GetHeader();
        var shape = nif.CreateShapeFromData(name, verts, tris, uvs);
        if (colours is not null)
            using (var cv = new vectorColor4()) { foreach (var c in colours) cv.Add(c); nif.SetColorsForShape(shape, cv); }
        nif.DeleteShader(shape);
        // Deleting blocks renumbers those after them: find the shape again.
        shape = nif.GetShapes().First(x => x.name.get() == name);
        uint shapeId = hdr.GetBlockID(shape);
        string shapeType = hdr.GetBlockTypeStringById(shapeId);
        if (shapeType != "BSTriShape") throw new InvalidDataException($"{name} is a {shapeType}, not a BSTriShape");
        var shader = new BSEffectShaderProperty
        {
            textureClampMode = 0,
            falloffStartAngle = 1, falloffStopAngle = 1, falloffStartOpacity = 1, falloffStopOpacity = 1,
            baseColor = new Color4(1, 1, 1, 1), baseColorScale = 1, softFalloffDepth = 100,
            envMapScale = 1, lumEmittance = 100, exposureOffset = 13.5f, finalExposureMin = 2, finalExposureMax = 3,
            sourceTexture = new NiString(texture),
        };
        shader.shaderFlags1 = 0x80000000;                                   // ZBuffer test
        shader.shaderFlags2 = 0x1 | 0x10 | (colours is not null ? 0x20u : 0);    // ZBuffer write, double-sided, vertex colours
        uint shaderId = hdr.AddBlock(BlockMeshBuilder.Owned(shader));
        BlockMeshBuilder.EditBlock<BSTriShape>(nif, shapeId, t => t.SetShaderPropertyRef(shaderId));
        nif.SetParentNode(hdr.GetBlockById(shapeId), nif.GetRootNode());
        return (shapeId, shaderId);
    }

    static void AddFlyby(NifFile nif, Flyby fb, MenuCamera cam, Banner? banner)
    {
        var hdr = nif.GetHeader();
        var root = nif.GetRootNode();
        // The game's 3D emblem doesn't draw with the video in the mesh (2026-10-08: the vanilla mesh's does, ours never
        // did, however the screen was placed), so the emblem is a layer of its own (intro.py banner, AddBanner) and the
        // mesh's is hidden. Hidden, not removed: the menu's code expects it as the root's first child (moving it from
        // there crashed the game at start).
        foreach (var shape in nif.GetShapes().ToList())
            BlockMeshBuilder.EditBlock<BSTriShape>(nif, hdr.GetBlockID(shape), t => t.flags |= 1);
        // The camera's frame, and where its view's corners meet the measured plane (y = wall_y) or one in front of it.
        var eye = new Vector3(cam.eye[0], cam.eye[1], cam.eye[2]);
        var fwd = Vector3.Normalize(new Vector3(cam.forward[0], cam.forward[1], cam.forward[2]));
        var up = Vector3.Normalize(new Vector3(cam.up[0], cam.up[1], cam.up[2]));
        var right = Vector3.Normalize(Vector3.Cross(fwd, up));
        float tanH = MathF.Tan(cam.hfov * MathF.PI / 360) * Overscan, tanV = MathF.Tan(cam.vfov * MathF.PI / 360) * Overscan;
        Vector3 OnPlane(float sx, float sy, float ahead)   // sx, sy: -1..1 across the (oversized) view
        {
            var ray = fwd + right * (sx * tanH) + up * (sy * tanV);
            return eye + ray * ((cam.wall_y - ahead - eye.Y) / ray.Y);
        }
        Vector3 OnWall(float sx, float sy) => OnPlane(sx, sy, 0);
        const float apron = 1.6f;                         // the screen reaches this far past the view, fading to black
        float cellU = (float)fb.frameWidth / fb.SheetW, cellV = (float)fb.frameHeight / fb.SheetH;
        float insetU = 0.5f / fb.SheetW, insetV = 0.5f / fb.SheetH;
        int perSheet = fb.cols * fb.rows;
        float loop = fb.frames / fb.fps;

        // The screen's grid: the frame covers -1..1, the apron past it repeats the frame's edge, darkening.
        const int gx = 12, gz = 8;
        var verts = new vectorVector3(); var cellUvs = new List<Vector2>(); var tris = new vectorTriangle();
        var colours = new List<Color4>();
        for (int j = 0; j <= gz; j++)
            for (int i = 0; i <= gx; i++)
            {
                float sx = (i / (float)gx * 2 - 1) * apron, sy = (j / (float)gz * 2 - 1) * apron;
                var p = OnWall(sx, sy);
                verts.Add(new nifly.Vector3(p.X, p.Y, p.Z));
                float u = Math.Clamp(0.5f + sx / 2, 0, 1), v = Math.Clamp(0.5f - sy / 2, 0, 1);
                cellUvs.Add(new Vector2(insetU + u * (cellU - 2 * insetU), insetV + v * (cellV - 2 * insetV)));
                float ex = Math.Max(0, MathF.Abs(sx) - 1) / (apron - 1), ez = Math.Max(0, MathF.Abs(sy) - 1) / (apron - 1);
                float k = Math.Clamp(1 - MathF.Max(ex, ez), 0, 1);
                k = k * k * (3 - 2 * k);
                colours.Add(new Color4(k, k, k, 1));
            }
        for (int j = 0; j < gz; j++)
            for (int i = 0; i < gx; i++)
            {
                int a = j * (gx + 1) + i, b = a + 1, c = a + gx + 1, e = c + 1;
                tris.Add(new Triangle((ushort)a, (ushort)b, (ushort)e));
                tris.Add(new Triangle((ushort)a, (ushort)e, (ushort)c));
            }

        bool hasBsx = false;
        for (uint id = 0; id < hdr.GetNumBlocks(); id++) hasBsx |= hdr.GetBlockTypeStringById(id) == "BSXFlags";
        // One shape per frame, its UVs on its own cell of its sheet, shown for its 1/fps in turn (NiVisController).
        // The menu doesn't run effect-shader UV controllers: stepping one shape per sheet through its cells by UV
        // offset showed only each sheet's first frame (measured in game, 2026-10-08: about one frame a second).
        for (int f = 0; f < fb.frames; f++)
        {
            int sheet = f / perSheet, cell = f % perSheet;
            float du = (cell % fb.cols) * cellU, dv = (cell / fb.cols) * cellV;
            var uvs = new vectorVector2();
            foreach (var uv in cellUvs) uvs.Add(new nifly.Vector2(uv.X + du, uv.Y + dv));
            string shapeName = $"TamrielFlyby{f:000}";
            var shape = nif.CreateShapeFromData(shapeName, verts, tris, uvs);
            using (var cv = new vectorColor4()) { foreach (var c in colours) cv.Add(c); nif.SetColorsForShape(shape, cv); }
            nif.DeleteShader(shape);
            // Deleting blocks renumbers those after them: find the shape again.
            shape = nif.GetShapes().First(x => x.name.get() == shapeName);
            uint shapeId = hdr.GetBlockID(shape);
            string shapeType = hdr.GetBlockTypeStringById(shapeId);
            if (shapeType != "BSTriShape") throw new InvalidDataException($"flyby shape is a {shapeType}, not a BSTriShape");
            var shader = new BSEffectShaderProperty
            {
                textureClampMode = 0,
                falloffStartAngle = 1, falloffStopAngle = 1, falloffStartOpacity = 1, falloffStopOpacity = 1,
                baseColor = new Color4(1, 1, 1, 1), baseColorScale = 1, softFalloffDepth = 100,
                envMapScale = 1, lumEmittance = 100, exposureOffset = 13.5f, finalExposureMin = 2, finalExposureMax = 3,
                sourceTexture = new NiString($@"{TextureDir}\flyby_{sheet:00}.dds"),
            };
            shader.shaderFlags1 = 0x80000000;            // ZBuffer test
            shader.shaderFlags2 = 0x1 | 0x10 | 0x20;     // ZBuffer write, double-sided, vertex colours
            uint shaderId = hdr.AddBlock(BlockMeshBuilder.Owned(shader));

            var vis = new NiBoolData();
            vis.data.SetInterpolationType(NiKeyType.CONST_KEY);
            NifKeys.Add(vis.data, 0, f == 0);
            if (f > 0) NifKeys.Add(vis.data, f / fb.fps, true);
            if (f < fb.frames - 1) NifKeys.Add(vis.data, (f + 1) / fb.fps, false);
            NifKeys.Add(vis.data, loop, f == 0);
            uint visData = hdr.AddBlock(BlockMeshBuilder.Owned(vis));
            var visInterp = new NiBoolInterpolator { boolValue = 2 };
            visInterp.dataRef.index = visData;
            uint visInterpId = hdr.AddBlock(BlockMeshBuilder.Owned(visInterp));
            var visCtlr = new NiVisController { flags = 0x48, frequency = 1, phase = 0, startTime = 0, stopTime = loop };
            visCtlr.targetRef.index = shapeId;
            visCtlr.interpolatorRef.index = visInterpId;
            uint visCtlrId = hdr.AddBlock(BlockMeshBuilder.Owned(visCtlr));
            BlockMeshBuilder.EditBlock<BSTriShape>(nif, shapeId, t =>
            {
                t.SetShaderPropertyRef(shaderId);
                t.controllerRef.index = visCtlrId;
                if (f != 0) t.flags |= 1;   // hidden until its turn
            });
            nif.SetParentNode(hdr.GetBlockById(shapeId), root);
        }
        // Animated, so the menu's scene runs the controllers.
        if (!hasBsx) nif.AssignExtraData(root, BlockMeshBuilder.Owned(new BSXFlags { integerData = 1, name = new NiStringRef("BSX") }));
        if (banner is not null) AddBanner(nif, banner, OnPlane);
    }

    /// <summary>
    /// The emblem on its banner (intro.py banner: banner.dds, uncompressed RGBA, and its place in the video's frame), a
    /// see-through layer just in front of the screen, where the frame puts it. Not in the video's frames: their DXT1
    /// compression bled its reds (user, 2026-10-08: "the logo is bleeding").
    /// </summary>
    static void AddBanner(NifFile nif, Banner b, Func<float, float, float, Vector3> onPlane)
    {
        const int gx = 6, gz = 10;
        var verts = new vectorVector3(); var uvs = new vectorVector2(); var tris = new vectorTriangle();
        for (int j = 0; j <= gz; j++)
            for (int i = 0; i <= gx; i++)
            {
                float s = i / (float)gx, t = j / (float)gz;
                float u = b.u0 + s * (b.u1 - b.u0), v = b.v0 + t * (b.v1 - b.v0);   // in the video's frame
                var p = onPlane(u * 2 - 1, 1 - v * 2, BannerAhead);
                verts.Add(new nifly.Vector3(p.X, p.Y, p.Z));
                uvs.Add(new nifly.Vector2(s, t));
            }
        for (int j = 0; j < gz; j++)
            for (int i = 0; i < gx; i++)
            {
                int a = j * (gx + 1) + i, c = a + gx + 1;
                tris.Add(new Triangle((ushort)a, (ushort)(a + 1), (ushort)(c + 1)));
                tris.Add(new Triangle((ushort)a, (ushort)(c + 1), (ushort)c));
            }
        var (_, shaderId) = AddUnlitShape(nif, "TamrielBanner", verts, tris, uvs, null, $@"{TextureDir}\banner.dds");
        BlockMeshBuilder.EditBlock<BSEffectShaderProperty>(nif, shaderId, sh => sh.shaderFlags2 = 0x10);   // double-sided, no depth writes
        var shape = nif.GetShapes().First(x => x.name.get() == "TamrielBanner");
        nif.AssignAlphaProperty(shape, BlockMeshBuilder.Owned(new NiAlphaProperty { flags = 0x00ED, threshold = 0 }));   // blended: src alpha, 1 - src alpha
    }

}
