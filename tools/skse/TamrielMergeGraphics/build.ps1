# Builds TamrielMergeGraphics.dll (an SKSE plugin) with the newest Visual Studio that has the C++ tools, against
# Dear ImGui and NVIDIA's DLSS SDK in ..\third_party, and puts NVIDIA's DLSS runtime (nvngx_dlss.dll) beside it.
# The shaders are compiled first with the Windows SDK's compilers: dxc (shader model 6.5: the ray tracing's compute
# shaders, inline ray queries) and fxc (shader model 5: the Direct3D 11 passes), into gen\*.h.
#   -Harness   also builds test\RtHarness.exe (the ray tracing's offline test; run it to check the GPU side)
param([switch]$Harness)
$ErrorActionPreference = "Stop"
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "Visual Studio not found (no vswhere.exe)" }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "Visual Studio with the C++ tools not found" }
$here = $PSScriptRoot
$tp = (Resolve-Path (Join-Path $here "..\third_party")).Path
$imgui = "$tp\imgui"; $dlss = "$tp\DLSS"

# The newest Windows SDK with both shader compilers (dxc beside its dxil.dll, so its output is signed).
$kits = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
$sdk = Get-ChildItem $kits -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^10\.' } | Sort-Object { [version]$_.Name } -Descending |
       ForEach-Object { Join-Path $_.FullName "x64" } | Where-Object { (Test-Path "$_\dxc.exe") -and (Test-Path "$_\fxc.exe") -and (Test-Path "$_\dxil.dll") } | Select-Object -First 1
if (-not $sdk) { throw "the Windows SDK's shader compilers (dxc.exe with dxil.dll, fxc.exe) not found under $kits" }
$gen = Join-Path $here "gen"
New-Item -ItemType Directory -Force $gen | Out-Null
$shaders = Join-Path $here "shaders"
$dxc = @(
    @("RtTrace.hlsl", "main", "g_RtTrace", "RtTrace"),
    @("RtDenoise.hlsl", "Temporal", "g_RtTemporal", "RtTemporal"),
    @("RtDenoise.hlsl", "Spatial", "g_RtSpatial", "RtSpatial"))
foreach ($s in $dxc) {
    $o = & "$sdk\dxc.exe" -nologo -T cs_6_5 -E $s[1] -O3 -Vn $s[2] -Fh "$gen\$($s[3]).h" "$shaders\$($s[0])" 2>&1
    if ($LASTEXITCODE -ne 0) { $o | Select-Object -Last 30; throw "dxc failed on $($s[0]) $($s[1])" }
}
$fxc = @(@("FullscreenVS", "vs_5_0"), @("PreparePS", "ps_5_0"), @("CompositePS", "ps_5_0"), @("NormalsPS", "ps_5_0"), @("SharpenPS", "ps_5_0"))
foreach ($s in $fxc) {
    $o = & "$sdk\fxc.exe" /nologo /T $s[1] /E $s[0] /O3 /Vn "g_$($s[0])" /Fh "$gen\$($s[0]).h" "$shaders\Game11.hlsl" 2>&1
    if ($LASTEXITCODE -ne 0) { $o | Select-Object -Last 30; throw "fxc failed on $($s[0])" }
}

$sources = "TamrielMergeGraphics.cpp RtCore.cpp `"$imgui\imgui.cpp`" `"$imgui\imgui_draw.cpp`" `"$imgui\imgui_tables.cpp`" `"$imgui\imgui_widgets.cpp`" " +
           "`"$imgui\backends\imgui_impl_dx11.cpp`" `"$imgui\backends\imgui_impl_win32.cpp`""
$libs = "d3d11.lib dxgi.lib dinput8.lib dxguid.lib d3dcompiler.lib user32.lib advapi32.lib version.lib d3d12.lib"
# A batch file of its own: cmd starts in its folder (cd /d), which PowerShell's location doesn't give a child process.
$bat = @"
@echo off
cd /d "$here"
call "$vs\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
cl /nologo /LD /O2 /EHsc /std:c++20 /MT /W3 /D_CRT_SECURE_NO_WARNINGS /DNOMINMAX /I"$imgui" /I"$dlss\include" $sources /Fe:TamrielMergeGraphics.dll /link "$dlss\lib\Windows_x86_64\x64\nvsdk_ngx_s.lib" $libs
if errorlevel 1 exit /b 1
"@
if ($Harness) {
    $bat += "`r`ncl /nologo /O2 /EHsc /std:c++20 /MT /W3 /D_CRT_SECURE_NO_WARNINGS /DNOMINMAX test\RtHarness.cpp RtCore.cpp /Fo:test\ /Fe:test\RtHarness.exe /link $libs`r`n"
}
$batFile = Join-Path $here "build.bat"
Set-Content -Path $batFile -Value $bat -Encoding ASCII
$out = & cmd.exe /c "`"$batFile`"" 2>&1
$code = $LASTEXITCODE
Remove-Item $batFile -ErrorAction SilentlyContinue
if ($code -ne 0) { $out | Select-Object -Last 40; throw "compile failed" }
$out | Where-Object { $_ -match "warning" } | Select-Object -First 20
Copy-Item "$dlss\lib\Windows_x86_64\rel\nvngx_dlss.dll" $here -Force
Get-ChildItem $here, (Join-Path $here "test") -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in ".obj", ".exp", ".lib" } | Remove-Item -ErrorAction SilentlyContinue
"built $(Join-Path $here 'TamrielMergeGraphics.dll')"
if ($Harness) { "built $(Join-Path $here 'test\RtHarness.exe')" }
