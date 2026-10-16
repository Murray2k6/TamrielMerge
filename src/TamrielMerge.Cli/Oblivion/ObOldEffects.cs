using System.Numerics;
using nifly;
using TamrielMerge.Skyrim.Meshes;
using V3 = System.Numerics.Vector3;
using N = TamrielMerge.Oblivion.Ob10Nif;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's older effect models (Gamebryo 10.1/10.2, read by <see cref="Ob10Nif"/>: dust clouds and falls,
/// fireflies, plasma, twinkling lights, the welkynd stones' glow, two traps) as Skyrim models. Their shapes are baked
/// into model space as other models' are; each particle system becomes a Skyrim particle system laid out as vanilla
/// effects are (as MwVfx does for Morrowind): particle data with room for Oblivion's particle count, an effect shader
/// with the particle texture and Oblivion's alpha, and Oblivion's own modifiers carried over one for one - age and
/// death, the box, sphere or cylinder emitter (a mesh emitter becomes a box over its mesh) at its emitter node, spawn,
/// grow and fade, colour over life, gravity, drag, rotation, position and bounds - with an emitter controller looping
/// at Oblivion's birth rate, or - where the effect's controller sequences drive it (an effect a script sets off with
/// PlayGroup, a trap's spray, an Idle loop) - those sequences as Skyrim ones, which PlayGamebryoAnimation plays.
/// Emitter and gravity nodes are placed where they stand: these models' moving parts (a trap's spikes) stay still.
/// </summary>
public sealed partial class ObMeshConverter
{
    /// <summary>What a conversion noticed that isn't a failure (particles that only play during an animation).</summary>
    public List<string> Notes { get; } = [];

    Result ConvertOldEffect(string model, string source, byte[] bytes, string modOutDir, bool moving, float? itemMass, string why)
    {
        Ob10Nif nif;
        try { nif = Ob10Nif.Read(bytes, source); }
        catch (InvalidDataException e) { throw new InvalidDataException($"{why}; and {e.Message}"); }
        string rel = OutputPath(model, moving, itemMass);

        // World transforms: each block's own after its parents' (a node holds its children).
        var parent = new Dictionary<int, int>();
        foreach (var n in nif.Blocks.OfType<N.Node>())
            foreach (int c in n.Children) if (c >= 0) parent[c] = n.Index;
        Matrix4x4 Local(N.AvObject o)
        {
            var r = Matrix4x4.Transpose(o.R);   // Gamebryo's rotation matrix applied to column vectors
            return Matrix4x4.CreateScale(o.Scale) * r * Matrix4x4.CreateTranslation(o.T);
        }
        Matrix4x4 World(int index)
        {
            var m = Matrix4x4.Identity;
            for (int i = index; i >= 0; i = parent.TryGetValue(i, out int up) ? up : -1)
                if (nif.Get<N.AvObject>(i) is { } o) m *= Local(o);
            return m;
        }
        bool Hidden(int index)
        {
            for (int i = index; i >= 0; i = parent.TryGetValue(i, out int up) ? up : -1)
                if (nif.Get<N.AvObject>(i) is { } o && ((o.Flags & 1) != 0 || IsEditorMarker(o.Name))) return true;
            return false;
        }

        // Plain shapes, baked into model space.
        var pieces = new List<Piece>();
        foreach (var s in nif.Blocks.OfType<N.Shape>().Where(s => s.Type is "NiTriShape" or "NiTriStrips"))
        {
            if (Hidden(s.Index) || nif.Get<N.GeomData>(s.Data) is not { Verts.Count: > 0, Tris.Count: > 0 } d) continue;
            var m = World(s.Index);
            var props = s.Props.Select(nif.Get<N.Property>).OfType<N.Property>().ToList();
            var texturing = props.FirstOrDefault(p => p.Type == "NiTexturingProperty");
            var alpha = props.FirstOrDefault(p => p.Type == "NiAlphaProperty");
            var material = props.FirstOrDefault(p => p.Type == "NiMaterialProperty");
            var piece = new Piece
            {
                Name = s.Name.Length > 0 ? s.Name : $"Shape{pieces.Count}",
                Verts = d.Verts.Select(v => V3.Transform(v, m)).ToList(),
                Normals = d.Normals.Count == d.Verts.Count ? d.Normals.Select(v => { var t = V3.TransformNormal(v, m); return t.LengthSquared() > 0 ? V3.Normalize(t) : V3.UnitZ; }).ToList() : [],
                Uvs = d.Uvs.Count == d.Verts.Count ? d.Uvs : Enumerable.Repeat(System.Numerics.Vector2.Zero, d.Verts.Count).ToList(),
                Colors = d.Colors,
                Tris = d.Tris,
                Texture = texturing is { BaseTexture: >= 0 } t ? nif.Get<N.Property>(t.BaseTexture)?.File : null,
                TwoSided = props.Any(p => p.Type == "NiStencilProperty"),
            };
            if (alpha is not null) { piece.AlphaFlags = alpha.Flags; piece.AlphaThreshold = alpha.Threshold; }
            if (material is not null) { piece.Glossiness = material.Glossiness; piece.Alpha = material.Alpha; piece.Emissive = material.Emissive; }
            pieces.Add(piece);
        }

        // Collision from the visible geometry when the model has any (Oblivion's havok here is boxes and hulls).
        var solid = new List<(V3[] Pts, V3 N)>();
        if (nif.Blocks.Any(b => b?.Type == "bhkCollisionObject"))
            foreach (var p in pieces)
                foreach (var (a, b, c) in p.Tris)
                {
                    var pts = new[] { p.Verts[a], p.Verts[b], p.Verts[c] };
                    var cr = V3.Cross(pts[1] - pts[0], pts[2] - pts[0]);
                    if (cr.LengthSquared() > 1e-6f) solid.Add((pts, V3.Normalize(cr)));
                }

        var systems = nif.Blocks.OfType<N.Shape>().Where(s => s.Type == "NiParticleSystem" && !Hidden(s.Index)).ToList();
        List<(string Name, float Length)> played = [];
        Write(pieces, solid, Path.Combine(modOutDir, rel), moving, itemMass,
            more: out_ =>
            {
                var made = systems.Select(ps => AddParticles(out_, nif, ps, World, model)).OfType<Emitting>().ToList();
                played = AddEmissionSequences(out_, nif, made);
            },
            bsxAdd: systems.Count > 0 ? 1 : 0);
        var all = pieces.SelectMany(p => p.Verts).Concat(systems.Select(s => World(s.Index).Translation)).ToList();
        return new Result(rel, pieces.Count + systems.Count, solid.Count,
            all.Count > 0 ? all.Aggregate(new V3(float.MaxValue), V3.Min) : V3.Zero,
            all.Count > 0 ? all.Aggregate(new V3(float.MinValue), V3.Max) : V3.Zero, systems.Count > 0, solid.Select(t => t.Pts).ToList())
        { Sequences = played, Moves = systems.Count > 0 };
    }

    static MatTransform ToMat(Matrix4x4 m)
    {
        Matrix4x4.Decompose(m, out var scale, out var rot, out var pos);
        var r = Matrix4x4.Transpose(Matrix4x4.CreateFromQuaternion(rot));
        return new MatTransform
        {
            translation = new nifly.Vector3(pos.X, pos.Y, pos.Z),
            rotation = new Matrix3(r.M11, r.M12, r.M13, r.M21, r.M22, r.M23, r.M31, r.M32, r.M33),
            scale = (scale.X + scale.Y + scale.Z) / 3,
        };
    }

    /// <summary>The value a float or bool interpolator settles on: its own, or its keys' (the largest).</summary>
    static float Settled(Ob10Nif nif, int interpolator, bool isBool)
    {
        if (nif.Get<N.Interpolator>(interpolator) is not { } i) return isBool ? 1 : 0;
        if (nif.Get<N.KeyData>(i.Data) is { Keys.Count: > 0 } keys) return keys.Keys.Max(k => k.Value.X);
        return isBool ? (i.BoolValue ? 1 : 0) : i.Value;
    }

    /// <summary>A converted particle system: its name, its emitter controller and emitter, and whether sequences drive
    /// its emission (then the emitter controller blends what they set, as vanilla's sequence-driven effects).</summary>
    sealed record Emitting(string Name, uint Controller, string Emitter, bool Driven);

    /// <summary>Whether sequences drive a system's emission (its emitter controller's interpolators are blends a
    /// controller manager sets), else its own birth rate and whether it emits.</summary>
    static (bool Driven, float Rate, bool Emits) Emission(Ob10Nif nif, N.Controller ctlr)
    {
        bool driven = nif.Get<N.Interpolator>(ctlr.Interpolator)?.Type.StartsWith("NiBlend") == true || ctlr.Interpolator < 0;
        return driven ? (true, 0, false) : (false, Settled(nif, ctlr.Interpolator, false), Settled(nif, ctlr.VisInterpolator, true) > 0);
    }

    /// <summary>
    /// The effect's controller sequences that drive its particles' emission (an effect a script sets off with
    /// PlayGroup Forward, a trap's spray, an Idle loop), as Skyrim sequences under a controller manager on the root,
    /// laid out as vanilla's (Lumbermill01WaterWheel01): each link sets an emitter controller's BirthRate or
    /// EmitterActive (named by its emitter) from a float or bool interpolator with Oblivion's keys. Skyrim's
    /// PlayGamebryoAnimation plays them by name. Sequences without emission (transforms only) aren't kept here.
    /// </summary>
    List<(string Name, float Length)> AddEmissionSequences(NifFile nif, Ob10Nif src, List<Emitting> made)
    {
        var driven = made.Where(m => m.Driven).GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.First());
        if (driven.Count == 0) return [];
        var hdr = nif.GetHeader();
        var root = nif.GetRootNode();
        uint rootId = hdr.GetBlockID(root);
        uint Add(NiObject block) => hdr.AddBlock(BlockMeshBuilder.Owned(block));
        var sequences = src.Blocks.OfType<N.Sequence>()
            .Select(s => (s, Links: s.Links.Where(l => l.ControllerType == "NiPSysEmitterCtlr" && driven.ContainsKey(l.Node) && l.InterpolatorId is "BirthRate" or "EmitterActive").ToList()))
            .Where(x => x.Links.Count > 0).ToList();
        if (sequences.Count == 0) return [];
        var manager = new NiControllerManager { flags = 0x4C, frequency = 1, phase = 0, startTime = float.MaxValue, stopTime = float.MinValue, cumulative = false };
        manager.targetRef.index = rootId;
        uint managerId = Add(manager);

        uint Interpolator(int source, bool isBool)
        {
            var i = src.Get<N.Interpolator>(source);
            var keys = i is null ? null : src.Get<N.KeyData>(i.Data);
            if (isBool)
            {
                var b = new NiBoolInterpolator { boolValue = (byte)(i?.BoolValue != false ? 1 : 0) };
                if (keys is { Keys.Count: > 0 })
                {
                    var d = new NiBoolData();
                    d.data.SetInterpolationType(NiKeyType.CONST_KEY);
                    foreach (var (t, v) in keys.Keys) NifKeys.Add(d.data, t, v.X > 0);
                    b.dataRef.index = Add(d);
                }
                return Add(b);
            }
            var f = new NiFloatInterpolator { floatValue = i?.Value ?? 0 };
            if (keys is { Keys.Count: > 0 })
            {
                var d = new NiFloatData();
                d.data.SetInterpolationType(NiKeyType.LINEAR_KEY);
                foreach (var (t, v) in keys.Keys) NifKeys.Add(d.data, t, v.X);
                f.dataRef.index = Add(d);
            }
            return Add(f);
        }

        var seqIds = new List<uint>();
        foreach (var (s, links) in sequences)
        {
            var seq = NiflyRaw.NewSequence(links.Count);
            var slots = NiflyRaw.Links(seq);
            for (int k = 0; k < links.Count; k++)
            {
                var l = links[k];
                var target = driven[l.Node];
                slots[k].interpolatorRef.index = Interpolator(l.Interpolator, l.InterpolatorId == "EmitterActive");
                slots[k].controllerRef.index = target.Controller;
                slots[k].priority = 0;
                slots[k].nodeName = new NiStringRef(l.Node);
                slots[k].ctrlType = new NiStringRef("NiPSysEmitterCtlr");
                slots[k].ctrlID = new NiStringRef(target.Emitter);
                slots[k].interpID = new NiStringRef(l.InterpolatorId);
                slots[k].propType = new NiStringRef("");
            }
            seq.name = new NiStringRef(s.Name);
            seq.cycleType = s.Cycle switch { 1 => CycleType.CYCLE_REVERSE, 2 => CycleType.CYCLE_CLAMP, _ => CycleType.CYCLE_LOOP };
            seq.frequency = s.Frequency;
            seq.startTime = s.Start;
            seq.stopTime = s.Stop;
            seq.weight = 1;
            seq.managerRef.index = managerId;
            seq.accumRootName = new NiStringRef(root.name.get());
            seq.textKeyRef.index = NoBlock;
            seqIds.Add(Add(seq));
        }

        // The object palette: the systems by name, for the sequences to find them.
        var systems = new List<(string Name, uint Id)>();
        for (uint i = 0; i < hdr.GetNumBlocks(); i++)
            if (hdr.GetBlockById(i) is NiParticleSystem ps && driven.ContainsKey(ps.name.get())) systems.Add((ps.name.get(), i));
        var palette = NiflyRaw.NewPalette(systems.Count + 1);
        var entries = NiflyRaw.PaletteObjects(palette);
        entries[0].name = new NiString(root.name.get()); entries[0].objectRef.index = rootId;
        for (int k = 0; k < systems.Count; k++) { entries[k + 1].name = new NiString(systems[k].Name); entries[k + 1].objectRef.index = systems[k].Id; }
        palette.sceneRef.index = rootId;
        uint paletteId = Add(palette);

        var m = (NiControllerManager)hdr.GetBlockById(managerId);
        foreach (var id in seqIds) m.controllerSequenceRefs.AddBlockRef(id);
        m.objectPaletteRef.index = paletteId;
        root.controllerRef.index = managerId;
        return sequences.Select(x => (x.s.Name, x.s.Stop - x.s.Start)).ToList();
    }
    Emitting? AddParticles(NifFile nif, Ob10Nif src, N.Shape ps, Func<int, Matrix4x4> world, string model)
    {
        var hdr = nif.GetHeader();
        var root = nif.GetRootNode();
        uint rootId = hdr.GetBlockID(root);
        uint Add(NiObject block) => hdr.AddBlock(BlockMeshBuilder.Owned(block));
        var mods = ps.Modifiers.Select(src.Get<N.Modifier>).OfType<N.Modifier>().ToList();
        var ctlr = src.Get<N.Controller>(ps.Controller);
        while (ctlr is not null && ctlr.Type != "NiPSysEmitterCtlr") ctlr = src.Get<N.Controller>(ctlr.Next);
        var emitter = mods.FirstOrDefault(m => m.Type.EndsWith("Emitter"));
        if (emitter is null || ctlr is null) { Notes.Add($"model {model}: particle system {ps.Name} has no emitter; not converted"); return null; }
        var (driven, rate, emits) = Emission(src, ctlr);

        // A node standing where a source node is (emitters, gravity), under the root.
        var nodes = new Dictionary<int, uint>();
        uint NodeAt(int source)
        {
            if (source < 0 || src.Get<N.AvObject>(source) is null) return uint.MaxValue;
            if (nodes.TryGetValue(source, out var hit)) return hit;
            var n = nif.AddNode($"{ps.Name}:{src.Get<N.AvObject>(source)!.Name}", ToMat(world(source)), root);
            return nodes[source] = hdr.GetBlockID(n);
        }

        var data = src.Get<N.GeomData>(ps.Data);
        int max = Math.Clamp(data?.ParticleCount ?? 0, 1, 2048);
        var psysData = new NiPSysData { hasRadii = true, hasSizes = true };
        using (var verts = new vectorVector3())
        using (var tris = new vectorTriangle())
        using (var uvs = new vectorVector2())
        using (var norms = new vectorVector3())
        {
            for (int i = 0; i < max; i++) verts.Add(new nifly.Vector3(0, 0, 0));
            psysData.Create(NiVersion.getSSE(), verts, tris, uvs, norms);
        }
        uint dataId = Add(psysData);

        // The look: the particle texture, Oblivion's alpha (its additive glows stay additive) and material alpha.
        var props = ps.Props.Select(src.Get<N.Property>).OfType<N.Property>().ToList();
        string? texture = props.FirstOrDefault(p => p.Type == "NiTexturingProperty") is { BaseTexture: >= 0 } tp ? src.Get<N.Property>(tp.BaseTexture)?.File : null;
        string? skyTexture = texture is null ? null : textures.Ensure(texture);
        var alphaProp = props.FirstOrDefault(p => p.Type == "NiAlphaProperty");
        var materialProp = props.FirstOrDefault(p => p.Type == "NiMaterialProperty");
        var shader = new BSEffectShaderProperty
        {
            textureClampMode = 0xFF03,
            falloffStartAngle = 1, falloffStopAngle = 1, falloffStartOpacity = 1, falloffStopOpacity = 1,
            baseColor = new Color4(1, 1, 1, materialProp?.Alpha ?? 1), baseColorScale = 1, softFalloffDepth = 100,
            envMapScale = 1, lumEmittance = 100, exposureOffset = 13.5f, finalExposureMin = 2, finalExposureMax = 3,
        };
        if (skyTexture is not null) shader.sourceTexture = new NiString(skyTexture);
        shader.shaderFlags1 = 0x80000000;   // ZBuffer test
        shader.shaderFlags2 = 0x20;         // vertex colours (the particles' colour)
        uint shaderId = Add(shader);
        uint alphaId = Add(new nifly.NiAlphaProperty { flags = alphaProp?.Flags ?? 0x10ED, threshold = alphaProp?.Threshold ?? 0 });

        var list = new List<NiPSysModifier>();
        string emitterName = "";
        foreach (var m in mods)
        {
            NiPSysModifier? made = null;
            switch (m.Type)
            {
                case "NiPSysAgeDeathModifier": made = new NiPSysAgeDeathModifier(); break;
                case "NiPSysBoxEmitter" or "NiPSysSphereEmitter" or "NiPSysCylinderEmitter" or "NiPSysMeshEmitter":
                {
                    NiPSysVolumeEmitter e;
                    uint node;
                    if (m.Type == "NiPSysSphereEmitter") { e = new NiPSysSphereEmitter { radius = m.Size.X }; node = NodeAt(m.EmitterObject); }
                    else if (m.Type == "NiPSysCylinderEmitter") { e = new NiPSysCylinderEmitter { radius = m.Size.X, height = m.Size.Y }; node = NodeAt(m.EmitterObject); }
                    else if (m.Type == "NiPSysBoxEmitter") { e = new NiPSysBoxEmitter { width = m.Size.X, height = m.Size.Y, depth = m.Size.Z }; node = NodeAt(m.EmitterObject); }
                    else
                    {
                        // A mesh emitter: a box over its mesh, at the mesh's place.
                        var mesh = src.Get<N.Shape>(m.EmitterObject);
                        var md = mesh is null ? null : src.Get<N.GeomData>(mesh.Data);
                        var lo = md is { Verts.Count: > 0 } ? md.Verts.Aggregate(V3.Min) : V3.Zero;
                        var hi = md is { Verts.Count: > 0 } ? md.Verts.Aggregate(V3.Max) : V3.Zero;
                        var size = hi - lo;
                        e = new NiPSysBoxEmitter { width = size.X, height = size.Y, depth = size.Z };
                        node = mesh is null ? uint.MaxValue : NodeAt(mesh.Index);
                        if (node != uint.MaxValue)
                        {
                            var at = (lo + hi) / 2;
                            var n = (NiNode)hdr.GetBlockById(node);
                            var t = n.transform;
                            var w = V3.Transform(at, world(mesh!.Index));
                            t.translation = new nifly.Vector3(w.X, w.Y, w.Z);
                            n.transform = t;
                        }
                    }
                    var b = (NiPSysEmitter)e;
                    b.speed = m.Speed; b.speedVariation = m.SpeedVar; b.declination = m.Declination; b.declinationVariation = m.DeclinationVar;
                    b.planarAngle = m.PlanarAngle; b.planarAngleVariation = m.PlanarAngleVar;
                    b.color = new Color4(m.Color.X, m.Color.Y, m.Color.Z, m.Color.W);
                    b.radius = m.Radius; b.radiusVariation = 0; b.lifeSpan = m.LifeSpan; b.lifeSpanVariation = m.LifeSpanVar;
                    // No emitter node: the system's own place.
                    e.emitterNodeRef.index = node == uint.MaxValue ? rootId : node;
                    made = e;
                    break;
                }
                case "NiPSysSpawnModifier":
                    made = new NiPSysSpawnModifier { numSpawnGenerations = m.SpawnGenerations, percentSpawned = m.PercentSpawned, minSpawned = m.MinSpawn, maxSpawned = m.MaxSpawn, spawnSpeedVariation = m.SpawnSpeedVar, spawnDirVariation = m.SpawnDirVar, lifeSpan = m.SpawnLife, lifeSpanVariation = m.SpawnLifeVar };
                    break;
                case "NiPSysGrowFadeModifier":
                    made = new NiPSysGrowFadeModifier { growTime = m.GrowTime, fadeTime = m.FadeTime, baseScale = 1 };
                    break;
                case "NiPSysColorModifier" when src.Get<N.KeyData>(m.ColorData) is { Keys.Count: > 0 } cd:
                {
                    var colors = new nifly.NiColorData();
                    colors.data.SetInterpolationType(NiKeyType.LINEAR_KEY);
                    foreach (var (time, color) in cd.Keys) NifKeys.Add(colors.data, time, color);
                    var cm = new NiPSysColorModifier();
                    cm.dataRef.index = Add(colors);
                    made = cm;
                    break;
                }
                case "NiPSysGravityModifier":
                {
                    var g = new NiPSysGravityModifier
                    {
                        gravityAxis = new nifly.Vector3(m.Axis.X, m.Axis.Y, m.Axis.Z), decay = m.Decay, strength = m.Strength,
                        forceType = m.ForceType == 1 ? ForceType.FORCE_SPHERICAL : ForceType.FORCE_PLANAR, turbulence = m.Turbulence, turbulenceScale = m.TurbulenceScale,
                    };
                    uint node = NodeAt(m.GravityObject);
                    g.gravityObjRef.index = node == uint.MaxValue ? rootId : node;
                    made = g;
                    break;
                }
                case "NiPSysDragModifier":
                {
                    var d = new NiPSysDragModifier { dragAxis = new nifly.Vector3(m.Axis.X, m.Axis.Y, m.Axis.Z), percentage = m.DragPercent, range = m.DragRange, rangeFalloff = m.DragFalloff };
                    d.parentRef.index = rootId;
                    made = d;
                    break;
                }
                case "NiPSysRotationModifier": made = new NiPSysRotationModifier { initialSpeed = 0 }; break;
                case "NiPSysPositionModifier": made = new NiPSysPositionModifier(); break;
                case "NiPSysBoundUpdateModifier": made = new NiPSysBoundUpdateModifier(); break;
            }
            if (made is null) continue;
            made.name = new NiStringRef(m.Name.Length > 0 ? m.Name : m.Type);
            made.order = m.Order;
            made.isActive = m.Active;
            if (made is NiPSysEmitter) emitterName = made.name.get();
            list.Add(made);
        }
        // Skyrim's vanilla systems always end with position and bounds.
        if (!list.Any(x => x is NiPSysPositionModifier)) list.Add(new NiPSysPositionModifier { name = new NiStringRef("NiPSysPositionModifier"), order = 6000, isActive = true });
        if (!list.Any(x => x is NiPSysBoundUpdateModifier)) list.Add(new NiPSysBoundUpdateModifier { name = new NiStringRef("NiPSysBoundUpdateModifier"), order = 7000, isActive = true });
        list.Sort((x, y) => x.order.CompareTo(y.order));

        // The system, its modifiers and controllers, numbered ahead so they can point at each other.
        uint psysId = hdr.GetNumBlocks();
        uint firstMod = psysId + 1;
        uint emitCtlrId = firstMod + (uint)list.Count, updateCtlrId = emitCtlrId + 1, rateId = updateCtlrId + 1, visId = rateId + 1, visDataId = visId + 1;
        var system = new NiParticleSystem
        {
            name = new NiStringRef(ps.Name.Length > 0 ? ps.Name : "Particles"), flags = 524302, isWorldSpace = ps.WorldSpace,
            vertFlags1 = 81, vertFlags4 = 4, vertFlags6 = 32, vertFlags7 = 64, vertFlags8 = 8,
        };
        system.transform = ToMat(world(ps.Index));
        system.dataRef.index = dataId;
        system.psysDataRef.index = dataId;
        system.shaderPropertyRef.index = shaderId;
        system.alphaPropertyRef.index = alphaId;
        system.controllerRef.index = emitCtlrId;
        for (int i = 0; i < list.Count; i++) system.modifierRefs.AddBlockRef(firstMod + (uint)i);
        if (Add(system) != psysId) throw new InvalidOperationException("block numbering");
        foreach (var mod in list)
        {
            mod.targetRef.index = psysId;
            Add(mod);
        }
        // Emission: looping on its own (active 0x8, loop, Skyrim's 0x40), or blended from the sequences that drive it
        // (vanilla's 0x68, with blend interpolators the manager sets).
        float stop = Math.Max(ctlr.Stop, ctlr.Start + 1);
        var emitCtlr = new NiPSysEmitterCtlr { flags = (ushort)(driven ? 0x68 : 0x48), frequency = 1, phase = 0, startTime = ctlr.Start, stopTime = stop, modifierName = new NiStringRef(emitterName) };
        emitCtlr.targetRef.index = psysId;
        emitCtlr.nextControllerRef.index = updateCtlrId;
        emitCtlr.interpolatorRef.index = rateId;
        emitCtlr.visInterpolatorRef.index = visId;
        Add(emitCtlr);
        var update = new NiPSysUpdateCtlr { flags = 0x48, frequency = 1, startTime = 0, stopTime = 0 };
        update.targetRef.index = psysId;
        Add(update);
        if (driven)
        {
            // As vanilla's: manager controlled, two slots, no single interpolator yet.
            static void Blend(NiBlendInterpolator b) { b.flags = InterpBlendFlags.INTERP_BLEND_MANAGER_CONTROLLED; b.arraySize = 2; b.weightThreshold = 0; b.singleIndex = 255; b.highPriority = (char)0xFFFD; b.nextHighPriority = (char)0xFFFD; b.singleTime = float.MinValue; }
            var blendRate = new NiBlendFloatInterpolator { value = float.MinValue }; Blend(blendRate);
            var blendVis = new NiBlendBoolInterpolator { value = true }; Blend(blendVis);
            Add(blendRate);
            Add(blendVis);
        }
        else
        {
            Add(new NiFloatInterpolator { floatValue = rate });
            var vis = new NiBoolInterpolator { boolValue = (byte)(emits ? 1 : 0) };
            vis.dataRef.index = visDataId;
            Add(vis);
            var visData = new NiBoolData();
            visData.data.SetInterpolationType(NiKeyType.CONST_KEY);
            NifKeys.Add(visData.data, ctlr.Start, emits);
            Add(visData);
            if (!emits) Notes.Add($"model {model}: particles {ps.Name} never emit (Oblivion's own emitter is off)");
        }
        nif.SetParentNode(hdr.GetBlockById(psysId), root);
        return new Emitting(system.name.get(), emitCtlrId, emitterName, driven);
    }
}
