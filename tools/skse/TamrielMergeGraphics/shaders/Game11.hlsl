// The Direct3D 11 side (shader model 5, drawn inside the game's frame): one full-screen triangle; the inputs the ray
// tracing reads, copied into textures shared with Direct3D 12; the ray tracing's results put into Skyrim's picture before
// its tonemapping; and the sharpening after DLSS. TamrielMerge's own (no other mod's code).
#include "../RtShared.h"

void FullscreenVS(uint id : SV_VertexID, out float4 pos : SV_Position)
{
    float2 p = float2((id << 1) & 2, id & 2);
    pos = float4(p * float2(2, -2) + float2(-1, 1), 0, 1);
}

// ---- Prepare: the depth (as floats), the motion vectors and the lit picture into the shared textures ----
Texture2D<float> GameDepth : register(t0);
Texture2D<float2> GameMotion : register(t1);
Texture2D<float4> GameHdr : register(t2);

struct Prepared { float depth : SV_Target0; float2 motion : SV_Target1; float4 hdr : SV_Target2; };

Prepared PreparePS(float4 pos : SV_Position)
{
    int3 q = int3(pos.xy, 0);
    Prepared o;
    o.depth = GameDepth.Load(q);
    o.motion = GameMotion.Load(q);
    o.hdr = float4(GameHdr.Load(q).rgb, 1);
    return o;
}

// ---- Composite: shadows and occlusion darken, bounce light and reflections add, before tonemapping ----
cbuffer CompositeCB : register(b0) { CompositeConstants K; }
Texture2D<float4> SharedHdr : register(t0);
Texture2D<float4> ResA : register(t1);
Texture2D<float4> ResB : register(t2);
Texture2D<float4> ResC : register(t3);
Texture2D<float> SharedDepth : register(t4);

float ViewZ(float d) { return K.depth.z / (K.depth.y - d * K.depth.w); }

float4 CompositePS(float4 pos : SV_Position) : SV_Target
{
    int2 q = int2(pos.xy);
    float3 c = SharedHdr.Load(int3(q, 0)).rgb;
    float d = SharedDepth.Load(int3(q, 0));
    if (d >= 0.999999) return float4(c, 1);
    float z = ViewZ(d);

    // The traced results (at their own, maybe lower, resolution): four texels, weighted by distance and by depth.
    float2 g = (float2(q) + 0.5) * K.rt.xy * K.drawn.zw - 0.5;
    int2 g0 = int2(floor(g));
    float2 f = g - float2(g0);
    float4 a = 0, b = 0, r = 0; float wsum = 0;
    [unroll] for (int i = 0; i < 4; ++i)
    {
        int2 o = int2(i & 1, i >> 1);
        int2 p = clamp(g0 + o, int2(0, 0), int2(K.rt.xy) - 1);
        float4 ta = ResA.Load(int3(p, 0));
        float w = (o.x ? f.x : 1.0 - f.x) * (o.y ? f.y : 1.0 - f.y) + 1e-4;
        w *= ta.z > 0.0 ? exp(-abs(ta.z - z) / (0.02 * z + 1.0)) : 0.0;
        a += ta * w; b += ResB.Load(int3(p, 0)) * w; r += ResC.Load(int3(p, 0)) * w; wsum += w;
    }
    if (wsum < 1e-5) return float4(c, 1);   // nothing traced near this depth: left as the game drew it
    a /= wsum; b /= wsum; r /= wsum;

    if (K.flags.x == 1) return float4(a.xxx, 1);
    if (K.flags.x == 2) return float4(a.yyy, 1);
    if (K.flags.x == 3) return float4(b.rgb, 1);
    if (K.flags.x == 4) return float4(r.rgb * r.w, 1);

    // Skyrim lights a surface in one pass, so the sun's part of its light isn't kept apart: shadows take up to about
    // half of it away, occlusion up to all of the strength set.
    float shadow = 1.0 - K.strength.x * (1.0 - a.x) * 0.5 * (K.flags.y ? 1.0 : 0.0);
    float occlusion = 1.0 - K.strength.y * (1.0 - a.y);
    // A colour of the surface (its hue, mid brightness) for the light it bounces back.
    float peak = max(max(c.r, c.g), c.b);
    float3 albedo = peak > 1e-4 ? saturate(c / peak) * 0.5 : 0.25;
    float3 lit = c * shadow * occlusion + K.strength.z * 0.6 * b.rgb * albedo * occlusion;
    lit = lerp(lit, r.rgb, saturate(r.w * K.strength.w * 0.35));
    return float4(lit, 1);
}

// Debug view 5: the normals the depth buffer gives (checks the camera's reconstruction by eye).
float4 NormalsPS(float4 pos : SV_Position) : SV_Target
{
    int2 q = int2(pos.xy);
    float d = SharedDepth.Load(int3(q, 0));
    if (d >= 0.999999) return float4(0, 0, 0, 1);
    float2 uv = (float2(q) + 0.5) * K.drawn.zw;
    float3 dir = K.forward.xyz + lerp(K.frustum.x, K.frustum.y, uv.x) * K.right.xyz + lerp(K.frustum.z, K.frustum.w, uv.y) * K.up.xyz;
    float3 p = dir * ViewZ(d);
    float3 dx = ddx(p), dy = ddy(p);
    float3 n = normalize(cross(dy, dx));
    if (dot(n, p) > 0) n = -n;
    return float4(n * 0.5 + 0.5, 1);
}

// ---- Sharpening after DLSS (DLSS 2.5.1 and later have none of their own) ----
// Each pixel pushed away from its four neighbours' average, by less where the neighbourhood already has strong
// contrast, and never past the neighbourhood's own darkest and brightest (no halos).
cbuffer SharpenCB : register(b1) { SharpenConstants S; }
Texture2D<float4> Source : register(t0);

float4 SharpenPS(float4 pos : SV_Position) : SV_Target
{
    int2 q = int2(pos.xy);
    int2 hi = int2(S.size.xy) - 1;
    float4 c = Source.Load(int3(q, 0));
    float3 n = Source.Load(int3(clamp(q + int2(0, -1), 0, hi), 0)).rgb;
    float3 s = Source.Load(int3(clamp(q + int2(0, 1), 0, hi), 0)).rgb;
    float3 e = Source.Load(int3(clamp(q + int2(1, 0), 0, hi), 0)).rgb;
    float3 w = Source.Load(int3(clamp(q + int2(-1, 0), 0, hi), 0)).rgb;
    float3 mn = min(c.rgb, min(min(n, s), min(e, w)));
    float3 mx = max(c.rgb, max(max(n, s), max(e, w)));
    float3 avg = (n + s + e + w) * 0.25;
    float contrast = dot(mx - mn, float3(0.2126, 0.7152, 0.0722));
    float amount = S.amount.x * 2.0 * (1.0 - saturate(contrast * 1.5));
    float3 r = clamp(c.rgb + (c.rgb - avg) * amount, mn, mx);
    return float4(r, c.a);
}
