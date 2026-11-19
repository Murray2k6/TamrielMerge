"""How blend_scene.py runs, not what it draws: the build's phase timings and memory, and how many shaders compile at once.
Kept apart from blend_scene.py because the menu's frames are kept while what they are made from is unchanged
(intro.py rt_render hashes blend_scene.py and blend_prep.py, not this file): a change here leaves every frame as it was
(2026-10-24: the shader workers' limit, rendered with it and without, gave the same frame byte for byte).
"""
import os
import time

PHASE_CLOCK = [time.time()]


def private_gb():
    """This process's committed memory (GB), for the build's phase lines."""
    import ctypes
    from ctypes import wintypes

    class PMC(ctypes.Structure):
        _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD), ("PeakWorkingSetSize", ctypes.c_size_t),
                    ("WorkingSetSize", ctypes.c_size_t), ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                    ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                    ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t), ("PrivateUsage", ctypes.c_size_t)]
    try:
        k = ctypes.windll.kernel32
        # 64-bit handles and the counters' pointer spelled out (left to ctypes' int defaults, the call failed: 0.0 GB).
        k.GetCurrentProcess.restype = wintypes.HANDLE
        k.K32GetProcessMemoryInfo.argtypes = [wintypes.HANDLE, ctypes.POINTER(PMC), wintypes.DWORD]
        k.K32GetProcessMemoryInfo.restype = wintypes.BOOL
        c = PMC(); c.cb = ctypes.sizeof(PMC)
        if not k.K32GetProcessMemoryInfo(k.GetCurrentProcess(), ctypes.byref(c), c.cb):
            return 0.0
        return c.PrivateUsage / 2 ** 30
    except (AttributeError, OSError):
        return 0.0


def phase(name):
    """A build step's time and the memory after it (user, 2026-10-24: "it shouldnt take that long for the flybys")."""
    now = time.time()
    print(f"  phase {name}: {now - PHASE_CLOCK[0]:.1f} s, {private_gb():.1f} GB", flush=True)
    PHASE_CLOCK[0] = now


def limit_shader_workers(bpy):
    """Few shader compiles at once (TM_SHADER_WORKERS, default 4): on Blender's own count (one a CPU thread) Whiterun's
    run went from 3.8 to 11.8 GB in 8 s as EEVEE started, its graphics memory 1.1 GB (2026-10-24, peaks 15-17 GB). Set
    after the factory settings are read (they reset the preferences)."""
    try:
        sysprefs = bpy.context.preferences.system
        sysprefs.shader_compilation_method = "THREAD"
        sysprefs.gpu_shader_workers = int(os.environ.get("TM_SHADER_WORKERS", "4"))
    except (AttributeError, TypeError, ValueError):
        print("  (no gpu_shader_workers)", flush=True)
