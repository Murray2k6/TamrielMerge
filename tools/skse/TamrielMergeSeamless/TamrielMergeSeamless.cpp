// TamrielMergeSeamless: an SKSE plugin that lets Daggerfall's overland be one seamless worldspace.
//
// Skyrim's Havok broad phase is a box reaching 64 cells from a worldspace's centre in every direction (262,144 units,
// 3745.38 Havok metres: an hkVector4 in the executable's read-only data, three copies and a zero). Past it physics
// fails: actors sink, doors stick, arrows pass through, items fall through floors. Daggerfall's overland is one
// worldspace of 1000 x 500 cells (user, 2026-10-12: travel seen in real time "without loading", the map not made
// smaller), so at load this plugin makes the box iPhysicsCells cells ([World] in TamrielMergeSeamless.ini beside it,
// default 512: the overland reaches 500 from its centre). The value is found by itself, so no address is needed, and
// an executable without exactly one copy of it is left alone. A bigger box makes the broad phase's grid coarser
// everywhere (more pairs to test, a little more work); positions keep Skyrim's float precision (about 2-4 mm at 500
// cells, 0.5 mm at the vanilla map's edges).
//
// That one change is all it does: no hooks, no threads, no input, no graphics (user, 2026-10-15: the graphics plugin,
// which held this patch until then, removed from the tool with its DLSS; "make a seperate seamless plugin").

#include <windows.h>
#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>

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

    // The folder this DLL is in (Data\SKSE\Plugins), for its ini.
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

    void PatchPhysicsWorld(int cells)
    {
        if (cells <= 64) { Log("physics world: vanilla, 64 cells"); return; }
        auto base = reinterpret_cast<std::uint8_t*>(GetModuleHandleA(nullptr));
        auto nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + reinterpret_cast<IMAGE_DOS_HEADER*>(base)->e_lfanew);
        auto sec = IMAGE_FIRST_SECTION(nt);
        const std::uint32_t vanilla[4] = { 0x456A161E, 0x456A161E, 0x456A161E, 0 };   // 3745.3823f x 3, 0
        std::uint8_t* hit = nullptr;
        int hits = 0;
        for (int i = 0; i < nt->FileHeader.NumberOfSections; ++i, ++sec)
        {
            if (std::memcmp(sec->Name, ".rdata", 6) != 0) continue;
            std::uint8_t* p = base + sec->VirtualAddress;
            for (std::size_t k = 0; k + 16 <= sec->Misc.VirtualSize; k += 4)
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
}

extern "C"
{
    // Constant data, written into the DLL itself: SKSE reads it from the file without running the DLL's
    // initialisers (a value filled in at load time reads as zeros: "disabled, bad version data").
    __declspec(dllexport) constinit SKSEPluginVersionData SKSEPlugin_Version = {
        1,                          // dataVersion
        1,                          // pluginVersion
        "TamrielMergeSeamless",     // name
        "TamrielMerge",             // author
        "",                         // supportEmail
        0,                          // versionIndependenceEx
        kVersionIndependent_AddressLibraryPostAE | kVersionIndependent_StructsPost629,
        {},                         // compatibleVersions
        0,                          // seVersionRequired
    };

    __declspec(dllexport) bool SKSEPlugin_Load(const void*)
    {
        char docs[MAX_PATH]{};
        if (GetEnvironmentVariableA("USERPROFILE", docs, MAX_PATH))
        {
            std::string dir = std::string(docs) + "\\Documents\\My Games\\Skyrim Special Edition\\SKSE\\";
            CreateDirectoryA(dir.c_str(), nullptr);
            logFile = std::fopen((dir + "TamrielMergeSeamless.log").c_str(), "w");
        }
        Log("TamrielMergeSeamless loaded");
        const std::string ini = PluginDirectory() + "TamrielMergeSeamless.ini";
        const int cells = std::clamp(static_cast<int>(GetPrivateProfileIntA("World", "iPhysicsCells", 512, ini.c_str())), 64, 2048);
        PatchPhysicsWorld(cells);
        return true;
    }
}
