# Daggerfall's locations on the overland

User, 2026-10-25: "theres no villages in the overland of daggerfall or towns, theres no daggerfall dungeon exterior
buildings"; "towns go on the overland like in daggerfall"; "npcs and enemies need to work fully too on the overland
and towns". Chosen: every location's exterior on the overland at full size, spread apart where they would overlap.

## Why they were separate

At one map pixel a cell (1000 x 500 cells) a location's RMB blocks are 2 x 2 cells each (4096 Daggerfall units x
BlockMeshBuilder.Scale 2.0 = 8192 units): Daggerfall city is 15 x 15 cells round a map pixel of 1. Measured
(`locations --overlaps`): at full size at least 7,498 of 13,410 locations overlap another, all 410 cities and 1,155 of
1,200 hamlets. So the towns were worldspaces of their own, entered by an invisible door at the map pixel.

## The design

The overland is two cells a map pixel (2000 x 1000 cells, two tiles; user's choice, "Bigger overland"): footprints
are then 11.4% of the 1,597,228 land cells, and 95% of locations stay within 6 cells of their pixel.

1. **Layout** (`Terrain/LocationLayout`): every location with exterior blocks gets an overland position, its blocks'
   footprint (cell-aligned: a block's corner on a cell corner, so a cell holds a quarter of a block and the street
   navmesh and ground repeat), and a ground level. Biggest first (cities, then by block count, then location index);
   each goes at its map pixel when clear, else to the nearest clear spot on land round it (rings outward), with a cell
   of open ground between footprints. No footprint comes within the tiles' overlap (8 cells) and a cell more of a tile
   seam: a tile shows its neighbour's land there but not what stands on it, so a town there would be bare ground from
   the other tile and appear whole on crossing (measured: 0 footprints within 8 cells of the seam at x = 0).
   Deterministic: computed from WOODS.WLD and the height field before any levelling, the same in every process;
   saved as Daggerfall.layout.json beside Daggerfall.esm for the parts.
2. **Land** (2026-11-03, user: "the towns were on hills"; `locations --layout --embank` measures it): sites are kept
   apart by a cell of open ground for every 722 units between their levels (10 degrees; 1 to 8 cells), each site's
   easing reaches as far out as the land needs to come within 10 degrees of its level (2 to 16 cells), and every vertex
   eases towards all the sites reaching it at once (weighted by nearness), not one after another. Steps over 30 degrees
   within a town's easing: 721 of 3,444 towns before, 46 after (the land as made has 34); the steepest 86 degrees
   before, 54 after. Then: the height field levelled to each footprint's ground level, easing back to the land round it (as
   Daggerfall Unity's TerrainHelper blends a location's ground); inside a footprint 24 units under it (its edge on
   it), so the blocks' ground meshes, standing at the level, don't fight the land drawn at the same height. Painted
   with a town ground texture without grass. The foliage keeps out of every footprint.
3. **Exteriors** (TownBuilder): every location's blocks (towns, villages, farms, homes, taverns, temples, graveyards,
   cults, covens, dungeon exteriors) built straight into the overland's cells at the layout's place: the block
   statics, block ground, street props, people and animals, street doors into the shared interiors. Daggerfall.esm
   builds Daggerfall city in its overland's own cells; the town parts (Daggerfall Towns.esm, 114 parts of 400 blocks
   for 15,251 locations) override the land plugins' cells there (so the land plugins are their masters). No town
   worldspaces, no entrance or edge doors; the location's cells carry its location record. Street people (wanderers)
   only where Daggerfall Unity puts a PopulationManager: towns, farms, wealthy homes, temples and taverns.
4. **Dungeons**: their entrance door set into the exterior block's dungeon entrance surface (DfTownLayout.
   DungeonEntrances), as a street door is (DungeonCellBuilder.SurfaceEntrance). 28 cemeteries (blocks GRVEAS10 and
   GRVEAS36: walls, plinths and gravestones only) have none, and Daggerfall Unity leaves their dungeons closed; they get
   Daggerfall's own graveyard crypt (GRVEAS00's model 41619, as that block places it) at the clear spot nearest their
   middle, its entrance their way in (DfTownLayout.WithCrypt; measured: 28 of 28). Outside the south side of the
   footprint only if a location had neither.
5. **Navmesh**: one overland navmesh made last (the conversion's "overland-navmesh" step, after the town and dungeon
   plugins), over the land and the collision of everything Daggerfall.esm, Daggerfall Towns.esm and Daggerfall
   Dungeons.esm stand on it, read from the models (Meshes/CollisionReader) rather than written out by the parts. A
   cell with something in it, or something near enough over its edge to reach into its mesh, goes through Recast in
   the open-country setting (as vanilla meshes its wilds; Daggerfall's streets are 256 units wide and more); bare land
   is meshed straight from its heights. Measured 2026-10-26: the fine setting on those cells, and on the bare land
   beside them, gave the first tile 180 million triangles in 670,511 navmeshes (5.7 GB written, 2.2x smaller
   compressed: past the 2 GB Mutagen reads); the same pieces are 7-12x smaller now (28, 66, 97 MB -> 4.3, 6.7, 8.0).
   2026-10-27: the whole first tile still came to 171 million triangles and a 2,669 MB plugin, and not from the towns:
   half the pieces were whole hills at about 760 triangles a cell, the bare-land grid split to single 128-unit quads
   because a block was walkable only when every quad in it was, and steep quads lie scattered through Daggerfall's
   hills. A block is now judged by its own slope at its own size, the grid's tolerance is 128 units (was 96), and a
   balance split that set one quarter of a block (the other three left unwritten) sets all four. Measured on the four
   worst pieces (4 x 4,096 cells, the land alone): 10.36 million triangles and 149 MB before, 3.69 million and 54 MB
   after (tolerance 96: 4.98 million; a 256-unit smallest block: 2.89 million, not taken). A tile's plugin at or past
   1,900 MB (Mutagen's limit) now stops the run at that tile instead of at the doors step hours later.
   Plugins a tile ("Daggerfall Navmesh.esm", "Daggerfall Navmesh 2.esm", and "Daggerfall Navmesh 1-2.esm" ... when a
   tile's navmesh is split by size: user 2026-10-28, "make a plugin"), loaded after the land plugins and before the
   towns (their cells are the land's; the towns' overrides of those cells come after); no navmesh links across tiles.
   A tile's pieces go into one plugin until its raw parts pass 2,400 MB (about 1,100 MB merged and compressed; the
   limit Mutagen reads is 1,900 MB), then into the next, which has the earlier ones as masters so its navmeshes link
   into theirs; a navmesh of an earlier plugin that links into a later one is written whole into the later plugin as
   an override (its own plugin's copy loses those links; the cells' children merge across plugins and a later NAVM
   replaces the earlier). Measured 2026-10-28 on a 2 x 2 block of pieces with a 20 MB budget: the fourth piece went to
   "Daggerfall Navmesh 1-2.esm" with 128 overrides carrying 651 links across, 221,141 links in all, the same as in one
   plugin; `navmesh-split-check --dir DIR` reads the plugins back and checks every link's target is there and in its
   plugin's masters. The conversion's last step (docs/building.md, the pack step) then joins these plugins with the
   others that fit, a later part into its earlier one with its overrides applied and its links to the new FormIDs, so the
   names above are those the build makes and `plugins.json` lists what stayed. Made in 64-cell pieces, each written out once the pieces east and north of it are made (its links to every
   neighbour in) and the freed memory given back: about a row of pieces held, 8-10 GB, where holding a tile to its end
   passed 18 GB a third of the way through. The overland doors are joined to it after (Daggerfall Overland Doors.esp,
   each touched cell copied from its latest record so a town's cell keeps its location).
6. **Map markers** where Daggerfall Unity's fast travel sets the traveller down (StreamingWorld.PositionPlayerToLocation):
   outside a side of the location a tenth of a block past its edge, a city on its start marker (199.10) nearest that
   point; the south side, a Skyrim marker standing in one place (all 410 cities have start markers, 1,640 in all).
7. **Travel** across the two tiles as one map: each travel world holds its worldspaces' origins and the tile core
   (TMTravelWorld.GX, Crossing); a walk to another tile heads for where the way leaves this one, the overland moves
   the traveller on (TMDfOverland), and the walk goes on; a ship is brought under the player after the move.
8. **The flyby** finds a location by its record (`~+DFLocDaggerfall17@location`); **the distant view** reads the
   town plugins' copies of the tiles like any other.

## Navmesh speed (measured 2026-10-31)

A full conversion's first tile took 4 h 40 min of working time on an 8-core Ryzen 3700X with the build on NVMe. Timed by stage on real pieces, the cost was not the triangles, the writing or the compaction (about 1 s a piece each):

- **Every Recast cell failed its open setting and began again.** The detail sample distance was 24 units, or 0.75 of the open setting's 32-unit voxels, under Recast's 0.9. DotRecast's `RcMeshDetails.TriangulateHull` then threw on all 13 tries of every cell, the exception was swallowed as a try's failure, and the cell was remade in the fine setting. `ExteriorConfig` now keeps it at 1.5 voxels or more; `NavmeshBuilder.TriesThrownSummary` puts any tries that throw in the build's log.
- **Collision models were read on one core**, 358 of 470 s of three town pieces on first read. `Standing.Fetch` reads a piece's new models on all cores (the file finder taken in turn) and `Solids` turns the references into place on all cores, joined in the same order.
- **The open setting's winning retries were tried last.** `OpenTries` tries 0, 3 and 6 first (they won 7,668 of 7,673 cells).
- **Door cells stay fine.** The open voxels left 7 of 54 doors with no navmesh within 128 units, so cells within 256 units of a door (`Standing.DoorCells`) are made in the fine setting.

| Three heavy town pieces (12,288 cells) | Before | After |
|---|---|---|
| Time | 870 s | about 135 s, first-read included |
| Triangles | 1,745,476 | 1,055,683 (60.5%) |
| Plugin | 31 MB | 19 MB |
| Doors reached of 54 | 54 | 54 |

`navmesh-compare` measures two builds against each other: 1.45% of the ground samples lost, 0.49% of them on the lowest floor there (the rest roofs and upper floors), 42 of 12,288 cells losing over 5% of their street-level ground, none over 10% (worst 8.1%), and 0.34% new ground. Not yet measured on a whole tile, or in game.

## Overlapping regions and dungeon navmesh speed (measured 2026-11-02)

User, 2026-11-02: "daggerfall navmeshing takes way to long on the dungeons"; "the overlapping navmeshing needs fixed". The last run spent 56 minutes on Daggerfall's 4,228 dungeons with 4.6 of 16 cores busy on average. Measured causes and fixes:

- **Regions over themselves.** Recast's watershed (and monotone) partitioning can put two floors stacked in the same columns into one region; DotRecast's `BuildRegions` says "If overlapping regions were found during merging, split those regions" and then only warns. The region's outline then runs over both floors and crosses itself: in The Hold of Copperham region 475 held floors at 2,052 and 3,076, a navmesh triangle ran from one to the other (corners 3,080/3,080/2,056), and 10,153 spans had no polygon. `NavmeshBuilder.BuildSplit` runs Recast's steps as `RcBuilder.Build` does, with `SplitOverlapping` between the regions and the outlines: each such region is regrown in pieces that never take a second span in a column (small pieces join a neighbouring piece where their columns don't clash). Every build uses it, the exteriors' too. Copperham: 469 s and 13 tries before, 8 s, whole on its first try.
- **One retry was pathological.** Layer partitioning with no outline simplification (try 6, and 11 at coarse voxels) took Copperham 375.7 s and 63.1 s in DotRecast's `BuildPolyMesh`; every other try 0.4 to 5.6 s. In the 2026-11-01 run it rescued no dungeon (98 retries, all won by tries 1, 2 and 5); Oblivion's exteriors used it for 619 meshes.
- **Retries ran one after another** while the batch waited. An interior's tries after a failed first one are now built side by side in waves (1, 2, 3, 4; then 5 to 8; then 9 to 12), taken in the same order as before, so the mesh is the same.
- **One slow dungeon held its batch.** Navmeshes were finished strictly in build order, so the layout stopped behind the oldest; only those still being made count against the window now, the finished ones taken in order as they come.
- **Laying out**, profiled (dotnet-trace): 38 of a part's 79 seconds were the block models every part made again for the blocks it used (now made once in Daggerfall.esm's build, `DungeonCellBuilder.PrebuildBlocks`, and taken from it by the dungeon and town parts); the light fixtures' questions scanned the whole dungeon's polygons (now binned, `LightFixturePlacer.PolyGrid`); props and clutter looked up their Skyrim record by reading Skyrim.esm from the start (now `PropReplacer.Record`, a link cache); each block's solid polygons were made again for every dungeon it is in (now once a block).

| Dungeon batch 15 (106 dungeons, scratch build) | Before | After |
|---|---|---|
| Batch | 11.0 min | 2.6 min (the block models still made in the part: the test's Daggerfall.esm is the old one) |
| Laying out and navmeshes | 543 s | 67 s |
| Writing | 23 s | 3 s |
| Retries / never whole | 1 / 1 | 0 / 0 |
| Floor far from the navmesh | 131 spans | 0 |

Not yet measured on a whole run, or in game. What is left of a part is Recast itself, about 3 s a dungeon of single, whole builds.

The exteriors' shut-in rays (`WoundInput`) cast level rays through bins cut into 128-unit slabs of height, as a level ray stays at one height: binned by x and y alone each bin held every face of its column, and Oblivion's 280,580,322 rays tested 39,361,983,354 triangles. `TM_RAY_CHECK=1` casts each level ray both ways and puts the comparison in the build's log.
