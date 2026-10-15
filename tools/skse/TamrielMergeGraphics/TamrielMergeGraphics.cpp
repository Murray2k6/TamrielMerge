// TamrielMergeGraphics: an SKSE plugin bringing Starfield's kind of PC graphics options to Skyrim, with their settings in
// the game (user, 2026-10-08: "a plugin made to enable rt/pt and dlss with ui settings added to the game", "starfield
// uses these and i need the same"): NVIDIA DLSS (DLAA and the upscaling modes) and a ray-traced lighting option, chosen
// in an in-game settings menu (F10) and kept in Data/SKSE/Plugins/TamrielMergeGraphics.ini.
//
// It hooks the frame's presentation (IDXGISwapChain::Present, through the swap chain's table of functions, so no game
// addresses are needed), draws the settings menu over the game with Dear ImGui (MIT), takes the keyboard and mouse from
// the game while the menu is open, runs NVIDIA DLSS (the DLSS SDK, NVIDIA RTX SDKs licence; its runtime nvngx_dlss.dll
// ships beside this plugin) in place of Skyrim's temporal anti-aliasing with a sharpening pass of our own after it, and
// ray traces shadows, ambient occlusion, bounce light and reflections on a Direct3D 12 device of its own (RtCore.cpp).
// All of it is TamrielMerge's own work, from Microsoft's and NVIDIA's SDK documentation; no other mod's code.
//
// Every hook is guarded: if anything isn't as expected the plugin logs it and leaves the game alone.

#include <windows.h>
#include <d3d11_4.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <dxgi.h>
#define DIRECTINPUT_VERSION 0x0800
#include <dinput.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cmath>
#include <fstream>
#include <string>
#include <atomic>
#include <mutex>
#include <algorithm>

#include <d3dcompiler.h>
#include <unordered_map>
#include <vector>

#include "imgui.h"
#include "backends/imgui_impl_dx11.h"
#include "backends/imgui_impl_win32.h"
#include "nvsdk_ngx.h"
#include "nvsdk_ngx_helpers.h"
#include "RtCore.h"
#include "gen/FullscreenVS.h"
#include "gen/SharpenPS.h"

extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND, UINT, WPARAM, LPARAM);

namespace
{
    // ---- SKSE plugin interface (SKSE64 PluginAPI.h, post-AE) ----
    struct SKSEPluginVersionData
    {
        std::uint32_t dataVersion;
        std::uint32_t pluginVersion;
        char name[256];
        char author[256];
        char supportEmail[252];
        std::uint32_t versionIndependenceEx;
        std::uint32_t versionIndependence;
        std::uint32_t compatibleVersions[16];
        std::uint32_t seVersionRequired;
    };
    constexpr std::uint32_t kVersionIndependent_AddressLibraryPostAE = 1 << 0;
    constexpr std::uint32_t kVersionIndependent_StructsPost629 = 1 << 2;

    FILE* logFile = nullptr;
    void Log(const char* fmt, auto... args)
    {
        if (!logFile) return;
        std::fprintf(logFile, fmt, args...);
        std::fputc('\n', logFile);
        std::fflush(logFile);
    }

    std::string PluginDirectory()
    {
        char path[MAX_PATH]{};
        HMODULE self = nullptr;
        GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCSTR>(&PluginDirectory), &self);
        GetModuleFileNameA(self, path, MAX_PATH);
        std::string s(path);
        return s.substr(0, s.find_last_of("\\/") + 1);
    }

    // ---- The game's renderer state (BSGraphics::State, Address Library ID 411479), where the camera's jitter lives ----
    bool GameVersion(int v[4])
    {
        char path[MAX_PATH]{};
        GetModuleFileNameA(nullptr, path, MAX_PATH);
        DWORD handle = 0, size = GetFileVersionInfoSizeA(path, &handle);
        if (size == 0) return false;
        std::vector<char> data(size);
        if (!GetFileVersionInfoA(path, 0, size, data.data())) return false;
        VS_FIXEDFILEINFO* info = nullptr;
        UINT len = 0;
        if (!VerQueryValueA(data.data(), "\\", reinterpret_cast<void**>(&info), &len) || !info) return false;
        v[0] = HIWORD(info->dwFileVersionMS); v[1] = LOWORD(info->dwFileVersionMS);
        v[2] = HIWORD(info->dwFileVersionLS); v[3] = LOWORD(info->dwFileVersionLS);
        return true;
    }

    // Address Library format 5 (the game's 1.7 releases): a 96-byte header with the count at byte 92, then one 32-bit
    // offset per ID.
    bool AddressLibraryOffset(const std::string& file, std::uint64_t wanted, std::uint64_t& result)
    {
        std::ifstream in(file, std::ios::binary);
        if (!in) return false;
        std::int32_t format = 0, count = 0;
        in.read(reinterpret_cast<char*>(&format), 4);
        if (!in || format != 5) return false;
        in.seekg(92);
        in.read(reinterpret_cast<char*>(&count), 4);
        if (!in || wanted >= static_cast<std::uint64_t>(count)) return false;
        std::uint32_t offset = 0;
        in.seekg(96 + static_cast<std::streamoff>(wanted) * 4);
        in.read(reinterpret_cast<char*>(&offset), 4);
        if (!in || offset == 0) return false;
        result = offset;
        return true;
    }

    std::uint8_t* gameState = nullptr;

    void FindGameState()
    {
        int v[4]{};
        if (!GameVersion(v)) { Log("renderer state: the game's version unreadable"); return; }
        char path[MAX_PATH]{};
        GetModuleFileNameA(nullptr, path, MAX_PATH);
        std::string dir(path);
        dir = dir.substr(0, dir.find_last_of("\\/") + 1);
        char lib[MAX_PATH];
        std::snprintf(lib, sizeof(lib), "%sData\\SKSE\\Plugins\\versionlib-%d-%d-%d-%d.bin", dir.c_str(), v[0], v[1], v[2], v[3]);
        std::uint64_t offset = 0;
        if (AddressLibraryOffset(lib, 411479, offset)) gameState = reinterpret_cast<std::uint8_t*>(GetModuleHandleA(nullptr)) + offset;
        else Log("renderer state: not in %s", lib);
    }

    bool SafeCopy(void* to, const void* from, size_t n)
    {
        __try { std::memcpy(to, from, n); return true; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    // ---- The scene graph (hardware ray tracing, stage 1: finding the world's meshes) ----
    // Found without a class library for this game version: the player through Address Library (ID 403521, checked
    // by its form ID 0x14), its 3D (TESObjectREFR + 0x68 loaded data, + 0x08 the 3D root), up the parent links
    // (NiAVObject + 0x30) to the world's root, then down through every node's children (NiNode + 0x110: an NiTArray
    // { vtable, data, capacity u16, free u16, size u16, grow u16 }). Types come from each object's MSVC RTTI, so
    // nothing is assumed about which class an object is. All reads are guarded: the game's main thread changes the
    // graph meanwhile.
    template <class T> bool Read(const void* at, T& out) { return SafeCopy(&out, at, sizeof(T)); }

    std::string TypeName(const void* obj)
    {
        const std::uint8_t* vtable = nullptr;
        if (!obj || !Read(obj, vtable) || !vtable) return "?";
        const std::uint8_t* locator = nullptr;
        if (!Read(vtable - 8, locator) || !locator) return "?";
        std::int32_t sig = -1, typeRva = 0, selfRva = 0;
        if (!Read(locator, sig) || sig != 1 || !Read(locator + 12, typeRva) || !Read(locator + 20, selfRva)) return "?";
        const std::uint8_t* base = locator - selfRva;
        char name[96]{};
        if (!SafeCopy(name, base + typeRva + 16, sizeof(name) - 1)) return "?";
        std::string n(name);
        if (n.rfind(".?AV", 0) == 0) n = n.substr(4);
        if (auto at = n.find("@@"); at != std::string::npos) n = n.substr(0, at);
        return n;
    }

    float HalfToFloat(std::uint16_t h)
    {
        std::uint32_t sign = (h & 0x8000u) << 16, exp = (h >> 10) & 0x1F, man = h & 0x3FF, bits;
        if (exp == 0) bits = man ? sign | ((man << 13) + (113u << 23)) - (1u << 23) : sign;   // subnormals approximated
        else if (exp == 31) bits = sign | 0x7F800000u | (man << 13);
        else bits = sign | ((exp + 112) << 23) | (man << 13);
        float f; std::memcpy(&f, &bits, 4); return f;
    }

    std::string ObjectName(const void* obj)
    {
        const char* text = nullptr;          // NiObjectNET + 0x10: a BSFixedString (its text pointer)
        char buf[80]{};
        if (!Read(static_cast<const std::uint8_t*>(obj) + 0x10, text) || !text || !SafeCopy(buf, text, sizeof(buf) - 1)) return "";
        return buf;
    }

    struct SceneDump
    {
        FILE* f = nullptr;
        std::unordered_map<std::string, int> types;
        int nodes = 0, shapes = 0, withRaw = 0, listed = 0;
        long long triangles = 0;
    };

    void DumpObject(SceneDump& d, const std::uint8_t* obj, int depth)
    {
        if (!obj || depth > 64) return;
        std::string type = TypeName(obj);
        d.types[type]++;
        float world[13]{};                    // NiAVObject + 0x7C: world transform (rotation 3x3, translation, scale)
        Read(obj + 0x7C, world);
        std::uint32_t flags = 0;
        Read(obj + 0xF4, flags);
        bool shape = type.find("TriShape") != std::string::npos;
        if (shape)
        {
            d.shapes++;
            const std::uint8_t* renderer = nullptr;
            std::uint64_t desc = 0;
            Read(obj + 0x138, renderer);      // BSGeometry + 0x110 runtime data: + 0x28 renderer data, + 0x38 vertex desc
            Read(obj + 0x148, desc);
            std::uint16_t tris = 0, verts = 0;
            Read(obj + 0x158, tris); Read(obj + 0x15A, verts);
            float bound[4]{};                 // the model bound (centre, radius), local
            Read(obj + 0x110, bound);
            std::uint64_t r[8]{};
            if (renderer) SafeCopy(r, renderer, sizeof(r));
            d.triangles += tris;
            if (r[4]) d.withRaw++;
            if (d.listed < 400 && depth > 3 && d.shapes % 25 == 1)
            {
                d.listed++;
                std::fprintf(d.f, "%*sSHAPE %s \"%s\" at (%.0f %.0f %.0f) tris %u verts %u desc %016llx bound (%.1f %.1f %.1f) r %.1f\n",
                             depth * 2, "", type.c_str(), ObjectName(obj).c_str(), world[9], world[10], world[11], tris, verts,
                             static_cast<unsigned long long>(desc), bound[0], bound[1], bound[2], bound[3]);
                std::fprintf(d.f, "%*s  renderer qwords: %llx %llx %llx %llx %llx %llx %llx %llx\n", depth * 2, "",
                             r[0], r[1], r[2], r[3], r[4], r[5], r[6], r[7]);
                // Vertex layout: stride = (desc & 0xF) * 4; position at offset 0, full precision (3 floats) if flag
                // 0x400 of desc >> 44 is set, else 3 halves.
                unsigned stride = static_cast<unsigned>(desc & 0xF) * 4;
                bool full = ((desc >> 44) & 0x400) != 0;
                for (int c = 0; c < 8; ++c)
                {
                    const std::uint8_t* q = reinterpret_cast<const std::uint8_t*>(r[c]);
                    if (!q || reinterpret_cast<std::uintptr_t>(q) < 0x10000 || stride == 0) continue;
                    std::string line;
                    bool ok = true;
                    for (int v = 0; v < 3 && ok; ++v)
                    {
                        float p[3]{};
                        if (full) ok = SafeCopy(p, q + v * stride, 12);
                        else
                        {
                            std::uint16_t h[3]{};
                            ok = SafeCopy(h, q + v * stride, 6);
                            for (int k = 0; k < 3; ++k) p[k] = HalfToFloat(h[k]);
                        }
                        char b[80];
                        std::snprintf(b, sizeof(b), " (%.1f %.1f %.1f)", p[0], p[1], p[2]);
                        line += b;
                    }
                    std::uint16_t idx[6]{};
                    SafeCopy(idx, q, sizeof(idx));
                    if (ok) std::fprintf(d.f, "%*s  q%d as vertices:%s | as u16: %u %u %u %u %u %u\n", depth * 2, "", c, line.c_str(),
                                         idx[0], idx[1], idx[2], idx[3], idx[4], idx[5]);
                }
            }
            return;
        }
        // Children, when it is a node.
        const std::uint8_t* data = nullptr;
        std::uint16_t capacity = 0, size = 0;
        if (!Read(obj + 0x118, data) || !Read(obj + 0x120, capacity) || !Read(obj + 0x124, size)) return;
        if (!data || size == 0 || size > capacity || capacity > 65000) return;
        d.nodes++;
        if (depth <= 4 && d.listed < 400)
        {
            d.listed++;
            std::fprintf(d.f, "%*sNODE %s \"%s\" at (%.0f %.0f %.0f) children %u flags %x\n", depth * 2, "", type.c_str(),
                         ObjectName(obj).c_str(), world[9], world[10], world[11], size, flags);
        }
        for (std::uint16_t i = 0; i < size; ++i)
        {
            const std::uint8_t* child = nullptr;
            if (Read(data + i * 8, child) && child) DumpObject(d, child, depth + 1);
        }
    }

    void DumpScene()
    {
        FILE* f = std::fopen((PluginDirectory() + "TamrielMergeGraphics-scene.txt").c_str(), "w");
        if (!f) return;
        int v[4]{};
        GameVersion(v);
        char path[MAX_PATH]{};
        GetModuleFileNameA(nullptr, path, MAX_PATH);
        std::string dir(path);
        dir = dir.substr(0, dir.find_last_of("\\/") + 1);
        char lib[MAX_PATH];
        std::snprintf(lib, sizeof(lib), "%sData\\SKSE\\Plugins\\versionlib-%d-%d-%d-%d.bin", dir.c_str(), v[0], v[1], v[2], v[3]);
        std::uint64_t offset = 0;
        const std::uint8_t* player = nullptr;
        if (AddressLibraryOffset(lib, 403521, offset))
            Read(reinterpret_cast<std::uint8_t*>(GetModuleHandleA(nullptr)) + offset, player);
        std::uint32_t formId = 0;
        Read(player + 0x14, formId);
        std::fprintf(f, "player %p (%s), form id %08x\n", static_cast<const void*>(player), TypeName(player).c_str(), formId);
        const std::uint8_t* loaded = nullptr, * root3d = nullptr;
        Read(player + 0x68, loaded);
        // The 3D root: the first pointer in the loaded data whose object is a node (its offset found, not assumed).
        if (loaded)
            for (int o = 0; o < 0x80 && !root3d; o += 8)
            {
                const std::uint8_t* q = nullptr;
                if (!Read(loaded + o, q) || !q) continue;
                std::string t = TypeName(q);
                std::fprintf(f, "  loaded + 0x%02x: %p %s\n", o, static_cast<const void*>(q), t.c_str());
                if (t.find("Node") != std::string::npos) root3d = q;
            }
        std::fprintf(f, "loaded data %p, 3D %p (%s \"%s\")\n", static_cast<const void*>(loaded), static_cast<const void*>(root3d),
                     TypeName(root3d).c_str(), ObjectName(root3d).c_str());
        // Up to the world's root.
        const std::uint8_t* top = root3d;
        for (int i = 0; i < 32 && top; ++i)
        {
            const std::uint8_t* parent = nullptr;
            Read(top + 0x30, parent);
            std::fprintf(f, "  up %d: %p %s \"%s\"\n", i, static_cast<const void*>(top), TypeName(top).c_str(), ObjectName(top).c_str());
            if (!parent) break;
            top = parent;
        }
        // The ray tracing's camera: NiCamera's world transform, world-to-clip matrix and frustum as floats.
        if (top)
            if (const std::uint8_t* camNode = [&]() -> const std::uint8_t* {
                    const std::uint8_t* data = nullptr; std::uint16_t cap = 0, size = 0;
                    if (!Read(top + 0x118, data) || !Read(top + 0x120, cap) || !Read(top + 0x124, size) || !data || size > cap || cap > 64) return nullptr;
                    for (std::uint16_t i = 0; i < size; ++i) { const std::uint8_t* c = nullptr; if (Read(data + i * 8, c) && c && ObjectName(c) == "WorldRoot CameraNode") return c; }
                    return nullptr; }())
            {
                const std::uint8_t* data = nullptr, * cam = nullptr;
                if (Read(camNode + 0x118, data) && data) Read(data, cam);
                std::fprintf(f, "camera node child: %s\n", TypeName(cam).c_str());
                float m[(0x1F0 - 0x7C) / 4]{};
                if (cam && SafeCopy(m, cam + 0x7C, sizeof(m)))
                    for (int i = 0; i < int(std::size(m)); ++i)
                        std::fprintf(f, "  NiCamera + 0x%X: %g%s", 0x7C + i * 4, m[i], (i % 4 == 3) ? "\n" : ";");
                std::fprintf(f, "\n");
            }
        SceneDump d{ f };
        if (top) DumpObject(d, top, 0);
        std::fprintf(f, "\n%d nodes, %d shapes (%d with CPU vertices), %lld triangles\n", d.nodes, d.shapes, d.withRaw, d.triangles);
        for (auto& [t, n] : d.types) std::fprintf(f, "  %6d %s\n", n, t.c_str());
        std::fclose(f);
        Log("scene dump: %d nodes, %d shapes (%d with CPU vertices)", d.nodes, d.shapes, d.withRaw);
    }

    /// A game setting's value, by its name ("bEnableAutoDynamicResolution:Display"): the engine's Setting objects are
    /// { vtable, value (8 bytes), const char* name }, the name in the executable's read-only data.
    void* FindGameSetting(const char* name)
    {
        auto base = reinterpret_cast<std::uint8_t*>(GetModuleHandleA(nullptr));
        auto nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + reinterpret_cast<IMAGE_DOS_HEADER*>(base)->e_lfanew);
        const size_t len = std::strlen(name) + 1;
        const std::uint8_t* text = nullptr;
        auto section = IMAGE_FIRST_SECTION(nt);
        for (int i = 0; i < nt->FileHeader.NumberOfSections && !text; ++i)
        {
            if (std::memcmp(section[i].Name, ".rdata", 6) != 0) continue;
            const std::uint8_t* start = base + section[i].VirtualAddress;
            size_t size = section[i].Misc.VirtualSize;
            for (size_t o = 0; o + len <= size; ++o)
                if (start[o] == static_cast<std::uint8_t>(name[0]) && std::memcmp(start + o, name, len) == 0 && (o == 0 || start[o - 1] == 0)) { text = start + o; break; }
        }
        if (!text) return nullptr;
        for (int i = 0; i < nt->FileHeader.NumberOfSections; ++i)
        {
            if (std::memcmp(section[i].Name, ".data", 5) != 0) continue;
            auto start = reinterpret_cast<const std::uint8_t* const*>(base + section[i].VirtualAddress);
            size_t count = section[i].Misc.VirtualSize / 8;
            for (size_t o = 2; o < count; ++o)
                if (start[o] == text) return const_cast<std::uint8_t**>(start + o - 1);
        }
        return nullptr;
    }

    // ---- Settings (Starfield's Display options, as far as Skyrim can have them) ----
    enum Upscaler : int { Off, DLAA, Quality, Balanced, Performance, UltraPerformance };
    const char* UpscalerNames[] = { "Off (Skyrim's own anti-aliasing)", "DLAA (native resolution)", "DLSS Quality", "DLSS Balanced", "DLSS Performance", "DLSS Ultra Performance" };

    struct Settings
    {
        int upscaler = DLAA;
        float sharpness = 0.2f;
        bool frameGeneration = false;
        bool rayTracedLighting = false;                    // off until the user turns it on
        int rtQuality = 1;                                 // 0 low, 1 medium, 2 high, 3 ultra
        float shadowStrength = 0.6f;                       // ray-traced sun shadows
        float occlusionStrength = 0.7f;                    // ray-traced ambient occlusion
        float lightingStrength = 1.0f;                     // one bounce of diffuse light
        float reflectionStrength = 0.5f;
        bool rtCharacters = true;                          // people and creatures as capsules along their bones
        float rtDistance = 12000.0f;                       // how far meshes are ray traced (units)
        int rtMemoryMB = 512;                              // video memory for the acceleration structures
        int rtDebugView = 0;                               // 0 the picture, 1 shadows, 2 occlusion, 3 bounce, 4 reflections, 5 normals
        int menuKey = VK_F10;
        bool capture = false;      // developer: record two frames' draws (finding the renderer's passes)
        int captureFrame = 900;
        float jitterSignX = -1.0f, jitterSignY = 1.0f;     // the game's jitter into DLSS's (pixels)
        float motionScaleX = 1.0f, motionScaleY = 1.0f;    // the motion vectors' screen fractions into pixels
        float mipBiasOffset = 0.0f;                        // added to log2(drawn / shown) for the textures' bias
        bool sceneDump = false;                            // developer: F12 writes the scene graph (ray tracing work)
        bool seamlessTransitions = true;                   // the last picture kept through fades and loading screens
        int physicsCells = 512;                            // the physics world's reach from a worldspace's centre (cells)
    } settings;
    std::string iniPath;

    void LoadSettings()
    {
        auto i = [&](const char* k, int d) { return static_cast<int>(GetPrivateProfileIntA("Graphics", k, d, iniPath.c_str())); };
        auto f = [&](const char* k, float d)
        {
            char buf[64]{};
            GetPrivateProfileStringA("Graphics", k, "", buf, sizeof(buf), iniPath.c_str());
            return buf[0] ? static_cast<float>(std::atof(buf)) : d;
        };
        settings.upscaler = std::clamp(i("iUpscaler", settings.upscaler), 0, static_cast<int>(UltraPerformance));
        settings.sharpness = f("fSharpness", settings.sharpness);
        settings.frameGeneration = i("bFrameGeneration", 0) != 0;
        // Ray tracing: off until turned on in the menu (its first stage hung the GPU on 2026-10-09; it is bounded and
        // guarded now, see RtCore.h).
        settings.rayTracedLighting = i("bRayTracedLighting", 0) != 0;
        settings.rtQuality = std::clamp(i("iRayTracingQuality", settings.rtQuality), 0, 3);
        settings.shadowStrength = std::clamp(f("fShadowStrength", settings.shadowStrength), 0.0f, 1.0f);
        settings.occlusionStrength = std::clamp(f("fOcclusionStrength", settings.occlusionStrength), 0.0f, 1.0f);
        settings.lightingStrength = std::clamp(f("fLightingStrength", settings.lightingStrength), 0.0f, 2.0f);
        settings.reflectionStrength = std::clamp(f("fReflectionStrength", settings.reflectionStrength), 0.0f, 1.0f);
        settings.rtCharacters = i("bRayTracedCharacters", 1) != 0;
        settings.rtDistance = std::clamp(f("fRayTracingDistance", settings.rtDistance), 2000.0f, 40000.0f);
        settings.rtMemoryMB = std::clamp(i("iRayTracingMemoryMB", settings.rtMemoryMB), 128, 2048);
        settings.rtDebugView = std::clamp(i("iRayTracingDebugView", 0), 0, 5);
        settings.menuKey = i("iMenuKey", VK_F10);
        settings.capture = i("bCapture", 0) != 0;
        settings.captureFrame = i("iCaptureFrame", 900);
        settings.jitterSignX = f("fJitterSignX", settings.jitterSignX);
        settings.jitterSignY = f("fJitterSignY", settings.jitterSignY);
        settings.motionScaleX = f("fMotionScaleX", settings.motionScaleX);
        settings.motionScaleY = f("fMotionScaleY", settings.motionScaleY);
        settings.mipBiasOffset = f("fMipBiasOffset", settings.mipBiasOffset);
        settings.sceneDump = i("bSceneDump", 0) != 0;
        settings.seamlessTransitions = i("bSeamlessTransitions", 1) != 0;
        settings.physicsCells = std::clamp(static_cast<int>(GetPrivateProfileIntA("World", "iPhysicsCells", 512, iniPath.c_str())), 64, 2048);
    }

    void SaveSettings()
    {
        auto w = [&](const char* k, const std::string& v) { WritePrivateProfileStringA("Graphics", k, v.c_str(), iniPath.c_str()); };
        w("iUpscaler", std::to_string(settings.upscaler));
        w("fSharpness", std::to_string(settings.sharpness));
        w("bFrameGeneration", settings.frameGeneration ? "1" : "0");
        w("bRayTracedLighting", settings.rayTracedLighting ? "1" : "0");
        WritePrivateProfileStringA("Graphics", "bRayTracingDevelop", nullptr, iniPath.c_str());   // the old lock, gone
        w("iRayTracingQuality", std::to_string(settings.rtQuality));
        w("fShadowStrength", std::to_string(settings.shadowStrength));
        w("fOcclusionStrength", std::to_string(settings.occlusionStrength));
        w("fLightingStrength", std::to_string(settings.lightingStrength));
        w("fReflectionStrength", std::to_string(settings.reflectionStrength));
        w("bRayTracedCharacters", settings.rtCharacters ? "1" : "0");
        w("fRayTracingDistance", std::to_string(settings.rtDistance));
        w("iRayTracingMemoryMB", std::to_string(settings.rtMemoryMB));
        w("iRayTracingDebugView", std::to_string(settings.rtDebugView));
        w("iMenuKey", std::to_string(settings.menuKey));
        w("bSeamlessTransitions", settings.seamlessTransitions ? "1" : "0");
        WritePrivateProfileStringA("World", "iPhysicsCells", std::to_string(settings.physicsCells).c_str(), iniPath.c_str());
    }

    // ---- the physics world's size ----
    // Skyrim's Havok broad phase is a box reaching 64 cells from a worldspace's centre in every direction (262,144 units,
    // 3745.38 Havok metres: an hkVector4 in the executable's read-only data, three copies and a zero). Past it physics
    // fails: actors sink, doors stick, arrows pass through, items fall through floors. Daggerfall's overland is one
    // worldspace of 1000 x 500 cells (user, 2026-10-12: travel seen in real time "without loading", the map not made
    // smaller), so at load the box is made iPhysicsCells cells ([World], default 512: the overland reaches 500 from its
    // centre). Found by its value, so no address is needed and an executable without it is left alone. A bigger box makes
    // the broad phase's grid coarser everywhere (more pairs to test, a little more work); positions themselves keep
    // Skyrim's float precision (about 2-4 mm at 500 cells, 0.5 mm at the vanilla map's edges).
    void PatchPhysicsWorld(int cells)
    {
        if (cells <= 64) { Log("physics world: vanilla, 64 cells"); return; }
        auto base = reinterpret_cast<uint8_t*>(GetModuleHandleA(nullptr));
        auto nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + reinterpret_cast<IMAGE_DOS_HEADER*>(base)->e_lfanew);
        auto sec = IMAGE_FIRST_SECTION(nt);
        const uint32_t vanilla[4] = { 0x456A161E, 0x456A161E, 0x456A161E, 0 };   // 3745.3823f x 3, 0
        uint8_t* hit = nullptr;
        int hits = 0;
        for (int i = 0; i < nt->FileHeader.NumberOfSections; ++i, ++sec)
        {
            if (std::memcmp(sec->Name, ".rdata", 6) != 0) continue;
            uint8_t* p = base + sec->VirtualAddress;
            for (size_t k = 0; k + 16 <= sec->Misc.VirtualSize; k += 4)
                if (std::memcmp(p + k, vanilla, 16) == 0) { hit = p + k; ++hits; }
        }
        if (hits != 1) { Log("physics world: the 64-cell box not found (%d matches), left as it is", hits); return; }
        const float half = cells * 4096.0f * 0.0142875f;
        const float box[4] = { half, half, half, 0.0f };
        DWORD old = 0;
        if (!VirtualProtect(hit, sizeof(box), PAGE_READWRITE, &old)) { Log("physics world: couldn't unprotect (%lu)", GetLastError()); return; }
        std::memcpy(hit, box, sizeof(box));
        VirtualProtect(hit, sizeof(box), old, &old);
        Log("physics world: %d cells from a worldspace's centre (%.1f Havok metres; vanilla 64)", cells, half);
    }

    ID3D11Device* device = nullptr;
    void SetupGuarded(IDXGISwapChain* swap);
    std::atomic<long long> drawCalls{ 0 };
    bool OnTaaDraw(ID3D11DeviceContext* ctx);
    bool OnUpscaleDraw(ID3D11DeviceContext* ctx);
    float textureBias = 0.0f;   // added to the game's mipmapped samplers while drawing below full size

    // Skyrim's temporal anti-aliasing pass (ISTemporalAA), found by its bytecode's FNV-1a hash: a full-screen draw
    // reading the tonemapped picture (t0), last frame's result (t1), the motion vectors (t2, R16G16 float) and the
    // depth (t3), writing the result (target 0) and the next frame's history (target 1). DLSS takes its place.
    constexpr std::uint64_t TaaShaderHash = 5915929377734644592ull;
    std::atomic<ID3D11PixelShader*> taaShader{ nullptr };
    // The game's upscale pass, drawn only below full resolution: the finished smaller picture to the full-size target.
    constexpr std::uint64_t UpscaleShaderHash = 4172866439863931375ull;
    std::atomic<ID3D11PixelShader*> upscaleShader{ nullptr };
    // Skyrim's tonemapping (ISHDRTonemap, from the 10-08 capture: t0 the bloom, t1 the lit R11G11B10 picture, t2 the
    // adapted brightness, target an 8-bit picture): the ray tracing goes into the lit picture just before it.
    constexpr std::uint64_t TonemapShaderHash = 16801336497243392347ull;
    std::atomic<ID3D11PixelShader*> tonemapShader{ nullptr };
    void OnTonemapDraw(ID3D11DeviceContext* ctx, UINT vertices);
    std::atomic<bool> inOurPass{ false };              // our own Direct3D 11 passes go through the hooked table too
    ID3D11PixelShader* currentPixelShader = nullptr;   // the render thread's, kept by PSSetShader
    bool PatchVtable(void** vtable, int index, void* hook, void** original);

    // ---- Capture (developer): the shaders the game makes, and every draw of two frames ----
    struct ShaderInfo { std::uint64_t hash; unsigned size; int textures; int cbuffers; unsigned cb0Size; };
    std::mutex shaderLock;
    std::unordered_map<void*, ShaderInfo> pixelShaders;
    std::atomic<bool> capturing{ false };
    FILE* captureFile = nullptr;
    unsigned captureStarted = 0;

    std::uint64_t Fnv(const void* data, size_t n)
    {
        std::uint64_t h = 1469598103934665603ull;
        for (size_t i = 0; i < n; ++i) { h ^= static_cast<const unsigned char*>(data)[i]; h *= 1099511628211ull; }
        return h;
    }

    using CreatePixelShader_t = HRESULT(STDMETHODCALLTYPE*)(ID3D11Device*, const void*, SIZE_T, ID3D11ClassLinkage*, ID3D11PixelShader**);
    CreatePixelShader_t realCreatePixelShader = nullptr;

    HRESULT STDMETHODCALLTYPE HookCreatePixelShader(ID3D11Device* self, const void* code, SIZE_T size, ID3D11ClassLinkage* link, ID3D11PixelShader** out)
    {
        HRESULT hr = realCreatePixelShader(self, code, size, link, out);
        if (SUCCEEDED(hr) && out && *out && code)
        {
            ShaderInfo info{ Fnv(code, size), static_cast<unsigned>(size), -1, -1, 0 };
            ID3D11ShaderReflection* refl = nullptr;
            if (SUCCEEDED(D3DReflect(code, size, IID_ID3D11ShaderReflection, reinterpret_cast<void**>(&refl))) && refl)
            {
                D3D11_SHADER_DESC d{};
                refl->GetDesc(&d);
                int tex = 0;
                for (UINT r = 0; r < d.BoundResources; ++r)
                {
                    D3D11_SHADER_INPUT_BIND_DESC b{};
                    if (SUCCEEDED(refl->GetResourceBindingDesc(r, &b)) && b.Type == D3D_SIT_TEXTURE) ++tex;
                }
                info.textures = tex;
                info.cbuffers = static_cast<int>(d.ConstantBuffers);
                if (d.ConstantBuffers > 0)
                    if (auto* cb = refl->GetConstantBufferByIndex(0)) { D3D11_SHADER_BUFFER_DESC bd{}; if (SUCCEEDED(cb->GetDesc(&bd))) info.cb0Size = bd.Size; }
                refl->Release();
            }
            if (info.hash == UpscaleShaderHash) upscaleShader = *out;
            if (info.hash == TonemapShaderHash) tonemapShader = *out;
            if (info.hash == TaaShaderHash)
            {
                taaShader = *out;
                ID3DBlob* text = nullptr;
                if (settings.capture && SUCCEEDED(D3DDisassemble(code, size, 0, nullptr, &text)) && text)
                {
                    if (FILE* f = std::fopen((PluginDirectory() + "TamrielMergeGraphics-taa.txt").c_str(), "w"))
                    {
                        std::fwrite(text->GetBufferPointer(), 1, text->GetBufferSize(), f);
                        std::fclose(f);
                    }
                    text->Release();
                }
            }
            std::lock_guard lk(shaderLock);
            pixelShaders[*out] = info;
        }
        return hr;
    }

    std::string Describe(ID3D11Resource* r)
    {
        if (!r) return "-";
        ID3D11Texture2D* t = nullptr;
        char buf[160];
        if (SUCCEEDED(r->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&t))) && t)
        {
            D3D11_TEXTURE2D_DESC d{};
            t->GetDesc(&d);
            t->Release();
            std::snprintf(buf, sizeof(buf), "%p %ux%u f%u", static_cast<void*>(r), d.Width, d.Height, d.Format);
        }
        else std::snprintf(buf, sizeof(buf), "%p (not 2D)", static_cast<void*>(r));
        return buf;
    }

    void RecordDraw(ID3D11DeviceContext* ctx, unsigned vertices)
    {
        ID3D11PixelShader* ps = nullptr;
        ctx->PSGetShader(&ps, nullptr, nullptr);
        ShaderInfo info{};
        { std::lock_guard lk(shaderLock); if (ps) if (auto it = pixelShaders.find(ps); it != pixelShaders.end()) info = it->second; }
        std::string line = "draw " + std::to_string(vertices) + " ps " + (ps ? std::to_string(info.hash) : "none");
        {
            D3D11_VIEWPORT vp{};
            UINT n = 1;
            ctx->RSGetViewports(&n, &vp);
            char v[64];
            std::snprintf(v, sizeof(v), " vp %.0fx%.0f", vp.Width, vp.Height);
            line += v;
        }
        if (info.hash == TaaShaderHash)
        {
            ID3D11Buffer* cb = nullptr;
            ctx->PSGetConstantBuffers(12, 1, &cb);
            if (cb)
            {
                D3D11_BUFFER_DESC bd{};
                cb->GetDesc(&bd);
                D3D11_BUFFER_DESC sd = bd;
                sd.Usage = D3D11_USAGE_STAGING; sd.BindFlags = 0; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ; sd.MiscFlags = 0;
                ID3D11Buffer* staging = nullptr;
                if (bd.ByteWidth >= 45 * 16 && SUCCEEDED(device->CreateBuffer(&sd, nullptr, &staging)) && staging)
                {
                    ctx->CopyResource(staging, cb);
                    D3D11_MAPPED_SUBRESOURCE m{};
                    if (SUCCEEDED(ctx->Map(staging, 0, D3D11_MAP_READ, 0, &m)))
                    {
                        const float* f = static_cast<const float*>(m.pData) + 43 * 4;
                        char v[160];
                        std::snprintf(v, sizeof(v), " cb12[43] %g %g %g %g [44] %g %g %g %g", f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7]);
                        line += v;
                        ctx->Unmap(staging, 0);
                    }
                    staging->Release();
                }
                cb->Release();
            }
        }
        char extra[96];
        std::snprintf(extra, sizeof(extra), " (tex %d cb %d cb0 %u)", info.textures, info.cbuffers, info.cb0Size);
        line += extra;
        ID3D11RenderTargetView* rtvs[8]{};
        ID3D11DepthStencilView* dsv = nullptr;
        ctx->OMGetRenderTargets(8, rtvs, &dsv);
        line += " | out";
        for (auto* v : rtvs)
            if (v) { ID3D11Resource* r = nullptr; v->GetResource(&r); line += " [" + Describe(r) + "]"; if (r) r->Release(); v->Release(); }
        if (dsv) { ID3D11Resource* r = nullptr; dsv->GetResource(&r); line += " depth[" + Describe(r) + "]"; if (r) r->Release(); dsv->Release(); }
        ID3D11ShaderResourceView* srvs[12]{};
        ctx->PSGetShaderResources(0, 12, srvs);
        line += " | in";
        for (int i = 0; i < 12; ++i)
            if (srvs[i]) { ID3D11Resource* r = nullptr; srvs[i]->GetResource(&r); line += " " + std::to_string(i) + "[" + Describe(r) + "]"; if (r) r->Release(); srvs[i]->Release(); }
        // A full-screen pass: its first constants too (the camera's jitter lives in such constants).
        if (vertices <= 6)
        {
            ID3D11Buffer* cb = nullptr;
            ctx->PSGetConstantBuffers(0, 1, &cb);
            if (cb)
            {
                D3D11_BUFFER_DESC bd{};
                cb->GetDesc(&bd);
                D3D11_BUFFER_DESC sd = bd;
                sd.Usage = D3D11_USAGE_STAGING; sd.BindFlags = 0; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ; sd.MiscFlags = 0;
                ID3D11Buffer* staging = nullptr;
                if (SUCCEEDED(device->CreateBuffer(&sd, nullptr, &staging)) && staging)
                {
                    ctx->CopyResource(staging, cb);
                    D3D11_MAPPED_SUBRESOURCE m{};
                    if (SUCCEEDED(ctx->Map(staging, 0, D3D11_MAP_READ, 0, &m)))
                    {
                        line += " | cb0";
                        const float* f = static_cast<const float*>(m.pData);
                        for (unsigned i = 0; i < std::min(32u, bd.ByteWidth / 4); ++i) { char v[24]; std::snprintf(v, sizeof(v), " %.5g", f[i]); line += v; }
                        ctx->Unmap(staging, 0);
                    }
                    staging->Release();
                }
                cb->Release();
            }
        }
        if (ps) ps->Release();
        std::fprintf(captureFile, "%s\n", line.c_str());
    }

    // The game's immediate context keeps its function table inside itself (the object's first pointer points 8 bytes
    // on) and d3d11 rewrites entries of it as it runs, so a patched entry doesn't last. The context is pointed at a
    // table of ours instead: each entry a stub that jumps through d3d11's own current entry (jmp [rcx + 8 + 8i]), the
    // draw calls ours, which call d3d11's current entry in turn.
    constexpr int ContextEntries = 160;   // beyond ID3D11DeviceContext4's last method
    void** inlineTable = nullptr;         // d3d11's table, inside the context

    template <class F> F Real(int index) { return reinterpret_cast<F>(inlineTable[index]); }

    using Draw_t = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, UINT, UINT);
    using DrawIndexed_t = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, UINT, UINT, INT);
    using DrawIndexedInstanced_t = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, UINT, UINT, UINT, INT, UINT);
    using DrawInstanced_t = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, UINT, UINT, UINT, UINT);

    using PSSetShader_t = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, ID3D11PixelShader*, ID3D11ClassInstance* const*, UINT);

    void STDMETHODCALLTYPE HookPSSetShader(ID3D11DeviceContext* ctx, ID3D11PixelShader* ps, ID3D11ClassInstance* const* instances, UINT count)
    {
        currentPixelShader = ps;
        Real<PSSetShader_t>(9)(ctx, ps, instances, count);
    }

    using PSSetSamplers_t = void(STDMETHODCALLTYPE*)(ID3D11DeviceContext*, UINT, UINT, ID3D11SamplerState* const*);
    // Below full size, the textures' samplers are swapped for copies biased by log2(drawn / shown), as DLSS asks.
    // The game's own samplers are kept referenced while copied (so a freed one's address can't come back as another).
    struct BiasedSampler { D3D11_SAMPLER_DESC desc; ID3D11SamplerState* copy; };
    std::unordered_map<ID3D11SamplerState*, BiasedSampler> biasedSamplers;
    std::unordered_map<ID3D11SamplerState*, ID3D11SamplerState*> originalOf;   // our copy -> the game's sampler
    float samplersBias = 0.0f;

    ID3D11SamplerState* Biased(ID3D11SamplerState* game)
    {
        if (!game) return game;
        if (originalOf.count(game)) return game;   // already one of ours
        D3D11_SAMPLER_DESC d{};
        game->GetDesc(&d);
        // Every mipmapped sampler (mips linear, point or anisotropic: the ground's too), not a plain point sampler
        // (full-screen passes) nor a comparison / minimum / maximum one (shadows).
        if (d.Filter == D3D11_FILTER_MIN_MAG_MIP_POINT || (d.Filter & 0x180) || d.MaxLOD <= 0.0f) return game;
        auto it = biasedSamplers.find(game);
        if (it != biasedSamplers.end())
        {
            if (std::memcmp(&it->second.desc, &d, sizeof(d)) == 0) return it->second.copy ? it->second.copy : game;
            if (it->second.copy) { originalOf.erase(it->second.copy); it->second.copy->Release(); }
            game->Release();
            biasedSamplers.erase(it);
        }
        D3D11_SAMPLER_DESC b = d;
        b.MipLODBias = std::clamp(d.MipLODBias + samplersBias, -16.0f, 15.99f);
        ID3D11SamplerState* copy = nullptr;
        if (FAILED(device->CreateSamplerState(&b, &copy))) copy = nullptr;
        game->AddRef();
        biasedSamplers[game] = { d, copy };
        if (copy) originalOf[copy] = game;
        return copy ? copy : game;
    }

    void STDMETHODCALLTYPE HookPSSetSamplers(ID3D11DeviceContext* ctx, UINT start, UINT count, ID3D11SamplerState* const* samplers)
    {
        if (textureBias != samplersBias)
        {
            for (auto& [game, b] : biasedSamplers) { if (b.copy) b.copy->Release(); game->Release(); }
            biasedSamplers.clear();
            originalOf.clear();
            samplersBias = textureBias;
        }
        if (samplersBias == 0.0f || !samplers || count == 0 || count > D3D11_COMMONSHADER_SAMPLER_SLOT_COUNT)
        {
            Real<PSSetSamplers_t>(10)(ctx, start, count, samplers);
            return;
        }
        ID3D11SamplerState* swapped[D3D11_COMMONSHADER_SAMPLER_SLOT_COUNT];
        for (UINT i = 0; i < count; ++i) swapped[i] = Biased(samplers[i]);
        Real<PSSetSamplers_t>(10)(ctx, start, count, swapped);
    }

    /// Once a frame: the samplers bound now passed through the bias again. Skyrim's renderer only sets a sampler when it
    /// changes, so the ones set before upscaling started (the ground's among them: the soft road in Quality mode,
    /// 2026-10-09) kept no bias; and when the bias changes or stops, ours go back to the game's.
    void RebindSamplers(ID3D11DeviceContext* ctx)
    {
        if (!ctx || !inlineTable || (textureBias == 0.0f && originalOf.empty())) return;
        ID3D11SamplerState* bound[D3D11_COMMONSHADER_SAMPLER_SLOT_COUNT]{};
        ctx->PSGetSamplers(0, D3D11_COMMONSHADER_SAMPLER_SLOT_COUNT, bound);
        ID3D11SamplerState* game[D3D11_COMMONSHADER_SAMPLER_SLOT_COUNT]{};
        for (UINT i = 0; i < D3D11_COMMONSHADER_SAMPLER_SLOT_COUNT; ++i)
        {
            auto it = bound[i] ? originalOf.find(bound[i]) : originalOf.end();
            game[i] = it != originalOf.end() ? it->second : bound[i];
            if (game[i]) game[i]->AddRef();   // kept alive across a bias change below
        }
        HookPSSetSamplers(ctx, 0, D3D11_COMMONSHADER_SAMPLER_SLOT_COUNT, game);
        for (UINT i = 0; i < D3D11_COMMONSHADER_SAMPLER_SLOT_COUNT; ++i) { if (game[i]) game[i]->Release(); if (bound[i]) bound[i]->Release(); }
    }

    /// DLSS in place of the TAA draw or the upscale pass.
    bool Replaced(ID3D11DeviceContext* ctx)
    {
        if (!currentPixelShader) return false;
        if (currentPixelShader == taaShader.load()) return OnTaaDraw(ctx);
        if (currentPixelShader == upscaleShader.load()) return OnUpscaleDraw(ctx);
        return false;
    }

    void STDMETHODCALLTYPE HookDrawIndexed(ID3D11DeviceContext* ctx, UINT count, UINT start, INT base)
    {
        if (capturing && captureFile && !inOurPass) RecordDraw(ctx, count);
        ++drawCalls;
        if (!inOurPass)
        {
            if (count <= 6) OnTonemapDraw(ctx, count);
            if (Replaced(ctx)) return;
        }
        Real<DrawIndexed_t>(12)(ctx, count, start, base);
    }

    void STDMETHODCALLTYPE HookDraw(ID3D11DeviceContext* ctx, UINT count, UINT start)
    {
        if (capturing && captureFile && !inOurPass) RecordDraw(ctx, count);
        ++drawCalls;
        if (!inOurPass)
        {
            if (count <= 6) OnTonemapDraw(ctx, count);
            if (Replaced(ctx)) return;
        }
        Real<Draw_t>(13)(ctx, count, start);
    }

    void STDMETHODCALLTYPE HookDrawIndexedInstanced(ID3D11DeviceContext* ctx, UINT count, UINT instances, UINT start, INT base, UINT startInstance)
    {
        if (capturing && captureFile) RecordDraw(ctx, count);
        ++drawCalls;
        Real<DrawIndexedInstanced_t>(20)(ctx, count, instances, start, base, startInstance);
    }

    void STDMETHODCALLTYPE HookDrawInstanced(ID3D11DeviceContext* ctx, UINT count, UINT instances, UINT start, UINT startInstance)
    {
        if (capturing && captureFile) RecordDraw(ctx, count);
        ++drawCalls;
        Real<DrawInstanced_t>(21)(ctx, count, instances, start, startInstance);
    }

    std::string ModuleOf(const void* p)
    {
        HMODULE m = nullptr;
        char name[MAX_PATH] = "(none)";
        if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, static_cast<LPCSTR>(p), &m) && m)
            GetModuleFileNameA(m, name, sizeof(name));
        return name;
    }

    /// The draw calls of the game's own context: DrawIndexed 12, Draw 13, DrawIndexedInstanced 20, DrawInstanced 21.
    void HookContext(ID3D11DeviceContext* ctx)
    {
        void** table = *reinterpret_cast<void***>(ctx);
        if (reinterpret_cast<char*>(table) != reinterpret_cast<char*>(ctx) + 8)
        {
            Log("context %p: its table %p (in %s) isn't the inline kind; draws not hooked", static_cast<void*>(ctx), static_cast<void*>(table), ModuleOf(table).c_str());
            return;
        }
        inlineTable = table;
        auto* stubs = static_cast<unsigned char*>(VirtualAlloc(nullptr, ContextEntries * 8, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
        auto** ours = static_cast<void**>(VirtualAlloc(nullptr, ContextEntries * sizeof(void*), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
        if (!stubs || !ours) { Log("context: no memory for the table"); return; }
        for (int i = 0; i < ContextEntries; ++i)
        {
            unsigned char* st = stubs + i * 8;
            std::int32_t disp = 8 + i * 8;
            st[0] = 0xFF; st[1] = 0xA1;                        // jmp qword ptr [rcx + disp32]
            std::memcpy(st + 2, &disp, 4);
            st[6] = 0xCC; st[7] = 0xCC;
            ours[i] = st;
        }
        DWORD old = 0;
        VirtualProtect(stubs, ContextEntries * 8, PAGE_EXECUTE_READ, &old);
        FlushInstructionCache(GetCurrentProcess(), stubs, ContextEntries * 8);
        ours[9] = reinterpret_cast<void*>(&HookPSSetShader);
        ours[10] = reinterpret_cast<void*>(&HookPSSetSamplers);
        ours[12] = reinterpret_cast<void*>(&HookDrawIndexed);
        ours[13] = reinterpret_cast<void*>(&HookDraw);
        ours[20] = reinterpret_cast<void*>(&HookDrawIndexedInstanced);
        ours[21] = reinterpret_cast<void*>(&HookDrawInstanced);
        InterlockedExchangePointer(reinterpret_cast<void* volatile*>(ctx), ours);
        Log("context %p: draw calls hooked through our table (d3d11's DrawIndexed in %s)", static_cast<void*>(ctx), ModuleOf(table[12]).c_str());
    }
    bool HookPresentTable();

    // ---- State ----
    std::atomic<bool> menuOpen{ false };
    bool ready = false;
    HWND gameWindow = nullptr;
    WNDPROC gameWndProc = nullptr;
    ID3D11DeviceContext* context = nullptr;
    ID3D11RenderTargetView* backBufferView = nullptr;
    std::string gpuName, dlssStatus = "not checked yet";
    bool dlssAvailable = false;
    NVSDK_NGX_Parameter* ngxParams = nullptr;

    // ---- DLSS (NGX) availability ----
    void StartNgx()
    {
        std::wstring dir;
        {
            // NVIDIA's runtime in a folder of its own, so SKSE doesn't take it for a plugin.
            std::string d = PluginDirectory() + "TamrielMergeGraphics\\";
            dir.assign(d.begin(), d.end());
        }
        const wchar_t* paths[] = { dir.c_str() };
        NVSDK_NGX_FeatureCommonInfo info{};
        info.PathListInfo.Path = paths;
        info.PathListInfo.Length = 1;
        // A project ID of our own (a GUID) with the "custom engine" type, as NVIDIA's SDK asks of non-registered apps.
        NVSDK_NGX_Result r = NVSDK_NGX_D3D11_Init_with_ProjectID("6f1c2b7e-3a4d-4f5e-9a1b-7c2d8e9f0a11", NVSDK_NGX_ENGINE_TYPE_CUSTOM, "1.0",
                                                               dir.c_str(), device, &info, NVSDK_NGX_Version_API);
        if (NVSDK_NGX_FAILED(r)) { dlssStatus = "NVIDIA NGX couldn't start (not an NVIDIA RTX card, or an old driver)"; Log("NGX init failed: 0x%08X", r); return; }
        NVSDK_NGX_Parameter* params = nullptr;
        r = NVSDK_NGX_D3D11_GetCapabilityParameters(&params);
        if (NVSDK_NGX_FAILED(r) || !params) { dlssStatus = "NVIDIA NGX gave no capabilities"; Log("NGX capabilities failed: 0x%08X", r); return; }
        int available = 0, needsDriver = 0;
        unsigned int major = 0, minor = 0;
        params->Get(NVSDK_NGX_Parameter_SuperSampling_Available, &available);
        params->Get(NVSDK_NGX_Parameter_SuperSampling_NeedsUpdatedDriver, &needsDriver);
        params->Get(NVSDK_NGX_Parameter_SuperSampling_MinDriverVersionMajor, &major);
        params->Get(NVSDK_NGX_Parameter_SuperSampling_MinDriverVersionMinor, &minor);
        dlssAvailable = available != 0;
        if (dlssAvailable) dlssStatus = "DLSS is available on this card";
        else if (needsDriver) dlssStatus = "DLSS needs a newer NVIDIA driver (" + std::to_string(major) + "." + std::to_string(minor) + " or later)";
        else dlssStatus = "DLSS isn't available on this card";
        Log("DLSS available %d, needs driver %d (%u.%u)", available, needsDriver, major, minor);
        ngxParams = params;
    }

    // ---- DLSS in place of Skyrim's TAA ----
    // Native (DLAA): the TAA draw becomes DLSS. Upscaling: the game draws into a smaller part of its targets (its own
    // dynamic resolution: BSGraphics::State + 0x114 / 0x118 the width and height ratios), the TAA draw passes that
    // picture through, and the game's own upscale pass (a full-screen draw reading that part) becomes DLSS.
    // The camera's jitter: State + 0x44 / 0x48 hold 2 * jitter / screen width (height), the jitter taken from an
    // 8-entry Halton table at + 0xD4 (base 2; base 3 at + 0xF4) by the frame count at + 0xD0. For upscaling, the next
    // frame's entry is refilled from a longer sequence, spread over a whole render pixel.
    const float ModeRatio[] = { 1.0f, 1.0f, 2.0f / 3.0f, 0.58f, 0.5f, 1.0f / 3.0f };
    const NVSDK_NGX_PerfQuality_Value ModeQuality[] = { NVSDK_NGX_PerfQuality_Value_DLAA, NVSDK_NGX_PerfQuality_Value_DLAA,
        NVSDK_NGX_PerfQuality_Value_MaxQuality, NVSDK_NGX_PerfQuality_Value_Balanced, NVSDK_NGX_PerfQuality_Value_MaxPerf,
        NVSDK_NGX_PerfQuality_Value_UltraPerformance };

    NVSDK_NGX_Handle* dlssFeature = nullptr;
    ID3D11Texture2D* dlssOutput = nullptr;
    UINT featureIn[2]{}, featureOut[2]{};
    int featureMode = -1;
    unsigned sizeChanges = 0;
    bool dlssBroken = false, dlssReset = true;
    unsigned dlssFrames = 0;
    std::atomic<unsigned> presentFrame{ 0 }, lastDlssFrame{ 0 };
    ID3DDeviceContextState* dlssState = nullptr;   // a clean pipeline state for NGX, the game's put back after
    float frameRatio = 1.0f;                       // the ratio this frame is drawn at (set as the last one ended)
    std::string dlssInfo;                          // what's drawing, for the menu
    // Which of the two passes the frame being drawn had: only a frame of the game world (with TAA) is drawn smaller,
    // since the splash, loading screens and menus have no upscale pass and would show in a corner, smaller.
    bool frameSawTaa = false, frameSawUpscale = false;
    unsigned upscaleHoldoff = 0;
    // Upscaling: what the TAA draw leaves for the upscale pass.
    ID3D11Resource* heldDepth = nullptr, * heldMotion = nullptr;
    UINT heldSize[2]{};
    float heldJitter[2]{};

    ID3D11Resource* BoundResource(ID3D11ShaderResourceView* v)
    {
        if (!v) return nullptr;
        ID3D11Resource* r = nullptr;
        v->GetResource(&r);
        v->Release();
        return r;
    }

    bool DlssUsable() { return settings.upscaler != Off && dlssAvailable && !dlssBroken && ngxParams && gameState; }

    void DlssFailed(const char* what, NVSDK_NGX_Result r)
    {
        Log("DLSS: %s (0x%08X); Skyrim's own anti-aliasing is kept", what, r);
        dlssBroken = true;
        dlssStatus = std::string("DLSS ") + what + " here; Skyrim's own anti-aliasing is kept (see the log)";
    }

    bool EnsureFeature(ID3D11DeviceContext* ctx, int mode, UINT inW, UINT inH, const D3D11_TEXTURE2D_DESC& od)
    {
        // Made for the largest picture it could be given (the screen's size): the part drawn can then change from
        // frame to frame (the game's dynamic resolution) without a new one.
        if (dlssFeature && featureMode == mode && featureOut[0] == od.Width && featureOut[1] == od.Height)
            return true;
        if (dlssFeature) { NVSDK_NGX_D3D11_ReleaseFeature(dlssFeature); dlssFeature = nullptr; }
        if (!dlssOutput || featureOut[0] != od.Width || featureOut[1] != od.Height)
        {
            if (dlssOutput) { dlssOutput->Release(); dlssOutput = nullptr; }
            D3D11_TEXTURE2D_DESC d = od;
            d.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
            d.MiscFlags = 0; d.CPUAccessFlags = 0; d.Usage = D3D11_USAGE_DEFAULT;
            if (FAILED(device->CreateTexture2D(&d, nullptr, &dlssOutput))) { DlssFailed("had no output texture", static_cast<NVSDK_NGX_Result>(0)); return false; }
        }
        if (!dlssState)
        {
            ID3D11Device1* d1 = nullptr;
            if (SUCCEEDED(device->QueryInterface(__uuidof(ID3D11Device1), reinterpret_cast<void**>(&d1))) && d1)
            {
                D3D_FEATURE_LEVEL level = device->GetFeatureLevel();
                d1->CreateDeviceContextState(0, &level, 1, D3D11_SDK_VERSION, __uuidof(ID3D11Device1), nullptr, &dlssState);
                d1->Release();
            }
        }
        NVSDK_NGX_DLSS_Create_Params cp{};
        cp.Feature.InWidth = od.Width; cp.Feature.InHeight = od.Height;
        cp.Feature.InTargetWidth = od.Width; cp.Feature.InTargetHeight = od.Height;
        cp.Feature.InPerfQualityValue = ModeQuality[mode];
        cp.InFeatureCreateFlags = NVSDK_NGX_DLSS_Feature_Flags_MVLowRes;
        NVSDK_NGX_Result r = NGX_D3D11_CREATE_DLSS_EXT(ctx, &dlssFeature, ngxParams, &cp);
        if (NVSDK_NGX_FAILED(r) || !dlssFeature) { dlssFeature = nullptr; DlssFailed("couldn't start", r); return false; }
        featureMode = mode;
        featureIn[0] = inW; featureIn[1] = inH; featureOut[0] = od.Width; featureOut[1] = od.Height;
        dlssReset = true;
        char info[128];
        std::snprintf(info, sizeof(info), "%s: %ux%u drawn, %ux%u shown.", UpscalerNames[mode], inW, inH, od.Width, od.Height);
        dlssInfo = info;
        Log("DLSS: %s", info);
        return true;
    }

    // ---- Sharpening after DLSS (DLSS 2.5.1 and later have none): our own pass (shaders\Game11.hlsl, SharpenPS) ----
    // From DLSS's output into the game's target, by the menu's Sharpness; 0 copies the output as it is.
    ID3D11VertexShader* sharpenVs = nullptr;
    ID3D11PixelShader* sharpenPs = nullptr;
    ID3D11Buffer* sharpenCb = nullptr;
    ID3D11ShaderResourceView* dlssOutputView = nullptr;
    struct TargetView { ID3D11Resource* res; ID3D11RenderTargetView* rtv; };
    std::vector<TargetView> sharpenTargets;
    bool sharpenBroken = false;

    ID3D11RenderTargetView* TargetFor(ID3D11Resource* r)
    {
        for (auto& t : sharpenTargets) if (t.res == r) return t.rtv;
        if (sharpenTargets.size() >= 8) { for (auto& t : sharpenTargets) { t.rtv->Release(); t.res->Release(); } sharpenTargets.clear(); }
        D3D11_TEXTURE2D_DESC d{};
        static_cast<ID3D11Texture2D*>(r)->GetDesc(&d);
        if (!(d.BindFlags & D3D11_BIND_RENDER_TARGET)) return nullptr;
        D3D11_RENDER_TARGET_VIEW_DESC v{};
        v.Format = d.Format == DXGI_FORMAT_R8G8B8A8_TYPELESS ? DXGI_FORMAT_R8G8B8A8_UNORM : d.Format == DXGI_FORMAT_R10G10B10A2_TYPELESS ? DXGI_FORMAT_R10G10B10A2_UNORM :
                   d.Format == DXGI_FORMAT_R16G16B16A16_TYPELESS ? DXGI_FORMAT_R16G16B16A16_FLOAT : d.Format;
        v.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
        ID3D11RenderTargetView* rtv = nullptr;
        if (FAILED(device->CreateRenderTargetView(r, &v, &rtv))) return nullptr;
        r->AddRef();
        sharpenTargets.push_back({ r, rtv });
        return rtv;
    }

    bool Sharpen(ID3D11DeviceContext* ctx, ID3D11Resource* out, const D3D11_TEXTURE2D_DESC& od)
    {
        if (sharpenBroken || !dlssState) return false;
        if (!sharpenPs)
        {
            D3D11_BUFFER_DESC bd{};
            bd.ByteWidth = sizeof(rt::SharpenConstants); bd.Usage = D3D11_USAGE_DYNAMIC; bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER; bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
            if (FAILED(device->CreateVertexShader(g_FullscreenVS, sizeof(g_FullscreenVS), nullptr, &sharpenVs)) ||
                FAILED(device->CreatePixelShader(g_SharpenPS, sizeof(g_SharpenPS), nullptr, &sharpenPs)) || FAILED(device->CreateBuffer(&bd, nullptr, &sharpenCb)))
            { sharpenBroken = true; Log("sharpening: its shaders didn't load; DLSS's output is shown unsharpened"); return false; }
        }
        if (!dlssOutputView && FAILED(device->CreateShaderResourceView(dlssOutput, nullptr, &dlssOutputView))) { dlssOutputView = nullptr; return false; }
        ID3D11RenderTargetView* rtv = TargetFor(out);
        if (!rtv) return false;
        ID3D11DeviceContext1* c1 = nullptr;
        if (FAILED(ctx->QueryInterface(__uuidof(ID3D11DeviceContext1), reinterpret_cast<void**>(&c1))) || !c1) return false;
        ID3DDeviceContextState* game = nullptr;
        c1->SwapDeviceContextState(dlssState, &game);
        D3D11_MAPPED_SUBRESOURCE m{};
        if (SUCCEEDED(ctx->Map(sharpenCb, 0, D3D11_MAP_WRITE_DISCARD, 0, &m)))
        {
            rt::SharpenConstants k{ { std::clamp(settings.sharpness, 0.0f, 1.0f), 0, 0, 0 }, { od.Width, od.Height, 0, 0 } };
            std::memcpy(m.pData, &k, sizeof(k));
            ctx->Unmap(sharpenCb, 0);
        }
        D3D11_VIEWPORT vp{ 0, 0, float(od.Width), float(od.Height), 0, 1 };
        ctx->OMSetRenderTargets(1, &rtv, nullptr);
        ctx->RSSetViewports(1, &vp);
        ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        ctx->VSSetShader(sharpenVs, nullptr, 0);
        ctx->PSSetShader(sharpenPs, nullptr, 0);
        ctx->PSSetConstantBuffers(1, 1, &sharpenCb);
        ctx->PSSetShaderResources(0, 1, &dlssOutputView);
        ctx->Draw(3, 0);
        ID3D11ShaderResourceView* none = nullptr;
        ctx->PSSetShaderResources(0, 1, &none);
        ctx->OMSetRenderTargets(0, nullptr, nullptr);
        c1->SwapDeviceContextState(game, nullptr);
        if (game) game->Release();
        c1->Release();
        return true;
    }

    /// DLSS from color (its top-left inW x inH, jittered by `jitter` in 2 * pixels / screen units) into `out` (sharpened
    /// by our own pass), and its unsharpened output into `history` when given.
    bool Evaluate(ID3D11DeviceContext* ctx, int mode, ID3D11Resource* color, ID3D11Resource* depth, ID3D11Resource* motion,
                  ID3D11Resource* out, UINT inW, UINT inH, const float jitter[2], ID3D11Resource* history = nullptr)
    {
        D3D11_TEXTURE2D_DESC od{};
        static_cast<ID3D11Texture2D*>(out)->GetDesc(&od);
        ID3D11Texture2D* before = dlssOutput;
        if (!EnsureFeature(ctx, mode, inW, inH, od)) return false;
        if (dlssOutput != before && dlssOutputView) { dlssOutputView->Release(); dlssOutputView = nullptr; }
        ID3D11PixelShader* keep = currentPixelShader;
        inOurPass = true;
        ID3DDeviceContextState* gameStateObj = nullptr;
        ID3D11DeviceContext1* c1 = nullptr;
        if (dlssState && SUCCEEDED(ctx->QueryInterface(__uuidof(ID3D11DeviceContext1), reinterpret_cast<void**>(&c1))) && c1)
            c1->SwapDeviceContextState(dlssState, &gameStateObj);
        NVSDK_NGX_D3D11_DLSS_Eval_Params ep{};
        ep.Feature.pInColor = color;
        ep.Feature.pInOutput = dlssOutput;
        ep.Feature.InSharpness = 0.0f;   // ignored by DLSS 2.5.1 and later; ours follows
        ep.pInDepth = depth;
        ep.pInMotionVectors = motion;
        ep.InJitterOffsetX = settings.jitterSignX * jitter[0] * inW * 0.5f;
        ep.InJitterOffsetY = settings.jitterSignY * jitter[1] * inH * 0.5f;
        ep.InRenderSubrectDimensions.Width = inW;
        ep.InRenderSubrectDimensions.Height = inH;
        ep.InReset = dlssReset ? 1 : 0;
        ep.InMVScaleX = settings.motionScaleX * inW;
        ep.InMVScaleY = settings.motionScaleY * inH;
        ep.InPreExposure = 1.0f;
        ep.InExposureScale = 1.0f;
        NVSDK_NGX_Result r = NGX_D3D11_EVALUATE_DLSS_EXT(ctx, dlssFeature, ngxParams, &ep);
        if (c1)
        {
            if (gameStateObj) { c1->SwapDeviceContextState(gameStateObj, nullptr); gameStateObj->Release(); }
            c1->Release();
        }
        if (NVSDK_NGX_FAILED(r)) { inOurPass = false; currentPixelShader = keep; DlssFailed("failed", r); return false; }
        if (history) ctx->CopyResource(history, dlssOutput);
        if (settings.sharpness < 0.005f || !Sharpen(ctx, out, od)) ctx->CopyResource(out, dlssOutput);
        inOurPass = false;
        currentPixelShader = keep;
        dlssReset = false;
        lastDlssFrame = presentFrame.load();
        if (inW != featureIn[0] || inH != featureIn[1])
        {
            featureIn[0] = inW; featureIn[1] = inH;
            char info[128];
            std::snprintf(info, sizeof(info), "%s: %ux%u drawn, %ux%u shown.", UpscalerNames[mode], inW, inH, od.Width, od.Height);
            dlssInfo = info;
            if (++sizeChanges <= 4) Log("DLSS: %s", info);
        }
        if (++dlssFrames == 1) Log("DLSS: first frame done (jitter %.3f, %.3f px)", ep.InJitterOffsetX, ep.InJitterOffsetY);
        return true;
    }

    void ReleaseHeld()
    {
        if (heldDepth) { heldDepth->Release(); heldDepth = nullptr; }
        if (heldMotion) { heldMotion->Release(); heldMotion = nullptr; }
    }

    float writtenJitter[8][2]{};   // the jitter table offsets written per slot (diagnostic)
    unsigned jitterChecks = 0;

    /// Skyrim's TAA draw: t0 the picture, t2 the motion vectors, t3 the depth; target 0 the result, 1 the history.
    void KeepTaaInputs(ID3D11Resource* depth, ID3D11Resource* motion);

    bool OnTaaDraw(ID3D11DeviceContext* ctx)
    {
        frameSawTaa = true;
        ReleaseHeld();
        {
            // Its depth and motion vectors, for the ray tracing at the next frame's tonemapping (the same textures).
            ID3D11ShaderResourceView* v[2]{};
            ctx->PSGetShaderResources(2, 2, v);
            ID3D11Resource* m = BoundResource(v[0]), * d = BoundResource(v[1]);
            KeepTaaInputs(d, m);
            if (m) m->Release();
            if (d) d->Release();
        }
        if (!DlssUsable()) return false;
        ID3D11ShaderResourceView* views[4]{};
        ctx->PSGetShaderResources(0, 4, views);
        ID3D11Resource* color = BoundResource(views[0]);
        BoundResource(views[1]);
        ID3D11Resource* motion = BoundResource(views[2]);
        ID3D11Resource* depth = BoundResource(views[3]);
        ID3D11RenderTargetView* targets[2]{};
        ctx->OMGetRenderTargets(2, targets, nullptr);
        ID3D11Resource* out = nullptr, * history = nullptr;
        if (targets[0]) { targets[0]->GetResource(&out); targets[0]->Release(); }
        if (targets[1]) { targets[1]->GetResource(&history); targets[1]->Release(); }
        D3D11_VIEWPORT vp{};
        UINT n = 1;
        ctx->RSGetViewports(&n, &vp);
        float jitter[2]{};
        SafeCopy(jitter, gameState + 0x44, sizeof(jitter));
        bool done = false;
        if (color && motion && depth && out && n == 1)
        {
            UINT w = static_cast<UINT>(vp.Width + 0.5f), h = static_cast<UINT>(vp.Height + 0.5f);
            if (frameRatio >= 0.999f)
            {
                // Skyrim's own history kept current (unsharpened), for switching back.
                done = Evaluate(ctx, DLAA, color, depth, motion, out, w, h, jitter, history);
            }
            else
            {
                // The jitter (user, 2026-10-09: "dlss and the other upscalers are jittery"). Settled from the game's code
                // (SkyrimSE.exe 1.7.104 at 0x14101E360, which advances State + 0xD0 and fills + 0x44): + 0x44 =
                // (2 * table - 1) / State + 0x24, and + 0x24 / + 0x28 are the screen's full width / height (the
                // dynamic resolution code steps its ratio in quarters of them): so 2j / full, as assumed. These lines
                // show it in game: the table's offset, + 0x44 in full and drawn pixels, and + 0x24 / + 0x28.
                if (jitterChecks < 24)
                {
                    unsigned count = 0, full[2]{};
                    SafeCopy(&count, gameState + 0xD0, sizeof(count));
                    SafeCopy(full, gameState + 0x24, sizeof(full));
                    D3D11_TEXTURE2D_DESC od{};
                    if (auto* t = static_cast<ID3D11Texture2D*>(out)) t->GetDesc(&od);
                    unsigned slot = count % 8;
                    if (jitterChecks++ % 3 == 0)
                        Log("jitter check: frame %u slot %u table %.3f, %.3f; game %.5f, %.5f = %.3f, %.3f full px = %.3f, %.3f drawn px (%ux%u of %ux%u; State + 0x24 %u x %u)",
                            count, slot, writtenJitter[slot][0], writtenJitter[slot][1], jitter[0], jitter[1],
                            jitter[0] * od.Width * 0.5f, jitter[1] * od.Height * 0.5f, jitter[0] * w * 0.5f, jitter[1] * h * 0.5f, w, h, od.Width, od.Height, full[0], full[1]);
                }
                // Passed through; DLSS runs at the upscale pass, from the game's finished (smaller) picture.
                D3D11_BOX box{ 0, 0, 0, w, h, 1 };
                ctx->CopySubresourceRegion(out, 0, 0, 0, 0, color, 0, &box);
                if (history) ctx->CopySubresourceRegion(history, 0, 0, 0, 0, color, 0, &box);
                heldDepth = depth; heldMotion = motion; depth = motion = nullptr;
                heldSize[0] = w; heldSize[1] = h;
                heldJitter[0] = jitter[0]; heldJitter[1] = jitter[1];
                done = true;
            }
        }
        for (ID3D11Resource* r : { color, motion, depth, out, history }) if (r) r->Release();
        return done;
    }

    /// The game's upscale pass (only drawn below full resolution): t0 the picture in its top-left part, target 0 the
    /// full-size result.
    bool OnUpscaleDraw(ID3D11DeviceContext* ctx)
    {
        frameSawUpscale = true;
        if (!heldDepth || !DlssUsable()) { ReleaseHeld(); return false; }
        ID3D11ShaderResourceView* view = nullptr;
        ctx->PSGetShaderResources(0, 1, &view);
        ID3D11Resource* color = BoundResource(view);
        ID3D11RenderTargetView* target = nullptr;
        ctx->OMGetRenderTargets(1, &target, nullptr);
        ID3D11Resource* out = nullptr;
        if (target) { target->GetResource(&out); target->Release(); }
        int mode = std::clamp(settings.upscaler, static_cast<int>(Quality), static_cast<int>(UltraPerformance));
        bool done = color && out && Evaluate(ctx, mode, color, heldDepth, heldMotion, out, heldSize[0], heldSize[1], heldJitter);
        if (color) color->Release();
        if (out) out->Release();
        ReleaseHeld();
        return done;
    }

    // The next frame: its drawing ratio, and (below full size) its jitter.
    float defaultHalton[16]{};
    bool haltonSaved = false, haltonChanged = false, ratioChanged = false;
    unsigned jitterIndex = 0;

    float Halton(unsigned i, unsigned base)
    {
        float f = 1, r = 0;
        for (; i > 0; i /= base) { f /= base; r += f * (i % base); }
        return r;
    }

    bool* autoDynamicResolution = nullptr;
    bool autoSaved = false, autoWas = false, dynamicLocked = false;

    void PrepareNextFrame()
    {
        if (!gameState) return;
        if (!haltonSaved) haltonSaved = SafeCopy(defaultHalton, gameState + 0xD4, sizeof(defaultHalton));
        // A frame drawn smaller that the game didn't stretch back: full size for a while (10 s at 60 fps).
        if (frameRatio < 0.999f && !frameSawUpscale && frameSawTaa)
        {
            upscaleHoldoff = 600;
            Log("DLSS: a frame drawn smaller had no upscale pass; full size for a while");
        }
        bool world = frameSawTaa;
        frameSawTaa = frameSawUpscale = false;
        if (upscaleHoldoff) --upscaleHoldoff;
        bool upscaling = DlssUsable() && settings.upscaler > DLAA && taaShader && upscaleShader && world && upscaleHoldoff == 0;
        float ratio = upscaling ? ModeRatio[settings.upscaler] : 1.0f;
        if (autoDynamicResolution)
        {
            if (upscaling && !autoSaved) { autoWas = *autoDynamicResolution; autoSaved = true; *autoDynamicResolution = false; }
            else if (!upscaling && autoSaved) { *autoDynamicResolution = autoWas; autoSaved = false; }
        }
        // The game's own dynamic resolution steps its ratio by +0.03 / -0.07 when frames are fast or slow (SkyrimSE.exe
        // 1.7.104 at 0x14101E130), whatever bEnableAutoDynamicResolution says, rounded to quarters of the screen's size:
        // 2/3 + 0.03 is 1336 x 752, so under DLSS Quality the drawn size flipped between that and our 1280 x 720 (the
        // log of 2026-10-09) and DLSS saw the picture jump. Its own lock (State + 0x128, a count it also takes itself)
        // is held while DLSS upscales, so only our ratio is used; the last frame's ratio (+ 0x11C / + 0x120, which the
        // locked update no longer copies) is kept by us.
        auto* lock = reinterpret_cast<volatile LONG*>(gameState + 0x128);
        if (upscaling && !dynamicLocked) { InterlockedIncrement(lock); dynamicLocked = true; Log("DLSS: the game's dynamic resolution locked to %.3f", ratio); }
        else if (!upscaling && dynamicLocked) { if (*lock > 0) InterlockedDecrement(lock); dynamicLocked = false; Log("DLSS: the game's dynamic resolution let go"); }
        if (upscaling || ratioChanged)
        {
            float prev[2] = { frameRatio, frameRatio }, r[2] = { ratio, ratio };
            std::memcpy(gameState + 0x11C, prev, sizeof(prev));
            std::memcpy(gameState + 0x114, r, sizeof(r));
            ratioChanged = upscaling;
        }
        if (upscaling)
        {
            // DLSS asks for about 8 * (screen / drawn)^2 jitter phases.
            unsigned phases = static_cast<unsigned>(8.0f / (ratio * ratio) + 0.5f);
            unsigned count = 0;
            SafeCopy(&count, gameState + 0xD0, sizeof(count));
            unsigned slot = (count + 1) % 8;
            unsigned i = (jitterIndex++ % phases) + 1;
            float x = 0.5f + (Halton(i, 2) - 0.5f) / ratio, y = 0.5f + (Halton(i, 3) - 0.5f) / ratio;
            std::memcpy(gameState + 0xD4 + slot * 4, &x, 4);
            std::memcpy(gameState + 0xF4 + slot * 4, &y, 4);
            writtenJitter[slot][0] = x - 0.5f; writtenJitter[slot][1] = y - 0.5f;
            haltonChanged = true;
        }
        else if (haltonChanged && haltonSaved)
        {
            std::memcpy(gameState + 0xD4, defaultHalton, sizeof(defaultHalton));
            haltonChanged = false;
        }
        if (ratio != frameRatio) dlssReset = true;
        frameRatio = ratio;
        textureBias = ratio < 0.999f ? std::log2(ratio) + settings.mipBiasOffset : 0.0f;
    }

    // ---- Hardware ray tracing (RtCore.cpp): the world from the scene graph, traced before Skyrim's tonemapping ----
    // Skyrim draws with Direct3D 11, which has no ray tracing; RtCore runs DXR on a Direct3D 12 device of its own on the
    // same GPU. Each frame, at the tonemapping pass (found by its shader, as TAA is): the scene graph's meshes and their
    // world transforms (camera-relative), people and creatures as capsules along their bones, the camera (NiCamera) and
    // the sun (the sky's light node); RtCore traces from the depth buffer and puts the results into the lit picture.
    // Layout found in game (1.7.104, docs/graphics-plugin.md): NiAVObject + 0x30 parent, + 0x7C world transform
    // (rotation 3x3 row-major, translation, scale), + 0xE4 world bound (centre, radius), + 0xF4 flags (bit 0 hidden);
    // NiNode + 0x110 children; BSGeometry + 0x110 model bound, + 0x120 / + 0x128 its two properties (alpha, shader;
    // told apart by their RTTI), + 0x130 skin instance, + 0x138 renderer block { ID3D11Buffer* vertices, ID3D11Buffer*
    // indices, vertex desc, refcount, CPU vertices, CPU 16-bit indices }; BSTriShape + 0x158 u16 triangles, + 0x15A
    // u16 vertices. Vertex desc: (desc & 0xF) * 4 bytes a vertex, UV offset ((desc >> 8) & 0xF) * 4: 16 for full
    // precision positions (3 floats), 8 for half (3 halves).
    rt::Engine* rtEngine = nullptr;
    std::string rtStatus = "Off.";
    bool rtTried = false;

    void RtLog(const char* s) { Log("%s", s); }

    const std::uint8_t* worldRoot = nullptr;
    unsigned rtFrames = 0;

    const std::uint8_t* FindWorldRoot()
    {
        int v[4]{};
        GameVersion(v);
        char path[MAX_PATH]{};
        GetModuleFileNameA(nullptr, path, MAX_PATH);
        std::string dir(path);
        dir = dir.substr(0, dir.find_last_of("\\/") + 1);
        char lib[MAX_PATH];
        std::snprintf(lib, sizeof(lib), "%sData\\SKSE\\Plugins\\versionlib-%d-%d-%d-%d.bin", dir.c_str(), v[0], v[1], v[2], v[3]);
        static std::uint64_t playerOffset = 0;
        if (!playerOffset && !AddressLibraryOffset(lib, 403521, playerOffset)) return nullptr;
        const std::uint8_t* player = nullptr, * loaded = nullptr, * node = nullptr;
        if (!Read(reinterpret_cast<std::uint8_t*>(GetModuleHandleA(nullptr)) + playerOffset, player) || !player) return nullptr;
        if (!Read(player + 0x68, loaded) || !loaded || !Read(loaded + 0x68, node) || !node) return nullptr;
        for (int i = 0; i < 32; ++i)
        {
            const std::uint8_t* parent = nullptr;
            if (!Read(node + 0x30, parent) || !parent) break;
            node = parent;
        }
        return TypeName(node) == "SceneGraph" ? node : nullptr;
    }

    // A node's children (NiNode + 0x110: an NiTArray { vtable, data, capacity u16, free u16, size u16, grow u16 }).
    bool Children(const std::uint8_t* obj, const std::uint8_t* const*& data, std::uint16_t& size)
    {
        std::uint16_t capacity = 0;
        data = nullptr; size = 0;
        if (!Read(obj + 0x118, data) || !Read(obj + 0x120, capacity) || !Read(obj + 0x124, size)) return false;
        return data && size > 0 && size <= capacity && capacity <= 65000;
    }

    const std::uint8_t* ChildNamed(const std::uint8_t* obj, const char* name)
    {
        const std::uint8_t* const* data; std::uint16_t size;
        if (!obj || !Children(obj, data, size)) return nullptr;
        for (std::uint16_t i = 0; i < size; ++i)
        {
            const std::uint8_t* c = nullptr;
            if (Read(data + i, c) && c && ObjectName(c) == name) return c;
        }
        return nullptr;
    }

    struct WorldTransform { float r[9]; float t[3]; float s; };
    bool ReadWorld(const std::uint8_t* obj, WorldTransform& w)
    {
        if (!Read(obj + 0x7C, w)) return false;
        for (float f : w.r) if (!std::isfinite(f) || std::fabs(f) > 2.0f) return false;
        for (float f : w.t) if (!std::isfinite(f) || std::fabs(f) > 1.0e7f) return false;
        return std::isfinite(w.s) && w.s > 0.0f && w.s < 1000.0f;
    }

    // ---- The camera: the main NiCamera ("WorldRoot CameraNode"'s child) ----
    // Its world transform (verified layout), its frustum (left, right, top, bottom, near, far: found once by its shape,
    // NiCamera + 0x150 expected) and its world-to-clip matrix (NiCamera + 0x110), which tells which of the rotation's
    // axes looks forward, right and up (Gamebryo's convention: column 0 forward, 1 up, 2 right).
    int frustumOffset = -1;
    int axisForward = 0, axisUp = 1, axisRight = 2;
    float signRight = 1, signUp = 1;
    bool axesChecked = false;

    bool PlausibleFrustum(const float* f, float aspect)
    {
        float l = f[0], r = f[1], t = f[2], b = f[3], n = f[4], fa = f[5];
        if (!(l < 0 && r > 0 && t > 0 && b < 0)) return false;
        if (std::fabs(l + r) > 0.05f * r || std::fabs(t + b) > 0.05f * t) return false;
        if (r > 10 || t > 10) return false;
        float a = (r - l) / (t - b);
        if (aspect > 0 && (a < aspect * 0.8f || a > aspect * 1.25f)) return false;
        return n > 0.5f && n < 1000 && fa > n * 10 && fa < 1.0e8f;
    }

    bool ReadCamera(const std::uint8_t* root, rt::Camera& cam, float pos[3], float aspect)
    {
        const std::uint8_t* camNode = ChildNamed(root, "WorldRoot CameraNode");
        const std::uint8_t* const* data; std::uint16_t size;
        if (!camNode || !Children(camNode, data, size)) return false;
        const std::uint8_t* camera = nullptr;
        for (std::uint16_t i = 0; i < size && !camera; ++i)
        {
            const std::uint8_t* c = nullptr;
            if (Read(data + i, c) && c && TypeName(c) == "NiCamera") camera = c;
        }
        if (!camera) return false;
        WorldTransform w;
        if (!ReadWorld(camera, w)) return false;
        float f[6]{};
        if (frustumOffset < 0)
        {
            for (int o = 0x110; o <= 0x1E8 && frustumOffset < 0; o += 4)
                if (Read(camera + o, f) && PlausibleFrustum(f, aspect)) frustumOffset = o;
            if (frustumOffset < 0) { static bool told = false; if (!told) { Log("ray tracing: the camera's frustum not found in NiCamera"); told = true; } return false; }
            Read(camera + frustumOffset, f);
            Log("ray tracing: camera frustum at NiCamera + 0x%X: left %.4f right %.4f top %.4f bottom %.4f near %.2f far %.0f", frustumOffset, f[0], f[1], f[2], f[3], f[4], f[5]);
        }
        if (!Read(camera + frustumOffset, f) || !PlausibleFrustum(f, 0)) return false;
        auto axis = [&](int k, float out[3]) { out[0] = w.r[k]; out[1] = w.r[3 + k]; out[2] = w.r[6 + k]; };
        if (!axesChecked)
        {
            axesChecked = true;
            float m[16]{};
            bool have = Read(camera + 0x110, m);
            // The world-to-clip matrix maps the camera's position plus its forward axis to the screen's centre.
            auto clip = [&](const float p[3], bool transposed, float out[4]) {
                for (int i = 0; i < 4; ++i)
                {
                    out[i] = 0;
                    for (int j = 0; j < 3; ++j) out[i] += (transposed ? m[j * 4 + i] : m[i * 4 + j]) * p[j];
                    out[i] += transposed ? m[12 + i] : m[i * 4 + 3];
                }
            };
            bool found = false;
            for (int tr = 0; tr < 2 && have && !found; ++tr)
                for (int k = 0; k < 3 && !found; ++k)
                {
                    float a[3], p[3], c[4];
                    axis(k, a);
                    for (int i = 0; i < 3; ++i) p[i] = w.t[i] + a[i] * 1000;
                    clip(p, tr != 0, c);
                    if (!(c[3] > 0 && std::fabs(c[0] / c[3]) < 0.02f && std::fabs(c[1] / c[3]) < 0.02f)) continue;
                    found = true;
                    axisForward = k;
                    for (int o = 0; o < 3; ++o)
                    {
                        if (o == k) continue;
                        float b[3]; axis(o, b);
                        for (int i = 0; i < 3; ++i) p[i] = w.t[i] + a[i] * 1000 + b[i] * 100;
                        clip(p, tr != 0, c);
                        float nx = c[0] / c[3], ny = c[1] / c[3];
                        if (std::fabs(nx) > std::fabs(ny)) { axisRight = o; signRight = nx > 0 ? 1.0f : -1.0f; }
                        else { axisUp = o; signUp = ny > 0 ? 1.0f : -1.0f; }
                    }
                    Log("ray tracing: camera axes from its world-to-clip matrix%s: forward column %d, right %s%d, up %s%d",
                        tr ? " (transposed)" : "", axisForward, signRight < 0 ? "-" : "", axisRight, signUp < 0 ? "-" : "", axisUp);
                }
            if (!found) Log("ray tracing: the camera's world-to-clip matrix didn't confirm its axes; Gamebryo's (forward column 0, up 1, right 2) used");
        }
        float a[3];
        axis(axisForward, cam.forward);
        axis(axisRight, a); for (int i = 0; i < 3; ++i) cam.right[i] = a[i] * signRight;
        axis(axisUp, a); for (int i = 0; i < 3; ++i) cam.up[i] = a[i] * signUp;
        for (int i = 0; i < 4; ++i) cam.frustum[i] = f[i];
        cam.nearZ = f[4]; cam.farZ = f[5];
        for (int i = 0; i < 3; ++i) pos[i] = w.t[i];
        return true;
    }

    // ---- The sun: the sky's node holding a NiDirectionalLight, its offset from the sky's centre ----
    bool ReadSun(const std::uint8_t* root, float sun[3], bool& hiddenSky)
    {
        hiddenSky = true;
        const std::uint8_t* ssn = ChildNamed(root, "shadow scene node");
        const std::uint8_t* sky = ChildNamed(ssn, "Sky");
        std::uint32_t flags = 1;
        if (!sky || !Read(sky + 0xF4, flags) || (flags & 1)) return false;
        hiddenSky = false;
        WorldTransform sw;
        if (!ReadWorld(sky, sw)) return false;
        const std::uint8_t* const* data; std::uint16_t size;
        if (!Children(sky, data, size)) return false;
        for (std::uint16_t i = 0; i < size && i < 16; ++i)
        {
            const std::uint8_t* c = nullptr;
            const std::uint8_t* const* cd; std::uint16_t cs;
            if (!Read(data + i, c) || !c || !Children(c, cd, cs)) continue;
            for (std::uint16_t j = 0; j < cs && j < 8; ++j)
            {
                const std::uint8_t* g = nullptr;
                if (!Read(cd + j, g) || !g || TypeName(g) != "NiDirectionalLight") continue;
                WorldTransform nw, lw;
                if (!ReadWorld(c, nw)) return false;
                float d[3] = { nw.t[0] - sw.t[0], nw.t[1] - sw.t[1], nw.t[2] - sw.t[2] };
                float len = std::sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
                if (len < 1.0f) return false;
                for (int k = 0; k < 3; ++k) sun[k] = d[k] / len;
                static int told = 0;
                if (told++ % 3600 == 0 && ReadWorld(g, lw))
                    Log("ray tracing: sun at %.0f deg elevation (the sky's light node; its light's own axis agrees %.2f)",
                        std::asin(std::clamp(sun[2], -1.0f, 1.0f)) * 57.29578f, -(lw.r[0] * sun[0] + lw.r[3] * sun[1] + lw.r[6] * sun[2]));
                return true;
            }
        }
        return false;
    }

    // ---- The meshes ----
    // What each class is, by its vtable (its RTTI read once): 1 a mesh we take, 2 one we don't (instanced or dynamic),
    // 3 a fade node (a reference's 3D root: where a person's or creature's bones are gathered), 0 anything else.
    std::unordered_map<const void*, std::uint8_t> classKind;
    std::uint8_t KindOf(const std::uint8_t* obj)
    {
        const void* vtable = nullptr;
        if (!Read(obj, vtable) || !vtable) return 2;
        auto it = classKind.find(vtable);
        if (it != classKind.end()) return it->second;
        std::string type = TypeName(obj);
        std::uint8_t kind = 0;
        if (type.find("TriShape") != std::string::npos)
            kind = (type.find("MultiStream") != std::string::npos || type.find("Dynamic") != std::string::npos) ? 2 : 1;
        else if (type == "BSFadeNode") kind = 3;
        else if (type.find("Particle") != std::string::npos || type.find("Light") != std::string::npos || type == "NiCamera") kind = 2;
        classKind[vtable] = kind;
        return kind;
    }

    // A mesh's properties: 0 solid, 1 alpha-tested (leaves), 2 left out (glass, decals, effects, water, sky).
    std::unordered_map<const void*, std::uint8_t> propertyKind;   // by vtable: 1 alpha, 2 a shader to leave out, 0 other
    int MeshClass(const std::uint8_t* shape)
    {
        int result = 0;
        for (int slot = 0; slot < 2; ++slot)
        {
            const std::uint8_t* p = nullptr;
            const void* vtable = nullptr;
            if (!Read(shape + 0x120 + slot * 8, p) || !p || !Read(p, vtable) || !vtable) continue;
            auto it = propertyKind.find(vtable);
            if (it == propertyKind.end())
            {
                std::string t = TypeName(p);
                std::uint8_t k = t == "NiAlphaProperty" ? 1 : (t == "BSEffectShaderProperty" || t == "BSWaterShaderProperty" || t == "BSSkyShaderProperty") ? 2 : 0;
                it = propertyKind.emplace(vtable, k).first;
            }
            if (it->second == 2) return 2;
            if (it->second == 1)
            {
                std::uint16_t f = 0;   // NiAlphaProperty + 0x30: bit 0 blending, bit 9 testing
                if (!Read(p + 0x30, f)) continue;
                if (f & 0x200) result = 1;
                else if (f & 1) return 2;
            }
        }
        return result;
    }

    std::uint64_t Mix(std::uint64_t h, std::uint64_t v) { h ^= v + 0x9E3779B97F4A7C15ull + (h << 6) + (h >> 2); return h; }

    struct Bone { float p[3]; int parent; float radius; const std::uint8_t* node; };
    struct Walk
    {
        std::vector<rt::Instance> instances;
        std::vector<rt::RtCapsule> capsules;
        float cam[3]{};
        float maxDist = 12000;
        double deadline = 0;
        bool outOfTime = false, characters = true;
        int shapes = 0, skinned = 0, noCpu = 0, rejected = 0, queued = 0, leftOut = 0, actors = 0;
        std::vector<Bone> bones;     // the current reference's nodes
        bool inGroup = false, groupSkinned = false;
    };

    double Now()
    {
        static LARGE_INTEGER f{};
        if (!f.QuadPart) QueryPerformanceFrequency(&f);
        LARGE_INTEGER c{};
        QueryPerformanceCounter(&c);
        return double(c.QuadPart) / double(f.QuadPart);
    }

    /// A limb's thickness by its bone's name (a person's skeleton); 0 for others (then a share of the bone's length).
    float BoneRadius(const std::string& n)
    {
        struct { const char* key; float r; } table[] = { { "Pelvis", 11 }, { "Spine", 11 }, { "Neck", 5 }, { "Head", 8 }, { "Thigh", 7 }, { "Calf", 5 },
            { "Foot", 4 }, { "UpperArm", 4.5f }, { "Forearm", 3.5f }, { "Hand", 3 }, { "Clavicle", 4 } };
        for (auto& t : table) if (n.find(t.key) != std::string::npos) return t.r;
        return 0;
    }

    bool LeftOutNode(const std::string& n)
    {
        for (const char* k : { "Weapon", "Shield", "Quiver", "Camera", "AnimObject", "MagicEffects", "SCB", "Arrow", "Bolt", "Finger", "Toe" })
            if (n.find(k) != std::string::npos) return true;
        return false;
    }

    /// A mesh's triangles copied out of the game (guarded: it may free them meanwhile) and checked: positions finite
    /// and inside the mesh's own bound, indices in range. Garbage (memory reused under us) is never built.
    bool CopyMesh(const std::uint8_t* shape, const std::uint64_t r[6], std::uint16_t tris, std::uint16_t verts, rt::Mesh& m)
    {
        std::uint64_t desc = r[2];
        UINT stride = static_cast<UINT>(desc & 0xF) * 4, uv = static_cast<UINT>((desc >> 8) & 0xF) * 4;
        bool half = uv == 8;
        if (stride < (half ? 6u : 12u)) return false;
        float bound[4]{};
        if (!Read(shape + 0x110, bound) || !(bound[3] > 0) || !std::isfinite(bound[3])) return false;
        std::vector<std::uint8_t> raw(size_t(verts) * stride);
        m.indices.resize(size_t(tris) * 3);
        if (!SafeCopy(raw.data(), reinterpret_cast<const void*>(r[4]), raw.size()) ||
            !SafeCopy(m.indices.data(), reinterpret_cast<const void*>(r[5]), m.indices.size() * 2)) return false;
        for (auto i : m.indices) if (i >= verts) return false;
        m.positions.resize(size_t(verts) * 3);
        float reach = bound[3] * 1.25f + 4.0f, reach2 = reach * reach;
        for (UINT v = 0; v < verts; ++v)
        {
            float p[3];
            const std::uint8_t* q = raw.data() + size_t(v) * stride;
            if (half) { std::uint16_t h[3]; std::memcpy(h, q, 6); for (int k = 0; k < 3; ++k) p[k] = HalfToFloat(h[k]); }
            else std::memcpy(p, q, 12);
            float d2 = 0;
            for (int k = 0; k < 3; ++k) { if (!std::isfinite(p[k])) return false; float d = p[k] - bound[k]; d2 += d * d; }
            if (d2 > reach2) return false;
            std::memcpy(&m.positions[size_t(v) * 3], p, 12);
        }
        return true;
    }

    void Visit(Walk& w, const std::uint8_t* obj, int depth, int parentBone)
    {
        if (!obj || depth > 64 || w.outOfTime) return;
        std::uint32_t flags = 0;
        if (!Read(obj + 0xF4, flags) || (flags & 1)) return;       // hidden: its whole subtree
        // Too far to matter (its world bound past the ray tracing's reach).
        float wb[4]{};
        if (Read(obj + 0xE4, wb) && wb[3] > 0 && std::isfinite(wb[3]))
        {
            float d[3] = { wb[0] - w.cam[0], wb[1] - w.cam[1], wb[2] - w.cam[2] };
            float reach = w.maxDist + wb[3];
            if (d[0] * d[0] + d[1] * d[1] + d[2] * d[2] > reach * reach) return;
        }
        std::uint8_t kind = KindOf(obj);
        if (kind == 2) return;
        if (kind == 1)
        {
            ++w.shapes;
            const std::uint8_t* skin = nullptr, * renderer = nullptr;
            Read(obj + 0x130, skin); Read(obj + 0x138, renderer);
            if (skin) { ++w.skinned; if (w.inGroup) w.groupSkinned = true; return; }
            int cls = MeshClass(obj);
            if (cls == 2) { ++w.leftOut; return; }
            std::uint64_t r[6]{};
            std::uint16_t tris = 0, verts = 0;
            if (!renderer || !SafeCopy(r, renderer, sizeof(r)) || !r[4] || !r[5] || !Read(obj + 0x158, tris) || !Read(obj + 0x15A, verts) || !tris || !verts)
            { ++w.noCpu; return; }
            // The mesh's identity: its data's addresses, sizes and first bytes (memory reused for another mesh differs).
            std::uint64_t head[4]{};
            SafeCopy(head, reinterpret_cast<const void*>(r[4]), std::min<size_t>(sizeof(head), size_t(verts) * ((r[2] & 0xF) * 4)));
            std::uint64_t key = Mix(Mix(Mix(Mix(r[4], r[5]), (std::uint64_t(tris) << 16) | verts), r[2]), Mix(Mix(head[0], head[1]), Mix(head[2], head[3])));
            key = Mix(key, cls);
            if (!rtEngine->HasMesh(key))
            {
                if (rtEngine->Wants(key) && rtEngine->Room() > 0)
                {
                    rt::Mesh m;
                    m.key = key;
                    m.alphaTested = cls == 1;
                    if (CopyMesh(obj, r, tris, verts, m)) { rtEngine->Queue(std::move(m)); ++w.queued; }
                    else ++w.rejected;
                }
                return;
            }
            WorldTransform t;
            if (!ReadWorld(obj, t)) return;
            rt::Instance x{ key, {} };
            for (int i = 0; i < 3; ++i)
            {
                for (int j = 0; j < 3; ++j) x.transform[i][j] = t.s * t.r[i * 3 + j];
                x.transform[i][3] = t.t[i] - w.cam[i];
            }
            w.instances.push_back(x);
            if ((w.instances.size() & 255) == 0 && Now() > w.deadline) w.outOfTime = true;
            return;
        }
        if (depth <= 3)
        {
            std::string name = ObjectName(obj);
            if (name == "Sky" || name == "Weather" || name == "WorldRoot CameraNode" || name == "LODRoot") return;
        }
        // A reference's 3D root: its nodes are gathered as bones; if a skinned mesh is found under it, they become
        // capsules (the mesh itself isn't traced: it moves with the bones).
        bool group = kind == 3 && !w.inGroup && w.characters;
        size_t first = w.bones.size();
        if (group) { w.inGroup = true; w.groupSkinned = false; }
        int me = parentBone;
        if (w.inGroup && !group)
        {
            WorldTransform t;
            if (ReadWorld(obj, t))
            {
                w.bones.push_back({ { t.t[0], t.t[1], t.t[2] }, parentBone, 0, obj });
                me = static_cast<int>(w.bones.size() - 1);
            }
        }
        const std::uint8_t* const* data; std::uint16_t size;
        if (Children(obj, data, size))
            for (std::uint16_t i = 0; i < size; ++i)
            {
                const std::uint8_t* child = nullptr;
                if (Read(data + i, child) && child) Visit(w, child, depth + 1, me);
            }
        if (group)
        {
            if (w.groupSkinned && w.capsules.size() < 4000)
            {
                ++w.actors;
                // The bones' names only now (a skinned mesh was found): thickness, and what isn't a limb.
                for (size_t b = first; b < w.bones.size(); ++b)
                {
                    std::string n = ObjectName(w.bones[b].node);
                    w.bones[b].radius = LeftOutNode(n) ? -1.0f : BoneRadius(n);
                }
                for (size_t b = first; b < w.bones.size(); ++b)
                {
                    const Bone& bn = w.bones[b];
                    if (bn.parent < static_cast<int>(first) || bn.radius < 0) continue;
                    const Bone& pa = w.bones[bn.parent];
                    if (pa.radius < 0) continue;
                    float d[3] = { bn.p[0] - pa.p[0], bn.p[1] - pa.p[1], bn.p[2] - pa.p[2] };
                    float len = std::sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
                    if (len < 2.0f || len > 80.0f) continue;
                    float r = std::min(pa.radius > 0 ? pa.radius : std::clamp(len * 0.3f, 2.5f, 10.0f), len);
                    rt::RtCapsule c{};
                    for (int k = 0; k < 3; ++k) { c.a[k] = pa.p[k] - w.cam[k]; c.b[k] = bn.p[k] - w.cam[k]; }
                    c.radius = r;
                    w.capsules.push_back(c);
                }
            }
            w.bones.resize(first);
            w.inGroup = false;
        }
    }

    // The Direct3D 11 resources the tonemapping pass and the next TAA draw use: the depth and motion vectors are
    // TAA's own inputs (kept from its last draw: the same textures every frame).
    ID3D11Resource* rtDepth = nullptr, * rtMotion = nullptr;
    unsigned tonemapFrame = ~0u;
    int tonemapLogged = 0;

    void KeepTaaInputs(ID3D11Resource* depth, ID3D11Resource* motion)
    {
        if (depth != rtDepth) { if (rtDepth) rtDepth->Release(); rtDepth = depth; if (depth) depth->AddRef(); }
        if (motion != rtMotion) { if (rtMotion) rtMotion->Release(); rtMotion = motion; if (motion) motion->AddRef(); }
    }

    DXGI_FORMAT TextureFormat(ID3D11Resource* r, UINT* w = nullptr, UINT* h = nullptr)
    {
        if (!r) return DXGI_FORMAT_UNKNOWN;
        ID3D11Texture2D* t = nullptr;
        if (FAILED(r->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&t))) || !t) return DXGI_FORMAT_UNKNOWN;
        D3D11_TEXTURE2D_DESC d{};
        t->GetDesc(&d);
        t->Release();
        if (w) *w = d.Width;
        if (h) *h = d.Height;
        return d.Format;
    }

    /// Whether this full-screen draw is Skyrim's tonemapping: its shader, or (another variant of it) its inputs: the lit
    /// picture (R11G11B10 float, the depth's size) at t1, the adapted brightness (a few pixels) at t2, an 8-bit target.
    ID3D11Resource* TonemapInput(ID3D11DeviceContext* ctx, UINT vertices)
    {
        if (vertices > 6 || !rtDepth) return nullptr;
        bool byShader = currentPixelShader && currentPixelShader == tonemapShader.load();
        ID3D11ShaderResourceView* views[3]{};
        ctx->PSGetShaderResources(0, 3, views);
        ID3D11Resource* hdr = BoundResource(views[1]);
        ID3D11Resource* lum = BoundResource(views[2]);
        if (views[0]) views[0]->Release();
        bool ok = false;
        if (hdr)
        {
            UINT w = 0, h = 0, dw = 0, dh = 0, lw = 0, lh = 0;
            bool hdrOk = TextureFormat(hdr, &w, &h) == DXGI_FORMAT_R11G11B10_FLOAT;
            TextureFormat(rtDepth, &dw, &dh);
            if (byShader) ok = hdrOk;
            else if (hdrOk && w == dw && h == dh && lum && TextureFormat(lum, &lw, &lh) != DXGI_FORMAT_UNKNOWN && lw <= 8 && lh <= 8)
            {
                ID3D11RenderTargetView* rtv = nullptr;
                ctx->OMGetRenderTargets(1, &rtv, nullptr);
                ID3D11Resource* target = nullptr;
                if (rtv) { rtv->GetResource(&target); rtv->Release(); }
                DXGI_FORMAT tf = TextureFormat(target);
                ok = tf == DXGI_FORMAT_R8G8B8A8_UNORM || tf == DXGI_FORMAT_R8G8B8A8_TYPELESS || tf == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB ||
                     tf == DXGI_FORMAT_R10G10B10A2_UNORM || tf == DXGI_FORMAT_R10G10B10A2_TYPELESS;
                if (target) target->Release();
                if (ok && tonemapLogged < 4)
                {
                    ++tonemapLogged;
                    std::uint64_t hash = 0;
                    { std::lock_guard lk(shaderLock); if (auto it = pixelShaders.find(currentPixelShader); it != pixelShaders.end()) hash = it->second.hash; }
                    Log("ray tracing: tonemapping pass found by its inputs (shader %llu)", static_cast<unsigned long long>(hash));
                }
            }
        }
        if (lum) lum->Release();
        if (!ok && hdr) { hdr->Release(); hdr = nullptr; }
        return hdr;
    }

    bool RayTracingWanted() { return settings.rayTracedLighting && rtEngine && rtEngine->Running(); }

    /// At the tonemapping draw: the scene gathered and traced, the results put into the lit picture; the draw then goes on.
    void OnTonemapDraw(ID3D11DeviceContext* ctx, UINT vertices)
    {
        if (!RayTracingWanted() || inOurPass || tonemapFrame == presentFrame.load()) return;
        ID3D11Resource* hdr = TonemapInput(ctx, vertices);
        if (!hdr) return;
        tonemapFrame = presentFrame.load();
        D3D11_VIEWPORT vp{};
        UINT n = 1;
        ctx->RSGetViewports(&n, &vp);
        if (!worldRoot || (rtFrames % 120) == 0) worldRoot = FindWorldRoot();
        ++rtFrames;
        rt::FrameInputs in;
        float camPos[3]{};
        UINT tw = 0, th = 0;
        TextureFormat(rtDepth, &tw, &th);
        in.drawnWidth = n ? static_cast<UINT>(vp.Width + 0.5f) : tw;
        in.drawnHeight = n ? static_cast<UINT>(vp.Height + 0.5f) : th;
        if (!worldRoot || !ReadCamera(worldRoot, in.camera, camPos, float(in.drawnWidth) / std::max(1u, in.drawnHeight)))
        { hdr->Release(); rtStatus = "Waiting for the game world (its camera)."; return; }
        bool hiddenSky = true;
        in.sunOn = ReadSun(worldRoot, in.sun, hiddenSky) && in.sun[2] > 0.03f;
        Walk w;
        std::memcpy(w.cam, camPos, sizeof(camPos));
        w.maxDist = settings.rtDistance;
        w.characters = settings.rtCharacters;
        double t0 = Now();
        w.deadline = t0 + 0.008;
        Visit(w, worldRoot, 0, -1);
        double walkMs = (Now() - t0) * 1000;
        static bool toldTime = false;
        if (w.outOfTime && !toldTime) { toldTime = true; Log("ray tracing: the scene walk took over 8 ms; the rest of the scene left out this frame"); }
        rt::Options o;
        o.quality = settings.rtQuality;
        o.shadows = in.sunOn ? settings.shadowStrength : 0.0f;
        o.occlusion = settings.occlusionStrength;
        o.bounce = settings.lightingStrength;
        o.reflections = settings.reflectionStrength;
        o.debugView = settings.rtDebugView;
        o.memoryBudget = std::uint64_t(settings.rtMemoryMB) << 20;
        in.depth = rtDepth; in.motion = rtMotion; in.hdr = hdr;
        ID3D11PixelShader* keep = currentPixelShader;
        inOurPass = true;
        bool shown = rtEngine->Render(ctx, in, w.instances, w.capsules, o);
        inOurPass = false;
        currentPixelShader = keep;
        hdr->Release();
        rtStatus = !rtEngine->Running() ? "Off: " + rtEngine->Status().substr(rtEngine->Status().find(':') + 1) :
                   !rtEngine->LinedUp() ? "Paused: the traced world doesn't line up with the picture (see the log)." :
                   shown ? "On." : "Waiting.";
        if (rtFrames % 600 == 1)
            Log("ray tracing: %s; scene: %d meshes (%d skinned, %d without CPU data, %d left out, %d rejected, %d queued), %d people/creatures as %zu capsules, sun %s, walk %.2f ms",
                rtEngine->Report().c_str(), w.shapes, w.skinned, w.noCpu, w.leftOut, w.rejected, w.queued, w.actors, w.capsules.size(),
                in.sunOn ? "on" : hiddenSky ? "none (no sky)" : "down", walkMs);
    }

    void StartRayTracingEngine()
    {
        if (rtTried) return;
        rtTried = true;
        if (rtEngine) { rtEngine->Stop(); delete rtEngine; }
        rtEngine = new rt::Engine();
        if (!rtEngine->Start(device, false, RtLog)) rtStatus = "Off: " + rtEngine->Status().substr(rtEngine->Status().find(':') + 1);
        else rtStatus = taaShader ? "Waiting for a game scene." : "Waiting for a game scene (ray tracing reads Skyrim's TAA inputs: TAA must be on).";
    }

    // ---- The game's window: the menu's keys and mouse ----
    std::atomic<long> messages{ 0 };
    std::atomic<long long> lastToggle{ 0 };

    void ToggleMenu()
    {
        long long now = static_cast<long long>(GetTickCount64());
        if (now - lastToggle < 250) return;      // the same press seen by both the window and the key poll
        lastToggle = now;
        menuOpen = !menuOpen;
        Log("menu %s", menuOpen ? "opened" : "closed");
        if (ready) ImGui::GetIO().MouseDrawCursor = menuOpen;
        if (!menuOpen) SaveSettings();
    }

    LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp)
    {
        ++messages;
        // F10 arrives as a system key (WM_SYSKEYDOWN), the others as WM_KEYDOWN; auto-repeats are ignored.
        if ((msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && static_cast<int>(wp) == settings.menuKey && !(lp & (1 << 30)))
        {
            ToggleMenu();
            return 0;
        }
        // Windows would otherwise open the (absent) menu bar on F10's release.
        if ((msg == WM_SYSKEYUP || msg == WM_SYSCHAR) && static_cast<int>(wp) == settings.menuKey) return 0;
        if (menuOpen && ready && ImGui_ImplWin32_WndProcHandler(hwnd, msg, wp, lp)) return 1;
        return CallWindowProcA(gameWndProc, hwnd, msg, wp, lp);
    }

    // ---- DirectInput: the game gets no keys or mouse while the menu is open ----
    using GetDeviceState_t = HRESULT(STDMETHODCALLTYPE*)(IDirectInputDevice8A*, DWORD, LPVOID);
    using GetDeviceData_t = HRESULT(STDMETHODCALLTYPE*)(IDirectInputDevice8A*, DWORD, LPDIDEVICEOBJECTDATA, LPDWORD, DWORD);
    GetDeviceState_t realGetDeviceState = nullptr;
    GetDeviceData_t realGetDeviceData = nullptr;

    // The menu's own cursor, moved by the mouse's DirectInput data (the game's window gets no mouse messages).
    std::mutex mouseLock;
    float cursorX = 0, cursorY = 0, wheel = 0;
    bool buttons[3]{};

    bool IsMouse(IDirectInputDevice8A* d)
    {
        DIDEVICEINSTANCEA inst{};
        inst.dwSize = sizeof(inst);
        return SUCCEEDED(d->GetDeviceInfo(&inst)) && GET_DIDEVICE_TYPE(inst.dwDevType) == DI8DEVTYPE_MOUSE;
    }

    HRESULT STDMETHODCALLTYPE HookGetDeviceState(IDirectInputDevice8A* self, DWORD size, LPVOID data)
    {
        HRESULT hr = realGetDeviceState(self, size, data);
        if (menuOpen && SUCCEEDED(hr) && data)
        {
            if ((size == sizeof(DIMOUSESTATE) || size == sizeof(DIMOUSESTATE2)) && IsMouse(self))
            {
                auto* m = static_cast<DIMOUSESTATE*>(data);
                std::lock_guard lk(mouseLock);
                cursorX += m->lX; cursorY += m->lY; wheel += m->lZ / 120.0f;
                for (int b = 0; b < 3; ++b) buttons[b] = (m->rgbButtons[b] & 0x80) != 0;
            }
            std::memset(data, 0, size);
        }
        return hr;
    }

    HRESULT STDMETHODCALLTYPE HookGetDeviceData(IDirectInputDevice8A* self, DWORD size, LPDIDEVICEOBJECTDATA data, LPDWORD count, DWORD flags)
    {
        HRESULT hr = realGetDeviceData(self, size, data, count, flags);
        if (menuOpen && SUCCEEDED(hr) && count)
        {
            if (data && !(flags & DIGDD_PEEK) && IsMouse(self))
            {
                std::lock_guard lk(mouseLock);
                for (DWORD i = 0; i < *count; ++i)
                {
                    auto& e = *reinterpret_cast<DIDEVICEOBJECTDATA*>(reinterpret_cast<char*>(data) + i * size);
                    if (e.dwOfs == DIMOFS_X) cursorX += static_cast<int>(e.dwData);
                    else if (e.dwOfs == DIMOFS_Y) cursorY += static_cast<int>(e.dwData);
                    else if (e.dwOfs == DIMOFS_Z) wheel += static_cast<int>(e.dwData) / 120.0f;
                    else if (e.dwOfs >= DIMOFS_BUTTON0 && e.dwOfs <= DIMOFS_BUTTON2) buttons[e.dwOfs - DIMOFS_BUTTON0] = (e.dwData & 0x80) != 0;
                }
            }
            *count = 0;
        }
        return hr;
    }

    bool PatchVtable(void** vtable, int index, void* hook, void** original)
    {
        DWORD old = 0;
        if (!VirtualProtect(&vtable[index], sizeof(void*), PAGE_EXECUTE_READWRITE, &old)) return false;
        *original = vtable[index];
        vtable[index] = hook;
        VirtualProtect(&vtable[index], sizeof(void*), old, &old);
        return true;
    }

    void HookDirectInput()
    {
        IDirectInput8A* di = nullptr;
        if (FAILED(DirectInput8Create(GetModuleHandleA(nullptr), DIRECTINPUT_VERSION, IID_IDirectInput8A, reinterpret_cast<void**>(&di), nullptr)) || !di)
        { Log("DirectInput: couldn't create"); return; }
        IDirectInputDevice8A* keyboard = nullptr;
        if (SUCCEEDED(di->CreateDevice(GUID_SysKeyboard, &keyboard, nullptr)) && keyboard)
        {
            void** vt = *reinterpret_cast<void***>(keyboard);
            // IDirectInputDevice8: GetDeviceState is entry 9, GetDeviceData 10 (shared by every device object).
            PatchVtable(vt, 9, reinterpret_cast<void*>(&HookGetDeviceState), reinterpret_cast<void**>(&realGetDeviceState));
            PatchVtable(vt, 10, reinterpret_cast<void*>(&HookGetDeviceData), reinterpret_cast<void**>(&realGetDeviceData));
            keyboard->Release();
            Log("DirectInput hooked");
        }
        di->Release();
    }

    // ---- Seamless transitions (user, 2026-10-10: "make a fake seamless transition between spaces") ----
    // Every load door, fast travel, overland tile crossing and sleep fades to black and/or shows the loading screen.
    // The game's own menus say when: its menu stack (RE::UI, Address Library ID 400327, + 0x110 a BSTArray of IMenu
    // pointers { data, capacity u32, size u32 at + 0x10 }), each menu named by its RTTI. When the loading screen or the
    // fader opens, the picture from just before is kept on the screen through the fade, the load and the fade-in, then
    // cross-faded into the new place. Without the menu stack (another game version) nothing is held.
    std::uint8_t* const* uiSingleton = nullptr;
    std::string transitionStatus = "Off.";
    bool uiChecked = false, uiFound = false;

    double Seconds()
    {
        static LARGE_INTEGER freq{};
        if (!freq.QuadPart) QueryPerformanceFrequency(&freq);
        LARGE_INTEGER now{};
        QueryPerformanceCounter(&now);
        return static_cast<double>(now.QuadPart) / freq.QuadPart;
    }

    void FindUi()
    {
        uiChecked = true;
        int v[4]{};
        if (!GameVersion(v)) return;
        char path[MAX_PATH]{};
        GetModuleFileNameA(nullptr, path, MAX_PATH);
        std::string dir(path);
        dir = dir.substr(0, dir.find_last_of("\\/") + 1);
        char lib[MAX_PATH];
        std::snprintf(lib, sizeof(lib), "%sData\\SKSE\\Plugins\\versionlib-%d-%d-%d-%d.bin", dir.c_str(), v[0], v[1], v[2], v[3]);
        std::uint64_t offset = 0;
        if (AddressLibraryOffset(lib, 400327, offset))
            uiSingleton = reinterpret_cast<std::uint8_t* const*>(reinterpret_cast<std::uint8_t*>(GetModuleHandleA(nullptr)) + offset);
        else Log("transitions: the menus' address not in %s", lib);
    }

    /// The open menus' class names, bottom to top; false when the menu stack can't be read (yet).
    bool MenuStack(std::vector<std::string>& out)
    {
        out.clear();
        if (!uiChecked) FindUi();
        const std::uint8_t* ui = nullptr;
        if (!uiSingleton || !Read(uiSingleton, ui) || !ui) return false;
        // RE::UI has no virtual functions (its bases are event sources and a singleton holder), so it has no RTTI to
        // check: the 10-14 log's "the menus' object is a ?, not UI" was that check, not a wrong address. The menu stack
        // is checked instead: every entry an IMenu with RTTI whose name is a menu's.
        const std::uint8_t* const* data = nullptr;
        std::uint32_t capacity = 0, size = 0;
        if (!Read(ui + 0x110, data) || !Read(ui + 0x118, capacity) || !Read(ui + 0x120, size)) return false;
        if (size > capacity || size > 64 || capacity > 4096 || (size && !data)) return false;
        static std::unordered_map<const void*, std::string> names;   // by vtable
        for (std::uint32_t i = 0; i < size; ++i)
        {
            const std::uint8_t* menu = nullptr;
            const void* vtable = nullptr;
            if (!Read(data + i, menu) || !menu || !Read(menu, vtable) || !vtable) return false;
            auto it = names.find(vtable);
            if (it == names.end()) it = names.emplace(vtable, TypeName(menu)).first;
            const std::string& n = it->second;
            // A class with RTTI (an IMenu of the game's, or a mod's): a wrong offset would point at no such object.
            if (n.empty() || n == "?")
            {
                static int told = 0;
                if (told++ < 3) Log("transitions: an entry of the menu stack has no class name (%p); not used", static_cast<const void*>(menu));
                return false;
            }
            out.push_back(n);
        }
        if (!uiFound && size > 0)
        {
            uiFound = true;
            Log("transitions: the game's menus found (UI at %p, %u open)", static_cast<const void*>(ui), size);
        }
        return uiFound;
    }

    // Menus that cover the world: no picture is kept while they are open (a save loaded from the journal shows the
    // loading screen, not a frozen journal).
    constexpr const char* CoveringMenus[] = { "MainMenu", "MistMenu", "JournalMenu", "Console", "StatsMenu", "TweenMenu",
        "InventoryMenu", "MagicMenu", "ContainerMenu", "BarterMenu", "GiftMenu", "BookMenu", "LockpickingMenu", "RaceSexMenu",
        "CreditsMenu", "TutorialMenu", "MessageBoxMenu", "LevelUpMenu", "CraftingMenu", "FavoritesMenu", "TrainingMenu",
        "ModManagerMenu", "CreationClubMenu", "KinectMenu" };

    enum class Transition { Watch, Hold, Release };
    Transition transition = Transition::Watch;
    struct Snapshot { ID3D11Texture2D* tex = nullptr; ID3D11ShaderResourceView* view = nullptr; double time = -100; };
    Snapshot snapshots[2];             // the last two world pictures; the older one is kept (the newer may hold the fade's start)
    int nextSnapshot = 0, heldSnapshot = 0;
    UINT snapshotWidth = 0, snapshotHeight = 0;
    DXGI_FORMAT snapshotFormat = DXGI_FORMAT_UNKNOWN;
    double holdStart = 0, clearSince = -1, releaseStart = 0;
    bool wasLoading = true;            // so a menu already open when the game starts is no edge

    struct TransitionOverlay { ID3D11ShaderResourceView* image = nullptr; float alpha = 0; bool loading = false; };

    bool EnsureSnapshots(ID3D11Texture2D* back)
    {
        D3D11_TEXTURE2D_DESC bd{};
        back->GetDesc(&bd);
        if (snapshots[0].tex && bd.Width == snapshotWidth && bd.Height == snapshotHeight && bd.Format == snapshotFormat) return true;
        if (bd.Width == snapshotWidth && bd.Height == snapshotHeight && bd.Format == snapshotFormat) return false;   // failed before
        for (auto& s : snapshots)
        {
            if (s.view) s.view->Release();
            if (s.tex) s.tex->Release();
            s = {};
        }
        snapshotWidth = bd.Width; snapshotHeight = bd.Height; snapshotFormat = bd.Format;
        if (bd.SampleDesc.Count != 1) return false;
        D3D11_TEXTURE2D_DESC d{};
        d.Width = bd.Width; d.Height = bd.Height; d.MipLevels = 1; d.ArraySize = 1; d.Format = bd.Format;
        d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_DEFAULT; d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        for (auto& s : snapshots)
            if (FAILED(device->CreateTexture2D(&d, nullptr, &s.tex)) || FAILED(device->CreateShaderResourceView(s.tex, nullptr, &s.view)))
            {
                Log("transitions: no picture textures (%ux%u format %d)", bd.Width, bd.Height, static_cast<int>(bd.Format));
                for (auto& q : snapshots) { if (q.view) q.view->Release(); if (q.tex) q.tex->Release(); q = {}; }
                return false;
            }
        Log("transitions: pictures kept at %ux%u", bd.Width, bd.Height);
        return true;
    }

    /// Before the menu is drawn: the held picture copied over the frame, or the cross-fade's picture and strength.
    TransitionOverlay TransitionFrame(IDXGISwapChain* swap)
    {
        TransitionOverlay o;
        static std::vector<std::string> stack, logged;
        if (!settings.seamlessTransitions) { transition = Transition::Watch; transitionStatus = "Off."; return o; }
        if (!MenuStack(stack))
        {
            transition = Transition::Watch;
            transitionStatus = uiChecked && !uiSingleton ? "Not available: the game's menus weren't found for this game version." : "Waiting for the game's menus.";
            return o;
        }
        // The menus as they change, for the log (the first 400 changes).
        static int changes = 0;
        if (stack != logged && changes < 400)
        {
            ++changes;
            std::string line;
            for (auto& m : stack) line += " " + m;
            Log("menus:%s", line.c_str());
            logged = stack;
        }
        bool loading = false, covered = false;
        for (auto& m : stack)
        {
            if (m == "LoadingMenu" || m == "FaderMenu") loading = true;
            for (auto c : CoveringMenus) if (m == c) covered = true;
        }
        ID3D11Texture2D* back = nullptr;
        if (FAILED(swap->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&back))) || !back) return o;
        if (!EnsureSnapshots(back)) { back->Release(); transitionStatus = "Not available: the screen's format can't be copied."; return o; }
        double t = Seconds();
        switch (transition)
        {
        case Transition::Watch:
        {
            const Snapshot& older = snapshots[nextSnapshot];
            if (loading && !wasLoading && t - older.time < 0.5)
            {
                transition = Transition::Hold;
                heldSnapshot = nextSnapshot;
                holdStart = t; clearSince = -1;
                context->CopyResource(back, older.tex);
            }
            else if (!loading && !covered)
            {
                context->CopyResource(snapshots[nextSnapshot].tex, back);
                snapshots[nextSnapshot].time = t;
                nextSnapshot ^= 1;
            }
            transitionStatus = "On: fades and loading screens keep the last picture, then blend into the new place.";
            break;
        }
        case Transition::Hold:
            context->CopyResource(back, snapshots[heldSnapshot].tex);
            o.loading = t - holdStart > 1.0;
            // Let go once the loading screen and the fader have been gone a moment (the fade-in's fader opens a
            // little after the loading screen closes).
            if (loading) clearSince = -1;
            else if (clearSince < 0) clearSince = t;
            if (clearSince >= 0 && t - clearSince >= 0.3) { transition = Transition::Release; releaseStart = t; }
            else if (t - holdStart > 45) { Log("transitions: held 45 s, let go"); transition = Transition::Release; releaseStart = t; }
            break;
        case Transition::Release:
        {
            if (loading && !wasLoading) { transition = Transition::Hold; holdStart = t; clearSince = -1; context->CopyResource(back, snapshots[heldSnapshot].tex); break; }
            float a = 1.0f - static_cast<float>((t - releaseStart) / 0.5);
            if (a <= 0) { transition = Transition::Watch; break; }
            o.image = snapshots[heldSnapshot].view;
            o.alpha = a * a * (3 - 2 * a);
            break;
        }
        }
        wasLoading = loading;
        back->Release();
        return o;
    }

    /// A small turning arc in the bottom right while a picture is held for long (the game hasn't stopped).
    void DrawLoadingMark()
    {
        ImGuiIO& io = ImGui::GetIO();
        float r = 14.0f * io.FontGlobalScale;
        ImVec2 c(io.DisplaySize.x - r * 3, io.DisplaySize.y - r * 3);
        float a0 = static_cast<float>(std::fmod(Seconds() * 4.0, 6.283185307));
        auto* dl = ImGui::GetForegroundDrawList();
        dl->PathArcTo(c, r, a0, a0 + 4.2f, 32);
        dl->PathStroke(IM_COL32(235, 225, 200, 170), 0, 2.5f * io.FontGlobalScale);
    }

    // ---- The menu ----
    void Style()
    {
        ImGuiStyle& s = ImGui::GetStyle();
        ImGui::StyleColorsDark();
        s.WindowRounding = 2; s.FrameRounding = 2; s.WindowBorderSize = 1;
        auto c = s.Colors;
        c[ImGuiCol_WindowBg] = ImVec4(0.04f, 0.04f, 0.04f, 0.88f);
        c[ImGuiCol_Border] = ImVec4(0.75f, 0.68f, 0.52f, 0.6f);
        c[ImGuiCol_TitleBgActive] = c[ImGuiCol_TitleBg] = ImVec4(0.12f, 0.1f, 0.08f, 1);
        c[ImGuiCol_FrameBg] = ImVec4(0.16f, 0.15f, 0.13f, 1);
        c[ImGuiCol_SliderGrab] = c[ImGuiCol_CheckMark] = ImVec4(0.86f, 0.78f, 0.6f, 1);
        c[ImGuiCol_Header] = c[ImGuiCol_Button] = ImVec4(0.3f, 0.26f, 0.2f, 1);
        c[ImGuiCol_HeaderHovered] = c[ImGuiCol_ButtonHovered] = ImVec4(0.45f, 0.38f, 0.28f, 1);
        c[ImGuiCol_Text] = ImVec4(0.92f, 0.89f, 0.82f, 1);
    }

    void DrawMenu()
    {
        ImGui::SetNextWindowSize(ImVec2(560 * ImGui::GetIO().FontGlobalScale, 0), ImGuiCond_Always);
        ImGui::SetNextWindowPos(ImVec2(ImGui::GetIO().DisplaySize.x * 0.5f, ImGui::GetIO().DisplaySize.y * 0.5f), ImGuiCond_Appearing, ImVec2(0.5f, 0.5f));
        bool open = true;
        ImGui::Begin("TamrielMerge Graphics", &open, ImGuiWindowFlags_NoCollapse | ImGuiWindowFlags_NoResize);
        ImGui::TextDisabled("%s", gpuName.c_str());
        ImGui::SeparatorText("Upscaling and anti-aliasing");
        if (ImGui::Combo("Upscaler", &settings.upscaler, UpscalerNames, IM_ARRAYSIZE(UpscalerNames))) dlssReset = true;
        // DLSS 2.5.1 and later have no sharpening of their own: ours runs after it (0 = none).
        ImGui::BeginDisabled(settings.upscaler == Off);
        ImGui::SliderFloat("Sharpness", &settings.sharpness, 0.0f, 1.0f, "%.2f");
        ImGui::EndDisabled();
        if (settings.upscaler != Off && !dlssBroken && dlssAvailable)
            ImGui::TextWrapped("%s", presentFrame - lastDlssFrame < 5 ? dlssInfo.c_str() :
                                     "Waiting for a game scene (DLSS takes the place of Skyrim's TAA, so TAA must be on in Skyrim's display settings).");
        else ImGui::TextWrapped("%s", dlssStatus.c_str());
        settings.frameGeneration = false;
        ImGui::BeginDisabled(true);
        ImGui::Checkbox("Frame generation (not available)", &settings.frameGeneration);
        ImGui::EndDisabled();
        if (ImGui::IsItemHovered(ImGuiHoveredFlags_AllowWhenDisabled))
            ImGui::SetTooltip("NVIDIA's frame generation works only with Direct3D 12 and Vulkan games, presenting from their own\n"
                              "swap chain with NVIDIA Reflex; Skyrim draws and presents with Direct3D 11. It would mean replacing\n"
                              "the game's swap chain with one of our own. Not built (see docs/graphics-plugin.md).");
        ImGui::SeparatorText("Ray-traced lighting");
        if (ImGui::Checkbox("Ray-traced lighting", &settings.rayTracedLighting) && settings.rayTracedLighting) rtTried = rtEngine && rtEngine->Running();
        ImGui::BeginDisabled(!settings.rayTracedLighting);
        const char* qualities[] = { "Low (half resolution)", "Medium (half resolution, more rays)", "High (full resolution)", "Ultra (full resolution, most rays)" };
        ImGui::Combo("Quality", &settings.rtQuality, qualities, IM_ARRAYSIZE(qualities));
        ImGui::SliderFloat("Shadows", &settings.shadowStrength, 0.0f, 1.0f, "%.2f");
        ImGui::SliderFloat("Ambient occlusion", &settings.occlusionStrength, 0.0f, 1.0f, "%.2f");
        ImGui::SliderFloat("Bounce light", &settings.lightingStrength, 0.0f, 2.0f, "%.2f");
        ImGui::SliderFloat("Reflections", &settings.reflectionStrength, 0.0f, 1.0f, "%.2f");
        ImGui::Checkbox("People and creatures cast shadows", &settings.rtCharacters);
        if (ImGui::TreeNode("More"))
        {
            ImGui::SliderFloat("Distance", &settings.rtDistance, 2000.0f, 40000.0f, "%.0f units");
            ImGui::SliderInt("Video memory (MB)", &settings.rtMemoryMB, 128, 2048);
            const char* views[] = { "The picture", "Shadows only", "Ambient occlusion only", "Bounce light only", "Reflections only", "Surface normals" };
            ImGui::Combo("Show", &settings.rtDebugView, views, IM_ARRAYSIZE(views));
            ImGui::TreePop();
        }
        ImGui::EndDisabled();
        if (settings.rayTracedLighting)
        {
            ImGui::TextWrapped("%s", rtStatus.c_str());
            if (rtEngine && rtEngine->Running() && rtEngine->FramesTraced() > 0)
                ImGui::TextDisabled("GPU %.1f ms; %zu meshes (%.0f MB); line-up %.0f%%", rtEngine->GpuMs(), rtEngine->BlasCount(),
                                    rtEngine->BlasBytes() / 1048576.0, std::max(0.0, rtEngine->LineUp() * 100));
        }
        ImGui::SeparatorText("Loading");
        ImGui::Checkbox("Seamless transitions", &settings.seamlessTransitions);
        ImGui::TextWrapped("%s", transitionStatus.c_str());
        ImGui::Separator();
        ImGui::TextDisabled("F10 closes this menu. Settings are saved when it closes.");
        ImGui::End();
        if (!open) { menuOpen = false; ImGui::GetIO().MouseDrawCursor = false; SaveSettings(); }
    }

    // ---- Present ----
    using Present_t = HRESULT(STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
    Present_t realPresent = nullptr;

    void Setup(IDXGISwapChain* swap)
    {
        if (FAILED(swap->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(&device))) || !device) { Log("no D3D11 device on the swap chain"); return; }
        device->GetImmediateContext(&context);
        DXGI_SWAP_CHAIN_DESC desc{};
        swap->GetDesc(&desc);
        gameWindow = desc.OutputWindow;
        IDXGIDevice* dxgi = nullptr;
        if (SUCCEEDED(device->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&dxgi))))
        {
            IDXGIAdapter* adapter = nullptr;
            if (SUCCEEDED(dxgi->GetAdapter(&adapter)))
            {
                DXGI_ADAPTER_DESC ad{};
                adapter->GetDesc(&ad);
                char name[128]{};
                WideCharToMultiByte(CP_UTF8, 0, ad.Description, -1, name, sizeof(name), nullptr, nullptr);
                gpuName = name;
                adapter->Release();
            }
            dxgi->Release();
        }
        Log("device ready: %s, %ux%u, window %p", gpuName.c_str(), desc.BufferDesc.Width, desc.BufferDesc.Height, gameWindow);
        ImGui::CreateContext();
        ImGui::GetIO().IniFilename = nullptr;
        ImGui::GetIO().ConfigFlags |= ImGuiConfigFlags_NoMouseCursorChange;
        Style();
        // Sized for the screen: ImGui's 13 px font is drawn for 720 lines.
        float scale = std::max(1.0f, desc.BufferDesc.Height / 720.0f);
        ImGui::GetIO().FontGlobalScale = scale;
        ImGui::GetStyle().ScaleAllSizes(scale);
        cursorX = desc.BufferDesc.Width / 2.0f; cursorY = desc.BufferDesc.Height / 2.0f;
        ImGui_ImplWin32_Init(gameWindow);
        ImGui_ImplDX11_Init(device, context);
        gameWndProc = reinterpret_cast<WNDPROC>(SetWindowLongPtrA(gameWindow, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&WndProc)));
        StartNgx();
        HookContext(context);
        FindGameState();
        Log("renderer state at %p", static_cast<void*>(gameState));
        autoDynamicResolution = static_cast<bool*>(FindGameSetting("bEnableAutoDynamicResolution:Display"));
        if (autoDynamicResolution) Log("the game's automatic dynamic resolution: %d", *autoDynamicResolution ? 1 : 0);
        else Log("the game's automatic dynamic resolution setting not found");
        ready = true;
    }

    HRESULT STDMETHODCALLTYPE HookPresent(IDXGISwapChain* swap, UINT sync, UINT flags)
    {
        static std::once_flag once;
        std::call_once(once, [&] { SetupGuarded(swap); });
        // The menu key read from the keyboard's state too, whatever reaches the window.
        static bool keyWasDown = false;
        bool keyDown = (GetAsyncKeyState(settings.menuKey) & 0x8000) != 0;
        if (keyDown && !keyWasDown && GetForegroundWindow() == gameWindow) ToggleMenu();
        keyWasDown = keyDown;
        static unsigned frames = 0;
        presentFrame = frames + 1;
        // The slowest frame since the last log line (hitches).
        static LARGE_INTEGER freq{}, last{};
        static double worstMs = 0;
        {
            LARGE_INTEGER now{};
            QueryPerformanceCounter(&now);
            if (!freq.QuadPart) QueryPerformanceFrequency(&freq);
            else if (last.QuadPart) worstMs = std::max(worstMs, (now.QuadPart - last.QuadPart) * 1000.0 / freq.QuadPart);
            last = now;
        }
        if (++frames % 300 == 0)
        {
            static unsigned long long lastTick = GetTickCount64();
            unsigned long long now = GetTickCount64();
            Log("frame %u (%.1f fps, slowest %.0f ms), window messages %ld, draws seen %lld", frames, 300000.0 / std::max(1ull, now - lastTick), worstMs, messages.load(), drawCalls.load());
            worstMs = 0;
            lastTick = now;
            if (context && inlineTable && *reinterpret_cast<void***>(context) == inlineTable) Log("  the context's table was put back to d3d11's");
        }
        // Ray-traced lighting switched on (in the menu or the ini): its Direct3D 12 side started; each frame its health
        // checked (a removed device or work over 2 s turns it off), what the GPU is done with freed.
        if (ready && settings.rayTracedLighting && !rtTried) StartRayTracingEngine();
        if (rtEngine && !settings.rayTracedLighting && rtEngine->Running() && !menuOpen)
        {
            // Turned off (and the menu closed): its video memory given back.
            rtEngine->Stop();
            rtTried = false;
            rtStatus = "Off.";
            Log("ray tracing: turned off; its memory freed");
        }
        if (rtEngine)
        {
            rtEngine->EndFrame();
            if (!rtEngine->Running() && settings.rayTracedLighting && rtStatus.rfind("Off", 0) != 0)
                rtStatus = "Off: " + rtEngine->Status().substr(rtEngine->Status().find(':') + 1);
        }
        if (settings.sceneDump)
        {
            static bool f12Was = false;
            bool f12 = (GetAsyncKeyState(VK_F12) & 0x8000) != 0;
            if (f12 && !f12Was) DumpScene();
            f12Was = f12;
        }
        if (settings.capture && device)
        {
            // Two frames in a row: each Present closes one and starts the next.
            if (capturing && captureFile) std::fprintf(captureFile, "--- end of frame %u\n", frames);
            if (capturing && captureFile && gameState)
            {
                // The renderer state's first 0x400 bytes as floats (the camera's jitter changes every frame).
                float st[256]{};
                if (SafeCopy(st, gameState, sizeof(st)))
                {
                    std::fprintf(captureFile, "state");
                    for (int i = 0; i < 256; ++i) std::fprintf(captureFile, " %x:%.6g", i * 4, st[i]);
                    std::fprintf(captureFile, "\n");
                }
            }
            // F11 (with bCapture on) or the set frame starts it.
            static bool f11Was = false;
            bool f11 = (GetAsyncKeyState(VK_F11) & 0x8000) != 0;
            static unsigned startAt = 0;
            if (f11 && !f11Was && !capturing) startAt = frames;
            f11Was = f11;
            if (!capturing && (frames == static_cast<unsigned>(settings.captureFrame) || frames == startAt))
            {
                captureFile = std::fopen((PluginDirectory() + "TamrielMergeGraphics-capture.txt").c_str(), "w");
                if (captureFile)
                {
                    std::lock_guard lk(shaderLock);
                    std::fprintf(captureFile, "shaders %zu\n", pixelShaders.size());
                    for (auto& [ptr, info] : pixelShaders)
                        std::fprintf(captureFile, "shader %p %llu size %u tex %d cb %d cb0 %u\n", ptr, static_cast<unsigned long long>(info.hash), info.size, info.textures, info.cbuffers, info.cb0Size);
                    capturing = true;
                    captureStarted = frames;
                    Log("capturing frames %u and %u", frames + 1, frames + 2);
                }
            }
            else if (capturing && frames >= captureStarted + 2)
            {
                capturing = false;
                std::fclose(captureFile); captureFile = nullptr;
                Log("capture written");
            }
        }
        TransitionOverlay overlay;
        if (ready) overlay = TransitionFrame(swap);
        if (ready && (menuOpen || overlay.image || overlay.loading))
        {
            ID3D11Texture2D* back = nullptr;
            if (SUCCEEDED(swap->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&back))) && back)
            {
                if (!backBufferView) device->CreateRenderTargetView(back, nullptr, &backBufferView);
                back->Release();
                ImGui_ImplDX11_NewFrame();
                ImGui_ImplWin32_NewFrame();
                {
                    // The DirectInput cursor, kept on the screen.
                    ImGuiIO& io = ImGui::GetIO();
                    std::lock_guard lk(mouseLock);
                    cursorX = std::clamp(cursorX, 0.0f, io.DisplaySize.x - 1);
                    cursorY = std::clamp(cursorY, 0.0f, io.DisplaySize.y - 1);
                    io.AddMousePosEvent(cursorX, cursorY);
                    for (int b = 0; b < 3; ++b) io.AddMouseButtonEvent(b, buttons[b]);
                    if (wheel != 0) { io.AddMouseWheelEvent(0, wheel); wheel = 0; }
                }
                ImGui::NewFrame();
                if (overlay.image)
                    ImGui::GetBackgroundDrawList()->AddImage(ImTextureRef(static_cast<ImTextureID>(reinterpret_cast<std::uintptr_t>(overlay.image))),
                        ImVec2(0, 0), ImGui::GetIO().DisplaySize, ImVec2(0, 0), ImVec2(1, 1), IM_COL32(255, 255, 255, static_cast<int>(overlay.alpha * 255)));
                if (overlay.loading) DrawLoadingMark();
                if (menuOpen) DrawMenu();
                ImGui::Render();
                context->OMSetRenderTargets(1, &backBufferView, nullptr);
                ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());
            }
        }
        HRESULT presented = realPresent(swap, sync, flags);
        if (ready)
        {
            PrepareNextFrame();
            RebindSamplers(context);   // the next frame's bias on the samplers already bound
        }
        return presented;
    }

    void HookAllUnguarded() { HookPresentTable(); HookDirectInput(); }
    void HookAll()
    {
        __try { HookAllUnguarded(); }
        __except (EXCEPTION_EXECUTE_HANDLER) { Log("hooking failed"); }
    }

    void SetupGuarded(IDXGISwapChain* swap)
    {
        __try { Setup(swap); }
        __except (EXCEPTION_EXECUTE_HANDLER) { Log("setup failed"); }
    }

    // The swap chain's function table, from a small device of our own (one table serves every DXGI swap chain).
    bool HookPresentTable()
    {
        WNDCLASSA wc{};
        wc.lpfnWndProc = DefWindowProcA;
        wc.hInstance = GetModuleHandleA(nullptr);
        wc.lpszClassName = "TamrielMergeGraphicsProbe";
        RegisterClassA(&wc);
        HWND probe = CreateWindowA(wc.lpszClassName, "", WS_OVERLAPPED, 0, 0, 16, 16, nullptr, nullptr, wc.hInstance, nullptr);
        DXGI_SWAP_CHAIN_DESC sd{};
        sd.BufferCount = 1;
        sd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        sd.OutputWindow = probe;
        sd.SampleDesc.Count = 1;
        sd.Windowed = TRUE;
        IDXGISwapChain* swap = nullptr;
        ID3D11Device* dev = nullptr;
        ID3D11DeviceContext* ctx = nullptr;
        D3D_FEATURE_LEVEL level{};
        HRESULT hr = D3D11CreateDeviceAndSwapChain(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &sd, &swap, &dev, &level, &ctx);
        bool ok = false;
        if (SUCCEEDED(hr) && swap)
        {
            void** vt = *reinterpret_cast<void***>(swap);
            ok = PatchVtable(vt, 8, reinterpret_cast<void*>(&HookPresent), reinterpret_cast<void**>(&realPresent));   // Present
            // The device's and context's tables, shared with the game's (ID3D11Device::CreatePixelShader is 15;
            // ID3D11DeviceContext::DrawIndexed 12, Draw 13): the shaders as they're made, and the draws.
            if (dev && ctx)
            {
                PatchVtable(*reinterpret_cast<void***>(dev), 15, reinterpret_cast<void*>(&HookCreatePixelShader), reinterpret_cast<void**>(&realCreatePixelShader));

            }
        }
        if (swap) swap->Release();
        if (ctx) ctx->Release();
        if (dev) dev->Release();
        DestroyWindow(probe);
        UnregisterClassA(wc.lpszClassName, wc.hInstance);
        Log(ok ? "Present hooked" : "couldn't hook Present (0x%08X)", hr);
        return ok;
    }
}

extern "C"
{
    __declspec(dllexport) constinit SKSEPluginVersionData SKSEPlugin_Version = {
        1, 1, "TamrielMergeGraphics", "TamrielMerge", "", 0,
        kVersionIndependent_AddressLibraryPostAE | kVersionIndependent_StructsPost629, {}, 0,
    };

    __declspec(dllexport) bool SKSEPlugin_Load(const void*)
    {
        std::string logPath = PluginDirectory() + "TamrielMergeGraphics.log";
        logFile = std::fopen(logPath.c_str(), "w");
        iniPath = PluginDirectory() + "TamrielMergeGraphics.ini";
        LoadSettings();
        SaveSettings();
        Log("TamrielMergeGraphics loaded; menu key %d", settings.menuKey);
        PatchPhysicsWorld(settings.physicsCells);
        HookAll();
        return true;
    }
}
