# Builds TamrielMergeNative.dll (an SKSE plugin) with the newest Visual Studio that has the C++ tools, CMake and Ninja,
# against SKSE 2.3.1's source and ianpatt's common library in ..\third_party (common built and installed to
# ..\third_party\extern first when it isn't there).
$ErrorActionPreference = "Stop"
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "Visual Studio not found (no vswhere.exe)" }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "Visual Studio with the C++ tools not found" }
$ninja = (Get-Command ninja -ErrorAction SilentlyContinue).Source
if (-not $ninja) { throw "Ninja not found on PATH" }
$here = $PSScriptRoot
$tp = (Resolve-Path (Join-Path $here "..\third_party")).Path
# A build folder made with a compiler Visual Studio has since updated away is started afresh (2026-11-02: its cache named
# MSVC 14.51.36231, gone, and CMake stopped at "is not a full path to an existing compiler tool").
# The newest Windows SDK manifest tool that runs: SDK 10.0.28000's mt.exe stopped with 0xC0000135 (a DLL of its own
# missing, 2026-11-02), and CMake can't build without one.
$mt = Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin\*\x64\mt.exe") -ErrorAction SilentlyContinue |
    Sort-Object { [version]($_.Directory.Parent.Name) } -Descending |
    Where-Object { & $_.FullName "/?" *> $null; $LASTEXITCODE -eq 0 } | Select-Object -First 1
$mtArg = if ($mt) { "-DCMAKE_MT=`"$($mt.FullName)`"" } else { "" }
$cache = Join-Path $here "build\CMakeCache.txt"
if (Test-Path $cache) {
    $cl = (Select-String -Path $cache -Pattern '^CMAKE_CXX_COMPILER:[A-Z]+=(.+)$' | Select-Object -First 1).Matches.Groups[1].Value
    if ($cl -and -not (Test-Path $cl)) { Remove-Item (Join-Path $here "build") -Recurse -Force }
}
$bat = @"
@echo off
call "$vs\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
cd /d "$tp"
if not exist "$tp\extern\lib\cmake\common\common-config.cmake" (
  cmake -G Ninja -DCMAKE_MAKE_PROGRAM="$ninja" -DCMAKE_BUILD_TYPE=Release $mtArg -B common\build -S common -DCMAKE_INSTALL_PREFIX="$tp\extern" || exit /b 1
  cmake --build common\build --target install || exit /b 1
)
cd /d "$here"
cmake -G Ninja -DCMAKE_MAKE_PROGRAM="$ninja" -DCMAKE_BUILD_TYPE=Release $mtArg -B build -S . || exit /b 1
cmake --build build || exit /b 1
"@
$batFile = Join-Path $here "build.bat"
Set-Content -Path $batFile -Value $bat -Encoding ASCII
$out = & cmd.exe /c "`"$batFile`"" 2>&1
$code = $LASTEXITCODE
Remove-Item $batFile -ErrorAction SilentlyContinue
if ($code -ne 0) { $out | Select-String -Pattern "error|Error" | Select-Object -First 40; throw "build failed" }
$out | Where-Object { $_ -match "warning C" -and $_ -match "TamrielMergeNative.cpp" } | Select-Object -First 20
Copy-Item (Join-Path $here "build\TamrielMergeNative.dll") $here -Force
"built $(Join-Path $here 'TamrielMergeNative.dll')"
