using nifly;

namespace TamrielMerge.Util;

/// <summary>Prints a NIF's block list with the fields that matter for static architecture meshes.</summary>
public static class NifDump
{
    public static void Dump(string path)
    {
        using var nif = new NifFile();
        if (nif.Load(path) != 0) { Console.WriteLine($"failed to load {path}"); return; }
        var hdr = nif.GetHeader();
        Console.WriteLine($"{Path.GetFileName(path)}: {hdr.GetVersion().GetVersionInfo()} blocks={hdr.GetNumBlocks()} valid={nif.IsValid()}");
        using var cache = new niflycpp.BlockCache(hdr);
        for (uint i = 0; i < hdr.GetNumBlocks(); i++)
        {
            string type = hdr.GetBlockById(i).GetBlockName();
            string detail = type switch
            {
                "BSXFlags" => $"integer={cache.EditableBlockById<BSXFlags>(i).integerData}",
                "BSBound" => $"name={cache.EditableBlockById<BSBound>(i).name.get()} center=({cache.EditableBlockById<BSBound>(i).center.x},{cache.EditableBlockById<BSBound>(i).center.y},{cache.EditableBlockById<BSBound>(i).center.z}) halfExtents=({cache.EditableBlockById<BSBound>(i).halfExtents.x},{cache.EditableBlockById<BSBound>(i).halfExtents.y},{cache.EditableBlockById<BSBound>(i).halfExtents.z})",
                "bhkCollisionObject" => $"flags={cache.EditableBlockById<bhkCollisionObject>(i).flags} target={cache.EditableBlockById<bhkCollisionObject>(i).targetRef.index} body={cache.EditableBlockById<bhkCollisionObject>(i).bodyRef.index}",
                "bhkRigidBody" or "bhkRigidBodyT" => RigidBody(cache.EditableBlockById<bhkRigidBody>(i)),
                "bhkMoppBvTreeShape" => "",
                "bhkCompressedMeshShape" => $"radius={cache.EditableBlockById<bhkCompressedMeshShape>(i).radius}",
                "bhkListShape" => ListShape(cache.EditableBlockById<bhkListShape>(i)),
                "bhkConvexVerticesShape" => $"verts={cache.EditableBlockById<bhkConvexVerticesShape>(i).verts.size()} normals={cache.EditableBlockById<bhkConvexVerticesShape>(i).normals.size()} radius={cache.EditableBlockById<bhkConvexVerticesShape>(i).radius} material={cache.EditableBlockById<bhkConvexVerticesShape>(i).GetMaterial()} {Prop("vertsProp", cache.EditableBlockById<bhkConvexVerticesShape>(i).vertsProp)} {Prop("normalsProp", cache.EditableBlockById<bhkConvexVerticesShape>(i).normalsProp)}",
                "BSLightingShaderProperty" => Shader(cache.EditableBlockById<BSLightingShaderProperty>(i)),
                "BSShaderTextureSet" => "textures=" + string.Join(" | ", cache.EditableBlockById<BSShaderTextureSet>(i).textures.items().Select(t => t.get()).Where(t => t.Length > 0)),
                "BSTriShape" => $"verts={cache.EditableBlockById<BSTriShape>(i).GetNumVertices()} tris={cache.EditableBlockById<BSTriShape>(i).GetNumTriangles()} flags={cache.EditableBlockById<BSTriShape>(i).flags}",
                "BSFadeNode" or "NiNode" => $"flags={cache.EditableBlockById<NiNode>(i).flags}",
                // The body parts a skin's partitions are (an armour addon shows the partitions of the slots it covers).
                "BSDismemberSkinInstance" => "partitions=" + string.Join(" ", cache.EditableBlockById<BSDismemberSkinInstance>(i).partitions.items().Select(p => $"{p.partID}/flags{(int)p.flags}")),
                _ => "",
            };
            Console.WriteLine($"  [{i}] {type} {detail}");
        }
    }

    static string Prop(string name, hkWorldObjCInfoProperty p) => $"{name}=({p.data},{p.size},0x{p.capacityAndFlags:X})";

    static string ListShape(bhkListShape l) =>
        $"children={l.subShapeRefs.GetSize()} material={l.GetMaterial()} {Prop("childShapeProp", l.childShapeProp)} {Prop("childFilterProp", l.childFilterProp)} filters={l.filters.size()} [{string.Join(" ", l.filters.items().Select(f => $"{f.layer}/{f.flagsAndParts}/{f.group}"))}]";

    static string RigidBody(bhkRigidBody rb) =>
        $"layer={rb.collisionFilter.layer} flagsAndParts={rb.collisionFilter.flagsAndParts} group={rb.collisionFilter.group} copyLayer={rb.collisionFilterCopy.layer} response={rb.collisionResponse} motion={rb.motionSystem} deact={rb.deactivatorType} solverDeact={rb.solverDeactivation} quality={rb.qualityType} mass={rb.mass} friction={rb.friction} restitution={rb.restitution} lindamp={rb.linearDamping} angdamp={rb.angularDamping} timeFactor={rb.timeFactor} gravity={rb.gravityFactor} maxLin={rb.maxLinearVelocity} maxAng={rb.maxAngularVelocity} penetration={rb.penetrationDepth} bodyFlags={rb.bodyFlags} unusedByte1={rb.unusedByte1} contactDelay={rb.processContactCallbackDelay} unkInt1={rb.unkInt1} rolling={rb.rollingFrictionMult} autoRemove={rb.autoRemoveLevel} respMod={rb.responseModifierFlag} shapeKeys={rb.numShapeKeysInContactPointProps} ppu={rb.forceCollideOntoPpu}";

    static string Shader(BSLightingShaderProperty s) =>
        $"type={s.GetShaderType()} flags1=0x{s.shaderFlags1:X8} flags2=0x{s.shaderFlags2:X8} glossiness={s.glossiness} specStrength={s.specularStrength} uvScale=({s.uvScale.u},{s.uvScale.v}) alpha={s.alpha}";
}
