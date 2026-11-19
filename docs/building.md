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
- Any drive (user, 2026-10-28: "the tool needs to convert on any drive letter that a user has"): the app's Options
  hold the output folder (remembered between runs), the build folder (where a conversion is staged) and the temp
  folder, each with Browse; they are TamrielMerge.settings.json's "output", "build" and "temp" in the working folder
  (the repository, or the program's folder), which the converter reads at start (Util/Paths; a paths.json beside the
  program is read only where the settings name no folder). Empty build and temp mean the output folder itself and a "TamrielMerge
  Temp" folder beside it: everything goes where the output is pointed (user, 2026-10-28: "it only made the folder and
  went back to d" - until then a hard-disk output was staged on a solid-state drive the tool found by itself, in the
  user's temp folder or at a drive's root, and a configured build folder held every build whatever --out said). A
  hard-disk output is slower; the log says so and names the Options to choose a build folder on a solid-state drive.
- The sprite-model maker (Daggerfall's sprites as models) works in the temp folder, `TamrielMerge Temp\model3d`
  (exported sprites, the job list, finished models, the outline check), not under tools (user, 2026-10-29: "the
  model3d tool keeps its files after conversion is complete"). Models shipped with the tool (the release's
  `tools\model3d\work\models`) seed it and stay where they are. The Daggerfall build imports the models into the
  output and writes `Meshes\TamrielMerge\DF\Models\models.json` there (each model's sprite, kind, mesh and bounds);
  at the end of the conversion the work folder is removed, and a later conversion into the same output takes the
  statics from that manifest without making anything again (`Sprite models: ... from the earlier conversion`).
- Setup's status lists what it can't download (user, 2026-10-29: "before the tool didnt install them all"): the
  creatures' source models (each from its author's page, `tools\creatures\CREDITS.md`; the release carries them) and
  the shipped sprite models. Everything else in tools comes from `setup` (ffmpeg, RAD Video Tools, Real-ESRGAN, the
  Python environments, TripoSR, the depth and normals models, FFDec, Java, Blender, FluidSynth, WildMIDI, the
  soundfont, the crash logger) or is made by runs (`lipcache`, `lipgen` slots); `tools\lhm` is a leftover nothing uses.
- Nexus Mods login (user, 2026-11-02: "add a login to nexus button on the tool so it can log you in if you are missing
  something too"; Setup/Nexus.cs, Gui/NexusLoginDialog.cs). At the top of the app's Tools box, "Log in to Nexus" opens
  Nexus's API keys page (https://next.nexusmods.com/settings/api-keys) and a box to paste the personal API key. Nexus's
  one-click login (its SSO) needs an application slug registered and approved by Nexus staff, which TamrielMerge
  doesn't have, so the key is pasted by hand. It is checked with `GET https://api.nexusmods.com/v1/users/validate.json`
  (headers `apikey`, `Application-Name: TamrielMerge`, `Application-Version`) and kept in TamrielMerge.settings.json as
  "nexus", sealed with DPAPI for the Windows user (with extra entropy), never in plain text, never logged. "nexusUser"
  and "nexusPremium" hold the name and account type for the line "Logged in to Nexus as NAME (premium/free)". Log out
  deletes all three. The key is only sent to api.nexusmods.com: the download links it gets are signed CDN links fetched
  without it. Nexus's rate limits are respected: each answer's `x-rl-hourly-remaining` / `x-rl-daily-remaining` are
  kept, and once both are used up (or on a 429) the request stops with a message naming when to try again. A wrong or
  revoked key (401), a refusal (403), a missing mod or file (404) and network failures come out as plain messages.
  TamrielMerge does not register itself for nxm:// links: Mod Organizer 2 owns them.
- Requirements published only on Nexus carry a Nexus source on their row (`Dependencies.Status.Source`: game domain,
  mod id, and the rule picking the files, a regular expression on the name and/or Nexus's category). The first is
  "Skyrim voice models (xVASynth)" (Setup/XvaSynthModels.cs): Skyrim SE mod 44184, category MAIN, installed into
  `tools\xvasynth\models\skyrim` (`Dependencies.XvaModelDir`, where the voice generator's `tools\voice\xva.py` reads
  them). The row counts the voice types the converter speaks that have a model by xva.py's own rule
  (`sk_<voice type>.pt` or `<voice type>.pt`, with its `.json`): the voices of the converted dialogue
  (`tools\voice\*-jobs.json`, each line's SpeakAs or VoiceType without its effect, recordings left out), the voices
  cloned for it (`tools\voice\refs`) and the one voice per race and sex (`VanillaActors.RaceVoices`). On the 2026-11-02
  tools folder that is 24 voice types, none with a model. Missing, the row has "Get from Nexus" ("Log in to Nexus first"
  when logged out, which opens the login). The key is validated again first (Premium may have started or lapsed).
  Premium: the mod's files are listed through the API, those the rule picks and still wanted (named for a missing voice
  type, the longer name winning so "Female Even Toned" isn't taken for MaleEvenToned; else the packs not yet imported,
  remembered in `nexus-files.json` beside the models) are downloaded into `tools\downloads\nexus`, unpacked (a .zip by
  .NET, a .7z or .rar by the tool's 7-Zip), every `.json` naming the game skyrim with its `.pt` moved in with the files
  named for it (`.hg.pt`, preview `.wav`), the archive deleted, and it stops as soon as every voice type has a model.
  Free account: Nexus forbids API downloads for it, so the files page
  (https://www.nexusmods.com/skyrimspecialedition/mods/44184?tab=files) opens in the browser, the log names the files
  to download, and the Downloads folder (Windows' known folder, `SHGetKnownFolderPath`) is watched for 30 minutes:
  each archive of the mod (Nexus names them `<file>-44184-<version>-<time>`), those already there included, is imported once whole (its size steady,
  no `.part`/`.crdownload` beside it), the user's own file left where it is. "Stop getting from Nexus" ends either.
  `setup` lists the missing voice models among what it can't install.
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
- Skyrim keeps at most 1,048,576 reference handles: every persistent reference takes one at load, and in a plugin
  without the master flag every reference in its worldspaces does (a user's Daggerfall Towns.esm of 3,462,154
  persistent references crashed the data load, 2026-10-27; a plain-ESP Creatures.esp of 537,318 references filled
  the table). The conversion counts them for its plugins at the end (`handle-count --dir DIR` does it alone) and
  fails past 700,000, leaving room for the game's own (58,125 in Skyrim and its DLCs) and other mods; the overland
  rework took Towns.esm to 125,722 and the whole set to 320,313. Every plugin small enough is given the light (ESL)
  flag (at most 4,096 new records with FormIDs up to 0xFFF, Skyrim 1.6.1130 and later; `light-flag --dir DIR` for an
  existing output): Start, Travel, Progression, Menu, Overland Doors and the Creatures plugins; the world plugins hold
  millions of records and stay full plugins. The light flag takes no load-order slot; it changes nothing about the
  handles, which the master flag governs, so every converted plugin, the small .esp files too, carries the master flag
  as well (set by its builder and again by the same pass; user 2026-10-28: "the other esps we have need to be esm's").
- As few full plugins as the 2 GB Mutagen reads allows (user 2026-10-28: Skyrim's 254 slots): the land planned at 1.85 GB
  a plugin (four for 1.39 million cells, 5,755 MB; six at 1.6 GB), the foliage at 15 million objects a plugin (two for
  26.6 million; the FormIDs below the shared band bound it), a navmesh tile in one plugin up to 3,900 MB of raw parts
  (about 1,770 MB merged; the first tile was 1,482 MB). With Daggerfall.esm, the towns and the dungeons Daggerfall is
  10 to 11 full plugins (was 15 to 17), and the seven small ones are light. The pack step (below) takes it lower; going
  past 2 GB a file (the engine's own ceiling is 4 GB a top-level group, and whether it loads a file between 2 and 4 GB
  is untested: `plugin-decompress` makes a test file) would take it lower still.

## The pack step

User, 2026-10-29: "we can load so many esm files with skyrim and we need to find a workaround". The conversion's last
step before the handle count and the flags (`pack-plugins --dir DIR [--budget MB] [--order A;B] [--plan]`, Skyrim/PluginPack,
Skyrim/PluginMerger.Join; `--skip Pack` leaves it out) joins Daggerfall's world plugins into as few as stay under
1,900 MB, the game seeing the same records. It comes last so every step before it reads the plugins by their usual names;
what they made (Doors, Creatures, Travel...) is re-pointed with the rest.

- It measures first: each plugin's size, records by type, masters and dependents, and every record two or more plugins
  hold, from the record headers alone (15 million records in 3 to 28 s). All of it goes in `pack-report.txt`.
- Two groups join when the size stays under the budget; their new records fit 16,775,168 FormIDs; at most one is frozen;
  a place in the load order exists with every plugin after its masters (a third plugin between them is a cycle); and at
  that place every contested record has the same winner. Of all pairs that may, the largest result under the budget
  joins, and again until none may.
- Frozen plugins keep their name and FormIDs and give a join its name: Daggerfall.esm and Daggerfall Towns.esm (script
  properties hold their FormIDs and names as numbers and strings: FactionIds, TownIds) and any plugin with dialogue
  (voice files are named by plugin and INFO FormID). Only the world plugins join: other plugins stay themselves, rewritten
  when a master of theirs was folded.
- A copy of a cell or worldspace equal to the winning one contests nothing (each plugin with something in a cell carries
  one). Without that the worldspace and its persistent cell, two records of 15 million, stopped every join across the
  world; with it the 10-25 output's contested records went from 384,772 to 7,698, and its plan from 4 plugins to 3.
- A record's header FormID and the group labels named after records are changed in place; FormIDs inside a record are
  found by Mutagen a record at a time (a 1.9 GB plugin never sits in memory as objects). A later copy replaces an earlier
  one, cells' children join, and the navmesh map NAVI joins entry by entry. A folded plugin's FormIDs stay where free, else
  take the first free from 0x800; an interior cell with a new FormID moves to its new block. A plugin whose name, masters
  and FormIDs all stay is copied. Mutagen's writing isn't always its reading: 154 of Daggerfall.esm's armours and factions
  came back changed, so outside the types proved to come back the same only the bytes that differ between Mutagen's
  writing before and after the remap change. Joined cells keep land, navmeshes, references in that order: mixed, Mutagen
  stops reading a cell's navmeshes at its first reference.
- Results are written to `_pack` beside the plugins and read back against their sources before anything moves: each record
  written the same as its source's with the links remapped (not Mutagen's Equals: 101 of 137 armours are unequal to
  themselves by it), no FormID twice, record counts by type, as many dangling links as before, every navmesh edge link
  on a navmesh, every door link on a record, every contested winner the same. Any failure leaves the build as it was
  and the conversion carries on with the plugins unjoined.
- `plugins.json` beside the plugins lists the final names in load order with their masters and what joined them. The install
  reads it (`install`, and the convert's own list), the plugin lists are put in that order, and the app's parts need
  nothing, as they list the names that are there. An earlier build's copies of a folded plugin are removed from the
  output, and an install removes them from Data. `--resume` can't carry on from packed plugins and builds Daggerfall again.
  Creature lists go with their plugin, in its new FormIDs.
- Measured 2026-10-30. The 10-25 output (`--plan`, read only, 6 world plugins, 4,661 MB): into 3. Real pairs in a scratch
  folder: Daggerfall.esm + Daggerfall Dungeons.esm, 2,454,644 records, 60,764 taking new FormIDs: 400 MB, 12 s to write and
  16 s to check, 26,542 navmesh edge links and 4,235 door links all resolving, reference handles the same (112,832).
  Daggerfall.esm + a land part from the build folder: 71,500 records, 55 MB. `pack-test --dir DIR` makes small plugins with a
  navmesh linked each way, overrides, FormID collisions, a third plugin and the two refusals: 23 proofs pass. A whole
  current build hasn't been packed yet: at this rate about half an hour, more for the navmeshes' compression, and with
  about 15 GB in all 8 plugins is the floor at this budget.

## Towards a one-hour conversion

User, 2026-10-14: "should be a 1 hour conversion" (the run of 10-13/14 took about 19 hours). What changed:

- Town and dungeon parts run several at once (`--parts-at-once N` to choose), each in a folder of its own beside a copy
  of Daggerfall.esm, its files moved into the build folder when it ends. A town part lays its towns out on one core (up
  to half the cores run parts); a dungeon part makes up to 8 navmeshes at once (a quarter of the cores). Budget 3 GB a
  part (a town part goes between 1.7 and 3.3 GB, the peaks brief), plus 2 GB for the shared heights and 4 GB for
  everything else. Each part starts when that much memory is free, judged as it starts and half a minute after
  the one before (judged once at the beginning, a moment of low memory had held the rest to 3 at a time). The joined plugins are the same however many ran
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
  Daggerfall Land.esm was then 2.01 GB, just under the 2 GB Mutagen reads: the land is now split (below).
- Garbage collection: the mesh code kept every polygon's texture coordinates in a ConditionalWeakTable, collision and
  navmesh polygons too, and every collection walked its millions of handles: a town part was paused 73% of its time.
  The coordinates now go in a table each thread empties as it writes its meshes, and the parts get a 256 MB first
  generation (they make 500 MB of short-lived objects a second). Town part 9 alone, with the mesh cache: 3.3 minutes,
  then 2.2, then 1.8 (peak 4.1 GB).
- The Morrowind and Oblivion scripts compile in batches of 400, up to four at once (Morrowind's 4,930 took 17 minutes
  one batch at a time); a Morrowind script the same as the last build's isn't compiled again.
- Not counted in the hour: remastering the intro videos and rendering the menu on the processor (hours; they reuse what
  an earlier run made), and voice generation.

### 2026-10-15: the next costs

User, 2026-10-15: "fix everything and the gaps" (the estimate for a fresh run was about two hours). Measured while a
conversion's recorded voices ran on the same machine (15 LipGenerators at once), so the times are slower than on a
quiet one; before and after were run back to back under the same load.

- Town parts. A trace of town part 9 (`dotnet-trace collect --profile dotnet-common,dotnet-sampled-thread-time`,
  converted to speedscope) showed Recast at four fifths of the part's work and the .NET finalizer thread busy for 111
  of the part's 127 seconds. Three changes:
  - nifly's Vector3, Vector2, Vector4, Triangle and HavokFilter wrappers (one per vertex, normal, UV, triangle and
    collision plane of every mesh BlockMeshBuilder writes) are disposed as they are added to nifly's lists instead of
    left to the finalizer, which freed them one by one under a lock after each had outlived a collection. The meshes
    are byte-identical (the 2,776 .nif files of part 9 from an empty mesh cache).
  - Street cells with the same input share Recast's mesh (`BuildExteriorCells(reuse: true)`): of part 9's 3,780
    street cells only 1,194 had different input, Daggerfall's towns being built of the same blocks, two cells a side.
    The key is a SHA-256 of the cell's triangles in its own coordinates (1/16 unit); the mesh is moved to each cell
    and its islands pruned for that cell's own people and doors.
  - The town parts share their Recast meshes, streets and building interiors, in `_parts\_shared\navmeshes`
    (`TAMRIELMERGE_NAVMESHES`, beside the town meshes): a part reads one another part made. Not the dungeon parts:
    part 2 of 40 found none of part 1's. 84 parts leave some tens of thousands of small files there (two parts:
    2,289 files, 9 MB).

  Town parts alone, warm mesh cache (wall time, processor time, peak working set):

  | Part | Before | After |
  | --- | --- | --- |
  | 9 (first part, nothing shared yet) | 126 s, 338 s, 3.68 GB | 78 s, 148 s, 2.35 GB |
  | 11 (after 9 and 10) | 126 s, 340 s, 3.78 GB | 62 s, 104 s, 2.47 GB |
  | 30 (Sentinel, the biggest town) | 136 s, 366 s, 3.82 GB | 63 s, 137 s, 3.14 GB |

  The record counts are the same (`plugin-compare`, which now also counts navmesh triangles and vertices); the
  navmesh triangles stay within the run-to-run spread (part 30: 460,887 before, 460,870 after; part 9: 435,394-435,416
  in three runs before, 435,374-435,427 in four after). The budget stays 3 GB a part: the biggest town's part still
  peaks at 3.1 GB. Dungeon part 2: 133 s and 3.2-4.2 GB, as before.
- Starting the parts. BuildParts let half a minute go between any two starts, so a part's memory showed before the
  next was judged; with town parts of one to two minutes that held them to two a minute, 42 minutes at the least
  for 84 parts (20 for the dungeons' 40). Now 5 seconds go between starts, and every part started in the last 45
  seconds counts its 3 GB on top of the one starting (free memory at least 4 + 3 x (1 + those)).
- The lip sync cache. Voice files are named by their line's FormID, so when the FormIDs shift (any change to a
  plugin's records before a line) every line was lip-synced again: over an hour for Oblivion's 15,000 lines.
  `VoiceFiles.Pack` keeps each packed .fuz in `tools\lipcache\<key[..2]>\<key>.fuz`, the key the first 32 hex digits
  of SHA-256 over the .wav's bytes, a zero byte and the line's text, and takes it from there for the same recording
  and text; one key at a time, so lines asked for at once (several voice types) are packed once. The copies are hard
  links on one volume (every writer replaces a .fuz by renaming it, never in place), else copies. `voice-cache-test`:
  8 Oblivion recordings decode to the same bytes twice; packed in 6.5 s, then under new names in 0.09 s, all from
  the cache and byte-identical. The voice summaries say how many came from the cache.
- LipGenerator's working folder. It resamples the sound into `tmp16khz.wav` in its working folder, and every
  parallel run used LipGenerator's own folder, so runs read each other's sound: 16 lines at once came out 10-40% off
  from the same lines alone (two alike), and failures' retries held packing to about 270 lines a minute, though one
  run takes half a second for 11 seconds of speech. `VoiceFiles.MakeLip` gives each run a folder of its own (kept for
  reuse) and a time limit by the line's length (15 s + 2 s a second, at most 60). Voice files and cache entries
  written before the fix (`VoiceFiles.LipSyncFixed`, `VoiceFiles.Current`) are packed again.
- Redguard's scripts. Its 904 scripts compiled in one compiler run, on one core (261 s on a quiet machine). They
  now go in batches (at most 400; as many as can run at once when there are 100 or more each), up to four at once,
  each in a folder of its own named for the process: the compile 511 s -> 147 s under load, the whole `rg-build`
  688 s -> 373 s. The plugin is the same; the .pex files have the same sizes and string tables (the compiler orders
  a script's strings by what else it had loaded, so 839 of 907 list them in another order).
- Daggerfall's models and textures move to a hard-disk `--out` while Redguard, Morrowind and Oblivion build, instead
  of with the rest after the creature models: `meshes\TamrielMerge\{DF,Towns,Dungeons}`, `textures\TamrielMerge\DF`
  and the overland's distant terrain (`meshes|textures\terrain\DFIliacBay`), about 13.5 GB and 104,000 files of the
  19 GB and 186,000 in the staging folder of the day's run. Nothing after reads them from the build folder alone
  (the distant objects look in `--out` too); the menu's render waits for the move, as does any other move.
- Looked at, left: the creature models take about a minute (ten from their shipped exports, 2 to 20 s each) and
  write the same animation lists, so they stay one at a time; the recorded voices already use every core; the
  intro videos reuse an earlier run's.

### The land's plugins

Mutagen's binary overlay reads a plugin only up to 2 GB, and Daggerfall Land.esm had reached 2.01 GB with its
navmeshes (2026-10-15, from a byte count of its records: LAND 1,627 MB, NAVM 225, CELL 27, NAVI 19, all compressed;
399,800 land cells in one worldspace). The land build (`OverlandTiles.LandPlugins`) now counts its land cells first,
the same way `BuildCell` finds them, and splits the land into as many plugins as an estimate of 5 KB a cell (land
and navmesh, measured) needs to stay under 1.6 GB each: two for the full map. `--land-plugins N` (build,
`df-overland-test`) picks the number.

- `Daggerfall Land.esm`, `Daggerfall Land 2.esm`... (`OverlandTiles.LandPluginNames`): each a run of the land's
  pieces (128 x 128 cells), cut where the plugins' bytes come out even; each one after the first has the ones before
  it as masters (the land textures are in the first).
- The last also holds every overland navmesh, over the other plugins' cells (their cells as overrides, without
  land), so the links between neighbouring cells' navmeshes and the navmesh map are all in one plugin
  (`OverlandNavmesh.Build` reads the cells of all of them).
- What reads the land: `overland-doors` uses the last land plugin (`NavmeshPluginIn`); the convert adds all of them
  in order (and on `--resume` keeps all or none) and removes land plugins an earlier run split otherwise from
  `--out`; `Setup.Parts` lists them (so the app, the load order and DistantLod see them); ModelCatalog leaves them
  out; the menu's Daggerfall shot loads them; `df-navmesh` takes a split land; `plugin-check` reads masters missing
  from `--data` from Skyrim's Data. The build warns when a land plugin still comes to 1.9 GB.

Proved on a small map (a full Daggerfall build takes 11 minutes and up to 20 GB): `df-overland-test --pixels-per-cell
4 --tiles all --land-plugins 2 --navmesh` makes 10,662 + 14,715 land cells in two plugins (47 + 91 MB; one plugin:
138 MB), the same 22,945 navmeshes, 1,296,003 triangles and 285,346 links as one plugin, 389 of them between cells of
different plugins, none dangling, a map entry per navmesh, and joins 40 stand-in doors (20 in each plugin's cells)
with OverlandDoorLinks; `plugin-check` finds 0 bad links in either land plugin and the doors plugins.

## Reference handles and the user's mods (2026-11-01)

The limit of 1,048,576 belongs to the whole load order, not to the converted plugins alone. `handle-count --load-order` counts every plugin switched on (Mod Organizer 2's selected profile when it has more on than Plugins.txt, with its mods' folders), and a conversion with `--install` reports it, warning past 850,000. `handle-count --why PLUGIN` says what a plugin's persistent references are; `--unneeded A;B` how many nothing points at.

Measured on the install of 2026-11-01: 31 plugins, 321,188 handles (30.6%), 238,550 of them Daggerfall's: 84,104 street doors quests or "Where is" point at, 54,125 scripted dungeon objects, 41,652 quest markers, 15,251 map markers, 8,464 load doors. Nothing pointed at 57,812.

Cut: "Where is" holds door positions as globals instead of doors (`TownTalk`, `TMDfTalk`); a dungeon object nothing in its cell points at is temporary (`ActionObjectBuilder.Unpersist`); a quest marker no quest took is temporary (`QuestSites`). Measured on real parts: dungeons part 1, 1,614 persistent against 2,383 a part before; towns part 3, no change (its 1,659 street doors are the quests' sites, `QuestSites.PerType`).

Left: `BDoor`, `BSpawn` and `BItem` hold about 125,000 references for the quests. Cutting them means quests finding a building some other way.

## Smaller plugins (2026-11-02)

User: "is there a way to compress the esm's so the files are smaller but they hold everything still"; "as its converting". Every conversion does two things after the plugins are packed (`Skyrim/PluginCompression`, off with `--skip Compress`; the `install` command's `--no-compress`):

- **Records compressed in the file.** Skyrim stores some records zlib-compressed (flag 0x40000, the data's size first); measured over Skyrim.esm, Update.esm and the three DLC, only four types: NAVM, LAND, CELL and NPC_. Those four are compressed in every converted plugin where it makes them smaller, the rest left byte for byte, and each rewritten plugin is checked against the original record by record (inflated where compressed) before it takes its place. The pack step's merger compressed only navmeshes and land, and only in what it joined: Oblivion.esm 844 MB -> 596 MB (32,720 navmeshes), Morrowind.esm 265 -> 192 MB, a few seconds each; Mutagen reads them the same (Oblivion's 14,122 persistent references counted alike). Dialogue (INFO, 2.46 million records in Daggerfall Towns.esm, 63% compressed) isn't: no plugin of Skyrim's compresses that type, so the game's reading it so is unproven.
- **On the disk.** Windows' LZX file compression (as `compact /exe:lzx`; NTFS only), which every program reads through unchanged: the output's plugins once the build is in place, Data's after installing. Measured on copies: Daggerfall Towns.esm 8.0 to 1, Daggerfall Foliage.esm 4.9 to 1, plugins already compressed in the file 1.1 to 1.2. A file written again is stored plain again (the next install compresses it again). Its effect on Skyrim's loading time is not measured.

What doesn't get smaller, and why the land, navmesh and foliage stay in several plugins: one plugin numbers at most 16,775,168 new records (foliage is 26.6 million), a top-level group holds at most 4 GB (its size field is 32 bits; Daggerfall's land is about 5.6 GB in one worldspace), and Mutagen reads at most 2 GB a plugin (the navmesh is 2.6 GB). Compression doesn't help with these: they count the records as they are.

## Work kept between conversions (2026-11-04)

User: "conversion takes to long" (the 2026-11-03 run: 13 h 39 min, 71 min of it paused; 1 h 36 min remaking the
videos and 1 h 42 min the menu flyby, both the same as the last run's). Now:

- **Videos** (`videos`): each finished video is kept in the tool's `out\video\work\done` with how it was made (`.made`:
  remaster, GPU, ray-traced look, quest-video options) and copied into a new output instead of made again.
- **Menu flyby** (`menu`): the flyby rendered before (each shot's upscaled frames in `out\menuart\intro\rt1440`) is packed
  again rather than exported, rendered and upscaled anew; `convert --remake-menu` (or `menu --remake`) renders it again.
  Its scenes' stamps name the plugins' and the converter's dates, so every conversion had rendered it all again.
- **On-disk compression**: only files that shrink 15% or more (eight 1 MB samples deflated) get LZX; the land, navmesh
  and Daggerfall.esm plugins (about 8.3 GB, 1.0-1.1 to 1, their records compressed already) are left as they are.
