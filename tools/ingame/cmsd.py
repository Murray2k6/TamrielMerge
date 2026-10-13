"""Reads the Havok collision of Skyrim SE NIFs (bhkCompressedMeshShapeData) and prints its slopes by area and, for
stairs, its shape along the climb (user, 2026-10-09: "find skyrims stairs and see how they work").
usage: python cmsd.py file.nif [...]"""
import math, struct, sys

HK = 69.99125


class R:
    def __init__(self, b, p=0): self.b, self.p = b, p
    def u8(self): v = self.b[self.p]; self.p += 1; return v
    def u16(self): v = struct.unpack_from("<H", self.b, self.p)[0]; self.p += 2; return v
    def u32(self): v = struct.unpack_from("<I", self.b, self.p)[0]; self.p += 4; return v
    def f32(self): v = struct.unpack_from("<f", self.b, self.p)[0]; self.p += 4; return v
    def v4(self): v = struct.unpack_from("<4f", self.b, self.p); self.p += 16; return v
    def sstr(self): n = self.u8(); s = self.b[self.p:self.p + n]; self.p += n; return s
    def istr(self): n = self.u32(); s = self.b[self.p:self.p + n]; self.p += n; return s


def blocks(path):
    b = open(path, "rb").read()
    r = R(b, b.index(b"\n") + 1)
    version = r.u32(); r.u8(); user = r.u32(); n = r.u32()
    bs = r.u32(); r.sstr();
    if bs > 130: r.u32()
    r.sstr(); r.sstr()
    if bs == 130: r.sstr()
    ntypes = r.u16(); types = [r.istr().decode() for _ in range(ntypes)]
    index = [r.u16() for _ in range(n)]
    sizes = [r.u32() for _ in range(n)]
    nstr = r.u32(); r.u32()
    for _ in range(nstr): r.istr()
    ngroups = r.u32()
    for _ in range(ngroups): r.u32()
    out = []
    for i in range(n):
        out.append((types[index[i] & 0x7FFF], b[r.p:r.p + sizes[i]]))
        r.p += sizes[i]
    return out


def triangles(data):
    r = R(data)
    r.u32(); r.u32(); r.u32(); r.u32()
    error = r.f32(); r.v4(); r.v4(); r.u8(); r.u8()
    for _ in range(3):
        n = r.u32(); r.p += 4 * n
    n = r.u32(); r.p += 8 * n          # chunk materials
    r.u32()                            # named materials
    n = r.u32(); transforms = [(r.v4(), r.v4()) for _ in range(n)]
    n = r.u32(); big = [r.v4() for _ in range(n)]
    n = r.u32(); bigtris = []
    for _ in range(n):
        a, b_, c = r.u16(), r.u16(), r.u16(); r.u32(); r.u16(); bigtris.append((a, b_, c))
    tris = []
    for a, b_, c in bigtris:
        if max(a, b_, c) < len(big): tris.append(tuple(tuple(x * HK for x in big[i][:3]) for i in (a, b_, c)))
    n = r.u32()
    for _ in range(n):
        t = r.v4(); r.u32(); r.u16(); ti = r.u16()
        nv = r.u32(); raw = [r.u16() for _ in range(nv)]
        ni = r.u32(); idx = [r.u16() for _ in range(ni)]
        ns = r.u32(); strips = [r.u16() for _ in range(ns)]
        nw = r.u32(); r.p += 2 * nw
        (tx, ty, tz, _), (qx, qy, qz, qw) = transforms[ti] if ti < len(transforms) else ((0, 0, 0, 0), (0, 0, 0, 1))
        verts = []
        for k in range(0, len(raw) - 2, 3):
            x, y, z = t[0] + raw[k] * error, t[1] + raw[k + 1] * error, t[2] + raw[k + 2] * error
            # rotate by the quaternion, then translate
            ix = qw * x + qy * z - qz * y; iy = qw * y + qz * x - qx * z; iz = qw * z + qx * y - qy * x; iw = -qx * x - qy * y - qz * z
            rx = ix * qw + iw * -qx + iy * -qz - iz * -qy
            ry = iy * qw + iw * -qy + iz * -qx - ix * -qz
            rz = iz * qw + iw * -qz + ix * -qy - iy * -qx
            verts.append(((rx + tx) * HK, (ry + ty) * HK, (rz + tz) * HK))
        at = 0
        for ln in strips:
            for k in range(ln - 2):
                i = (idx[at + k], idx[at + k + 1], idx[at + k + 2])
                if max(i) < len(verts): tris.append(tuple(verts[j] for j in i))
            at += ln
        for k in range(at, len(idx) - 2, 3):
            i = (idx[k], idx[k + 1], idx[k + 2])
            if max(i) < len(verts): tris.append(tuple(verts[j] for j in i))
    return tris


def analyse(path):
    tris = []
    for kind, data in blocks(path):
        if kind == "bhkCompressedMeshShapeData":
            tris += triangles(data)
    print("==", path.replace("\\", "/").split("/")[-1], f"{len(tris)} collision triangles")
    if not tris:
        return
    slopes = {}
    total = 0
    for a, b, c in tris:
        u = [b[i] - a[i] for i in range(3)]; v = [c[i] - a[i] for i in range(3)]
        n = (u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0])
        area = math.sqrt(sum(x * x for x in n)) / 2
        if area < 1e-3: continue
        deg = math.degrees(math.acos(min(1, abs(n[2]) / (2 * area))))
        slopes[int(deg // 5) * 5] = slopes.get(int(deg // 5) * 5, 0) + area
        total += area
    print("  slope by area:", ", ".join(f"{k}-{k + 4} deg {v * 100 / total:.0f}%" for k, v in sorted(slopes.items()) if v / total > 0.02))
    # Sloped walkable faces (10-60 deg, facing up): their steepness, the ramps of a staircase.
    ramps = {}
    for a, b, c in tris:
        u = [b[i] - a[i] for i in range(3)]; v = [c[i] - a[i] for i in range(3)]
        n = (u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0])
        area = math.sqrt(sum(x * x for x in n)) / 2
        if area < 1: continue
        deg = math.degrees(math.acos(min(1, abs(n[2]) / (2 * area))))
        if 10 <= deg <= 60:
            ramps[round(deg)] = ramps.get(round(deg), 0) + area
    if ramps:
        top = sorted(ramps.items(), key=lambda kv: -kv[1])[:5]
        print("  ramp faces (10-60 deg) by area:", ", ".join(f"{d} deg {a:.0f}" for d, a in top))


for f in sys.argv[1:]:
    try:
        analyse(f)
    except Exception as e:
        print("==", f, "failed:", e)
