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

## Built, not yet tested in game

- Seamless transitions (user, 2026-10-10: "make a fake seamless transition between spaces"; bSeamlessTransitions,
  on by default, "Loading" in the F10 menu). Load doors, fast travel, overland tile crossings and sleeping fade to
  black and/or show the loading screen. Instead, the last picture stays on the screen through the fade, the load and
  the fade-in, then blends into the new place over 0.5 s. A small turning arc shows in the bottom right if it is held
  for more than 1 s.
  - The trigger is the game's own menu stack. RE::UI is Address Library ID 400327, checked once by its RTTI name "UI".
    At + 0x110 is a BSTArray of IMenu pointers (data, capacity u32 at + 0x118, size u32 at + 0x120), and each menu is
    named by its RTTI. LoadingMenu or FaderMenu opening starts the hold.
  - The picture is held until both have been closed for 0.3 s, because the fade-in's fader opens just after the
    loading screen closes. There is a 45 s limit.
  - Two pictures of the world are kept, alternately, from frames without a covering menu (journal, inventory,
    console and the others in CoveringMenus). The older one is shown, and only if it is under 0.5 s old. So a save
    loaded from the journal, or the fade-in after a load, shows the game's own screens.
  - Every change of the menu stack is logged ("menus: HUDMenu ..."). If FaderMenu turns out to stay open all the
    time, or the menus' names differ, the log shows it.

## Next

- Quality-mode ground textures are softer than native (the road in Riverwood); tune the mip bias, or bias the
  terrain's samplers too.
- Sharpening of our own (DLSS 2.5.1 and later have none), for the menu's slider.
- Ray tracing stage 2: inline ray queries (DXC, compute) for sun shadows and ambient occlusion from the depth
  buffer's positions, shared to D3D11 and composited before tonemapping; then one-bounce GI, then reflections.
- Frame generation is hardest under D3D11 (NVIDIA's frame generation targets D3D12); left until the rest works.

Skyrim SE draws with D3D11, which has no ray tracing, so DXR runs on a D3D12 device of our own beside it, fed from
the scene graph (above).

## Address Library format 5

Skyrim 1.7's Address Library (versionlib-1-7-*.bin) is a new format: a 96-byte header (format 5, version, the
executable's name in 64 bytes, pointer size, 4 unused, count at byte 92) and then one 32-bit offset per ID (the ID is
the index). TamrielMergeEra reads it (it read only format 2 before, which is why its dates stayed 4E on 1.7.104).
