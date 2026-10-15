# Builds TamrielMergeSeamless.dll (an SKSE plugin) with the newest Visual Studio that has the C++ tools.
$ErrorActionPreference = "Stop"
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "Visual Studio not found (no vswhere.exe)" }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "Visual Studio with the C++ tools not found" }
$vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat"
Push-Location $PSScriptRoot
try {
    # vcvars64.bat itself complains about vswhere.exe not being on PATH; harmless, so its output is dropped.
    cmd /c "`"$vcvars`" >nul 2>&1 && cl /nologo /LD /O2 /EHsc /std:c++20 /MT /W3 /D_CRT_SECURE_NO_WARNINGS TamrielMergeSeamless.cpp /Fe:TamrielMergeSeamless.dll"
    if ($LASTEXITCODE -ne 0) { throw "compile failed" }
    Remove-Item *.obj, *.exp, *.lib -ErrorAction SilentlyContinue
    "built $(Join-Path $PSScriptRoot 'TamrielMergeSeamless.dll')"
}
finally { Pop-Location }
