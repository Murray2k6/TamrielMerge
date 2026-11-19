// HangTest: a window that answers for two seconds, then stops answering for twenty, with TamrielMergeCrashLog.dll
// loaded on its main thread as SKSE loads it. The plugin's log beside it must then hold a HANG report with the main
// thread's call stack, in HangInside.
#include <windows.h>
#include <cstdio>

static LRESULT CALLBACK Proc(HWND h, UINT m, WPARAM w, LPARAM l) { return DefWindowProcA(h, m, w, l); }

extern "C" __declspec(noinline) void HangInside()
{
    Sleep(20000);
}

int main()
{
    WNDCLASSA wc{};
    wc.lpfnWndProc = Proc;
    wc.hInstance = GetModuleHandleA(nullptr);
    wc.lpszClassName = "TMHangTest";
    RegisterClassA(&wc);
    HWND w = CreateWindowA("TMHangTest", "TM hang test", WS_OVERLAPPEDWINDOW, 0, 0, 200, 100, nullptr, nullptr, wc.hInstance, nullptr);
    ShowWindow(w, SW_SHOWNOACTIVATE);
    HMODULE dll = LoadLibraryA("TamrielMergeCrashLog.dll");
    auto load = dll ? reinterpret_cast<bool (*)(const void*)>(GetProcAddress(dll, "SKSEPlugin_Load")) : nullptr;
    if (!load || !load(nullptr)) { std::puts("the plugin didn't load"); return 2; }
    for (DWORD until = GetTickCount() + 2000; GetTickCount() < until;)
    {
        MSG msg;
        while (PeekMessageA(&msg, nullptr, 0, 0, PM_REMOVE)) DispatchMessageA(&msg);
        Sleep(10);
    }
    std::puts("hanging now");
    HangInside();
    return 0;
}
