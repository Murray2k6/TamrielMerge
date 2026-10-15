// Ray tracing, Direct3D 12 compute with inline ray queries (DXR 1.1, shader model 6.5). For each traced pixel, from the
// position the depth buffer gives: a ray to the sun (shadows), short rays around the normal (ambient occlusion), longer
// ones for one bounce of diffuse light, and a mirror ray (reflections). What a bounce or reflection ray hits is lit with
// the picture's own colour where that point is on the screen (the game's lighting, so no materials are needed).
// Every loop is bounded and every ray has a set length, so a frame's work can't run away.
// TamrielMerge's own (no other mod's code).
#include "RtCommon.hlsli"

RaytracingAccelerationStructure Scene : register(t0);
struct Capsule { float3 a; float radius; float3 b; float pad; };
StructuredBuffer<Capsule> Capsules : register(t1);
Texture2D<float4> Hdr : register(t4);

RWTexture2D<float4> OutA : register(u0);   // shadow, occlusion, view depth, 1
RWTexture2D<float4> OutB : register(u1);   // bounce light rgb, 1
RWTexture2D<float4> OutC : register(u2);   // reflection rgb, its weight (Fresnel, 0 when nothing was found)
RWByteAddressBuffer Stats : register(u3);  // the line-up check: samples, agree, hit nearer, hit farther or missed

static const uint MaskWorld = 0x01, MaskCapsules = 0x02;
static const uint MaxCandidates = 24;

// Where a ray first meets a capsule (t in the ray's units), or -1.
float CapsuleHit(float3 ro, float3 rd, Capsule c)
{
    float3 ba = c.b - c.a, oa = ro - c.a;
    float baba = dot(ba, ba), bard = dot(ba, rd), baoa = dot(ba, oa), rdoa = dot(rd, oa), oaoa = dot(oa, oa), rdrd = dot(rd, rd);
    float a = baba * rdrd - bard * bard;
    float b = baba * rdoa - baoa * bard;
    float cc = baba * oaoa - baoa * baoa - c.radius * c.radius * baba;
    float h = b * b - a * cc;
    if (h >= 0.0 && a > 1e-8)
    {
        float t = (-b - sqrt(h)) / a;
        float y = baoa + t * bard;
        if (y > 0.0 && y < baba) return t;
    }
    // The end caps (spheres).
    float best = -1.0;
    [unroll] for (int e = 0; e < 2; ++e)
    {
        float3 oc = e == 0 ? oa : ro - c.b;
        float bb = dot(rd, oc), c2 = dot(oc, oc) - c.radius * c.radius;
        float h2 = bb * bb - rdrd * c2;
        if (h2 > 0.0)
        {
            float t = (-bb - sqrt(h2)) / rdrd;
            if (t > 0.0 && (best < 0.0 || t < best)) best = t;
        }
    }
    return best;
}

// The first hit along a ray (or any hit, with firstHit), handling alpha-tested meshes (covering half of the time,
// varied by the seed, so they average out over frames to a partial shadow) and capsules.
bool Trace(float3 o, float3 d, float tmax, uint mask, bool firstHit, uint seed, out float tHit)
{
    RayQuery<RAY_FLAG_NONE> q;
    RayDesc r;
    r.Origin = o; r.Direction = d; r.TMin = 0.0; r.TMax = tmax;
    q.TraceRayInline(Scene, firstHit ? RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH : RAY_FLAG_NONE, mask, r);
    uint i = 0;
    [loop] for (; i < MaxCandidates; ++i)
    {
        if (!q.Proceed()) break;
        if (q.CandidateType() == CANDIDATE_NON_OPAQUE_TRIANGLE)
        {
            uint h = Hash(seed ^ (q.CandidatePrimitiveIndex() * 9781u) ^ (q.CandidateInstanceIndex() * 6271u));
            if ((h & 1u) == 0u) q.CommitNonOpaqueTriangleHit();
        }
        else
        {
            uint ci = q.CandidatePrimitiveIndex();
            if (ci < C.flags.x)
            {
                float t = CapsuleHit(q.CandidateObjectRayOrigin(), q.CandidateObjectRayDirection(), Capsules[ci]);
                if (t >= q.RayTMin() && t < q.CommittedRayT()) q.CommitProceduralPrimitiveHit(t);
            }
        }
    }
    if (i == MaxCandidates) q.Abort();
    if (q.CommittedStatus() != COMMITTED_NOTHING) { tHit = q.CommittedRayT(); return true; }
    tHit = tmax;
    return false;
}

bool Project(float3 p, out float2 uvN, out float z)
{
    z = dot(p, C.forward.xyz);
    uvN = 0;
    if (z < C.depth.x) return false;
    float x = dot(p, C.right.xyz) / z, y = dot(p, C.up.xyz) / z;
    uvN = float2((x - C.frustum.x) / (C.frustum.y - C.frustum.x), (C.frustum.z - y) / (C.frustum.z - C.frustum.w));
    return all(uvN >= 0.0) && all(uvN <= 1.0);
}

// The light leaving a point a ray hit, or the sky the ray went to: the picture's colour where that is on the screen.
float3 Radiance(float3 o, float3 d, bool hit, float t, out bool valid)
{
    valid = false;
    float2 uvN; float z;
    float3 p = hit ? o + d * t : d * 1.0e4;
    if (!Project(p, uvN, z)) return 0;
    int2 q = ClampPixel(int2(uvN * float2(C.size.xy)));
    float dz = Depth.Load(int3(q, 0));
    if (hit)
    {
        if (dz >= 0.999999 || abs(ViewZ(dz) - z) > 0.05 * z + 8.0) return 0;   // hidden on the screen: unknown
    }
    else if (dz < 0.999999) return 0;                                              // the sky is behind something there
    valid = true;
    float3 c = Hdr.Load(int3(q, 0)).rgb;
    float l = dot(c, float3(0.2126, 0.7152, 0.0722));
    return l > 4.0 ? c * (4.0 / l) : c;                                            // no fireflies from the sun's disc
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= C.size.zw)) return;
    int2 q = ClampPixel(int2((float2(id.xy) + 0.5) * float2(C.size.xy) / float2(C.size.zw)));
    bool sky;
    float3 p = PositionAt(q, sky);
    if (sky)
    {
        OutA[id.xy] = float4(1, 1, 0, 1);
        OutB[id.xy] = 0;
        OutC[id.xy] = 0;
        return;
    }
    float z = dot(p, C.forward.xyz);
    float3 n = NormalAt(q, p);
    float3 v = normalize(p);
    uint seed = Hash(id.x * 1973u + id.y * 9277u + 26699u) ^ Hash(C.frame.x * 747796405u + 1u);
    float3 o = p + n * (0.75 + 0.0015 * z);

    // The line-up check: a ray from the camera through this pixel should end where the depth buffer says.
    if (C.flags.y > 0 && (id.x % C.flags.y) == 0 && (id.y % C.flags.y) == 0)
    {
        float dist = length(p), t;
        bool hit = Trace(0, v, dist * 1.25 + 64.0, MaskWorld, false, seed, t);
        uint slot = !hit ? 12u : abs(t - dist) <= 0.03 * dist + 8.0 ? 4u : t < dist ? 8u : 12u;
        Stats.InterlockedAdd(0, 1);
        Stats.InterlockedAdd(slot, 1);
    }

    float shadow = 1.0;
    float3 sun = C.sun.xyz;
    if (C.sun.w > 0.0 && C.strength.x > 0.0 && dot(n, sun) > 0.0)
    {
        float3 t, b;
        Basis(sun, t, b);
        float3 d = normalize(sun + (t * (Rand(seed) - 0.5) + b * (Rand(seed) - 0.5)) * 0.02);   // the sun's disc, a little wide
        float th;
        shadow = Trace(o, d, C.dist.w, MaskWorld | MaskCapsules, true, seed, th) ? 0.0 : 1.0;
    }

    float ao = 1.0;
    if (C.strength.y > 0.0 && C.frame.z > 0)
    {
        float sum = 0.0;
        for (uint r = 0; r < C.frame.z; ++r)
        {
            float3 d = CosineDir(n, Rand(seed), Rand(seed));
            float th;
            bool hit = Trace(o, d, C.dist.x, MaskWorld | MaskCapsules, false, seed, th);
            sum += hit ? saturate(th / C.dist.x) : 1.0;
        }
        ao = sum / C.frame.z;
    }

    float3 bounce = 0;
    if (C.strength.z > 0.0 && C.frame.w > 0)
    {
        for (uint r = 0; r < C.frame.w; ++r)
        {
            float3 d = CosineDir(n, Rand(seed), Rand(seed));
            float th;
            bool hit = Trace(o, d, C.dist.y, MaskWorld | MaskCapsules, false, seed, th);
            bool valid;
            bounce += Radiance(o, d, hit, th, valid);
        }
        bounce /= C.frame.w;
    }

    float4 refl = 0;
    if (C.strength.w > 0.0)
    {
        float3 d = reflect(v, n);
        float th;
        bool hit = Trace(o, d, C.dist.z, MaskWorld | MaskCapsules, false, seed, th);
        bool valid;
        float3 c = Radiance(o, d, hit, th, valid);
        float f = 0.04 + 0.96 * pow(1.0 - saturate(dot(n, -v)), 5.0);
        refl = valid ? float4(c, f) : 0;
    }

    OutA[id.xy] = float4(shadow, ao, z, 1);
    OutB[id.xy] = float4(bounce, 1);
    OutC[id.xy] = refl;
}
