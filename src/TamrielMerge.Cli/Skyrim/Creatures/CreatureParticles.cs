using TamrielMerge.Morrowind;

namespace TamrielMerge.Skyrim.Creatures;

/// <summary>
/// Whether a built Morrowind creature's particle systems are Morrowind's (user, 2026-10-21: "we need a particle tool then
/// as well"): each of the model's particle controllers against the system written into the Skyrim body, read back from
/// the file: there at all, under the bone its particles' node became, its emitter (a particle array's from the creature's
/// own mesh, else a box of Morrowind's size), speed, declination, planar angle and their variations, lifetime, size,
/// birth rate, colour, cycle, emit window, texture, and each Morrowind modifier as its Skyrim one (a modifier with none
/// is an issue, never quietly dropped). `creature-particles`.
/// </summary>
public static class CreatureParticles
{
    public sealed record Report(int Morrowind, int Skyrim, int Issues, List<string> Lines);

    public static Report Check(Tes3Data data, string model, string dataDir, string id)
    {
        var lines = new List<string>();
        int issues = 0;
        void Issue(string s) { issues++; lines.Add("  ISSUE " + s); }
        string mp = model.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? model : @"meshes\" + model;
        var mw = Nif4.Read(data.Read(mp) ?? throw new FileNotFoundException(mp), mp);
        var controllers = mw.Blocks.OfType<NiParticleSystemController>().ToList();

        using var nif = new nifly.NifFile();
        nif.Load(Path.Combine(dataDir, CreatureRig.MeshFolder(id), id + ".nif"));
        var hdr = nif.GetHeader();
        var systems = new Dictionary<string, nifly.NiParticleSystem>(StringComparer.Ordinal);
        for (uint i = 0; i < hdr.GetNumBlocks(); i++)
            if (hdr.GetBlockById(i) is nifly.NiParticleSystem ps) systems.TryAdd(ps.name.get(), ps);

        foreach (var c in controllers)
        {
            if (mw.Get<NiParticles>(c.Target) is not { } particles) { Issue($"controller {c.Index} drives no particle system"); continue; }
            string name = MwVfx.ParticleName(particles, creature: true);
            if (!systems.TryGetValue(name, out var ps)) { Issue($"{name}: not in the body"); continue; }
            var parts = new List<string>();
            bool ok = true;
            void Same(string what, float mwValue, float skValue, float tol = 1e-4f)
            {
                if (MathF.Abs(mwValue - skValue) > tol * MathF.Max(1, MathF.Abs(mwValue))) { Issue($"{name}: {what} Morrowind {mwValue}, Skyrim {skValue}"); ok = false; }
            }
            // Where it hangs.
            string? mwParent = mw.Blocks.OfType<NiNode>().FirstOrDefault(n => n.Children.Contains(particles.Index))?.Name;
            string? skParent = nif.GetParentNode(ps)?.name.get();
            if (mwParent is not null && MwCreatureSource.BoneName(mwParent) != System.Text.RegularExpressions.Regex.Replace(skParent ?? "", @" \[vis \d+\]$", "")) { Issue($"{name}: under {skParent}, Morrowind's under {mwParent}"); ok = false; }
            // Its modifiers, read back.
            using var mods = new nifly.vectoruint32();
            ps.modifierRefs.GetIndices(mods);
            var modBlocks = mods.Select(m => hdr.GetBlockById(m)).ToList();
            var emitter = modBlocks.OfType<nifly.NiPSysEmitter>().FirstOrDefault();
            if (emitter is null) { Issue($"{name}: no emitter"); continue; }
            bool wantMesh = c.Type == "NiBSPArrayController";
            bool isMesh = emitter is nifly.NiPSysMeshEmitter;
            if (wantMesh != isMesh) { Issue($"{name}: {(isMesh ? "mesh" : "box")} emitter for Morrowind's {(wantMesh ? "particle array" : "spray")}"); ok = false; }
            Same("speed", c.Speed, emitter.speed); Same("speed variation", c.SpeedVariation, emitter.speedVariation);
            Same("declination", c.Declination, emitter.declination); Same("declination variation", c.DeclinationVariation, emitter.declinationVariation);
            Same("planar angle", c.PlanarAngle, emitter.planarAngle); Same("planar angle variation", c.PlanarAngleVariation, emitter.planarAngleVariation);
            Same("lifetime", c.Lifetime, emitter.lifeSpan); Same("lifetime variation", c.LifetimeVariation, emitter.lifeSpanVariation);
            Same("size", c.InitialSize, emitter.radius);
            Same("colour red", c.InitialColor.X, emitter.color.r); Same("colour green", c.InitialColor.Y, emitter.color.g); Same("colour blue", c.InitialColor.Z, emitter.color.b); Same("colour alpha", c.InitialColor.W, emitter.color.a);
            if (emitter is nifly.NiPSysBoxEmitter box)
            {
                Same("box width", c.EmitterDimensions.X * 2, box.width); Same("box height", c.EmitterDimensions.Y * 2, box.height); Same("box depth", c.EmitterDimensions.Z * 2, box.depth);
            }
            string emitsFrom = isMesh ? "from its mesh" : "box";
            if (emitter is nifly.NiPSysMeshEmitter mesh)
            {
                using var meshes = new nifly.vectoruint32();
                mesh.meshRefs.GetIndices(meshes);
                if (meshes.Count == 0) { Issue($"{name}: mesh emitter with no mesh"); ok = false; }
                else if (MwCreatureSource.EmitsAtVertex(c))
                {
                    // At the vertices of the first geometry under its emitter.
                    var first = MwCreatureSource.FirstGeometry(mw, c.Emitter);
                    string wantShape = first is null ? "" : CreatureRig.FileSafe($"{id}_{(first.Name.Length > 0 ? first.Name : $"shape{first.Index}")}");
                    string got = (hdr.GetBlockById(meshes[0]) as nifly.NiAVObject)?.name.get() ?? "";
                    if (meshes.Count != 1 || got != wantShape) { Issue($"{name}: emits from {got} ({meshes.Count} meshes), Morrowind from {wantShape}'s vertices"); ok = false; }
                    else emitsFrom = $"at {first!.Name.Trim()}'s vertices";
                }
                else
                {
                    // Along its emitter's bones: its own emitter mesh, a vertex per point, heading off each vertex's normal.
                    var points = MwCreatureSource.EmitterPoints(mw, MwCreatureSource.SceneParents(mw), c);
                    string wantMeshName = CreatureRig.FileSafe($"{id}_{mw.Get<NiObjectNET>(c.Emitter)?.Name} #{c.Index} [emit]");
                    var shape = meshes.Count == 1 ? hdr.GetBlockById(meshes[0]) as nifly.NiShape : null;
                    string got = shape?.name.get() ?? "";
                    int verts = shape is null ? 0 : nif.GetVertsForShape(shape)?.Count ?? 0;
                    if (got != wantMeshName) { Issue($"{name}: emits from {got}, expected its emitter mesh {wantMeshName}"); ok = false; }
                    else if (verts < points.Count || verts > points.Count + 2) { Issue($"{name}: emitter mesh has {verts} vertices for {points.Count} points"); ok = false; }
                    else if (mesh.velocityType != nifly.VelocityType.VELOCITY_USE_NORMALS) { Issue($"{name}: emits along {mesh.velocityType}, not each point's node's +Z (its normal)"); ok = false; }
                    else emitsFrom = $"along {points.Select(p => p.Node).Distinct().Count()} bones of {mw.Get<NiObjectNET>(c.Emitter)?.Name}, {points.Count} points";
                }
            }
            // Each Morrowind modifier as its Skyrim one.
            var want = new List<string>();
            for (int m = c.Modifier, guard = 0; m >= 0 && guard < 16; guard++)
            {
                var mod = mw.Get<NiParticleModifier>(m);
                if (mod is null) break;
                string? sk = mod switch
                {
                    NiParticleGrowFade => "NiPSysGrowFadeModifier",
                    NiParticleColorModifier => "NiPSysColorModifier",
                    NiGravity => "NiPSysGravityModifier",
                    NiParticleRotation => "NiPSysRotationModifier",
                    _ => null,
                };
                if (sk is null) { Issue($"{name}: Morrowind's {mod.Type} has no Skyrim counterpart here"); ok = false; }
                else want.Add(sk);
                m = mod.Next;
            }
            var have = modBlocks.Select(b => b.GetBlockName()).ToList();
            foreach (var w in want.Where(w => !have.Contains(w))) { Issue($"{name}: no {w}"); ok = false; }
            // The emitter controller: cycle, times, rate.
            var ctlr = hdr.GetBlockById(ps.controllerRef.index) as nifly.NiPSysEmitterCtlr;
            if (ctlr is null) { Issue($"{name}: no emitter controller"); continue; }
            if ((ctlr.flags & 0x06) != (c.Flags & 0x06)) { Issue($"{name}: cycle {(ctlr.flags >> 1) & 3}, Morrowind's {(c.Flags >> 1) & 3}"); ok = false; }
            Same("start", c.Start, ctlr.startTime); Same("stop", c.Stop, ctlr.stopTime); Same("frequency", c.Frequency, ctlr.frequency);
            int max = Math.Max(1, Math.Max(c.NumParticles, mw.Get<NiGeometryData>(particles.Data)?.Vertices.Length ?? 0));
            float rate = c.UseBirthRate ? c.BirthRate : max / Math.Max(0.05f, c.Lifetime + c.LifetimeVariation / 2);
            if (hdr.GetBlockById(ctlr.interpolatorRef.index) is nifly.NiFloatInterpolator fi) Same("birth rate", rate, fi.floatValue);
            else { Issue($"{name}: no birth rate"); ok = false; }
            // Its texture.
            string? texture = null;
            foreach (int pi in particles.Properties)
                if (mw.Get<NiTexturingProperty>(pi)?.Base is { } bt && mw.Get<NiSourceTexture>(bt.Source)?.FileName is { } file) texture = file;
            string skTex = hdr.GetBlockById(ps.shaderPropertyRef.index) is nifly.BSEffectShaderProperty sh ? sh.sourceTexture.get() : "";
            if (texture is not null && !File.Exists(Path.Combine(dataDir, skTex))) { Issue($"{name}: texture {skTex} (Morrowind's {texture}) not in the data"); ok = false; }
            if (ok) parts.Add($"{name}: as Morrowind's ({emitsFrom}, under {skParent}, {have.Count} modifiers, {rate:F1} a second, texture {Path.GetFileName(skTex)})");
            lines.AddRange(parts.Select(p => "  " + p));
        }
        foreach (var extra in systems.Keys.Where(k => controllers.All(c => mw.Get<NiParticles>(c.Target) is not { } p || MwVfx.ParticleName(p, creature: true) != k)))
            Issue($"{extra}: in the body but not one of Morrowind's");
        return new Report(controllers.Count, systems.Count, issues, lines);
    }
}
