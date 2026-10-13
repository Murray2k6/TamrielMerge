// TamrielMergeEra: an SKSE plugin that makes Skyrim's date read the right era for TamrielMerge's time travel.
//
// Skyrim prints dates (wait menu, journal, saves) with the era fixed in the executable: the format ", 4E %d".
// TamrielMerge's "Travel Through Time" power sets the game's year to the visited time (2E 864, 3E 405, 3E 427,
// 3E 433, 4E 201), so this plugin rewrites the two era characters of that format in memory from the current year:
// 800 and up is the Second Era, 300-799 the Third, anything else the Fourth. Nothing in the game's files changes.
//
// It needs Address Library for SKSE Plugins (Data/SKSE/Plugins/versionlib-<game version>.bin) to find the game's
// form table, where it looks up the GameYear global (form 0x35) once the game's data is loaded, then checks its
// value once a second. Every read of game memory is guarded; if anything doesn't match, the date is left alone.
//
// Reference for the formats used (MIT): CommonLibSSE-NG, REL/ID.h (Address Library database), RE/T/TESForm.h
// (allForms, Address Library ID 400507), RE/B/BSTHashMap.h (BSTScatterTable layout), RE/T/TESGlobal.h (value at
// 0x34).

#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <string>
#include <thread>
#include <vector>

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

    std::string GameDirectory()
    {
        char path[MAX_PATH]{};
        GetModuleFileNameA(nullptr, path, MAX_PATH);
        std::string s(path);
        return s.substr(0, s.find_last_of("\\/") + 1);
    }

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

    // ---- Address Library ----
    // Format 5 (the game's 1.7 releases): a 96-byte header (format, version x4, the executable's name in 64 bytes,
    // pointer size, 4 unused, count), then one 32-bit offset per ID, the ID being the index (0 = none).
    // Format 2 (1.6): IDs and offsets delta-encoded, below.
    bool AddressLibraryOffset(const std::string& file, std::uint64_t wanted, std::uint64_t& result)
    {
        std::ifstream in(file, std::ios::binary);
        if (!in) return false;
        auto read = [&](auto& v) { in.read(reinterpret_cast<char*>(&v), sizeof(v)); return static_cast<bool>(in); };
        std::int32_t format = 0, version[4]{}, nameLen = 0, pointerSize = 0, count = 0;
        if (!read(format)) return false;
        if (format == 5)
        {
            in.seekg(92);
            if (!read(count) || wanted >= static_cast<std::uint64_t>(count)) return false;
            std::uint32_t offset = 0;
            in.seekg(96 + static_cast<std::streamoff>(wanted) * 4);
            if (!read(offset) || offset == 0) return false;
            result = offset;
            return true;
        }
        if (format != 2) return false;
        for (auto& v : version) read(v);
        read(nameLen);
        in.ignore(nameLen);
        read(pointerSize);
        read(count);
        std::uint64_t id = 0, offset = 0, prevId = 0, prevOffset = 0;
        for (std::int32_t i = 0; i < count; ++i)
        {
            std::uint8_t type = 0, b8 = 0;
            std::uint16_t b16 = 0;
            std::uint32_t b32 = 0;
            if (!read(type)) return false;
            const std::uint8_t lo = type & 0xF, hi = type >> 4;
            switch (lo)
            {
            case 0: read(id); break;
            case 1: id = prevId + 1; break;
            case 2: read(b8); id = prevId + b8; break;
            case 3: read(b8); id = prevId - b8; break;
            case 4: read(b16); id = prevId + b16; break;
            case 5: read(b16); id = prevId - b16; break;
            case 6: read(b16); id = b16; break;
            case 7: read(b32); id = b32; break;
            default: return false;
            }
            const std::uint64_t tmp = (hi & 8) ? prevOffset / pointerSize : prevOffset;
            switch (hi & 7)
            {
            case 0: read(offset); break;
            case 1: offset = tmp + 1; break;
            case 2: read(b8); offset = tmp + b8; break;
            case 3: read(b8); offset = tmp - b8; break;
            case 4: read(b16); offset = tmp + b16; break;
            case 5: read(b16); offset = tmp - b16; break;
            case 6: read(b16); offset = b16; break;
            case 7: read(b32); offset = b32; break;
            }
            if (hi & 8) offset *= pointerSize;
            if (id == wanted) { result = offset; return true; }
            prevOffset = offset;
            prevId = id;
        }
        return false;
    }

    // ---- Guarded memory access ----
    template <class T>
    bool SafeRead(const void* address, T& out)
    {
        __try { out = *reinterpret_cast<const volatile T*>(address); return true; }
        __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
    }

    // The "4" of ", 4E %d" in the executable's image.
    char* FindEraText()
    {
        auto base = reinterpret_cast<std::uint8_t*>(GetModuleHandleA(nullptr));
        auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
        auto nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
        auto section = IMAGE_FIRST_SECTION(nt);
        static const char pattern[] = ", 4E %d";
        for (int s = 0; s < nt->FileHeader.NumberOfSections; ++s, ++section)
        {
            if (std::memcmp(section->Name, ".rdata", 6) != 0) continue;
            auto start = base + section->VirtualAddress;
            std::size_t size = section->Misc.VirtualSize;
            for (std::size_t i = 0; i + sizeof(pattern) <= size; ++i)
                if (std::memcmp(start + i, pattern, sizeof(pattern)) == 0) return reinterpret_cast<char*>(start + i + 2);
        }
        return nullptr;
    }

    void WriteEra(char* at, char digit)
    {
        DWORD old = 0;
        if (!VirtualProtect(at, 1, PAGE_READWRITE, &old)) return;
        *at = digit;
        VirtualProtect(at, 1, old, &old);
    }

    // The GameYear global (form 0x35) in the engine's form table: BSTScatterTable (capacity at 0x0C, entries at
    // 0x28), entries of { u32 formID, pad, TESForm* form, entry* next } (next null = empty slot).
    std::uint8_t* FindGameYear(std::uintptr_t allFormsAddress)
    {
        std::uint8_t* map = nullptr;
        if (!SafeRead(reinterpret_cast<void*>(allFormsAddress), map) || !map) return nullptr;
        std::uint32_t capacity = 0;
        std::uint8_t* entries = nullptr;
        if (!SafeRead(map + 0x0C, capacity) || !SafeRead(map + 0x28, entries) || !entries || capacity == 0 || capacity > (1u << 26)) return nullptr;
        for (std::uint32_t i = 0; i < capacity; ++i)
        {
            std::uint8_t* entry = entries + std::size_t(i) * 24;
            void* next = nullptr;
            std::uint32_t formId = 0;
            std::uint8_t* form = nullptr;
            if (!SafeRead(entry + 16, next)) return nullptr;
            if (!next || !SafeRead(entry, formId) || formId != 0x35) continue;
            if (!SafeRead(entry + 8, form) || !form) return nullptr;
            std::uint32_t ownId = 0;
            std::uint8_t formType = 0;
            if (SafeRead(form + 0x14, ownId) && ownId == 0x35 && SafeRead(form + 0x1A, formType) && formType == 0x09) return form;   // GLOB
            return nullptr;
        }
        return nullptr;
    }

    void Watch()
    {
        int v[4]{};
        if (!GameVersion(v)) { Log("could not read the game's version"); return; }
        char lib[MAX_PATH];
        std::snprintf(lib, sizeof(lib), "%sData\\SKSE\\Plugins\\versionlib-%d-%d-%d-%d.bin", GameDirectory().c_str(), v[0], v[1], v[2], v[3]);
        std::uint64_t allFormsOffset = 0;
        if (!AddressLibraryOffset(lib, 400507, allFormsOffset))
        {
            Log("Address Library not found or without the form table for %d.%d.%d.%d (%s): dates keep 4E", v[0], v[1], v[2], v[3], lib);
            return;
        }
        char* era = FindEraText();
        if (!era) { Log("date format \", 4E %%d\" not found: dates keep 4E"); return; }
        const auto allForms = reinterpret_cast<std::uintptr_t>(GetModuleHandleA(nullptr)) + allFormsOffset;
        Log("game %d.%d.%d.%d, form table at +0x%llx, era text at %p", v[0], v[1], v[2], v[3], static_cast<unsigned long long>(allFormsOffset), era);

        std::uint8_t* gameYear = nullptr;
        char shown = '4';
        for (;;)
        {
            std::this_thread::sleep_for(std::chrono::seconds(1));
            if (!gameYear)
            {
                gameYear = FindGameYear(allForms);
                if (gameYear) Log("GameYear found");
                continue;
            }
            float year = 0;
            if (!SafeRead(gameYear + 0x34, year)) { gameYear = nullptr; continue; }
            const int y = static_cast<int>(year);
            const char digit = y >= 800 ? '2' : y >= 300 ? '3' : '4';
            if (digit != shown)
            {
                WriteEra(era, digit);
                shown = digit;
                Log("year %d: era %cE", y, digit);
            }
        }
    }
}

extern "C"
{
    // Constant data, written into the DLL itself: SKSE reads it from the file without running the DLL's
    // initialisers (a value filled in at load time reads as zeros: "disabled, bad version data").
    __declspec(dllexport) constinit SKSEPluginVersionData SKSEPlugin_Version = {
        1,                      // dataVersion
        1,                      // pluginVersion
        "TamrielMergeEra",      // name
        "TamrielMerge",         // author
        "",                     // supportEmail
        0,                      // versionIndependenceEx
        kVersionIndependent_AddressLibraryPostAE | kVersionIndependent_StructsPost629,
        {},                     // compatibleVersions
        0,                      // seVersionRequired
    };

    __declspec(dllexport) bool SKSEPlugin_Load(const void*)
    {
        char docs[MAX_PATH]{};
        if (GetEnvironmentVariableA("USERPROFILE", docs, MAX_PATH))
        {
            std::string dir = std::string(docs) + "\\Documents\\My Games\\Skyrim Special Edition\\SKSE\\";
            CreateDirectoryA(dir.c_str(), nullptr);
            logFile = std::fopen((dir + "TamrielMergeEra.log").c_str(), "w");
        }
        Log("TamrielMergeEra loaded");
        std::thread(Watch).detach();
        return true;
    }
}
