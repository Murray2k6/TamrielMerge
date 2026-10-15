// Ray tracing's denoising, Direct3D 12 compute: a temporal pass (each pixel's results blended with the last frame's at
// the place the motion vectors point to, unless the depth there differs: a newly seen surface starts again), then a
// small spatial pass (a 5 x 5 blur that keeps to surfaces of the same depth). TamrielMerge's own.
#include "RtCommon.hlsli"

Texture2D<float2> Motion : register(t3);   // the game's motion vectors, full resolution: previous = current + motion (uv)
Texture2D<float4> InA : register(t5);
Texture2D<float4> InB : register(t6);
Texture2D<float4> InC : register(t7);
Texture2D<float4> HistA : register(t8);
Texture2D<float4> HistB : register(t9);
Texture2D<float4> HistC : register(t10);
RWTexture2D<float4> OutA : register(u0);
RWTexture2D<float4> OutB : register(u1);
RWTexture2D<float4> OutC : register(u2);

[numthreads(8, 8, 1)]
void Temporal(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= C.size.zw)) return;
    float4 a = InA[id.xy], b = InB[id.xy], c = InC[id.xy];
    float z = a.z;
    bool usable = C.frame.y != 0 && z > 0.0;
    float4 ha = 0, hb = 0, hc = 0;
    if (usable)
    {
        float2 uvN = (float2(id.xy) + 0.5) / float2(C.size.zw);
        int2 q = ClampPixel(int2(uvN * float2(C.size.xy)));
        float2 prev = uvN + Motion.Load(int3(q, 0));
        // The last frame's traced part, in its texture's pixels.
        float2 prevSize = C.prevScale.xy * C.rtTex.xy;
        int2 hp = int2(prev * prevSize);
        usable = all(prev >= 0.0) && all(prev < 1.0) && all(hp < int2(prevSize));
        if (usable)
        {
            ha = HistA.Load(int3(hp, 0));
            usable = ha.z > 0.0 && abs(ha.z - z) <= 0.05 * z + 2.0;
            hb = HistB.Load(int3(hp, 0));
            hc = HistC.Load(int3(hp, 0));
        }
    }
    float count = usable ? min(ha.w + 1.0, float(C.flags.z)) : 1.0;
    float k = 1.0 / count;
    OutA[id.xy] = float4(lerp(ha.xy, a.xy, k), z, count);
    OutB[id.xy] = float4(lerp(hb.rgb, b.rgb, k), 1);
    OutC[id.xy] = lerp(hc, c, k);
}

[numthreads(8, 8, 1)]
void Spatial(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= C.size.zw)) return;
    float4 a0 = InA[id.xy];
    float z = a0.z;
    if (z <= 0.0) { OutA[id.xy] = a0; OutB[id.xy] = InB[id.xy]; OutC[id.xy] = InC[id.xy]; return; }
    float2 sumA = 0; float3 sumB = 0; float4 sumC = 0; float wsum = 0;
    // Fewer frames behind a pixel: a wider blur (up to 2 pixels); well-settled ones keep their detail.
    float spread = a0.w >= 8.0 ? 1.0 : 2.0;
    [unroll] for (int y = -2; y <= 2; ++y)
        [unroll] for (int x = -2; x <= 2; ++x)
        {
            int2 p = clamp(int2(id.xy) + int2(x, y) * int(spread), int2(0, 0), int2(C.size.zw) - 1);
            float4 a = InA[p];
            float w = exp(-0.3 * float(x * x + y * y)) * exp(-abs(a.z - z) / (0.02 * z + 1.0));
            w *= a.z > 0.0 ? 1.0 : 0.0;
            sumA += a.xy * w; sumB += InB[p].rgb * w; sumC += InC[p] * w; wsum += w;
        }
    wsum = max(wsum, 1e-6);
    OutA[id.xy] = float4(sumA / wsum, z, a0.w);
    OutB[id.xy] = float4(sumB / wsum, 1);
    OutC[id.xy] = sumC / wsum;
}
