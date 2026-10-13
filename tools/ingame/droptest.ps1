param([string] $Points, [string] $Prefix, [double] $Wait = 4)
# Expects the console OPEN. For each "name:x,y,z": set the player there, close the console so the game runs for
# $Wait seconds, reopen it and read the height (screenshot). Leaves the console open.
$s = $PSScriptRoot
& "$s\game.ps1" -Action focus -Arg SkyrimSE | Out-Null
foreach ($p in $Points.Split(';')) {
    $name, $xyz = $p.Split(':'); $x, $y, $z = $xyz.Split(',')
    foreach ($cmd in @("player.setpos x $x", "player.setpos y $y", "player.setpos z $z")) {
        & "$s\game.ps1" -Action type -Arg $cmd | Out-Null; & "$s\game.ps1" -Action keys -Arg enter | Out-Null; Start-Sleep -Milliseconds 250
    }
    & "$s\game.ps1" -Action keys -Arg '`' | Out-Null
    Start-Sleep -Milliseconds ([int]($Wait * 1000))
    & "$s\game.ps1" -Action keys -Arg '`' | Out-Null; Start-Sleep -Milliseconds 500
    & "$s\game.ps1" -Action type -Arg "player.getpos z" | Out-Null; & "$s\game.ps1" -Action keys -Arg enter | Out-Null; Start-Sleep -Milliseconds 300; & "$s\game.ps1" -Action type -Arg "player.getpos x" | Out-Null; & "$s\game.ps1" -Action keys -Arg enter | Out-Null; Start-Sleep -Milliseconds 400
    & "$s\game.ps1" -Action shot -Out "$s\$Prefix-$name.png" -Scale 0.6 | Out-Null
    "done $name"
}
