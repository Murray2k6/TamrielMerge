# Skyrim Special Edition's Data folder, wherever a store installed it (dot-source; then SkyrimData). Found the way the
# converter finds it (GameInstalls.cs): Steam's libraries (libraryfolders.vdf) for the app manifest of 489830 and the
# folder it names, then GOG's registered installs. Nothing here names a drive: the install was moved from E: to C: on
# 2026-11-15 and every test script had the old path written into it.
function SkyrimData {
    $isData = { param($root) $d = Join-Path $root 'Data'; (Test-Path (Join-Path $d 'Skyrim.esm')) -and (Test-Path (Join-Path $d 'Dragonborn.esm')) }
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { $steam = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam' -ErrorAction SilentlyContinue).InstallPath }
    if ($steam) {
        $libs = @([IO.Path]::GetFullPath($steam))
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches([IO.File]::ReadAllText($vdf), '"path"\s+"([^"]+)"')) { $libs += [IO.Path]::GetFullPath($m.Groups[1].Value.Replace('\\', '\')) }
        }
        foreach ($lib in ($libs | Select-Object -Unique)) {
            $manifest = Join-Path $lib 'steamapps\appmanifest_489830.acf'
            if (-not (Test-Path $manifest)) { continue }
            $m = [regex]::Match([IO.File]::ReadAllText($manifest), '"installdir"\s+"([^"]+)"')
            if (-not $m.Success) { continue }
            $root = Join-Path $lib (Join-Path 'steamapps\common' $m.Groups[1].Value)
            if (& $isData $root) { return (Join-Path $root 'Data') }
        }
    }
    foreach ($key in 'HKLM:\SOFTWARE\WOW6432Node\GOG.com\Games', 'HKLM:\SOFTWARE\GOG.com\Games') {
        if (-not (Test-Path $key)) { continue }
        foreach ($game in Get-ChildItem $key) {
            $p = Get-ItemProperty $game.PSPath -ErrorAction SilentlyContinue
            if ($p.path -and $p.gameName -match 'Skyrim Special Edition|Skyrim Anniversary Edition' -and (& $isData $p.path)) { return (Join-Path $p.path 'Data') }
        }
    }
    throw 'Skyrim Special Edition is not installed through Steam or GOG'
}
# A scratch folder on the game's own drive: test installs go in by hard link, which only works within one drive.
function SkyrimScratch { Join-Path ([IO.Path]::GetPathRoot((SkyrimData))) 'tm-scratch' }
