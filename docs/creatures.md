# Creatures with their own skeleton

Daggerfall's creatures only ever existed as sprites; the user chose free downloaded models for them, each with **its own
skeleton and animations**, not borrowed Skyrim bodies (2026-10-10: "just make a new skeleton", "we use skyrim se not le").
Models, licences and credits: `tools\creatures\<creature>\`, `tools\creatures\CREDITS.md`.

## Pipeline

1. **Config** `tools\creatures\<id>\creature.json`: the source model, armature, meshes, height in Skyrim units, which way
   it faces in its file, its animations (`{"idle": "<action>", "walk": ..., "attack": ..., "death": ...}`) and, for
   models whose files don't link them, its textures.
2. **Export** `tools\creatures\export_creature.py` (Blender, scripts disabled): the deform bones (and their ancestors), the
   skinned meshes (up to four weights a vertex) and every animation baked frame by frame with IK and control rigs
   resolved, turned to face +Y, stood on z = 0 and scaled to the height -> `work\creature.json`.
3. **Write** `Skyrim/Creatures/CreatureRig.cs` (`creature-build --creature <id> [--out DATA]`), Skyrim SE files under
   `meshes\actors\tamrielmerge\<id>\`:
   - `character assets\skeleton.hkx`: hkaSkeleton, every bone under `NPC Root [Root]`;
   - `character assets\skeleton.nif`: the same bones as nodes;
   - `<id>.nif`: the body, BSTriShape skinned (BSDismemberSkinInstance, NiSkinData with weights and bone bounds,
     NiSkinPartition), textures to `textures\actors\tamrielmerge\<id>\`;
   - `animations\<name>.hkx`: hkaInterleavedUncompressedAnimation + binding, one annotation track per bone.
   Havok files are written with HKX2Library (kreny/ret2end, MIT, vendored in `src\ThirdParty\HKX2Library`; it reads
   vanilla SE files and writes them back at the same size, differing only in float rounding: `hkx-roundtrip`).
4. **Check offline** `creature-pose --creature <id> --anim <name> --frames a,b`: reads back the written NIF and .hkx and
   skins the mesh the way the engine does, drawing side/front/top views (giant scorpion verified: whole, facing +Y,
   walking).

## Behaviour, records, install

- `CreatureBehavior`: a vanilla creature's behaviour project as template (config `template`: mudcrab, troll,
  slaughterfish, icewraith, chaurushunter; single-file graphs), turned to the creature: its own project/character/
  behaviour files, every template animation written from one of its clips (attack/move/hit/idle by name), bone ties
  remapped, clip triggers and the template clips' annotations retimed, animation data (animationdata, boundanims,
  animationsetdata with the plain-CRC32 file hashes, both dirlist.txt) when the template has any (Dawnguard's chaurus
  hunter has none and runs on annotations).
- `CreatureRecords` -> one light plugin per game, "<Game> Creatures.esp" over that game's plugin only (Daggerfall
  Creatures.esp, Morrowind Creatures.esp; user, 2026-10-11: "are they set for when a user does not have a certain game":
  the earlier single TamrielMerge Creatures.esp needed Daggerfall.esm and Morrowind.esm both, so Skyrim wouldn't load
  without either). Per creature a race copied from the template's (skeleton, behaviour), a skin (armour + addon wearing
  the body), and overrides of the game plugin's actors (config `actors`); a config's `"game"` names its game
  (Daggerfall's when none). `creature-plugin --creatures a,b --plugins <game.esm;...> [--install]`: a creature whose game
  isn't among --plugins is left out. --install switches the old single plugin off (it isn't deleted). The "Creature
  models" part in the app's mod organizer switches them.
- Daggerfall, 2026-10-10: giant scorpion, imp, giant bat, daedroth, flesh atronach, lich (+ ancient lich), wereboar,
  dragonling, lamia, iron atronach built, posed offline and installed (12 actors).
- Morrowind and Redguard get better models found online too (user), not their original creatures; nothing from Tamriel
  Rebuilt/Skywind. `mw-creature` (MwCreatureSource: a Morrowind creature's own NIF + KF) stays only as a fallback.

## Deaths and better bodies

- Deaths (user, 2026-10-10: "fix the deaths"): every creature's template plays a death on "DeathAnimation" (scrib,
  frost atronach, hagraven, chaurus hunter, slaughterfish, ice wraith, wisp, storm atronach, riekling). The templates'
  death clips play whatever file came to hand (stagger, looping idle), so every clip named for dying and every clip under
  a death/ragdoll state plays the creature's own death once and holds it (`Animations\TMDeath.HKX`). A model without a
  death gets one (`CreatureRig.EnsureDeath`): it topples onto its side and settles onto the ground, measured by skinning
  the fallen pose. No ragdoll yet, so bodies stay where they fell.
- A better body on a good rig (`body` in creature.json, export_creature.py): the downloaded mesh is scaled to the mesh it
  replaces, the rig's arms posed towards its hands, weights copied from the old mesh by nearest surface (Blender's
  automatic weights fail on many downloads), and the body brought to the rig's rest pose by inverse skinning. The lich is
  D.Cipher's robed lich on the Dark Priest's skeleton and animations.
- The exporter reports how much each animation moves (" <-- STILL" when nothing does).

## Still to do

- Ragdolls (the templates die into one); body part data for the new skeletons.
- In-game test of the installed creatures (behaviour, hits, death).
- Remastered UVs and textures for every creature (user: "some daggerfall model textures need remastered for the enemies
  ... and uvs"); smoothing for the low-poly imp and lich.
- The other creatures' configs; animations a model lacks (the troll has one action).
- **Skeleton root data** (2026-10-11, user: "some creatures are invisible in dungeons", "all that we replaced"): the
  game drew none of the creatures with their own skeletons. Every vanilla skeleton.nif has, on its root, "BBX" (BSBound,
  the actor's bounding box), "BSX" (BSXFlags 198), "SkeletonID" and "BSBoneLOD"; ours had only the bones. CreatureRig
  now writes BBX from the bodies at rest (standing on the ground, as vanilla's: e.g. the imp 82 x 30 x 95) and BSX 192
  (vanilla's without Havok and Ragdoll: no ragdoll bodies). `creature-skeleton-fix [--dir]` adds them to creatures built
  before. `actor-models --cell <name> --plugins <a;b>` lists what draws each actor a cell can hold (race, skeleton,
  body for the race's armor race, files there or not).
- **Animation data from the single files** (2026-10-16, user: "fix the imp and scorpion not showing"): the imp, giant
  scorpion, giant bat and dragonling were built on Dawnguard's chaurus hunter and Dragonborn's ash hopper, whose
  animation data the game keeps only in `meshes\animationdatasinglefile.txt` and `animationsetdatasinglefile.txt` (no
  `animationdata\<project>.txt` of their own), so CreatureBehavior wrote none for them and the game drew none of them.
  It now takes a template's data, motion and set files from the single files when it has no files of its own
  (`creature-animdata-check`: 300 of the 323 projects that have both read the same; the rest differ in content, the
  single files being the newer).
- **In the conversion** (2026-10-11): `convert` has a "Creature models" step after the main menu. Every creature in
  tools\creatures whose game was converted is built into the output (`creature-build`; Morrowind's from the user's own
  game, remastered only with `--gpu`, else `--no-remaster`), then `creature-plugin` writes one light plugin per game
  over that game's plugin, and the install copies and enables them. `--skip "Creature models"` leaves it out.
  The tool ships what that needs (about 280 MB): each creature's config, its Blender export (work\creature.json) and
  the textures the export uses, copied into work\tex (at most 2048 px) with paths relative to the export
  (export_creature.py now writes them so; CreatureRig.Load resolves them), plus the scripts and CREDITS.md. Every
  model's licence allows this (CC0, CC-BY 3.0/4.0, CC-BY-SA 3.0 with the credits shipped). Not the downloads.
