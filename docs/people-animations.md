# People moving as their own games' do

Morrowind's and Oblivion's NPCs are Skyrim actors on Skyrim's skeleton (their armour re-rigged onto it: worn-armour.md).
Their own games' character animations are retargeted onto that skeleton and replace Skyrim's character animations for
them alone, through Open Animation Replacer (user, 2026-10-21: "all bones and animations need fixed across games",
"npcs too", "fix the npcs"). Code: `Skyrim/Animations/NpcAnimations.cs` (and `.Oblivion.cs`), `Morrowind/MwActors.cs`,
`Oblivion/ObPeople.cs`.

## Sets

| Set | Skeleton | Animations | Keyword |
|---|---|---|---|
| MorrowindMen / Women | base_anim.nif / base_anim_female.nif | xbase_anim.kf (the women's file over it) | TMMwPersonMan / Woman |
| MorrowindBeastMen / Women | base_animKnA.nif | xbase_animKnA.kf over xbase_anim.kf | TMMwBeastMan / Woman |
| MorrowindArgonianMen / Women | base_animKnA.nif | xargonian_swimkna.kf (swimming) | TMMwArgonianMan / Woman |
| OblivionMen / Women | characters\_male\skeleton.nif | characters\_male\*.kf (one file a group) | TMObPersonMan / Woman |
| OblivionBeastMen / Women | characters\_male\skeletonbeast.nif | the same files | TMObBeastMan / Woman |

Morrowind's files are those its engine loads for a person (OpenMW's NpcAnimation::updateNpcBase); an Oblivion NPC's set
is the skeleton its record names. Men's sets go onto Skyrim's male skeleton, women's onto its female one. Each NPC carries
its set's keyword, and each placed one is scaled to its own game's height (the head's height on its skeleton times its
race's height, over Skyrim's).

## Retargeting

Each Biped bone's anatomical frame (along the bone to the next, square to the body's forward) is copied onto the Skyrim
bone it becomes, whatever the two rigs' axes or bind poses. A bone with no next bone carries its turn from the bone above.
Skyrim's centre of mass follows Morrowind's "Bip01" or Oblivion's "Bip01 NonAccum", scaled to Skyrim's height, with the
travel taken out (Skyrim moves the actor). Oblivion's tracks are read from both kinds of interpolator it uses: keyframe
data and compressed B-splines (23 of idle.kf's 73 bones). The B-spline is the cubic over clamped uniform knots, each value
bias + short / 32767 x multiplier, a rotation made unit after: every rotation curve's end points decode to within
0.00042 of unit length. An Oblivion file keys only the bones it moves, as Oblivion layers a move over the idle under it,
so a bone a file leaves alone comes from the group under it: a walking attack's legs from that walk, a weapon's moves'
from its idle, the weapon idle's from the plain idle.

## Which file is what

`Table` (Morrowind) and `ObTable` (Oblivion) give each Skyrim character animation its source: standing, walking and
running every way, turning, drawing and putting away, each attack (Skyrim's right swing, left swing and power attacks as
each game's own), blocking, recoiling and staggering, the bow (drawn, held, loosed), casting, shield and torch, swimming,
sneaking, jumping, and Skyrim's idle fidgets as each game's own idles. Oblivion has no knockout or death animations (its
actors fall as ragdolls, as Skyrim's do), so those stay Skyrim's; Morrowind's knockout is Skyrim's bleed-out.

## Events

Skyrim's engine and behaviours wait on animation events (the blow's HitFrame, weaponDraw, footfalls, attackStop and the
rest). A clip's events are in the character project's animation data under the clip's name, and the clip generator of
that name in the project's behaviours names the file it plays. (Its number in the data only ties it to its motion data:
before 2026-10-21 that number was taken for the file, and events went to the wrong files.) A replacement file serves
every clip that plays it, so:

- Where every clip an NPC plays fires the same events (first-person clips and the player's twins of "...NPC" clips
  aside; sounds only some play left out), the file carries them at its own game's moments: the blow at "Hit", the
  draw at the equip's "Attach", putting away at "Detach", an arrow nocked and loosed at the bow's "Attach" and
  "Release", a spell at its "Release", footfalls at the game's footfall keys, the rest at the same share of the clip.
  OAR takes them from the file alone.
- Where clips fire different events (13 to 16 files a set: mt_idle's PickNewIdle and the furniture exits' IdleFurnitureExit,
  power attacks' intro, jumping, swimming's get-up), each clip keeps its own (a second submod, `<set>_SkyrimEvents`),
  the file running on past the latest of them: a loop by whole turns of it (no jump at the seam), the turns after the
  first carrying the shared events at the game's own moments.

## Commands

- `npc-anims --game morrowind|oblivion [--set S] [--out DIR] [--keywords ...]`: writes the sets (the builds do it
  themselves, beside the plugin). `--tracks [--keys]`: the sources, their times and keys, and the bones each leaves to
  the group under it; the B-spline decoding's measure. `--map`: each Skyrim bone and the source bone driving it.
- `npc-anim-check --game ... [--data DIR] [--all] [--differing] [--clips files]`: every written file posed from the
  file against the source at the same moment (each driven bone's anatomical frame, the centre of mass), its frames, its
  events (all there, none past its end), in the folder its events call for.
- `npc-anim-preview --game ... --set S --file F --frames a,b --png P`: Skyrim's body skinned to the file beside the
  source skeleton, from the side and the front.

Measured 2026-10-21: Morrowind 2,126 files, worst bone 0.069°, centre 0.000; Oblivion 2,096 files, worst bone 0.079°,
centre 0.000; 0 issues.

## Limits

- Morrowind's race weight (its x/y scale) has no Skyrim equivalent.
- Deaths are Skyrim's ragdolls (Oblivion's are ragdolls too).
- Daggerfall's and Redguard's people have no source animations (sprites, and Redguard's own format).
