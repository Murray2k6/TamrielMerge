# Worn armour and clothing

User request (2026-10-15): "make all placeholders real code leaving no placeholder behind and working". Morrowind's and
Oblivion's armour and clothing used to look like a vanilla Skyrim template when worn (iron armour, farm clothes):
their own models are fitted to their own skeletons. They now wear their own models, re-rigged onto Skyrim's skeleton.
Bethesda's Skyrim remakes (Morrowind's chitin, bonemold, netch, Dwemer, glass, ebony, daedric, orcish, imperial,
steel, iron, leather and fur sets, MwItems.CanonArmor) keep Skyrim's remake by design.

## How a worn model is made

The engine is `Skyrim/Meshes/WornMesh.cs`, shared by both games (both are built on 3ds Max's Biped skeleton,
"Bip01 ..."; Skyrim's is "NPC ... [...]").

1. **Read in the source bind pose.**
   - Morrowind (`Morrowind/MwWornMesh.cs`): an ARMO/CLOT lists body parts (INDX part index, BNAM male and CNAM female
     BODY record). A skinned BODY model (meshes\a\, meshes\c\) brings its own copy of the Bip01 skeleton in its bind
     pose; only its shapes under the node named for the part ("Chest", "Left Hand" ...) are used, as OpenMW does. A
     rigid model rides the part's attachment node in base_anim.nif / base_anim_female.nif ("Right Foot" under "Bip01
     R Foot" ...); left-side nodes draw the right-hand model mirrored (OpenMW attach.cpp: scale -1, 1, 1).
   - Oblivion (`Oblivion/ObWorn.cs`): the biped models (male and female) read with nifly; skinned shapes in skin space,
     each bone at its bind from the skin data, carried to the skeleton's space; helmets and shields are rigid on the
     bone their "Prn" extra data names (on meshes\characters\_male\skeleton.nif). Oblivion's own bare-skin pieces
     (textures\characters\...) are left out: Skyrim's body is worn under the armour instead.
2. **Re-rig (`WornMesh.Retarget`).** Each source bone maps to the Skyrim bone of the same name; Morrowind's
   three-finger hands are shared over Skyrim's five fingers (finger 1 = index + middle, 2 = ring + little), Oblivion's
   five map one to one, twist bones go to Skyrim's first twist bone. Each bone moves its geometry rigidly from its own
   bind to the Skyrim bone's: turned by the difference of the two bones' anatomical frames (x along the bone to its
   next joint, roll by the body's forward +Y, or up for bones that run forward) - Morrowind's Bip01 frames and Skyrim's
   differ by a quarter turn, so the bones' own frames can't be used - and stretched along the bone to the Skyrim
   bone's length. Vertices blend their bones' moves by weight, so limbs stay joined at the joints and take Skyrim's
   proportions. `worn-bones [--model a\x.nif] [--female]` prints each bone's mapping.
3. **Fit (`WornMesh.FitOutside`).** Against Skyrim's naked body, hands or feet (character assets, both weights _0 and
   _1, male and female): every vertex within 10 units of the skin and less than the layer's margin outside it is pushed
   out along the skin's normal; every skin point poking through a cloth face between its vertices pushes that face's
   corners out; the pushes are smoothed over the cloth's own edges (four passes, never below a vertex's own push), up
   to four rounds. Margins by layer: underwear-like clothes 0.4-0.5, belts 0.9, armour 1.2, pauldrons 1.6, robes 1.8.
4. **Write (`WornMesh.Write`).** A vanilla-style armour NIF: NiNode root, the bones used as nodes at their Skyrim bind,
   each shape a skinned BSTriShape (BSDismemberSkinInstance with the slot as its partition, NiSkinData with weights and
   bounds, NiSkinPartition), the source's textures (Morrowind's through MwTextures with their relief normal maps,
   matte; Oblivion's with their _n/_g maps, specular from the normal map's alpha as its statics), alpha and glow.
   Files: `meshes\tamrielmerge\mw\worn\<id>\{m,f}_{0,1}.nif` and `1st_{m,f}_{0,1}.nif` (first person: the triangles on
   the arms and hands), `meshes\tamrielmerge\ob\worn\<model><hash>\...` for Oblivion (one per distinct model set).
5. **Records.** The item keeps its vanilla template's copy (keywords, sounds, armour type); its armor addons are copies
   of the template's own (so their races - human, Argonian, Khajiit variants - and footstep sounds stay), with the new
   models (weight slider on) and the slots below.

## Slots

Skyrim's naked body is one piece (slot 32) where Morrowind's is many, so most parts layer over it in slots of their own
(46-60) and can be worn together as in Morrowind:

| Morrowind | Skyrim slot | Notes |
|---|---|---|
| Helmet | 31 hair (+30 head when it covers Morrowind's head part) | not fitted |
| Cuirass | 32 body | carries Skyrim's naked body under it, so arms and legs still show |
| Left / right pauldron | 57 / 58 | |
| Greaves | 53 | |
| Boots, shoes | 37 feet | fitted to the feet |
| Right gauntlet, right glove | 33 hands | carries both bare hands (Skyrim hides both with the slot) |
| Left gauntlet, left glove, left bracer | 60 | |
| Right bracer | 34 forearms | |
| Shield | 39 shield | rides the left hand |
| Shirt / robe / belt / skirt / pants | 46 / 47 / 48 / 49 / 52 | |
| Rings, amulets, belts without body parts | their slot | draw nothing worn, as in Morrowind |

Oblivion: upper body = 32 body (with Skyrim's body under it), lower body = 53, both (robes) = 32 + 53, hands 33, feet
37, head 30 + 31, hair 31, shield 39; rings and amulets draw nothing worn, as in Oblivion.

Known limits: Morrowind hides a cuirass or greaves under a robe or skirt; Skyrim can't hide one worn item under
another, so a robe worn over armour may show the armour through it. Helmets aren't fitted to the head (heads differ by
NPC). Not yet seen in the game.

## When it is built

`mw-build` and `ob-build` convert every armour and clothing item's worn models before the cells, in parallel
(`PrepareWorn`: geometry on all cores, reading, texture conversion and NIF writing one at a time; Oblivion converts
each distinct model set once). 2026-10-15: Morrowind 547 items in ~35 s, Oblivion 332 model sets in ~65 s, none
failed; ~216 MB and ~415 MB of meshes. An item whose model can't be converted keeps its template's look and is listed
as a problem ("worn ...: ... (template look kept)"); the build prints how many.

## Checking

- `mw-worn --match REGEX [--take N] --out DIR` / `ob-worn --match REGEX --out DIR`: those items' worn models and
  records, with problems.
- `worn-check --file a.nif;b.nif [--female] [--part body|hands|feet] [--weight 0|1] [--png out.png]`: the written NIFs
  read back on Skyrim's skeleton: per shape its bounds, bones and how much sinks into the naked body; `--png` draws all
  of them together from behind and the side (flat-shaded; skin tan).
