using System.Numerics;
using nifly;
using TamrielMerge.Skyrim.Meshes;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's magic visuals (VFX_* statics: particle systems, NIF 4.0.0.2) as Skyrim effect meshes. The node tree is
/// kept (with its keyframed motion as transform controllers), and each NiParticleSystemController with the particles
/// it drives becomes a Skyrim NiParticleSystem laid out as vanilla effects are (magspiderspitmouthfx.nif): particle
/// data, an effect shader with the particle texture, the alpha property, a box emitter at Morrowind's emitter node
/// (speed, declination, planar angle, size, lifetime, colour), emission from Morrowind's emit start to stop at its
/// birth rate (or particle count over lifetime when the rate is automatic), and its modifiers: age/death, grow/fade,
/// colour over life, gravity (planar or spherical), rotation, position, bounds. Plain shapes in the effect are drawn
/// with the effect shader. Times, cycles and units carry over as they are (Morrowind and Skyrim share units).
/// </summary>
public sealed class MwVfx(Tes3Data data, MwTextures textures)
{
    readonly Dictionary<string, string?> converted = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Problems { get; } = [];

    static T Owned<T>(T block) where T : class => BlockMeshBuilder.Owned(block);

    /// <summary>Converts the effect model (Morrowind data path under meshes\), writing Meshes\{outRel}; returns the
    /// model path relative to Meshes, or null.</summary>
    public string? Convert(string model, string modOutDir)
    {
        if (converted.TryGetValue(model, out var hit)) return hit;
        converted[model] = null;
        string source = model.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? model : @"meshes\" + model;
        var bytes = data.Read(source);
        if (bytes is null) { Problems.Add($"vfx {model}: not found"); return null; }
        string rel = Path.Combine("TamrielMerge", "MW", "vfx", Path.ChangeExtension(Path.GetFileName(model), ".nif"));
        try
        {
            Write(Nif4.Read(bytes, model), Path.Combine(modOutDir, "Meshes", rel), Path.GetFileNameWithoutExtension(model));
            return converted[model] = rel;
        }
        catch (Exception e) { Problems.Add($"vfx {model}: {e.Message}"); return null; }
    }

    sealed class Build(NifFile nif)
    {
        public readonly NifFile Nif = nif;
        public readonly NiHeader Hdr = nif.GetHeader();
        public readonly Dictionary<int, uint> Nodes = new();
        public uint Next => Hdr.GetNumBlocks();
        public uint Add(NiObject block) => Hdr.AddBlock(Owned(block));
    }

    void Write(Nif4 mw, string outPath, string name)
    {
        using var nif = new NifFile();
        nif.Create(NiVersion.getSSE());
        var b = new Build(nif);
        var fade = Owned(new BSFadeNode());
        fade.name = new NiStringRef(name);
        b.Hdr.ReplaceBlock(b.Hdr.GetBlockID(nif.GetRootNode()), fade);
        var root = nif.GetRootNode();
        BlockMeshBuilder.AddBsxFlags(nif, root, 1);   // animated

        // The node tree under the root.
        foreach (int r in mw.Roots.Count > 0 ? mw.Roots : [0]) AddNode(b, mw, r, root, isRoot: true);
        // Particle systems, then plain shapes.
        foreach (var c in mw.Blocks.OfType<NiParticleSystemController>())
            if (mw.Get<NiParticles>(c.Target) is { } particles) AddParticles(b, mw, c, particles);
        foreach (var shape in mw.Blocks.OfType<NiTriShape>().Where(s => s is not NiParticles))
            AddShape(b, mw, shape);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        if (nif.Save(outPath) != 0) throw new IOException($"nifly failed to save {outPath}");
    }

    static MatTransform Transform(NifTransform t)
    {
        var m = new MatTransform { translation = new nifly.Vector3(t.Translation.X, t.Translation.Y, t.Translation.Z), scale = t.Scale };
        var r = t.Rotation;
        m.rotation = new Matrix3(r.M11, r.M12, r.M13, r.M21, r.M22, r.M23, r.M31, r.M32, r.M33);
        return m;
    }

    /// <summary>A node (and its children that are nodes), with its keyframed motion.</summary>
    void AddNode(Build b, Nif4 mw, int index, nifly.NiNode parent, bool isRoot = false)
    {
        if (mw.Get<NiNode>(index) is not { } node || node is NiTriShape || b.Nodes.ContainsKey(index)) return;
        var added = b.Nif.AddNode(string.IsNullOrEmpty(node.Name) ? $"Node{index}" : node.Name, Transform(isRoot ? NifTransform.Identity with { Scale = node.Transform.Scale } : node.Transform), parent);
        uint id = b.Hdr.GetBlockID(added);
        b.Nodes[index] = id;
        if (mw.Get<NiController>(node.Controller) is { Type: "NiKeyframeController" } kc && mw.Get<NiKeyframeData>(kc.Data) is { } kd)
            Animate(b, id, kc, kd);
        foreach (int child in node.Children) AddNode(b, mw, child, (nifly.NiNode)b.Hdr.GetBlockById(id));
    }

    /// <summary>Gamebryo controller flags: bit 3 active, bits 1-2 cycle (0 loop, 1 reverse, 2 clamp); Skyrim's
    /// effects also set 0x40.</summary>
    /// Morrowind plays a spell's visual once, so it is played once here too (clamped): Skyrim keeps a magic effect's
    /// art for the whole effect, and a looping visual would repeat for as long as a summon or bound weapon lasts.
    static ushort Flags(NiController c) => 0x4C;

    void Animate(Build b, uint nodeId, NiController kc, NiKeyframeData kd)
    {
        var data = new NiTransformData();
        if (kd.Translations.Count > 0)
        {
            data.translations.SetInterpolationType(NiKeyType.LINEAR_KEY);
            foreach (var (t, v) in kd.Translations) NifKeys.Add(data.translations, t, v);
        }
        if (kd.Rotations.Count > 0)
        {
            // Quaternion keys as X/Y/Z Euler keys (the wrapper can't add quaternion keys), unwrapped so spins keep
            // turning the same way.
            data.rotationType = NiKeyType.XYZ_ROTATION_KEY;
            var groups = new[] { data.xRotations, data.yRotations, data.zRotations };
            foreach (var g in groups) g.SetInterpolationType(NiKeyType.LINEAR_KEY);
            var last = Vector3.Zero;
            bool first = true;
            foreach (var (t, q) in kd.Rotations)
            {
                var e = Euler(q);
                if (!first) e = new Vector3(Unwrap(last.X, e.X), Unwrap(last.Y, e.Y), Unwrap(last.Z, e.Z));
                first = false;
                last = e;
                NifKeys.Add(groups[0], t, e.X); NifKeys.Add(groups[1], t, e.Y); NifKeys.Add(groups[2], t, e.Z);
            }
        }
        if (kd.Scales.Count > 0)
        {
            data.scales.SetInterpolationType(NiKeyType.LINEAR_KEY);
            foreach (var (t, v) in kd.Scales) NifKeys.Add(data.scales, t, v.X);
        }
        uint dataId = b.Add(data);
        var interp = new NiTransformInterpolator { scale = float.MinValue };
        interp.translation = new nifly.Vector3(float.MinValue, float.MinValue, float.MinValue);
        interp.dataRef.index = dataId;
        uint interpId = b.Add(interp);
        var ctlr = new NiTransformController { flags = Flags(kc), frequency = kc.Frequency, phase = kc.Phase, startTime = kc.Start, stopTime = kc.Stop };
        ctlr.targetRef.index = nodeId;
        ctlr.interpolatorRef.index = interpId;
        uint ctlrId = b.Add(ctlr);
        BlockMeshBuilder.EditBlock<nifly.NiNode>(b.Nif, nodeId, n => n.controllerRef.index = ctlrId);
    }

    static float Unwrap(float previous, float angle)
    {
        while (angle - previous > MathF.PI) angle -= 2 * MathF.PI;
        while (angle - previous < -MathF.PI) angle += 2 * MathF.PI;
        return angle;
    }

    /// <summary>X, Y, Z angles (applied X then Y then Z) of a rotation.</summary>
    static Vector3 Euler(System.Numerics.Quaternion q)
    {
        var m = Matrix4x4.CreateFromQuaternion(q);
        float y = MathF.Asin(Math.Clamp(-m.M13, -1, 1));
        float x = MathF.Atan2(m.M23, m.M33);
        float z = MathF.Atan2(m.M12, m.M11);
        return new Vector3(x, y, z);
    }

    (string? Texture, NiAlphaProperty? Alpha, NiMaterialProperty? Material) Look(Nif4 mw, NiNode shape)
    {
        string? texture = null; NiAlphaProperty? alpha = null; NiMaterialProperty? material = null;
        foreach (var p in shape.Properties.Select(mw.Get<NifBlock>))
            switch (p)
            {
                case NiTexturingProperty { Base: { } tex } when mw.Get<NiSourceTexture>(tex.Source) is { FileName: { } file }:
                    texture = textures.Ensure(file).Path;
                    break;
                case NiAlphaProperty a: alpha = a; break;
                case NiMaterialProperty m: material = m; break;
            }
        return (texture, alpha, material);
    }

    uint Shader(Build b, string? texture, NiMaterialProperty? material)
    {
        var s = new BSEffectShaderProperty
        {
            textureClampMode = 0xFF03,
            falloffStartAngle = 1, falloffStopAngle = 1, falloffStartOpacity = 1, falloffStopOpacity = 1,
            baseColor = new Color4(1, 1, 1, material?.Alpha ?? 1), baseColorScale = 1, softFalloffDepth = 100,
            envMapScale = 1, lumEmittance = 100, exposureOffset = 13.5f, finalExposureMin = 2, finalExposureMax = 3,
        };
        if (texture is not null) s.sourceTexture = new NiString(texture);
        s.shaderFlags1 = 0x80000000;   // ZBuffer test
        s.shaderFlags2 = 0x20;         // vertex colours (the particles' colour)
        return b.Add(s);
    }

    uint Alpha(Build b, NiAlphaProperty? a) =>
        b.Add(new nifly.NiAlphaProperty { flags = a?.Flags ?? 0x10ED, threshold = a?.Threshold ?? 0 });

    void AddParticles(Build b, Nif4 mw, NiParticleSystemController c, NiParticles particles)
    {
        int parentIndex = mw.Blocks.OfType<NiNode>().FirstOrDefault(n => n.Children.Contains(particles.Index))?.Index ?? -1;
        if (!b.Nodes.TryGetValue(parentIndex, out var parentId)) parentId = b.Hdr.GetBlockID(b.Nif.GetRootNode());
        uint emitterNode = b.Nodes.TryGetValue(c.Emitter, out var en) ? en : parentId;
        var (texture, alphaProp, material) = Look(mw, particles);
        int max = Math.Max(1, Math.Max(c.NumParticles, mw.Get<NiGeometryData>(particles.Data)?.Vertices.Length ?? 0));
        float rate = c.UseBirthRate ? c.BirthRate : max / Math.Max(0.05f, c.Lifetime + c.LifetimeVariation / 2);

        // Particle data: room for the most particles alive at once.
        var data = new NiPSysData { hasRadii = true, hasSizes = true };
        using (var verts = new vectorVector3())
        using (var tris = new vectorTriangle())
        using (var uvs = new vectorVector2())
        using (var norms = new vectorVector3())
        {
            for (int i = 0; i < max; i++) verts.Add(new nifly.Vector3(0, 0, 0));
            data.Create(NiVersion.getSSE(), verts, tris, uvs, norms);
        }
        uint dataId = b.Add(data);
        uint shaderId = Shader(b, texture, material);
        uint alphaId = Alpha(b, alphaProp);

        // Modifiers, in vanilla's order numbers.
        var mods = new List<NiPSysModifier>();
        string psysName = string.IsNullOrEmpty(particles.Name) ? $"Particles{particles.Index}" : particles.Name;
        mods.Add(new NiPSysAgeDeathModifier { name = new NiStringRef("NiPSysAgeDeath"), order = 0, isActive = true });
        var emitter = new NiPSysBoxEmitter
        {
            name = new NiStringRef("NiPSysBoxEmitter"), order = 1000, isActive = true,
            speed = c.Speed, speedVariation = c.SpeedVariation,
            declination = c.Declination, declinationVariation = c.DeclinationVariation,
            planarAngle = c.PlanarAngle, planarAngleVariation = c.PlanarAngleVariation,
            color = new Color4(c.InitialColor.X, c.InitialColor.Y, c.InitialColor.Z, c.InitialColor.W),
            radius = c.InitialSize, radiusVariation = 0,
            lifeSpan = c.Lifetime, lifeSpanVariation = c.LifetimeVariation,
            width = c.EmitterDimensions.X * 2, height = c.EmitterDimensions.Y * 2, depth = c.EmitterDimensions.Z * 2,
        };
        emitter.emitterNodeRef.index = emitterNode;
        mods.Add(emitter);
        var spawn = new NiPSysSpawnModifier { name = new NiStringRef("NiPSysSpawnModifier"), order = 1000, isActive = true, percentSpawned = 1, minSpawned = 1, maxSpawned = 1, lifeSpan = c.Lifetime, lifeSpanVariation = c.LifetimeVariation };
        mods.Add(spawn);
        nifly.NiColorData? colors = null;
        for (int m = c.Modifier, guard = 0; m >= 0 && guard < 16; guard++)
        {
            var mod = mw.Get<NiParticleModifier>(m);
            if (mod is null) break;
            switch (mod)
            {
                case NiParticleGrowFade gf:
                    mods.Add(new NiPSysGrowFadeModifier { name = new NiStringRef("NiPSysGrowFadeModifier"), order = 3000, isActive = true, growTime = gf.Grow, fadeTime = gf.Fade, baseScale = 1 });
                    break;
                case NiParticleColorModifier cm when mw.Get<NiColorData>(cm.ColorData) is { } cd:
                    colors = new nifly.NiColorData();
                    colors.data.SetInterpolationType(NiKeyType.LINEAR_KEY);
                    foreach (var (time, color) in cd.Keys) NifKeys.Add(colors.data, time, color);
                    break;
                case NiGravity g:
                    var gravity = new NiPSysGravityModifier
                    {
                        name = new NiStringRef("NiPSysGravityModifier"), order = 4000, isActive = true,
                        gravityAxis = new nifly.Vector3(g.Direction.X, g.Direction.Y, g.Direction.Z), decay = g.Decay, strength = g.Force,
                        forceType = g.ForceType == 1 ? ForceType.FORCE_SPHERICAL : ForceType.FORCE_PLANAR, turbulence = 0, turbulenceScale = 1,
                    };
                    // A spherical pull centres on a node at Morrowind's position; a planar one uses the parent's axes.
                    gravity.gravityObjRef.index = g.ForceType == 1
                        ? b.Hdr.GetBlockID(b.Nif.AddNode($"Gravity{mod.Index}", Transform(NifTransform.Identity with { Translation = g.Position }), (nifly.NiNode)b.Hdr.GetBlockById(parentId)))
                        : parentId;
                    mods.Add(gravity);
                    break;
                case NiParticleRotation rot:
                    mods.Add(new NiPSysRotationModifier
                    {
                        name = new NiStringRef("NiPSysRotationModifier"), order = 3000, isActive = true, initialSpeed = rot.Speed, randomInitialAxis = rot.RandomAxis,
                        initialAxis = new nifly.Vector3(rot.Axis.X, rot.Axis.Y, rot.Axis.Z),
                    });
                    break;
            }
            m = mod.Next;
        }
        uint colorDataId = colors is null ? uint.MaxValue : b.Add(colors);
        if (colors is not null)
        {
            var cm = new NiPSysColorModifier { name = new NiStringRef("NiPSysColorModifier"), order = 3000, isActive = true };
            cm.dataRef.index = colorDataId;
            mods.Add(cm);
        }
        mods.Add(new NiPSysPositionModifier { name = new NiStringRef("NiPSysPositionModifier"), order = 6000, isActive = true });
        mods.Add(new NiPSysBoundUpdateModifier { name = new NiStringRef("NiPSysBoundUpdateModifier"), order = 7000, isActive = true });
        mods.Sort((x, y) => x.order.CompareTo(y.order));
        // Names must be unique within the system.
        for (int i = 0; i < mods.Count; i++) mods[i].name = new NiStringRef($"{mods[i].name.get()}:{i}");

        // The system, its modifiers and controllers, numbered ahead so they can point at each other.
        uint psysId = b.Next;
        uint firstMod = psysId + 1;
        uint emitCtlrId = firstMod + (uint)mods.Count, updateCtlrId = emitCtlrId + 1, rateId = updateCtlrId + 1, visId = rateId + 1, visDataId = visId + 1;
        var psys = new NiParticleSystem
        {
            name = new NiStringRef(psysName), flags = 524302, isWorldSpace = false,
            vertFlags1 = 81, vertFlags4 = 4, vertFlags6 = 32, vertFlags7 = 64, vertFlags8 = 8,
        };
        psys.transform = Transform(particles.Transform);
        psys.dataRef.index = dataId;
        psys.psysDataRef.index = dataId;
        psys.shaderPropertyRef.index = shaderId;
        psys.alphaPropertyRef.index = alphaId;
        psys.controllerRef.index = emitCtlrId;
        for (int i = 0; i < mods.Count; i++) psys.modifierRefs.AddBlockRef(firstMod + (uint)i);
        if (b.Add(psys) != psysId) throw new InvalidOperationException("block numbering");
        foreach (var mod in mods)
        {
            mod.targetRef.index = psysId;
            b.Add(mod);
        }
        var emitCtlr = new NiPSysEmitterCtlr { flags = Flags(c), frequency = c.Frequency, phase = c.Phase, startTime = c.Start, stopTime = c.Stop, modifierName = emitter.name };
        emitCtlr.targetRef.index = psysId;
        emitCtlr.nextControllerRef.index = updateCtlrId;
        emitCtlr.interpolatorRef.index = rateId;
        emitCtlr.visInterpolatorRef.index = visId;
        b.Add(emitCtlr);
        var update = new NiPSysUpdateCtlr { flags = 76, frequency = 1, startTime = 0, stopTime = 0 };
        update.targetRef.index = psysId;
        b.Add(update);
        b.Add(new NiFloatInterpolator { floatValue = rate });
        var vis = new NiBoolInterpolator { boolValue = 2 };
        vis.dataRef.index = visDataId;
        b.Add(vis);
        // Emitting from Morrowind's emit start to its stop.
        var visData = new NiBoolData();
        visData.data.SetInterpolationType(NiKeyType.CONST_KEY);
        if (c.EmitStart > c.Start) NifKeys.Add(visData.data, c.Start, false);
        NifKeys.Add(visData.data, c.EmitStart, true);
        NifKeys.Add(visData.data, Math.Max(c.EmitStop, c.EmitStart), false);
        b.Add(visData);
        b.Nif.SetParentNode(b.Hdr.GetBlockById(psysId), (nifly.NiNode)b.Hdr.GetBlockById(parentId));
    }

    /// <summary>A plain shape of the effect (glows, rings), still, drawn with the effect shader.</summary>
    void AddShape(Build b, Nif4 mw, NiTriShape shape)
    {
        if (shape.Hidden || mw.Get<NiTriShapeData>(shape.Data) is not { } d || d.Vertices.Length == 0) return;
        int parentIndex = mw.Blocks.OfType<NiNode>().FirstOrDefault(n => n.Children.Contains(shape.Index))?.Index ?? -1;
        if (!b.Nodes.TryGetValue(parentIndex, out var parentId)) return;
        using var verts = new vectorVector3();
        using var uvs = new vectorVector2();
        using var tris = new vectorTriangle();
        foreach (var v in d.Vertices) verts.Add(new nifly.Vector3(v.X, v.Y, v.Z));
        var uv = d.UVSets.FirstOrDefault() ?? new System.Numerics.Vector2[d.Vertices.Length];
        foreach (var t in uv) uvs.Add(new nifly.Vector2(t.X, t.Y));
        for (int i = 0; i + 2 < d.Triangles.Length; i += 3) tris.Add(new Triangle(d.Triangles[i], d.Triangles[i + 1], d.Triangles[i + 2]));
        var s = b.Nif.CreateShapeFromData(string.IsNullOrEmpty(shape.Name) ? $"Shape{shape.Index}" : shape.Name, verts, tris, uvs);
        uint shapeId = b.Hdr.GetBlockID(s);
        b.Nif.DeleteShader(s);
        var (texture, alpha, material) = Look(mw, shape);
        uint shaderId = Shader(b, texture, material);
        uint alphaId = Alpha(b, alpha);
        BlockMeshBuilder.EditBlock<BSTriShape>(b.Nif, shapeId, t =>
        {
            t.transform = Transform(shape.Transform);
            t.SetShaderPropertyRef(shaderId);
            t.SetAlphaPropertyRef(alphaId);
        });
        b.Nif.SetParentNode(b.Hdr.GetBlockById(shapeId), (nifly.NiNode)b.Hdr.GetBlockById(parentId));
    }
}
