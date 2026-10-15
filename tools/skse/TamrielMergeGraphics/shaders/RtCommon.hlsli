// Shared by the ray tracing's compute shaders (Direct3D 12, shader model 6.5): the constants, the depth buffer's
// positions, random numbers. TamrielMerge's own (no other mod's code).
#include "../RtShared.h"

ConstantBuffer<RtConstants> C : register(b0);

// The game's depth (standard D3D depth, copied as floats), in the drawn part's full-resolution pixels.
Texture2D<float> Depth : register(t2);

float ViewZ(float d) { return C.depth.z / (C.depth.y - d * C.depth.w); }

// The view direction through a point of the drawn picture (uv 0..1, y down), with its forward component 1.
float3 ViewDir(float2 uvN)
{
    return C.forward.xyz + lerp(C.frustum.x, C.frustum.y, uvN.x) * C.right.xyz + lerp(C.frustum.z, C.frustum.w, uvN.y) * C.up.xyz;
}

int2 ClampPixel(int2 q) { return clamp(q, int2(0, 0), int2(C.size.xy) - 1); }

float3 PositionAt(int2 q, out bool sky)
{
    q = ClampPixel(q);
    float d = Depth.Load(int3(q, 0));
    sky = d >= 0.999999;
    float2 uvN = (float2(q) + 0.5) / float2(C.size.xy);
    return ViewDir(uvN) * ViewZ(min(d, 0.999999));
}

// Surface normal from the depth buffer: of the two neighbours on each axis, the one nearer in depth (edges stay sharp).
float3 NormalAt(int2 q, float3 p)
{
    bool s;
    float3 px = PositionAt(q + int2(1, 0), s), nx = PositionAt(q - int2(1, 0), s);
    float3 py = PositionAt(q + int2(0, 1), s), ny = PositionAt(q - int2(0, 1), s);
    float3 f = C.forward.xyz;
    float3 dx = abs(dot(px - p, f)) < abs(dot(p - nx, f)) ? px - p : p - nx;
    float3 dy = abs(dot(py - p, f)) < abs(dot(p - ny, f)) ? py - p : p - ny;
    float3 n = cross(dy, dx);
    float len = length(n);
    n = len > 1e-12 ? n / len : -normalize(p);
    return dot(n, p) > 0 ? -n : n;     // facing the camera
}

uint Hash(uint x)
{
    x ^= x >> 16; x *= 0x7feb352du;
    x ^= x >> 15; x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

float Rand(inout uint s) { s = Hash(s); return float(s >> 8) * (1.0 / 16777216.0); }

void Basis(float3 n, out float3 t, out float3 b)
{
    float3 a = abs(n.z) < 0.999 ? float3(0, 0, 1) : float3(1, 0, 0);
    t = normalize(cross(a, n));
    b = cross(n, t);
}

// A direction around n, weighted by the cosine (diffuse light's own weighting).
float3 CosineDir(float3 n, float u1, float u2)
{
    float3 t, b;
    Basis(n, t, b);
    float r = sqrt(u1), phi = 6.28318531 * u2;
    return normalize(t * (r * cos(phi)) + b * (r * sin(phi)) + n * sqrt(max(0.0, 1.0 - u1)));
}
