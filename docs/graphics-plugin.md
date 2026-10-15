# TamrielMergeGraphics (SKSE plugin)

User request (2026-10-08): "a plugin made to enable rt/pt and dlss with ui settings added to the game"; "starfield
uses these and i need the same" (Starfield's PC options: DLSS super resolution and DLAA, other upscalers, frame
generation, in the game's own settings); not Community Shaders.

Source: tools/skse/TamrielMergeGraphics (build.ps1: Visual Studio's C++ tools, Dear ImGui (MIT) and NVIDIA's DLSS SDK
(NVIDIA RTX SDKs licence) cloned into tools/skse/third_party, git-ignored). Shipped by every conversion
(AltStartBuilder.CopyEraPlugin): SKSE\Plugins\TamrielMergeGraphics.dll, and NVIDIA's runtime in
SKSE\Plugins\TamrielMergeGraphics\nvngx_dlss.dll (fetched by setup; in a folder of its own so SKSE doesn't probe it).

## Done (verified in game, 1.7.104, RTX 5060)

- Hooks IDXGISwapChain::Present through the swap chain's function table (a probe device of its own at load), so no
  game addresses are needed.
- The settings menu (F10; iMenuKey) drawn with ImGui over the game, sized to the screen: upscaler (Off, DLAA, DLSS
  Quality/Balanced/Performance/Ultra Performance), sharpness (coming), frame generation (not yet), ray-traced lighting with
  bounce light and reflection strength. Saved to SKSE\Plugins\TamrielMergeGraphics.ini when it closes (under MO2 that
  file lands in MO2's overwrite folder).
- Input: the game reads keyboard and mouse through DirectInput (its window gets almost no messages), so the menu key is
  polled each frame (F10 also arrives as WM_SYSKEYDOWN), and the menu's cursor and clicks come from the mouse's
  DirectInput data, which the game doesn't get while the menu is open (IDirectInputDevice8 GetDeviceState/Data
  hooked in the shared table).
- NVIDIA NGX started on the game's D3D11 device (project-ID init, custom engine): DLSS reported available.

- DLSS in place of Skyrim's TAA (verified in Riverwood: DLAA sharper than TAA; Quality clean while the camera
  turns, no ghosting):
  - The D3D11 immediate context keeps its function table inside the object (its first pointer points 8 bytes on) and
    d3d11 rewrites entries of it as it runs, so patched entries don't last. The context is pointed at a table of ours
    instead: one stub per entry jumping through d3d11's current entry (`jmp [rcx + 8 + 8i]`), with our PSSetShader (9),
    PSSetSamplers (10), DrawIndexed (12), Draw (13), DrawIndexedInstanced (20), DrawInstanced (21).
  - The TAA pass is found by its pixel shader's bytecode (FNV-1a 5915929377734644592, ISTemporalAA): t0 the tonemapped
    picture (RGBA8), t1 last frame's result, t2 the motion vectors (R16G16 float, screen fractions, prev = cur + mv),
    t3 the depth (R24G8, standard, not inverted); target 0 the result, target 1 the next history (two textures, swapped
    each frame).
  - The jitter: BSGraphics::State (Address Library ID 411479) + 0x44 / 0x48 hold 2 * jitter / screen width (height),
    the jitter taken from its 8-entry Halton table at + 0xD4 (base 2; base 3 at + 0xF4) by the frame count at + 0xD0.
    DLSS gets x = -0x44 * w / 2, y = +0x48 * h / 2 (checked: the other sign is blurrier).
  - DLAA: the TAA draw becomes a DLSS evaluation (in a clean ID3DDeviceContextState, the game's state put back after),
    its output copied into both of TAA's targets.
  - Upscaling (Quality 2/3, Balanced 0.58, Performance 1/2, Ultra Performance 1/3): the game's own dynamic resolution
    (State + 0x114 / 0x118 the width and height ratios, set as each frame ends) draws into the top-left part of its
    targets. TAA then works at that size, and a later pass, ISUpsampleDynamicResolution (shader 4172866439863931375),
    stretches the finished picture to the screen. So the TAA draw passes the picture through (DLSS does the temporal
    work), and the upscale pass becomes DLSS from the smaller part to the full target. The next frame's Halton entry is
    refilled from a longer sequence (8 / ratio^2 phases) spread over a whole render pixel. The game's mipmapped
    samplers are swapped for copies biased by log2(ratio) (+ fMipBiasOffset). The DLSS feature is made for the full
    size, so a drawn size changing between frames needs no new one.
  - DLSS needs Skyrim's TAA on (bUseTAA=1); without it the menu says so.
  - Developer switches in the ini: bCapture=1 (F11 writes every draw of two frames, the renderer state and the TAA
    shader's disassembly to TamrielMergeGraphics-capture.txt / -taa.txt), fJitterSignX/Y, fMotionScaleX/Y,
    fMipBiasOffset.

- Hardware ray tracing, stage 1 (user, 2026-10-08: "the game needs raytracing with its dlss", "skse can too"; staged
  DXR chosen; no Community Shaders, see CLAUDE.md). Verified in Riverwood:
  - The scene graph without a class library: the player via Address Library ID 403521 (form ID 0x14 checks it),
    its 3D at TESObjectREFR + 0x68 (loaded data) + 0x68, parents (NiAVObject + 0x30) up to `SceneGraph "WorldRoot Node"`.
    Types from MSVC RTTI (vtable - 8: complete object locator), cached per vtable. NiAVObject + 0x7C world transform
    (rotation 3x3 row-major, applied R * s * v + t: checked against the world bound at + 0xE4, error 0.0000),
    + 0xF4 flags (bit 0 hidden); NiNode + 0x110 children (NiTArray: data + 0x118, capacity + 0x120, size + 0x124);
    BSGeometry + 0x110 model bound, + 0x130 skin instance, + 0x138 renderer block { ID3D11Buffer* vertices,
    ID3D11Buffer* indices, vertex desc, refcount, CPU vertices, CPU 16-bit indices }; BSTriShape + 0x158 u16
    triangles, + 0x15A u16 vertices; vertices (desc & 0xF) * 4 bytes, position 3 floats at offset 0 (full precision
    in memory). 96% of meshes keep their CPU copies.
  - A Direct3D 12 device on the game's GPU (adapter LUID): RTX 5060 reports DXR tier 12 (1.2) and the game's
    device has shared fences (ID3D11Device5).
  - BLASes from the CPU copies (48 a frame, cached by mesh, dropped after 10 s unused), TLAS rebuilt each frame:
    Riverwood 7171 instances over 1375 BLASes (61 MB). Skinned meshes (people) not yet; instanced grass/dynamic shapes
    skipped. The first walk cost 39 ms a frame (RTTI per object); types are now cached per vtable (to measure).
  - On 2026-10-09 it hung the GPU (a TDR, LiveKernelEvent 141) on a world load, at 15 fps, and was locked off
    (bRayTracingDevelop). Its likely causes, found in its code: it waited on the CPU for the last frame's GPU work every
    frame (the 15 fps), and that wait gave up after 2 s and carried on as if the work were done: it reset a command
    allocator the GPU was still running, rewrote the TLAS's instance buffer under it and released uploads, scratch and
    BLASes it was still reading. A world load (the game's own GPU work heavy) is when such a wait times out. It also
    made committed resources for every mesh every frame, and never checked for a removed device. All replaced below.

## Built 2026-10-15, not yet tested in game (user: "complete the raytracer and dlss")

Source: RtCore.h / RtCore.cpp (the ray tracing, knowing nothing of Skyrim), RtShared.h (constants shared with the
shaders), shaders\RtTrace.hlsl, RtDenoise.hlsl, RtCommon.hlsli (compute, shader model 6.5, compiled by the Windows
SDK's dxc) and Game11.hlsl (the Direct3D 11 passes, fxc), test\RtHarness.cpp. build.ps1 compiles the shaders into
gen\*.h (not kept in git) and embeds them; `build.ps1 -Harness` also builds the test.

All of it is our own, written from Microsoft's Direct3D 12 / DXR documentation and NVIDIA's DLSS SDK documentation (the
DLSS SDK under NVIDIA's RTX SDKs licence, as before); no other mod's code or design (no Community Shaders, ever). Facts
about the game come from our own captures and from reading SkyrimSE.exe 1.7.104 (addresses below).

### Ray tracing, made safe (RtCore.h's header lists it)

- The CPU never waits on the GPU in a frame. Four frame contexts (command allocator, two command lists, 12 MB of upload
  memory, a readback buffer) are reused only once the GPU is done with them; if it isn't, that frame isn't ray traced
  (the picture is left as the game drew it). Waits with a time limit happen only when textures are recreated (the
  first frame, a new resolution or quality), and failing one turns ray tracing off.
- Nothing the GPU may still read is freed or rewritten: releases wait for the fence value of the last frame that used
  them; BLASes are freed after 600 frames unused and only once their last frame is done.
- Bounded work: at most 24 BLAS builds and 262,144 triangles a frame, 40,000 instances, 4,096 capsules, a fixed number of
  rays per pixel and ray lengths per quality, 24 candidates per ray at most (alpha-tested leaves, capsules). What every
  frame needs (constants, instances, capsules, the TLAS's scratch) is set aside before new meshes take the rest of the
  frame's upload and scratch memory, so streaming meshes in (entering a city) never costs a frame its TLAS; scratch
  grows between frames only. A mesh that fails its checks isn't copied again for 30 s; the scene walk stops at 8 ms
  (checked every 64 nodes).
- Stopping waits up to 2 s for the GPU; if it isn't done, nothing is released (kept until the game ends).
- Writes into the game's memory (the ratio, the jitter table, the dynamic resolution lock) are exception-guarded, and
  the lock (and the last-ratio write) only happen on 1.7.104, whose code they were read from.
- Children of scene-graph nodes are read up to the array's used length (+ 0x122: it can keep holes), each checked.
- BLASes live in 64 MB pooled buffers (free lists), under a budget (iRayTracingMemoryMB, 512) and stop growing when
  DXGI says video memory is 95% used.
- Meshes are checked before they're built: indices in range, every position finite and inside the mesh's own bound
  (BSGeometry + 0x110, x 1.25), positions full or half precision by the vertex desc's UV offset (16 or 8 bytes). A
  mesh's key includes its data's first bytes, so memory reused for another mesh gets a new BLAS.
- Direct3D 11 / 12: the shared textures are made by Direct3D 11 (NT handles) and opened by Direct3D 12 (they're
  simultaneous-access there, so never transitioned); two shared fences: Direct3D 11 signals after copying the inputs
  and flushes, then the Direct3D 12 compute queue waits for that value; Direct3D 12 signals when done, and Direct3D 11
  waits on the GPU for that value only after the work was submitted.
- Fallback: a removed device (GetDeviceRemovedReason), a failed call, or a frame's work unfinished after 2 s turns ray
  tracing off for the session ("ray tracing: OFF: ..." in the log, the reason in the menu). Toggling it off and on in
  the menu tries again. Turning it off frees its memory.

### Ray tracing, stage 2: the picture

- Where: at Skyrim's tonemapping pass, found by its shader (ISHDRTonemap, FNV-1a 16801336497243392347, from the 10-08
  capture: t0 the bloom, t1 the lit picture R11G11B10 float, t2 the adapted brightness 2x2, target RGBA8), or, for
  another variant of it, by those inputs (logged: "tonemapping pass found by its inputs (shader ...)"). The depth and
  motion vectors are TAA's own inputs (t3, t2), kept from its last draw (the same textures every frame), so ray tracing
  needs Skyrim's TAA on, as DLSS does.
- Each frame there: a Direct3D 11 pass copies the depth (as floats), the motion vectors and the lit picture into
  shared textures; Direct3D 12 builds new BLASes and the TLAS, traces, denoises; Direct3D 11 waits for it on the GPU
  and draws the results into the lit picture; then the game's tonemapping goes on.
- The scene, camera-relative (so the far overland keeps float precision): the scene graph's static meshes within
  fRayTracingDistance (12,000 units) of the camera (LODRoot, sky, weather and the camera's node left out; glass, decals,
  effects, water and sky shaders left out by their shader property's class; alpha-tested ones, by NiAlphaProperty's
  flags at + 0x30, cover half of the time). People and creatures: a reference's 3D root (BSFadeNode) with a skinned mesh
  under it becomes capsules from each node to its parent (limb thickness by the bone's name for people's skeletons,
  else a share of its length). Real skinned triangles would need NiSkinInstance / NiSkinPartition's layout, which
  can't be checked outside the game; the capsules need only what's verified (bRayTracedCharacters).
- The camera: the main NiCamera ("WorldRoot CameraNode"'s child): its world transform (verified layout); its frustum
  (left, right, top, bottom, near, far) found once in it by its shape (NiCamera + 0x150 expected; logged "camera
  frustum at NiCamera + 0x..."); which axes look forward, right and up, read from its world-to-clip matrix (+ 0x110: the
  camera's position plus an axis must land in the screen's centre; logged "camera axes ..."), else Gamebryo's (forward
  column 0, up 1, right 2). Depth: standard D3D depth with the frustum's near and far.
- The sun: the sky's ("shadow scene node" > "Sky") child node holding a NiDirectionalLight; its offset from the sky's
  centre is the sun's direction (the 10-08 scene dump: the sun's node 354, 40, 47 units from the sky's centre). No sky,
  or the sun under 2 degrees: no ray-traced sun shadows (occlusion and bounce light stay).
- The shaders (inline ray queries, compute): sun shadows (a ray to a slightly widened sun disc), ambient occlusion
  (cosine-weighted rays, 110 units, partial with distance), one bounce of diffuse light and reflections (lit with the
  picture's own colour where the hit is on the screen and not hidden; the sky where a ray leaves; otherwise nothing:
  no materials needed, never brighter than the scene), a temporal pass (history at the motion vectors' place, reset where
  the depth differs, up to 16 frames) and a spatial 5 x 5 depth-aware blur.
- The composite (before tonemapping): Skyrim lights a surface in one pass, so the sun's share isn't kept apart:
  shadows take up to half of the light away (x fShadowStrength), occlusion up to fOcclusionStrength of it; bounce light
  adds the light times a colour of the surface (its hue at half brightness) x fLightingStrength; reflections blend by
  Fresnel x fReflectionStrength x 0.35. The sky is left alone.
- A line-up check guards it: every 8th pixel a camera ray is traced; it should end where the depth buffer says. If
  fewer than 25% agree for three 60-frame windows running (the camera or the world read wrongly), the results aren't
  put into the picture ("Paused" in the menu, "doesn't line up" in the log) until over 40% agree again.
- The menu (F10, "Ray-traced lighting"): on/off (bRayTracedLighting, off by default), Quality (iRayTracingQuality: Low
  and Medium trace at half resolution, High and Ultra at full; 1 to 4 occlusion rays, 1 to 3 bounce rays), Shadows
  (fShadowStrength 0.6), Ambient occlusion (fOcclusionStrength 0.7), Bounce light (fLightingStrength 1.0), Reflections
  (fReflectionStrength 0.5), People and creatures cast shadows (bRayTracedCharacters 1); under More: Distance
  (fRayTracingDistance 12000), Video memory (iRayTracingMemoryMB 512), Show (iRayTracingDebugView: the picture, or
  shadows / occlusion / bounce / reflections / normals alone). The status line and GPU time, meshes and line-up show
  under them. bRayTracingDevelop is gone (removed from the ini).

### DLSS remainder

- Sharpening of our own (DLSS 2.5.1 and later ignore theirs): Game11.hlsl SharpenPS after DLSS's output, into the
  game's target: each pixel pushed from its four neighbours' average, less where the neighbourhood already has strong
  contrast, never past its neighbourhood's darkest and brightest (no halos). The menu's Sharpness slider (fSharpness,
  0.2); 0 copies DLSS's output as it is. TAA's own history gets the unsharpened output.
- The jitter (10-09: "dlss and the other upscalers are jittery"), settled from the game's code: SkyrimSE.exe 1.7.104 at
  0x14101E360 advances the count (State + 0xD0, mod 8), then writes + 0x44 = (2 x table2[count] - 1) / State + 0x24 and
  + 0x48 likewise with table3 and + 0x28 (also + 0x4C / + 0x50). + 0x24 / + 0x28 are the screen's full width and height
  (1920 / 1080 in the capture; the dynamic resolution code steps its ratio in quarters of them). So + 0x44 is 2j / full,
  as assumed, and the projection code (0x1415451A2) takes it as is. The jitter wasn't the problem: the drawn size was. The
  game's own dynamic resolution (0x14101E130) steps its ratio by +0.03 / -0.07 when frames are fast or slow, whatever
  bEnableAutoDynamicResolution says, rounded to quarters of the screen: 2/3 + 0.03 is exactly 1336 x 752, the size the
  10-09 log alternated with our 1280 x 720. It skips itself while State + 0x128 (a lock count the game also takes) is
  non-zero, so the plugin holds that lock while DLSS upscales ("DLSS: the game's dynamic resolution locked to 0.667"),
  and writes the last frame's ratio (+ 0x11C / + 0x120, which the skipped update no longer copies) itself. The jitter
  check lines now also print + 0x24 / + 0x28.
- The ground's softness in Quality mode: Skyrim's renderer sets a sampler only when it changes, so samplers bound
  before upscaling started (the ground's among them) kept no bias. Now, once a frame, the samplers bound are passed
  through the bias again (and given back when it stops); and every mipmapped sampler is biased (mips point, linear or
  anisotropic), not only the linear-mip ones; plain point and comparison samplers are left alone.
- Frame generation: not available, greyed out in the menu with the reason in its tooltip. NVIDIA's frame generation
  (DLSS-FG, the SDK's nvngx_dlssg, 310.7) evaluates only on Direct3D 12 or Vulkan command lists, and its guide makes
  the application present the generated and the real frames itself, paced evenly from a thread of its own, with NVIDIA
  Reflex for the latency (and hardware-accelerated GPU scheduling on). Skyrim renders and presents with Direct3D 11. It
  could only be done by replacing the game's swap chain with a Direct3D 12 one of ours on its window (hooked at the
  game's device creation, before our current hooks), copying each finished frame across, generating and pacing
  ourselves, and handling full screen, resizing and alt-tab; the UI would have to be told apart or it smears. That is
  a presentation layer of its own that can't be built or checked without running the game frame by frame, and a
  mistake there stops the game showing anything. Not built.

### Seamless transitions: the menus found

- RE::UI (Address Library ID 400327) has no virtual functions (its bases are event sources and a singleton holder), so
  it has no RTTI: the 10-14 log's "the menus' object is a ?, not UI" was the check itself, not a wrong address. The
  menu stack is checked instead: at + 0x110 a BSTArray (data, capacity u32 + 0x118, size u32 + 0x120), every entry an
  object with RTTI (an IMenu). Found: "transitions: the game's menus found (UI at ..., N open)". The rest as below.

- Seamless transitions (user, 2026-10-10: "make a fake seamless transition between spaces"; bSeamlessTransitions,
  on by default, "Loading" in the F10 menu). Load doors, fast travel, overland tile crossings and sleeping fade to
  black and/or show the loading screen. Instead, the last picture stays on the screen through the fade, the load and
  the fade-in, then blends into the new place over 0.5 s. A small turning arc shows in the bottom right if it is held
  for more than 1 s.
  - The trigger is the game's own menu stack (above). LoadingMenu or FaderMenu opening starts the hold.
  - The picture is held until both have been closed for 0.3 s, because the fade-in's fader opens just after the
    loading screen closes. There is a 45 s limit.
  - Two pictures of the world are kept, alternately, from frames without a covering menu (journal, inventory,
    console and the others in CoveringMenus). The older one is shown, and only if it is under 0.5 s old. So a save
    loaded from the journal, or the fade-in after a load, shows the game's own screens.
  - Every change of the menu stack is logged ("menus: HUDMenu ..."). If FaderMenu turns out to stay open all the
    time, or the menus' names differ, the log shows it.

### Checked offline (2026-10-15)

`build.ps1 -Harness`, then `test\RtHarness.exe` (exit code 0 = passed; `--no-debug` without the debug layers, `--out
DIR` for its pictures). It makes the same pair the plugin makes in the game (the "game's" Direct3D 11 device with the
debug layer, RtCore's Direct3D 12 device with the debug layer and GPU-based validation), draws a scene the way Skyrim
leaves its buffers (a ground, a box, a low bench, a capsule for a person; R11G11B10 lit picture, R16G16 motion, R24G8
depth), feeds RtCore as the plugin does and reads the results back. On the RTX 5060 all checks pass with no debug
message: frames traced 40 of 40; line-up 100%; the box's and the capsule's shadows where they fall, open ground lit;
occlusion lower where the box meets the ground and by the bench; red bounce light in front of the red box; reflections
on grazing ground; the composite darker in the shadow, the sky untouched; the same at every quality and debug view and
with a 960 x 540 part drawn of 1280 x 720 (dynamic resolution) traced at half; 600 new meshes (half alpha-tested) and
4,000 instances built a frame's share at a time; sharpening steepens an edge with no overshoot and leaves flat areas
alone; and with the Direct3D 12 device removed mid-frame, Direct3D 11's wait is released, ray tracing turns itself off
and the "game's" device carries on. GPU time 0.5 ms a frame at 1280 x 720 High for that small scene (without the
debug layers); the whole run takes under 2 s without them, about 30 s with GPU-based validation.

Not checkable offline: anything read from the game (the scene walk, the camera, the sun, the tonemapping pass, the
lock, the menus). Loading the built plugin outside the game was tried too, but Malwarebytes on this PC quarantines
TamrielMergeGraphics.dll (and a test program linking its code) as "Malware.AI" as soon as another program loads it (the
old plugin too): a heuristic false positive; in the game it has run for days. If it ever quarantines the installed
plugin, restore it in Malwarebytes and exclude the game's (or Mod Organizer's) folder.

### In-game test (for the user)

1. Put the new tools\skse\TamrielMergeGraphics\TamrielMergeGraphics.dll over the installed one in SKSE\Plugins (or let
   the converter install it); nvngx_dlss.dll stays in SKSE\Plugins\TamrielMergeGraphics\. Skyrim's
   TAA on (Display settings). Run "Check game" (game-check) afterwards: it now reads the plugin's log where it really
   is (the game's Data\SKSE\Plugins, or Mod Organizer's overwrite\SKSE\Plugins) and prints a "Graphics plugin" part.
2. DLSS first (ray tracing off): stand on Riverwood's road, F10, Upscaler DLSS Quality. Expect in the log "DLSS: the
   game's dynamic resolution locked to 0.667" and "DLSS Quality: 1280x720 drawn, 1920x1080 shown." with no line about
   1336x752; the jitter check lines show "State + 0x24 1920 x 1080". The road should be as sharp as native now. Move the
   Sharpness slider: 0 is DLSS's own output, 0.5 visibly crisper, no bright halos on roof edges against the sky.
3. Ray tracing: F10, tick Ray-traced lighting, Quality Medium. Within a second the log has "ray tracing: Direct3D 12
   device on the game's GPU, DXR tier 1.2, shader model 6.5", "ray tracing: ready", "camera frustum at NiCamera + 0x150
   ..." (near about 15, far in the hundred thousands), "camera axes from its world-to-clip matrix: forward column 0,
   right 2, up 1" (or with a minus: either is fine if it says it was confirmed), "sun at N deg elevation", and either
   nothing about the tonemapping (the shader hash matched) or "tonemapping pass found by its inputs". Every 10 s a line
   "ray tracing: N instances, N meshes (N MB), ..., GPU N ms, line-up N% ..." The line-up should be well over 50% in
   Riverwood (grass and people aren't in it, so not 100%).
4. Look: in Riverwood by day, under the bridge and the market stalls' roofs (occlusion, shadows), a person standing in
   the sun (a soft shadow from their capsules), the river at a low angle (reflections). Under F10 > More > Show, each
   effect alone: shadows should fall away from the sun, occlusion darken corners and under things, normals look smooth
   and change colour with the surfaces' facing (if they swim with the camera, the camera is read wrongly: send the
   log). GPU time at Medium should be a few ms.
5. Stress: walk through a load door, fast travel and wait an hour while it's on. Expect no hang; the 10 s lines may count some
   frames "skipped" (the GPU was behind; nothing waited), but there should be no "OFF". If it does turn itself off, the
   line says why ("device was removed", "took over 2 s"); send that log. Leaving it ticked off for the session frees its memory.
6. Seamless transitions: through a load door: the log should say "transitions: the game's menus found (UI at ...)" and
   list "menus: ... LoadingMenu" when it loads; the old picture stays up and blends into the new place.

## Next

- The in-game test above; then tune the strengths from what it looks like (the composite's shares are guesses until
  seen in game).
- Real skinned triangles for people (NiSkinInstance's bones and NiSkinPartition's vertices, read and checked against
  the mesh's bound in game), if the capsules look too coarse.
- Frame generation only if a Direct3D 12 presentation layer is wanted (above).

Skyrim SE draws with D3D11, which has no ray tracing, so DXR runs on a D3D12 device of our own beside it, fed from
the scene graph (above).

## Address Library format 5

Skyrim 1.7's Address Library (versionlib-1-7-*.bin) is a new format: a 96-byte header (format 5, version, the
executable's name in 64 bytes, pointer size, 4 unused, count at byte 92) and then one 32-bit offset per ID (the ID is
the index). TamrielMergeEra reads it (it read only format 2 before, which is why its dates stayed 4E on 1.7.104).
