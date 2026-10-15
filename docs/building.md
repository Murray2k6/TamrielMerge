# Where a convert builds

`convert` writes hundreds of thousands of small files (Daggerfall's land and LOD, its towns' and dungeons' meshes and
textures). On a hard disk the head seeks between them and the processors sit idle waiting (2026-10-13: town batches
went from 6 to 13 minutes at 0% processor, the disk's queue at 44 and writing 0.6 MB/s; user: "why is the cpu not being
used"). So the convert looks at the user's drives itself (user: "with a users dir drives must be looked at
automatically"; Util/Drives.cs):

- Each fixed drive is asked whether it seeks (Windows' own test, the one its defragmenter uses), so no drive letter is
  assumed.
- When `--out` is on a hard disk, the build is made on a solid-state drive with room: the user's own temp folder
  (`%TEMP%\TamrielMergeBuild`) when that drive qualifies, else `TamrielMergeBuild` at the root of the roomiest one.
- Every file is still written in full; after the creature models the whole build is moved into `--out` one file at a
  time, so the hard disk writes each in one go.
- Room is left for Windows (user: "leave enough room for windows"): 32 GB stays free on Windows' own drive, 10 GB on any
  other. A drive is only chosen when the estimated build (`ConvertSize`: about 40 GB for Daggerfall, 6 Morrowind, 8
  Oblivion, 1 Redguard) fits beyond that; if the build grows past the estimate, what is staged is moved into `--out`
  between steps before the reserve is reached.
- Intro videos and recorded voices go straight into `--out`, where an earlier run's are kept. The steps after the move
  (creatures, start, travel, progression, the user's model choices, install, voice generation) use `--out` too.
- A stopped run's staging folder is cleared at the next run's start. A move that fails (a file in `--out` in use) leaves
  the rest in the staging folder and names it in the summary.
- `--no-staging` builds in place. `drives` prints each drive as the convert sees it and where a convert would build.

The land's LOD (Skyrim/TerrainLod.cs) uses the same test when it is built on its own.

## Using the cores

User, 2026-10-13: "use the cores". Sampling a running town batch (`dotnet-stack`) showed most of its first minutes went
to importing the 604 sprite models, one at a time, in every one of the 84 batches. `SpriteModelImporter.ImportAll` now
makes the models' files on all but two cores and adds their records afterwards in the models' order. On one thread that
took 176 s; on many it takes 74 s, with byte-identical files and the same records and form IDs
(`sprite-import-test --threads N --list FILE`). Mesh writing still takes turns (nifly isn't safe across threads). Already
on many cores: the land, terrain LOD, distant objects, outside navmeshes, and the dungeons' navmeshes, which are made
while the next dungeons are laid out. Still on one thread: laying out each town's houses and interiors, and joining the
parts. The batches themselves run one at a time for memory.

## Resuming and plugin size

- `convert --resume` carries on a stopped run: it keeps Daggerfall.esm and its land, each finished town or dungeon
  part, and a joined Daggerfall Towns/Dungeons.esm made after this Daggerfall.esm; it keeps the staging folder and
  carries on there (`Drives.ExistingStaging`) whatever room the estimate wants. On 2026-10-13 a resume found too
  little free room for a whole new build, built in place on the hard disk, and rebuilt the towns it should have kept.
- Joining parts (PluginMerger) writes navmesh and land records compressed, as Skyrim's own plugins do (zlib, record
  flag 0x40000, the data's size first). The joined Daggerfall Towns.esm had reached 2,065 MB, 1,176 MB of it navmeshes,
  past the 2 GB Mutagen can read (the overland doors step failed on it); compressed it is 1,408 MB, and the dungeons
  536 MB -> 342 MB. Read back with Mutagen, the records, navmeshes and land are identical (`plugin-compare`).
  `plugin-compress --plugin FILE` does the same to an existing plugin.

## Towards a one-hour conversion

User, 2026-10-14: "should be a 1 hour conversion" (the run of 10-13/14 took about 19 hours). What changed:

- Town and dungeon parts run several at once (`--parts-at-once N` to choose), each in a folder of its own beside a copy
  of Daggerfall.esm, its files moved into the build folder when it ends. A town part lays its towns out on one core (up
  to half the cores run parts); a dungeon part makes up to 8 navmeshes at once (a quarter of the cores). Budget 3 GB a
  part (a town part goes between 1.7 and 3.3 GB, the peaks brief), plus 2 GB for the shared heights and 4 GB for
  everything else. The joined plugins are the same however many ran
  together: each part's form IDs are its own.
- What every part made the same is made once and shared (`_parts\_shared`): the overland's heights (2 GB; each part
  spent 22 s making its own copy, and held it in memory), which the parts read in place from one file
  (`HeightField.Shared`, `TAMRIELMERGE_HEIGHTS`), and the town block and building meshes, which the first part to make
  one leaves for the rest (`TAMRIELMERGE_TOWN_MESHES`). The parts get a 256 MB first GC generation (`DOTNET_GCgen0size`).
  Town part 9 on its own, 2026-10-14: 4.8 min and 5.7 GB before, 3.6 min and 3.4 GB after (setup 28 s to 8 s). The old
  3 GB budget had run six 5.7 GB parts in 32 GB, into the page file: parts took 6.5-8.4 min in the 19-hour run.
- Town parts place Daggerfall.esm's own sprite models and grass instead of importing all 604 models and writing their
  files again in every part (a part's setup went to 29 s; a part now takes about 3.5 minutes).
- Redguard, Morrowind and Oblivion are built side by side, each in a process of its own.
- Distant LOD shares one texture sheet per game and builds its worlds in parallel (it had made a sheet of its own for
  each of Daggerfall's 3,444 town worlds, one world at a time), and Daggerfall's trees made from its sprites go in as
  pictures like Skyrim's trees (they went in whole, 8,000 triangles each): Daggerfall's objects and trees went from over
  two hours and an estimated 50 GB to 6.4 minutes and 3.6 GB. `TAMRIELMERGE_LOD_STATS=1` lists the heaviest models.
- The overland's navmesh is made from the land's heights instead of by Recast (`NavmeshBuilder.GenerateTerrainGrid`):
  2048-unit blocks split while more than 96 units off the ground or crossing into deep water or a slope too steep to
  walk, neighbours one split apart at most, edge midpoints where the next block is split. Recast had voxelised the
  400,000 cells for over 50 minutes on every core and left 3 triangles a cell, hundreds of units off the ground on the
  hills; now 3.5 minutes, 14 million triangles (38 a cell), a mean of 18 units off the ground, no cracks
  (`df-navmesh-test`). The whole Daggerfall build (Daggerfall.esm 2.7 min, land 4.9, navmesh 3.5) takes 11 minutes.
  Daggerfall Land.esm is then 2.01 GB: Mutagen can't read a plugin over 2 GB, so a finer navmesh needs the land split.
- Garbage collection: the mesh code kept every polygon's texture coordinates in a ConditionalWeakTable, collision and
  navmesh polygons too, and every collection walked its millions of handles: a town part was paused 73% of its time.
  The coordinates now go in a table each thread empties as it writes its meshes, and the parts get a 256 MB first
  generation (they make 500 MB of short-lived objects a second). Town part 9 alone, with the mesh cache: 3.3 minutes,
  then 2.2, then 1.8 (peak 4.1 GB).
- The Morrowind and Oblivion scripts compile in batches of 400, up to four at once (Morrowind's 4,930 took 17 minutes
  one batch at a time); a Morrowind script the same as the last build's isn't compiled again.
- Not counted in the hour: remastering the intro videos and rendering the menu on the processor (hours; they reuse what
  an earlier run made), and voice generation.
