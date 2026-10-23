// TamrielMergeCrashLog: an SKSE plugin that writes down what the game was doing when it crashes (user, 2026-10-23,
// after a crash while the data loaded: "make a logger"). TamrielMerge's own code: no other mod's.
//
// From the moment SKSE loads it, a vectored exception handler (it runs before any of the game's own handlers) and
// an unhandled-exception filter watch for the faults that end the game (access violations, illegal instructions,
// stack overflow, division by zero, ...). For each it writes a report to TamrielMergeCrashLog.log beside the plugin
// (in Mod Organizer 2's overwrite folder when the game runs through it):
//   - the exception, where it happened as module + offset (SkyrimSE.exe's offsets look up offline in the exe), and
//     for an access violation the address read or written;
//   - the registers and a walk of the stack (module + offset per frame);
//   - what the registers and the first stack slots point at: a module's code, a C++ object (its class from the
//     compiler's RTTI) and, for a game form, its FormID (the high byte is the plugin's place in the load order) and
//     form type, or text;
//   - the loaded modules.
// A fault the game catches itself comes first-chance and is written too, marked so; the report written last before
// the game ends is the crash. At most 32 reports a run. It patches nothing and uses no game addresses, so it loads on
// every runtime.

#include <windows.h>
#include <dbghelp.h>
#include <psapi.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <ctime>
#include <string>

#pragma comment(lib, "dbghelp.lib")
#pragma comment(lib, "psapi.lib")

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
    constexpr std::uint32_t kVersionIndependentEx_NoStructUse = 1 << 0;
    constexpr std::uint32_t kVersionIndependent_Signatures = 1 << 1;

    std::string logPath;
    volatile LONG reports = 0;
    thread_local bool inReport = false;
    PVOID vectored = nullptr;
    LPTOP_LEVEL_EXCEPTION_FILTER previous = nullptr;
    CRITICAL_SECTION writing;

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

    bool Fatal(DWORD code)
    {
        switch (code)
        {
        case EXCEPTION_ACCESS_VIOLATION: case EXCEPTION_ILLEGAL_INSTRUCTION: case EXCEPTION_PRIV_INSTRUCTION:
        case EXCEPTION_STACK_OVERFLOW: case EXCEPTION_INT_DIVIDE_BY_ZERO: case EXCEPTION_ARRAY_BOUNDS_EXCEEDED:
        case EXCEPTION_DATATYPE_MISALIGNMENT: case EXCEPTION_IN_PAGE_ERROR: case EXCEPTION_NONCONTINUABLE_EXCEPTION:
        case 0xC0000409: /* stack buffer overrun */ case 0xC0000374: /* heap corruption */
            return true;
        default:
            return false;
        }
    }

    const char* CodeName(DWORD code)
    {
        switch (code)
        {
        case EXCEPTION_ACCESS_VIOLATION: return "EXCEPTION_ACCESS_VIOLATION";
        case EXCEPTION_ILLEGAL_INSTRUCTION: return "EXCEPTION_ILLEGAL_INSTRUCTION";
        case EXCEPTION_PRIV_INSTRUCTION: return "EXCEPTION_PRIV_INSTRUCTION";
        case EXCEPTION_STACK_OVERFLOW: return "EXCEPTION_STACK_OVERFLOW";
        case EXCEPTION_INT_DIVIDE_BY_ZERO: return "EXCEPTION_INT_DIVIDE_BY_ZERO";
        case EXCEPTION_ARRAY_BOUNDS_EXCEEDED: return "EXCEPTION_ARRAY_BOUNDS_EXCEEDED";
        case EXCEPTION_DATATYPE_MISALIGNMENT: return "EXCEPTION_DATATYPE_MISALIGNMENT";
        case EXCEPTION_IN_PAGE_ERROR: return "EXCEPTION_IN_PAGE_ERROR";
        case EXCEPTION_NONCONTINUABLE_EXCEPTION: return "EXCEPTION_NONCONTINUABLE_EXCEPTION";
        case 0xC0000409: return "STATUS_STACK_BUFFER_OVERRUN";
        case 0xC0000374: return "STATUS_HEAP_CORRUPTION";
        default: return "exception";
        }
    }

    // Memory that can be read (committed, not a guard page).
    bool Readable(const void* p, std::size_t n)
    {
        MEMORY_BASIC_INFORMATION mbi{};
        auto a = reinterpret_cast<std::uintptr_t>(p);
        if (a < 0x10000) return false;
        while (n > 0)
        {
            if (!VirtualQuery(reinterpret_cast<void*>(a), &mbi, sizeof(mbi))) return false;
            if (mbi.State != MEM_COMMIT || (mbi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) || mbi.Protect == 0) return false;
            if (!(mbi.Protect & (PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY))) return false;
            std::uintptr_t end = reinterpret_cast<std::uintptr_t>(mbi.BaseAddress) + mbi.RegionSize;
            std::size_t here = end - a;
            if (here >= n) return true;
            n -= here;
            a = end;
        }
        return true;
    }

    // "Module+offset" when the address is inside a loaded module.
    bool ModuleOf(std::uintptr_t a, char* out, std::size_t size, std::uintptr_t* base = nullptr)
    {
        HMODULE m = nullptr;
        if (!GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, reinterpret_cast<LPCSTR>(a), &m) || !m)
            return false;
        char path[MAX_PATH]{};
        GetModuleFileNameA(m, path, MAX_PATH);
        const char* name = std::strrchr(path, '\\');
        name = name ? name + 1 : path;
        std::snprintf(out, size, "%s+%llX", name, static_cast<unsigned long long>(a - reinterpret_cast<std::uintptr_t>(m)));
        if (base) *base = reinterpret_cast<std::uintptr_t>(m);
        return true;
    }

    // The class of a C++ object from its vtable's RTTI (MSVC x64: the complete object locator before the vtable).
    bool ClassOf(std::uintptr_t obj, char* out, std::size_t size)
    {
        if (!Readable(reinterpret_cast<void*>(obj), 8)) return false;
        std::uintptr_t vtable = *reinterpret_cast<std::uintptr_t*>(obj);
        std::uintptr_t base = 0;
        char mod[MAX_PATH];
        if (!ModuleOf(vtable, mod, sizeof(mod), &base) || !Readable(reinterpret_cast<void*>(vtable - 8), 8)) return false;
        std::uintptr_t col = *reinterpret_cast<std::uintptr_t*>(vtable - 8);
        if (!Readable(reinterpret_cast<void*>(col), 24)) return false;
        auto c = reinterpret_cast<const std::uint32_t*>(col);
        if (c[0] != 1 || base + c[5] != col) return false;   // signature 1, pSelf: the locator's own RVA
        std::uintptr_t td = base + c[3];
        if (!Readable(reinterpret_cast<void*>(td + 16), 4)) return false;
        const char* name = reinterpret_cast<const char*>(td + 16);   // ".?AVTESObjectREFR@@"
        if (!Readable(name, 64) || name[0] != '.' || name[1] != '?') return false;
        const char* start = name + 4;
        std::size_t n = 0;
        while (n + 1 < size && start[n] && !(start[n] == '@' && start[n + 1] == '@')) { out[n] = start[n]; ++n; }
        out[n] = 0;
        return n > 0;
    }

    // The game's form classes (TESForm and what derives from it) keep their FormID at +0x14 and type at +0x1A.
    bool IsFormClass(const char* cls)
    {
        static const char* prefixes[] = { "TES", "BGS", "Actor", "Character", "PlayerCharacter", "Script", "SpellItem", "EffectSetting",
                                          "MagicItem", "AlchemyItem", "EnchantmentItem", "ScrollItem", "IngredientItem", "Hazard", "ArrowProjectile",
                                          "MissileProjectile", "BeamProjectile", "FlameProjectile", "ConeProjectile", "BarrierProjectile",
                                          "GrenadeProjectile", "Projectile", "NavMesh", "Explosion" };
        for (const char* p : prefixes)
            if (std::strncmp(cls, p, std::strlen(p)) == 0) return true;
        return false;
    }

    bool Executable(std::uintptr_t a)
    {
        MEMORY_BASIC_INFORMATION mbi{};
        return VirtualQuery(reinterpret_cast<void*>(a), &mbi, sizeof(mbi)) &&
               (mbi.Protect & (PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY)) != 0;
    }

    // Text: four or more printable characters ending in a zero (or running on).
    int TextAt(std::uintptr_t v)
    {
        if (!Readable(reinterpret_cast<void*>(v), 64)) return 0;
        auto s = reinterpret_cast<const char*>(v);
        int n = 0;
        while (n < 63 && s[n] >= 0x20 && s[n] < 0x7F) ++n;
        return n >= 4 && (s[n] == 0 || n == 63) ? n : 0;
    }

    void Describe(FILE* f, std::uintptr_t v)
    {
        char buf[512];
        char cls[256];
        if (ModuleOf(v, buf, sizeof(buf)))
        {
            std::fprintf(f, " %s", buf);
            // In a module's data (not its code): it may be an object or a text there.
            if (!Executable(v))
            {
                if (ClassOf(v, cls, sizeof(cls))) std::fprintf(f, " (%s*)", cls);
                else if (int n = TextAt(v)) std::fprintf(f, " \"%.*s\"", n, reinterpret_cast<const char*>(v));
            }
            return;
        }
        if (ClassOf(v, cls, sizeof(cls)))
        {
            std::fprintf(f, " (%s*)", cls);
            if (IsFormClass(cls) && Readable(reinterpret_cast<void*>(v + 0x14), 8))
                std::fprintf(f, " FormID %08X type %02X", *reinterpret_cast<std::uint32_t*>(v + 0x14), *reinterpret_cast<std::uint8_t*>(v + 0x1A));
            return;
        }
        if (int n = TextAt(v)) std::fprintf(f, " \"%.*s\"", n, reinterpret_cast<const char*>(v));
    }

    void Report(EXCEPTION_POINTERS* ep, bool unhandled)
    {
        if (inReport || InterlockedIncrement(&reports) > 32) return;
        inReport = true;
        EnterCriticalSection(&writing);
        FILE* f = std::fopen(logPath.c_str(), "a");
        if (f)
        {
            auto* er = ep->ExceptionRecord;
            auto* cx = ep->ContextRecord;
            std::time_t now = std::time(nullptr);
            char when[64];
            std::strftime(when, sizeof(when), "%Y-%m-%d %H:%M:%S", std::localtime(&now));
            char where[512] = "?";
            ModuleOf(reinterpret_cast<std::uintptr_t>(er->ExceptionAddress), where, sizeof(where));
            std::fprintf(f, "\n==== %s %s (%08lX) at %s, thread %lu, %s ====\n", when, CodeName(er->ExceptionCode), er->ExceptionCode, where,
                         GetCurrentThreadId(), unhandled ? "UNHANDLED: the game ends here" : "first-chance (the game may still catch it)");
            if (er->ExceptionCode == EXCEPTION_ACCESS_VIOLATION && er->NumberParameters >= 2)
                std::fprintf(f, "%s address %016llX\n", er->ExceptionInformation[0] == 0 ? "reading" : er->ExceptionInformation[0] == 1 ? "writing" : "executing",
                             static_cast<unsigned long long>(er->ExceptionInformation[1]));

            std::fprintf(f, "Registers:\n");
            struct { const char* n; DWORD64 v; } regs[] = {
                { "RAX", cx->Rax }, { "RBX", cx->Rbx }, { "RCX", cx->Rcx }, { "RDX", cx->Rdx }, { "RSI", cx->Rsi }, { "RDI", cx->Rdi },
                { "RBP", cx->Rbp }, { "RSP", cx->Rsp }, { "R8", cx->R8 }, { "R9", cx->R9 }, { "R10", cx->R10 }, { "R11", cx->R11 },
                { "R12", cx->R12 }, { "R13", cx->R13 }, { "R14", cx->R14 }, { "R15", cx->R15 }, { "RIP", cx->Rip } };
            for (auto& r : regs)
            {
                std::fprintf(f, "  %-3s %016llX", r.n, static_cast<unsigned long long>(r.v));
                Describe(f, static_cast<std::uintptr_t>(r.v));
                std::fputc('\n', f);
            }

            std::fprintf(f, "Call stack:\n");
            HANDLE process = GetCurrentProcess(), thread = GetCurrentThread();
            static bool symbols = false;
            if (!symbols) { SymSetOptions(SYMOPT_UNDNAME | SYMOPT_DEFERRED_LOADS); symbols = SymInitialize(process, nullptr, TRUE) != FALSE; }
            CONTEXT walk = *cx;
            STACKFRAME64 frame{};
            frame.AddrPC.Offset = walk.Rip; frame.AddrPC.Mode = AddrModeFlat;
            frame.AddrStack.Offset = walk.Rsp; frame.AddrStack.Mode = AddrModeFlat;
            frame.AddrFrame.Offset = walk.Rbp; frame.AddrFrame.Mode = AddrModeFlat;
            for (int i = 0; i < 48; ++i)
            {
                if (!StackWalk64(IMAGE_FILE_MACHINE_AMD64, process, thread, &frame, &walk, nullptr, SymFunctionTableAccess64, SymGetModuleBase64, nullptr)) break;
                if (frame.AddrPC.Offset == 0) break;
                char mod[512] = "?";
                ModuleOf(static_cast<std::uintptr_t>(frame.AddrPC.Offset), mod, sizeof(mod));
                std::fprintf(f, "  [%2d] %016llX %s", i, static_cast<unsigned long long>(frame.AddrPC.Offset), mod);
                alignas(SYMBOL_INFO) char symbuf[sizeof(SYMBOL_INFO) + 256]{};
                auto* sym = reinterpret_cast<SYMBOL_INFO*>(symbuf);
                sym->SizeOfStruct = sizeof(SYMBOL_INFO);
                sym->MaxNameLen = 255;
                DWORD64 disp = 0;
                if (symbols && SymFromAddr(process, frame.AddrPC.Offset, &disp, sym)) std::fprintf(f, " %s+%llX", sym->Name, static_cast<unsigned long long>(disp));
                std::fputc('\n', f);
            }

            std::fprintf(f, "Stack (what its first slots point at):\n");
            auto sp = reinterpret_cast<const std::uint64_t*>(cx->Rsp);
            for (int i = 0; i < 128; ++i)
            {
                if (!Readable(sp + i, 8)) break;
                std::uint64_t v = sp[i];
                char buf[512];
                char cls[256];
                bool named = ModuleOf(static_cast<std::uintptr_t>(v), buf, sizeof(buf)) || ClassOf(static_cast<std::uintptr_t>(v), cls, sizeof(cls))
                             || TextAt(static_cast<std::uintptr_t>(v)) > 0;
                if (!named) continue;
                std::fprintf(f, "  [RSP+%03X] %016llX", i * 8, static_cast<unsigned long long>(v));
                Describe(f, static_cast<std::uintptr_t>(v));
                std::fputc('\n', f);
            }

            std::fprintf(f, "Modules:\n");
            HMODULE mods[512];
            DWORD needed = 0;
            if (EnumProcessModules(process, mods, sizeof(mods), &needed))
                for (DWORD i = 0; i < needed / sizeof(HMODULE) && i < 512; ++i)
                {
                    char path[MAX_PATH]{};
                    MODULEINFO mi{};
                    GetModuleFileNameA(mods[i], path, MAX_PATH);
                    GetModuleInformation(process, mods[i], &mi, sizeof(mi));
                    std::fprintf(f, "  %016llX %08lX %s\n", reinterpret_cast<unsigned long long>(mi.lpBaseOfDll), mi.SizeOfImage, path);
                }
            std::fclose(f);
        }
        LeaveCriticalSection(&writing);
        inReport = false;
    }

    LONG CALLBACK OnException(EXCEPTION_POINTERS* ep)
    {
        if (Fatal(ep->ExceptionRecord->ExceptionCode)) Report(ep, false);
        return EXCEPTION_CONTINUE_SEARCH;
    }

    LONG WINAPI OnUnhandled(EXCEPTION_POINTERS* ep)
    {
        Report(ep, true);
        return previous ? previous(ep) : EXCEPTION_CONTINUE_SEARCH;
    }
}

extern "C"
{
    // Constant data in the DLL itself (SKSE reads it from the file).
    __declspec(dllexport) constinit SKSEPluginVersionData SKSEPlugin_Version = {
        1,                          // dataVersion
        1,                          // pluginVersion
        "TamrielMergeCrashLog",     // name
        "TamrielMerge",             // author
        "",                         // supportEmail
        kVersionIndependentEx_NoStructUse,
        kVersionIndependent_Signatures,
        {},                         // compatibleVersions
        0,                          // seVersionRequired
    };

    // Starts watching (SKSE calls it; a test program may too).
    __declspec(dllexport) bool SKSEPlugin_Load(const void*)
    {
        InitializeCriticalSection(&writing);
        logPath = PluginDirectory() + "TamrielMergeCrashLog.log";
        if (FILE* f = std::fopen(logPath.c_str(), "w"))
        {
            std::time_t now = std::time(nullptr);
            char when[64];
            std::strftime(when, sizeof(when), "%Y-%m-%d %H:%M:%S", std::localtime(&now));
            char exe[MAX_PATH]{};
            GetModuleFileNameA(nullptr, exe, MAX_PATH);
            std::fprintf(f, "TamrielMergeCrashLog 1: watching %s from %s\n", exe, when);
            std::fclose(f);
        }
        vectored = AddVectoredExceptionHandler(1, OnException);
        previous = SetUnhandledExceptionFilter(OnUnhandled);
        return vectored != nullptr;
    }
}
