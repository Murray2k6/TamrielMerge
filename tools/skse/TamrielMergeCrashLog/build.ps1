# Builds TamrielMergeCrashLog.dll (an SKSE plugin) with the newest Visual Studio that has the C++ tools.
#   -Test   also builds test\CrashTest.exe and runs it: it must leave a report naming its object's class and FormID.
param([switch]$Test)
$ErrorActionPreference = "Stop"
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) { throw "Visual Studio not found (no vswhere.exe)" }
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw "Visual Studio with the C++ tools not found" }
$here = $PSScriptRoot
$bat = @"
@echo off
cd /d "$here"
call "$vs\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
cl /nologo /LD /O2 /EHsc /std:c++20 /MT /W3 /D_CRT_SECURE_NO_WARNINGS TamrielMergeCrashLog.cpp /Fe:TamrielMergeCrashLog.dll /link dbghelp.lib psapi.lib
if errorlevel 1 exit /b 1
"@
if ($Test) { $bat += "`r`ncl /nologo /O1 /EHsc /std:c++20 /MT /GR test\CrashTest.cpp /Fo:test\ /Fe:test\CrashTest.exe`r`nif errorlevel 1 exit /b 1`r`n" }
$batFile = Join-Path $here "build.bat"
Set-Content -Path $batFile -Value $bat -Encoding ASCII
$out = & cmd.exe /c "`"$batFile`"" 2>&1
$code = $LASTEXITCODE
Remove-Item $batFile -ErrorAction SilentlyContinue
if ($code -ne 0) { $out | Select-Object -Last 30; throw "compile failed" }
$out | Where-Object { $_ -match "warning" } | Select-Object -First 20
Get-ChildItem $here, (Join-Path $here "test") -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in ".obj", ".exp", ".lib" } | Remove-Item -ErrorAction SilentlyContinue
"built $(Join-Path $here 'TamrielMergeCrashLog.dll')"
if ($Test) {
    # The test crashes on purpose; the plugin writes its log beside itself, so the test runs on a copy in test\.
    Copy-Item (Join-Path $here "TamrielMergeCrashLog.dll") (Join-Path $here "test") -Force
    $log = Join-Path $here "test\TamrielMergeCrashLog.log"
    Remove-Item $log -ErrorAction SilentlyContinue
    $p = Start-Process -FilePath (Join-Path $here "test\CrashTest.exe") -ArgumentList "`"$(Join-Path $here 'test\TamrielMergeCrashLog.dll')`"" -WorkingDirectory (Join-Path $here "test") -Wait -PassThru -NoNewWindow
    $text = if (Test-Path $log) { Get-Content $log -Raw } else { "" }
    $checks = [ordered]@{
        "the crash written" = $text -match "EXCEPTION_ACCESS_VIOLATION"
        "where, in CrashTest.exe" = $text -match "at CrashTest\.exe\+"
        "the address written" = $text -match "writing address"
        "a call stack" = $text -match "\[ 0\] [0-9A-F]+ CrashTest\.exe\+"
        "the object's class" = $text -match "\(TESObjectREFR\*\)"
        "its FormID and type" = $text -match "FormID 0A012345 type 3D"
        "the text" = $text -match '"Daggerfall Land\.esm"'
        "the modules" = $text -match "TamrielMergeCrashLog\.dll"
    }
    $checks.GetEnumerator() | ForEach-Object { "  {0,-26} {1}" -f $_.Key, $(if ($_.Value) { "pass" } else { "FAIL" }) }
    "test: exit code {0:X8}, {1} of {2} checks pass" -f $p.ExitCode, ($checks.Values | Where-Object { $_ }).Count, $checks.Count
}
