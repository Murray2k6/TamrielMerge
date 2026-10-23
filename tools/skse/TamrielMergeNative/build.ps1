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
$bat = @"
@echo off
call "$vs\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
cd /d "$tp"
if not exist "$tp\extern\lib\cmake\common\common-config.cmake" (
  cmake -G Ninja -DCMAKE_MAKE_PROGRAM="$ninja" -DCMAKE_BUILD_TYPE=Release -B common\build -S common -DCMAKE_INSTALL_PREFIX="$tp\extern" || exit /b 1
  cmake --build common\build --target install || exit /b 1
)
cd /d "$here"
cmake -G Ninja -DCMAKE_MAKE_PROGRAM="$ninja" -DCMAKE_BUILD_TYPE=Release -B build -S . || exit /b 1
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
