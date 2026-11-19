# Helpers for driving Skyrim in a test (dot-source). MO2's lock window takes the foreground now and then: every step
# puts the game in front first.
$Game = 'D:\TamrielMerge\tools\ingame\game.ps1'
$Shots = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not ('FgWin' -as [type])) { Add-Type @"
using System; using System.Runtime.InteropServices;
public static class FgWin {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  public static uint FgPid() { uint p; GetWindowThreadProcessId(GetForegroundWindow(), out p); return p; }
}
"@ }
# The game in front, for sure: MO2's window minimised, then asked for until it is (Windows refuses a foreground change
# now and then; a tap of Alt lets it through).
function Front {
    $g = Get-Process SkyrimSE -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $g) { throw 'Skyrim is not running' }
    for ($i = 0; $i -lt 20; $i++) {
        if ([FgWin]::FgPid() -eq [uint32]$g.Id) { Start-Sleep -Milliseconds 300; return }
        Get-Process ModOrganizer -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | ForEach-Object { [FgWin]::ShowWindow($_.MainWindowHandle, 6) | Out-Null }
        [FgWin]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [FgWin]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
        [FgWin]::ShowWindow($g.MainWindowHandle, 9) | Out-Null; [FgWin]::SetForegroundWindow($g.MainWindowHandle) | Out-Null
        Start-Sleep -Milliseconds 700
    }
    throw 'could not put Skyrim in front'
}
function Keys($k) { Front; & $Game keys -Arg $k | Out-Null }
function Hold($k, $ms) { Front; & $Game hold -Arg $k -Ms $ms | Out-Null }
function Look($dx, $dy) { Front; & $Game move -X $dx -Y $dy | Out-Null }
# A message box's button: move the game's cursor with Look (about 1.57 screen pixels a unit in a box) until the button
# shows its arrow marks on both sides, the cursor's tip on the row of its text (a tip above the text does nothing: 2026-11-12),
# then Click. Keys do nothing in a box. The main menu takes the arrow keys and Enter.
function Click { Front; & $Game press | Out-Null }
# The plugin's test watcher ([Test] Watch=1 in Data\SKSE\Plugins\TamrielMergeNative.ini): a word written to
# TamrielMergeNative.cmd is answered in MO2's overwrite folder. State: which menus are open and where the player is
# (a hashtable; $null when the plugin doesn't answer). Dump: every reference of the player's cell, as lines.
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'skyrim.ps1')
$PluginDir = Join-Path (SkyrimData) 'SKSE\Plugins'
$Overwrite = 'C:\Modding\MO2\overwrite\SKSE\Plugins'
# Read while the game writes it: tried again for a moment when the file is held.
function ReadShared($p) { for ($i = 0; ; $i++) { try { $fs = [IO.File]::Open($p, 'Open', 'Read', 'ReadWrite,Delete'); try { return (New-Object IO.StreamReader $fs).ReadToEnd() } finally { $fs.Close() } } catch [IO.IOException] { if ($i -ge 20) { throw }; Start-Sleep -Milliseconds 100 } } }
function State {
    $sf = Join-Path $Overwrite 'TamrielMergeNative.state'
    $before = if (Test-Path $sf) { (Get-Item $sf).LastWriteTimeUtc } else { [datetime]::MinValue }
    [IO.File]::WriteAllText((Join-Path $PluginDir 'TamrielMergeNative.cmd'), "state $([guid]::NewGuid())")
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 150
        if ((Test-Path $sf) -and (Get-Item $sf).LastWriteTimeUtc -gt $before) {
            $h = @{}; foreach ($line in (ReadShared $sf) -split "`n") { $k, $v = $line.Split('=', 2); if ($k) { $h[$k.Trim()] = "$v".Trim() } }; return $h
        }
    }
    $null
}
function Dump {
    $log = Join-Path $Overwrite 'TamrielMergeNative.log'
    $n = ([regex]::Matches((ReadShared $log), 'DumpCell: \d+ references')).Count
    [IO.File]::WriteAllText((Join-Path $PluginDir 'TamrielMergeNative.cmd'), "dump $([guid]::NewGuid())")
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $t = ReadShared $log
        if (([regex]::Matches($t, 'DumpCell: \d+ references')).Count -gt $n) { return ($t.Substring($t.LastIndexOf('DumpCell: cell')) -split "`r?`n") }
    }
    throw 'the plugin did not dump the cell (is the game paused under a menu?)'
}
# The temporary log of the faces (the plugin's DumpHeads): every person of the player's cell with the head parts of its
# record, its race's default ones and what the game put on its head (shapes, shaders, textures, tint), as lines.
function Heads {
    $log = Join-Path $Overwrite 'TamrielMergeNative.log'
    $before = (ReadShared $log).Length
    [IO.File]::WriteAllText((Join-Path $PluginDir 'TamrielMergeNative.cmd'), "heads $([guid]::NewGuid())")
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $t = ReadShared $log
        if ($t.Length -gt $before -and $t.Substring($before) -match 'DumpHeads: \d+ people') { return ($t.Substring($before) -split "`r?`n") }
    }
    throw 'the plugin did not log the heads (an older plugin, or the game paused under a menu?)'
}
# A reference's collision as the game has it now (the plugin's DumpBodies): each node with collision, its place and turn
# against its Havok body's. $Id: the reference's form ID in the game's load order (8 hex digits).
function Bodies([string]$Id) {
    $log = Join-Path $Overwrite 'TamrielMergeNative.log'
    $before = (ReadShared $log).Length
    [IO.File]::WriteAllText((Join-Path $PluginDir 'TamrielMergeNative.cmd'), "bodies $Id")
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $t = ReadShared $log
        if ($t.Length -gt $before -and $t.Substring($before) -match "DumpBodies: (?i:$Id)") {
            Start-Sleep -Milliseconds 250
            return ((ReadShared $log).Substring($before) -split "`r?`n" | Where-Object { $_ })
        }
    }
    throw 'the plugin did not log the bodies (an older plugin, or the game paused under a menu?)'
}
# The console brought to the state asked for (its key toggles it: pressed only when the plugin says it is the other way).
function SetConsole([bool]$open) {
    for ($i = 0; $i -lt 4; $i++) {
        $s = State
        if ($null -eq $s) { throw 'the plugin does not answer (no test watcher?)' }
        if (($s['Console'] -eq '1') -eq $open) { return }
        Front; & $Game keys -Arg '`' | Out-Null; Start-Sleep -Milliseconds 700
    }
    throw "could not $(if ($open) { 'open' } else { 'close' }) the console"
}
function Con($c) { SetConsole $true; Front; & $Game type -Arg $c | Out-Null; & $Game keys -Arg 'enter' | Out-Null; Start-Sleep -Milliseconds 600; SetConsole $false }
# Waits until no loading screen is up and the player stands in a cell (seconds at most).
function WaitLoaded($seconds = 120) {
    $t = 0; Start-Sleep 2
    while ($t -lt $seconds) { $s = State; if ($s -and $s['Loading Menu'] -eq '0' -and $s['Main Menu'] -eq '0' -and $s['cell'] -and $s['cell'] -ne '00000000') { Start-Sleep 2; return $s }; Start-Sleep 1; $t++ }
    throw 'still loading'
}
function Shot($name, $scale = 0.5) { Front; & $Game shot -Out "$Shots\$name.png" -Scale $scale }
function ConShot($name, $scale = 0.5) { SetConsole $true; Start-Sleep -Milliseconds 500; & $Game shot -Out "$Shots\$name.png" -Scale $scale; SetConsole $false }
function StopGame {
    $p = Get-CimInstance Win32_Process -Filter "Name='SkyrimSE.exe'"; if ($p) { Stop-Process -Id $p.ProcessId -Force }
    Start-Sleep 5; Get-Process ModOrganizer -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null }; Start-Sleep 5
}
# Starts the game through MO2 and waits for the main menu, then coc's to a cell.
function StartGame($cell, $loadSeconds = 60) {
    Start-Process 'C:\Modding\MO2\ModOrganizer.exe' -ArgumentList '"moshortcut://:SKSE"'
    $t = 0; while ($t -lt 240) { Start-Sleep 5; $t += 5; $g2 = Get-Process SkyrimSE -ErrorAction SilentlyContinue; if ($g2 -and $g2.MainWindowHandle -ne 0) { break } }
    # The main menu up (the plugin says so), then the cell by the console, and its loading over.
    # (Up to ten minutes: with every converted game installed the data took 3 min 45 s to load on 2026-11-14, and a flow
    # that went on after three pressed its keys into the loading screen.)
    $t = 0; while ($t -lt 600) { Start-Sleep 3; $t += 3; $s = State; if ($s -and $s['Main Menu'] -eq '1' -and $s['Loading Menu'] -eq '0') { break } }
    Start-Sleep 6
    if ($cell) { Con "coc $cell"; WaitLoaded $loadSeconds | Out-Null }
}
# The console's "sv" of a reference, paged up n times: one shot per page (name0..), for reading a script's variables.
function SvShots($ref, $name, $pages = 6) {
    Front; & $Game keys -Arg '`' | Out-Null; Start-Sleep 1
    & $Game type -Arg "prid $ref" | Out-Null; & $Game keys -Arg 'enter' | Out-Null; Start-Sleep -Milliseconds 300
    & $Game type -Arg 'sv' | Out-Null; & $Game keys -Arg 'enter' | Out-Null; Start-Sleep 1
    for ($i = 0; $i -lt $pages; $i++) { & $Game shot -Out "$Shots\$name$i.png" -Scale 1.0 | Out-Null; & $Game keys -Arg 'pgup' | Out-Null; Start-Sleep -Milliseconds 300 }
    & $Game keys -Arg '`' | Out-Null
}
# God mode, by the console (user, 2026-11-14: "use the console for god mode"): the one thing a scene test types there.
function God { Con 'tgm' }
