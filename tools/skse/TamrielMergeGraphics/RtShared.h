// The constants shared by the ray tracing's C++ (RtCore.cpp) and its shaders (shaders\*.hlsl): one layout, so the two
// can't drift apart. Every member is 16 bytes (HLSL's constant packing).
#ifndef TM_RT_SHARED_H
#define TM_RT_SHARED_H

#ifdef __cplusplus
#include <cstdint>
namespace rt
{
    struct float4 { float x, y, z, w; };
    struct uint4 { std::uint32_t x, y, z, w; };
#endif

    // Ray tracing (Direct3D 12, compute). Positions are camera-relative world units (the camera at the origin, the
    // world's axes), so the TLAS and the rays keep float precision far from the world's centre.
    struct RtConstants
    {
        float4 forward;     // the camera's axes in world space; w unused
        float4 right;
        float4 up;
        float4 frustum;     // left, right, top, bottom of the view at distance 1 (top > 0 > bottom)
        float4 depth;       // near, far, near * far, far - near (standard D3D depth, not inverted)
        float4 sun;         // xyz towards the sun; w 1 when there is a sun to trace, else 0
        float4 dist;        // occlusion radius, bounce light distance, reflection distance, shadow distance
        float4 strength;    // shadows, occlusion, bounce light, reflections (0 skips that kind of ray)
        uint4 size;         // drawn width, height (full resolution); ray traced width, height
        float4 tex;         // the shared textures' width, height, 1 / width, 1 / height (full resolution)
        float4 rtTex;       // the ray traced textures' width, height, 1 / width, 1 / height
        uint4 frame;        // frame number, history usable (0 / 1), occlusion rays, bounce rays
        uint4 flags;        // capsule count, sample step of the line-up check, max history frames, unused
        float4 prevScale;   // last frame's traced width / height over the texture's (uv to texture), unused zw
    };

    // The composite (Direct3D 11, a pixel shader before Skyrim's tonemapping).
    struct CompositeConstants
    {
        float4 strength;    // shadows, occlusion, bounce light, reflections
        float4 drawn;       // drawn width, height, 1 / width, 1 / height
        float4 rt;          // traced width, height (of the drawn part), 1 / texture width, 1 / texture height
        float4 depth;       // as RtConstants.depth
        uint4 flags;        // debug view (0 the picture, 1 shadows, 2 occlusion, 3 bounce light, 4 reflections, 5 normals), sun, unused
        float4 forward;     // the camera (debug view of normals)
        float4 right;
        float4 up;
        float4 frustum;
    };

    // Sharpening after DLSS.
    struct SharpenConstants
    {
        float4 amount;      // strength, unused
        uint4 size;         // width, height of the region, unused
    };

#ifdef __cplusplus
    // One capsule (a person's or creature's limb), camera-relative; the shaders' Capsule (float3 a, radius, float3 b, pad).
    struct RtCapsule
    {
        float a[3]; float radius;
        float b[3]; float pad;
    };
}
#endif
#endif
