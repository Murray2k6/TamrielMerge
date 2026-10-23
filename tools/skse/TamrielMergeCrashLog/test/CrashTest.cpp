// CrashTest: TamrielMergeCrashLog's offline test. Loads the plugin, starts it as SKSE would, and crashes with a form-like
// object in hand (a class named TESObjectREFR, FormID 0x0A012345 at +0x14, type 0x3D at +0x1A) and a text, so the
// report must show the access violation, the call stack, the object's class and FormID, and the text.
#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <cstring>

class TESObjectREFR
{
public:
    virtual ~TESObjectREFR() {}
    virtual int Kind() { return 1; }
    std::uint8_t pad08[0x0C]{};
    std::uint32_t formID = 0x0A012345;
    std::uint8_t pad18[2]{};
    std::uint8_t formType = 0x3D;
};

__declspec(noinline) int Crash(TESObjectREFR* ref, const char* text)
{
    volatile int* nowhere = reinterpret_cast<volatile int*>(static_cast<std::uintptr_t>(ref->formID & 0xF0));
    *nowhere = static_cast<int>(std::strlen(text)) + ref->Kind();
    return 0;
}

int main(int argc, char** argv)
{
    HMODULE dll = LoadLibraryA(argc > 1 ? argv[1] : "TamrielMergeCrashLog.dll");
    if (!dll) { std::printf("no plugin\n"); return 2; }
    auto load = reinterpret_cast<bool (*)(const void*)>(GetProcAddress(dll, "SKSEPlugin_Load"));
    if (!load || !load(nullptr)) { std::printf("plugin didn't start\n"); return 3; }
    auto* ref = new TESObjectREFR();
    // A text the program really holds (a constant one would be folded away): on the heap, through a volatile pointer.
    char* const text = new char[32];
    std::strcpy(text, "Daggerfall Land.esm");
    const char* volatile held = text;
    std::printf("crashing now\n");
    std::fflush(stdout);
    return Crash(ref, held);
}
