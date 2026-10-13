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
