"""Which texture files each material of an FBX uses (from the file's own connections, as Blender's importer missed
them for ship.fbx): python fbx_links.py ship.fbx -> JSON {material: {property: file}} on stdout."""
import json, struct, sys, zlib


def read_nodes(b, pos, end, wide):
    nodes = []
    while pos < end:
        if wide:
            end_off, nprops, plen = struct.unpack_from("<QQQ", b, pos); pos += 24
        else:
            end_off, nprops, plen = struct.unpack_from("<III", b, pos); pos += 12
        nlen = b[pos]; pos += 1
        if end_off == 0:
            break
        name = b[pos:pos + nlen].decode("ascii", "replace"); pos += nlen
        props = []
        p = pos
        for _ in range(nprops):
            t = chr(b[p]); p += 1
            if t in "YCIFDL":
                fmt = {"Y": "<h", "C": "<?", "I": "<i", "F": "<f", "D": "<d", "L": "<q"}[t]
                props.append(struct.unpack_from(fmt, b, p)[0]); p += struct.calcsize(fmt)
            elif t in "SR":
                n = struct.unpack_from("<I", b, p)[0]; p += 4
                raw = b[p:p + n]; p += n
                props.append(raw.decode("utf-8", "replace") if t == "S" else raw)
            else:  # arrays
                count, enc, clen = struct.unpack_from("<III", b, p); p += 12
                p += clen
                props.append(None)
        pos += plen
        children = read_nodes(b, pos, end_off, wide) if pos < end_off else []
        nodes.append((name, props, children))
        pos = end_off
    return nodes


def main(path):
    b = open(path, "rb").read()
    version = struct.unpack_from("<I", b, 23)[0]
    top = read_nodes(b, 27, len(b), version >= 7500)
    objects = next(c for n, p, c in top if n == "Objects")
    conns = next(c for n, p, c in top if n == "Connections")
    names, files = {}, {}
    for name, props, children in objects:
        oid = props[0]
        label = props[1].split("\x00")[0] if isinstance(props[1], str) else ""
        names[oid] = (name, label)
        if name == "Texture":
            for cn, cp, cc in children:
                if cn in ("RelativeFilename", "FileName") and cp:
                    files[oid] = cp[0].replace("\\", "/").split("/")[-1]
    # Texture -> Material, directly or through a LayeredTexture (Maya's export: Texture -OO-> LayeredTexture -OP-> Material).
    layered = {}
    for name, props, _ in conns:
        if props[0] == "OO" and names.get(props[1], ("",))[0] == "Texture" and names.get(props[2], ("",))[0] == "LayeredTexture":
            layered.setdefault(props[2], props[1])
    out = {}
    for name, props, _ in conns:
        if props[0] != "OP" or names.get(props[2], ("",))[0] != "Material":
            continue
        kind = names.get(props[1], ("",))[0]
        tex = props[1] if kind == "Texture" else layered.get(props[1]) if kind == "LayeredTexture" else None
        if tex is not None:
            out.setdefault(names[props[2]][1], {})[props[3]] = files.get(tex, names[tex][1])
    print(json.dumps(out, indent=1, sort_keys=True))


main(sys.argv[1])
