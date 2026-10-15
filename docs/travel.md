# Travel

User requests (2026-10-12): "i need the travel like daggerfalls, like it can take days to get to a town"; "it also has to
by the users game list and what they own" (chose: every converted world of the user's games); "except we will travel and
see it realtime seeing yourself travel without loading" (chose: adjustable speed, encounters stop the journey, ships
sail in real time); "remember we need the era to be labled with the destination".

## What the player gets

The Tamriel menu's **Travel** tab (TMTravel, `TamrielMerge Travel.esp`):

1. **Worlds**: every converted world in the user's conversion, each labelled with its time's era and year ("3E 427")
   and whether it's this time, another time, or one not reached yet in the story.
2. A world's **regions** (Daggerfall's regions, Skyrim's holds, Cyrodiil's counties, Vvardenfell's regions), then a
   region's **towns** with roughly how long the road is.
3. A town's **trip**, as Daggerfall's travel window: the days it takes and the price, and its options: travel
   cautiously or recklessly, stay at inns or camp out, by ship when it helps, on horseback (when a horse is near), and
   the **pace** (1, the natural pace, to 10, the fastest the land can be loaded at). **Begin the journey**.

The journey is made in real time, in the world the player is in, without loading screens:

- The player is walked there by the game's own pathing (navmesh), on the horse when riding one, in third person.
- The clock runs so that Daggerfall's time for the trip has passed on arrival (TimeScale set each half second from the
  time and the way left); whatever didn't pass on the way passes on arrival.
- The pace multiplies the traveller's speed, up to what uGridsToLoad allows: `CellsPerSecond` (1.5) cells a second at
  5 grids, proportionally less with more grids loaded, so raising uGridsToLoad keeps the streaming steady.
- In the Iliac Bay, Daggerfall's enemies nearby (docs/daggerfall-senses.md: one that sees the player, or one within
  classic Daggerfall's spawn area around them) stop the journey too, checked every 2 seconds, and a journey can't begin
  or continue while they're near ("You cannot travel with enemies nearby."). Skyrim's map travel there is switched off
  while the map is open near them.
- A fight (the player in combat) stops the journey where it is, as does a movement key; **Continue the journey** in
  the Travel tab picks it up from there (its price already paid; the time left is the part of the trip still ahead).
- Across the sea (a town on another landmass, or whenever by ship is chosen and the way crosses water): the player
  walks to the nearest harbour on their landmass, boards a ship (Skyrim's ShipLarge01 on an activator) that sails the
  route to the harbour nearest the town (moved waypoint to waypoint, the player on its deck), and walks on.
- Arriving cautiously, the traveller is rested (health, stamina, magicka) and arrives by day (Daggerfall Unity: after
  7:10 when it would be earlier, or after 17:00).
- A town in **another time** already visited is reached through time (TMStoryOrder.TravelThroughTimeTo, as the Travel
  Through Time power goes: the coin and calendar of that time); one not reached yet in the story can't be.
- **Skyrim's own map travel** within a world takes Daggerfall's time too (TMTravelPlayer.OnPlayerFastTravelEnd: the
  difference is added).

## Daggerfall's numbers (Daggerfall Unity's TravelTimeCalculator, DaggerfallTravelPopUp)

Every world is priced as Daggerfall prices its map: one cell is one Daggerfall map pixel (the Iliac Bay is built at
that scale). Stepping pixel by pixel along the straight line (Bresenham), each land pixel costs
`(102 x transport / 256) x (512 - terrain) / 256` minutes (transport 256 on foot, 128 on a horse; terrain 240 desert,
220 mountain, 200 rainforest and swamp, 230 subtropical, 250 the woods), each sea pixel 255 (51 by ship); camping out
takes 300/256 as long; recklessly, half. The price: inns 5 gold a day of the land part and one night at least; a ship's
passage 25 per 24 sea pixels and one. Only the Iliac Bay has Daggerfall's climates; the other worlds are priced as woods.

## The data (TravelPluginBuilder, `travel-build`, run by convert after the start plugin)

- **The Iliac Bay** (from Daggerfall.esm's DFOverlandQuest: its regions, towns and their map markers): Daggerfall's
  map in 4-pixel squares (TMTravelClimate: the terrain for the price, the landmasses so a town across the sea is known
  to need a ship), its harbours (Daggerfall's port towns, plus the town nearest the sea on every landmass with towns but
  no port), sea hubs (harbours within 20 squares share one) and the routes between hubs (shortest over the sea, cut to
  the squares where they must turn). 2026-10-12: 3,444 towns in 43 regions, 13 landmasses with towns, 22 harbours,
  10 hubs.
- **Skyrim**: its cities, towns and settlements (the hold capitals have marker types of their own), each in the hold
  of the nearest capital; a walled city's marker is inside its own worldspace, so the journey ends at its gate (the
  load door into it). 26 towns, 9 holds.
- **Cyrodiil**: the converted Oblivion.esm has no map markers, so Oblivion's own (read from the Oblivion install) are
  placed in the converted Tamriel worldspace at the same spots; a walled city is one destination at its converted gate;
  by county (the nearest city). 49 towns, 8 counties.
- **Vvardenfell**: the converted Morrowind.esm's town map markers, by the region of their cells. 95 towns, 15 regions.
- **Stros M'Kai**: its converter makes no town map markers; not in travel yet (a small island in four worldspaces).

## The physics box (TamrielMergeSeamless)

Daggerfall's overland is one worldspace of 1000 x 500 cells (so travel is seamless). Skyrim's physics ends 64 cells
from a worldspace's centre (its Havok broad phase box: 262,144 units, an hkVector4 in the executable's read-only data);
the SKSE plugin TamrielMergeSeamless (tools/skse/TamrielMergeSeamless; that change and nothing else) finds it by value
at load and makes it `[World] iPhysicsCells` cells (512; TamrielMergeSeamless.ini beside it). It was part of the
graphics plugin until that was removed from the tool (user, 2026-10-15).

## Needs checking in game (not run here)

- The physics box at the map's far east and west (actors, doors, arrows, items, 400-500 cells out).
- The walk: the game's pathing for an AI-driven player over long distances, at high speed multipliers, mounted.
- The ship: an activator moved with TranslateTo and the player translated with it on its deck (ShipWaterZ -250, ShipDeckZ 330: ShipLarge01's planking 560-590 above its origin;
  ShipSpeed 600 a second at the natural pace).
- TimeScale up to 20,000 during long journeys.
