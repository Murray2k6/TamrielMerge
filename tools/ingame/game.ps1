param(
    [Parameter(Mandatory)] [ValidateSet('shot', 'keys', 'type', 'focus', 'click', 'hold', 'move', 'press')] [string] $Action,
    [string] $Arg = '',
    [string] $Out = '',
    [int] $X = 0, [int] $Y = 0, [int] $Ms = 0, [double] $Scale = 0.5
)
# Screenshots and scan-code keyboard/mouse input for driving Skyrim while diagnosing (DirectInput reads scan codes).
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
if (-not ('GameInput' -as [type])) {
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class GameInput {
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct UNION { [FieldOffset(0)] public KEYBDINPUT k; [FieldOffset(0)] public MOUSEINPUT m; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public UNION u; }
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    public static uint ForegroundPid() { uint pid; GetWindowThreadProcessId(GetForegroundWindow(), out pid); return pid; }
    const uint SCANCODE = 0x8, KEYUP = 0x2, EXTENDED = 0x1;
    public static void Key(ushort scan, bool up, bool ext) {
        var i = new INPUT { type = 1 };
        i.u.k = new KEYBDINPUT { wScan = scan, dwFlags = SCANCODE | (up ? KEYUP : 0) | (ext ? EXTENDED : 0) };
        SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT)));
    }
    public static void Mouse(uint flags) {
        var i = new INPUT { type = 0 };
        i.u.m = new MOUSEINPUT { dwFlags = flags };
        SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT)));
    }
    public static void MoveBy(int dx, int dy) {
        var i = new INPUT { type = 0 };
        i.u.m = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = 0x1 };
        SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT)));
    }
}
"@
}

# Scan codes (set 1) for keys used in Skyrim menus and the console.
$scan = @{
    'esc'=0x01; '1'=0x02; '2'=0x03; '3'=0x04; '4'=0x05; '5'=0x06; '6'=0x07; '7'=0x08; '8'=0x09; '9'=0x0A; '0'=0x0B; '-'=0x0C; '='=0x0D;
    'backspace'=0x0E; 'tab'=0x0F; 'q'=0x10; 'w'=0x11; 'e'=0x12; 'r'=0x13; 't'=0x14; 'y'=0x15; 'u'=0x16; 'i'=0x17; 'o'=0x18; 'p'=0x19;
    'enter'=0x1C; 'a'=0x1E; 's'=0x1F; 'd'=0x20; 'f'=0x21; 'g'=0x22; 'h'=0x23; 'j'=0x24; 'k'=0x25; 'l'=0x26; ';'=0x27; "'"=0x28; '`'=0x29;
    'shift'=0x2A; 'z'=0x2C; 'x'=0x2D; 'c'=0x2E; 'v'=0x2F; 'b'=0x30; 'n'=0x31; 'm'=0x32; ','=0x33; '.'=0x34; '/'=0x35; 'space'=0x39;
    'up'=0x48; 'down'=0x50; 'left'=0x4B; 'right'=0x4D; 'f5'=0x3F; 'f9'=0x43
}
$extended = @('up', 'down', 'left', 'right')

# Input only ever goes to the game: before each key, Skyrim must be the foreground window, or this stops (once,
# focus moved mid-test and the typed console commands landed in the user's editor).
function Guard {
    $fg = [GameInput]::ForegroundPid()
    $game = Get-Process -Name SkyrimSE -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $game -or $fg -ne [uint32]$game.Id) { throw "Skyrim isn't the foreground window: input stopped" }
}

function Press([string] $k, [int] $holdMs = 40) {
    Guard
    $code = $scan[$k.ToLower()]
    if ($null -eq $code) { throw "unknown key $k" }
    $ext = $extended -contains $k.ToLower()
    [GameInput]::Key([uint16]$code, $false, $ext); Start-Sleep -Milliseconds $holdMs
    [GameInput]::Key([uint16]$code, $true, $ext); Start-Sleep -Milliseconds 60
}

switch ($Action) {
    'focus' {
        $p = Get-Process | Where-Object { $_.ProcessName -match $Arg -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
        if (-not $p) { "no window for $Arg"; break }
        [GameInput]::ShowWindow($p.MainWindowHandle, 9) | Out-Null
        [GameInput]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
        "focused $($p.ProcessName) $($p.Id)"
    }
    'shot' {
        $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
        $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
        $w = [int]($b.Width * $Scale); $h = [int]($b.Height * $Scale)
        $small = New-Object System.Drawing.Bitmap $bmp, $w, $h
        $small.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose(); $small.Dispose()
        "saved $Out ($w x $h of $($b.Width) x $($b.Height))"
    }
    'keys' { foreach ($k in $Arg.Split(' ', [StringSplitOptions]::RemoveEmptyEntries)) { Press $k } ; "sent $Arg" }
    'hold' { Press $Arg $Ms; "held $Arg $Ms ms" }
    'type' {
        foreach ($ch in $Arg.ToCharArray()) {
            $c = [string]$ch
            if ($c -eq ' ') { Press 'space'; continue }
            if ($c -cmatch '[A-Z]') { [GameInput]::Key(0x2A, $false, $false); Press $c.ToLower(); [GameInput]::Key(0x2A, $true, $false); continue }
            if ($c -eq '_') { [GameInput]::Key(0x2A, $false, $false); Press '-'; [GameInput]::Key(0x2A, $true, $false); continue }
            Press $c
        }
        "typed $Arg"
    }
    'move' {
        # Relative mouse movement in small steps (the game's own cursor and camera read movement, not position).
        Guard
        $steps = [Math]::Max(1, [int]([Math]::Max([Math]::Abs($X), [Math]::Abs($Y)) / 10))
        for ($k = 0; $k -lt $steps; $k++) { [GameInput]::MoveBy([int]($X / $steps), [int]($Y / $steps)); Start-Sleep -Milliseconds 10 }
        "moved $X,$Y"
    }
    'press' {
        # A left click where the game's cursor is.
        Guard
        [GameInput]::Mouse(0x2); Start-Sleep -Milliseconds 60; [GameInput]::Mouse(0x4)
        "pressed"
    }
    'click' {
        Guard
        [GameInput]::SetCursorPos($X, $Y) | Out-Null; Start-Sleep -Milliseconds 80
        [GameInput]::Mouse(0x2); Start-Sleep -Milliseconds 60; [GameInput]::Mouse(0x4)
        "clicked $X,$Y"
    }
}
