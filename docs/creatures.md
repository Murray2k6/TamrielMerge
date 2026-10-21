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

## Every creature as itself (2026-10-21)

User: "the cliffracers need to be identical to the originals in morrowind", "ice wraith and cliffracer arent the same",
"same goes for daggerfalls imp", "all creatures". Nothing of another creature is borrowed any more:

- **Its own behaviour** (`CreatureGraph`, a config without `template`): written from its own clips only. It answers what
  Skyrim's engine sends every creature (the events and variables all 53 vanilla creature graphs share: `hkx-graph
  --contract`), laid out as vanilla's are (`hkx-graph` on the wisp's and the draugr's): idle and walk at rest, drawing and
  putting away, combat idle and run with IsAttackReady, each attack on its attackStart_ event with IsAttacking and
  attackStop, hit on staggerStart and recoilStart, its death held on DeathAnimation, Ragdoll or RagdollInstant, look-at on
  its head. Armed creatures pick their held weapon's style by iRightHandType (0 hand to hand .. 7 bow, as the draugr's
  selectors), a bow's shot as the engine's draw / hold / release (bowAttackStart, bowDrawn, attackRelease, arrowRelease).
  Its animation data (clips' events, motion at its own speeds with Y forward, attack sets) is its own.
- **Its own records** (`CreatureRecords.AddOwn`): a race made new (its game's health, magicka and stamina, its average blow
  as unarmed damage, its reach; flesh that bleeds; flies, swims or stays put as its game has it; both hands and a shield for
  an armed one), body parts on its own bones, a movement type at its game's speeds, and its own voice type, class, combat
  style and sound descriptors; the game plugin's actors overridden with them, their own numbers as offsets, no stand-in's
  sounds, faction or death item, a stronger or weaker blow than the race's through an ability.
- **Morrowind, every model** (`CreatureSpec.MorrowindAuto`, `creature-build --all-morrowind`): 89 models, each from the
  game's own NIF and .kf (a biped's also from meshes\xbase_anim.kf, its own groups first, as Morrowind loads them):
  - its .kf's sound keys play its own sounds (its SNDG through its original's ID, else the game's defaults; "Sound: X"
    the sound X; no footfalls in the air for a flier), converted to 16-bit WAVs under sound\fx\tamrielmerge\creatures;
  - "Attack1: Hit" (and a weapon's "Chop Hit", a bow's "Shoot Release") times its blow;
  - looping clips are the groups' loop parts; the movement root's (Bip01 / Root Bone) X and Y held as Morrowind holds
    them (left in, the cliff racer flew 1,888 units ahead in 20 frames);
  - its own skin binds (Morrowind's creatures are bound in other poses than the skeleton's rest: carried, the ash
    ghoul's worst vertex went from 7.3 units to 0.011) and its scale keys (the Heart of Akulakhan's beat: 71 -> 0.001);
  - its "Bounding Box" node as the actor's box (from the ground up to the box's top);
  - WEAPON and SHIELD on its "Weapon Bone" and "Shield Bone" (both games' weapons point along +Y from the grip:
    `weapon-frames`), sheath nodes hidden (Morrowind never shows a weapon put away);
  - weapon styles from its creatures' inventories (leveled lists opened): idle, walk, run, chop / slash / thrust (each
    "Start" to its follow-through), drawing and putting away.
- **Checks**: `mw-creature-check --creature <id>` skins every frame of every clip as Morrowind does (its own node tree, keys
  and NiSkinData, with arithmetic of its own) against the written files skinned as Skyrim does, vertex by vertex, naming
  the worst vertex and its bones; `creature-bones [--all]` checks skeleton.hkx against skeleton.nif, the body's binds
  (against the source's own for a Morrowind model) and weights, every animation's tracks, the graph's look-at bone, the
  head and torso, and each bone against the Morrowind node it came from.

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
