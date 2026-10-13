# Builds TamrielMergeGraphics.dll (an SKSE plugin) with the newest Visual Studio that has the C++ tools, against
# Dear ImGui and NVIDIA's DLSS SDK in ..\third_party, and puts NVIDIA's DLSS runtime (nvngx_dlss.dll) beside it.
$ErrorActionPreference = "Stop"
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "Visual Studio not found (no vswhere.exe)" }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "Visual Studio with the C++ tools not found" }
$here = $PSScriptRoot
$tp = (Resolve-Path (Join-Path $here "..\third_party")).Path
$imgui = "$tp\imgui"; $dlss = "$tp\DLSS"
$sources = "TamrielMergeGraphics.cpp `"$imgui\imgui.cpp`" `"$imgui\imgui_draw.cpp`" `"$imgui\imgui_tables.cpp`" `"$imgui\imgui_widgets.cpp`" " +
           "`"$imgui\backends\imgui_impl_dx11.cpp`" `"$imgui\backends\imgui_impl_win32.cpp`""
# A batch file of its own: cmd starts in its folder (cd /d), which PowerShell's location doesn't give a child process.
$bat = @"
@echo off
cd /d "$here"
call "$vs\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
cl /nologo /LD /O2 /EHsc /std:c++20 /MT /W3 /D_CRT_SECURE_NO_WARNINGS /DNOMINMAX /I"$imgui" /I"$dlss\include" $sources /Fe:TamrielMergeGraphics.dll /link "$dlss\lib\Windows_x86_64\x64\nvsdk_ngx_s.lib" d3d11.lib dxgi.lib dinput8.lib dxguid.lib d3dcompiler.lib user32.lib advapi32.lib version.lib d3d12.lib
"@
$batFile = Join-Path $here "build.bat"
Set-Content -Path $batFile -Value $bat -Encoding ASCII
$out = & cmd.exe /c "`"$batFile`"" 2>&1
$code = $LASTEXITCODE
Remove-Item $batFile -ErrorAction SilentlyContinue
if ($code -ne 0) { $out | Select-Object -Last 40; throw "compile failed" }
Copy-Item "$dlss\lib\Windows_x86_64\rel\nvngx_dlss.dll" $here -Force
Get-ChildItem $here | Where-Object { $_.Extension -in ".obj", ".exp", ".lib" } | Remove-Item -ErrorAction SilentlyContinue
"built $(Join-Path $here 'TamrielMergeGraphics.dll')"
