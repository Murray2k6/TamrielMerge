# Morrowind's weather, climate and water

User request (2026-10-15): "make all placeholders real code leaving no placeholder behind and working". Vvardenfell and
Solstheim (worldspace MWVvardenfell) used Skyrim's climate and water and vanilla Skyrim weathers per region. They now
have Morrowind's own (`Morrowind/MwWeather.cs`), read from Morrowind.ini, where the game keeps them.

## Weathers

Morrowind's ten weathers, in its region records' (REGN WEAT) order - clear, cloudy, foggy, overcast, rain,
thunderstorm, ash storm, blight storm, and Bloodmoon's snow and blizzard - are Skyrim weathers MWWeather&lt;name&gt;,
each a copy of the vanilla weather of its kind (SkyrimClear, SkyrimCloudy, SkyrimFog, SkyrimCloudy, SkyrimOvercastRain,
SkyrimStormRain, Dragonborn's DLC02VolcanicAshStorm01 for both storms, SkyrimOvercastSnow, SkyrimStormSnow: its image
spaces, lens flare and header) given Morrowind's:

- **Sky and fog** ([Weather X] Sky/Fog Sunrise/Day/Sunset/Night Color): the sky colour overhead, blending to the fog
  colour at the horizon; fog near/far colour the fog colour; clouds and water tinted by it.
- **Light**: ambient flat on every side (Morrowind has no directional ambient), sunlight Morrowind's sun colour x 0.64
  (Skyrim's clear-day sun reads that bright), the sun disc x 0.31 with Morrowind's sunset disc colour, sun glare by
  Glare View, glare colour from [Weather] Sun Glare Fader Color.
- **Fog distance**: Morrowind's fog starts (1 - depth) of the way to the view distance; the view distance is the
  vanilla weather's own visibility (Skyrim draws distant land), shortened by a depth over 1 (blizzards, storms).
- **Clouds**: Morrowind's cloud texture (Tx_Sky_*.dds, converted by MwTextures) as the overhead layer, at its cloud
  speed (x 0.02, Skyrim's scale) and its Clouds Maximum Percent as opacity; Skyrim's other layers and sky statics off.
- **Wind** speed; thunder only in the thunderstorm.
- **Precipitation**: rain and snow keep Skyrim's particle settings of the kind (MWParticlesRain/Thunder/Snow/Blizzard)
  drawn with Morrowind's drop and flake (tx_raindrop_01, tx_bm_snowflakes_01), denser with Max Raindrops/Snowflakes.
- **Storms**: Morrowind's own storm models - ashcloud.nif, blightcloud.nif, blizzard.nif - converted to Skyrim particle
  systems by MwVfx, as camera-attached visual effects (MWStorm&lt;name&gt;Effect, attach to camera and inherit
  rotation, as Skyrim's own weather effects); ash and blight take the neutral overcast image spaces (not Solstheim's).
- **Sounds**: the ambient loop (ashstorm, Blight, BM Blizzard), rain loop (Rain, rain heavy) and thunder (Thunder0-3)
  sounds as Skyrim descriptors with Morrowind's files, set up like Skyrim's rain loop and distant thunder.

Scripts' ChangeWeather/GetCurrentWeather use the same records.

## Climates and regions

Each Morrowind region is a Skyrim region (MWRegion&lt;name&gt;) over its cells with Morrowind's weathers at its own
chances, and every cell lists its region; each region also has a climate of its own (MWClimate&lt;name&gt;) with those
chances. The worldspace's climate, MWClimateVvardenfell, mixes all regions' chances by their cells. Climates carry
Morrowind's sun (tx_sun_05, glare tx_sun_flash_grey_05) and its sunrise and sunset ([Weather] Sunrise/Sunset Time and
Duration, with the sky's pre/post times: 5:30-9:00 and 16:30-20:30); the moons (Masser and Secunda) and stars are
Skyrim's.

## Water

One water record, MWWater, on Skyrim's default water: shallow colour the average of Morrowind's surface texture
(water00), reflections lighter, deep colour Morrowind's UnderwaterColor, opacity its World Alpha (0.75), underwater fog
from UnderwaterDayFog, and three frames of its animated surface (water00, 10, 21) as the noise layers, normal maps from
their brightness, tiled as Morrowind tiles them (SurfaceTileCount per cell). The worldspace's water and LOD water,
at Morrowind's sea level 0, and every interior with water use it.

## Checking

`mw-build --cells "^Seyda Neen, Census" --exterior-box "-3,-10,-1,-8" --no-dialogue --no-scripts --out DIR` builds a
few interiors and the exterior cells in that box only (prints the weathers, climates, regions and water), then
`verify --plugin DIR\Morrowind.esm` and `plugin-check --data DIR --plugins Morrowind.esm`; `esm-dump --plugin
DIR\Morrowind.esm --edid "^MWWeather"` shows every field. Not yet seen in the game.
