# Oblivion's scripts in Skyrim

Every Oblivion script of the converted plugins (vanilla and OBSE) is translated into Papyrus by `ObPapyrus`
(`src/TamrielMerge.Cli/Oblivion`), compiled with the Creation Kit's compiler against Skyrim's and SKSE's sources and
the converter's own helper scripts (`src/TamrielMerge.Cli/Papyrus/TMOb*.psc`), and attached to the converted records
that carried it in Oblivion. `TamrielMerge.Cli ob-script-translate --out DIR --compile` does the translation and the
compile alone (coverage.txt: statements translated per plugin; carriers.txt: what carries each script;
compile.txt: the compiler's output).

## What Oblivion's engine did for its scripts

Much of what Oblivion's scripts ask, its engine kept track of. Skyrim's engine keeps other things, so the converted
scripts ask **TMObState**, a quest made by the converter (always running), and the scripts it comes with:

| Oblivion | In Skyrim |
|---|---|
| `MenuMode` blocks (every frame in a menu) | `OB_MenuMode()`: run every tenth of a second while one of the menus it's for is open (SKSE `RegisterForMenu` / `OnMenuOpen`, `Utility.WaitMenuMode`), and right after a message box the script shows (its button is read there, as Oblivion's MenuMode did); `MenuMode 1001…1049` → the Skyrim menu of that kind |
| `OnAlarm crime [criminal]` | `OnTMObAlarm`: TMObState tells every listening script of a crime (SKSE mod event); those that see it (the victim, whoever detects the criminal or sees the victim) run the block |
| `OnSell` | `OnContainerChanged` from the player while the barter menu is open |
| `PlayGroup` / `LoopGroup` / `IsAnimPlaying` | the model's controller sequence of that name (`PlayGamebryoAnimation`; the converted models keep Oblivion's sequences, see "Moving parts" below), and how long it plays (2 s, 1 s fast, 3 s idles) |
| `AddScriptPackage` / `RemoveScriptPackage` / `GetCurrentAIPackage` / `GetCurrentAIProcedure` / `SetPackDuration` / `Wait` | TMObState's package aliases (one per use of each script package): the actor is put in the alias running the package; Oblivion's packages are converted from vanilla packages of the same kind (Travel, Sandbox, Follow, Escort, Sleep, Eat, Flee, Hold position) aimed where Oblivion's were |
| Crimes: `GetCrimeKnown`, `GetPCFactionMurder/Steal/Attack` (and `Set…`), `IsPCAMurderer`, `GetAmountSoldStolen` | TMObActor on every Oblivion person and creature reports assaults and murders by the player; TMObPlayer (the player's alias) theft, pickpocketing, trespass and selling stolen goods |
| `GetCrimeGold` / `ModCrimeGold` / `SetCrimeGold` | the player's: Skyrim's own bounty in the lands' crime factions (OBCrimeCyrodiil, OBCrimeShiveringIsles; their sum, as Oblivion has one bounty, kept in TMOB_PCCrimeGold too; setting it sets the land the player is in and clears the other); another actor's: kept by TMObState, and a bounty makes it attacked on sight |
| `GetPCMiscStat` / `ModPCMiscStat` | Skyrim's own stat where it keeps one (days passed, locks picked, books read…) plus what scripts and the tracked crimes add |
| `GetDestroyed`, `GetRestrained`, `GetTalkedToPC`, `IsTalking`, `SetNoRumors` | kept by TMObState (Skyrim can set but not read these) |
| `CloseCurrentOblivionGate` / `ForceCloseOblivionGate` | the gate the player went through (a gate's script, activated by the player) is destroyed and the player is back in front of it |
| `GetPlayerInSEWorld` / `SetPlayerInSEWorld`, `IsPlayerInJail` | globals TMOB_PCInSEWorld, TMOB_PCInJail; TMObState sets TMOB_PCInJail while the player is in one of Oblivion's jails (in the cell, within 512 units of its prison marker: where Skyrim puts an arrested player), checked every 5 s by TMObPlayer; dialogue and package conditions on `IsPlayerInJail` read it too |
| `ShowBirthSignMenu` / `GetIsPlayerBirthsign` | a menu of Oblivion's birthsigns (two pages past nine); the chosen sign's spells are the player's |
| `PlayMagicEffectVisuals` / `HasMagicEffect` (by effect code) | the effect's shader as a vanilla look-alike; the converted magic effects for that code |
| `IsPCSleeping`, `WakeUpPC`, `IsTimePassing` | Skyrim's sleep events (`RegisterForSleep`); the sleep menu closed as Escape would |
| `Lock level`, `GetLockLevel` | Skyrim's lock at the nearest tier, and the Oblivion lock (TMObLock) of a converted door or container |

Functions with a Skyrim or SKSE counterpart are mapped onto it (`IsPlayersLastRiddenHorse` → `Game.GetPlayersLastRiddenHorse`,
`SetActorFullName` → SKSE `SetName`, `SetItemValue` → SKSE `SetGoldValue`, `GetIsCreature` → a non-playable race,
`CreateFullActorCopy` → a copy of the actor's base with its inventory and worn items, …).

Reading the source as Oblivion's compiler did: a ref variable that only ever holds numbers is a number; a reference
stored in a number variable is its FormID, a number stored in a ref variable the form with that FormID; a name no
record has as its EditorID is the form the compiled script refers to in its place (SCRO), and a base object's
EditorID names its one reference (`BaseID.variable`); an `else`/`elseif` without its `if` is code Oblivion never runs;
`x >= 0 && < 10` compares `x` twice.

## Carriers

| Oblivion record | Carries its script as |
|---|---|
| quest, NPC, creature, leveled creature, container, door, light, activator, weapon, armour, clothing, book, key, misc item, ingredient, sigil stone | the converted record (Skyrim's kind carries scripts) |
| scripted magic effect (SEFF) of a spell, enchantment, scroll, potion or ingredient | a Skyrim magic effect (archetype Script) made for it, on the converted spell, enchantment, scroll, potion or ingredient |
| flora with a script (Nirnroot, …) | Skyrim flora (harvested for its ingredient) |
| furniture (beds, chairs, benches, thrones), with or without a script | Skyrim furniture with seat or bed markers made from Oblivion's (see "Furniture" below), carrying its script |
| potion or soul gem with an object script (a quest's wine, a collector's soul gem), alchemy apparatus | a misc item (Skyrim's potions and soul gems carry no scripts; apparatus are Skyrim clutter) |

Scripts no record uses in Oblivion either (253: test scripts, leftovers and old versions) are translated and compiled but carried by nothing.

## Statements with nothing to do in Skyrim

These translate to a comment, because what they changed doesn't exist in Skyrim's engine:

- `SetSceneIsComplex`, `PurgeCellBuffers`, `SetAllReachable`, `SetAllVisible`: rendering, memory and pathing hints for
  Oblivion's engine.
- `SetNoAvoidance`: Skyrim's actors steer around each other on their own; there's no switch.
- `ResetFallDamageTimer`: Skyrim measures a fall from where it starts; a teleport isn't one.
- `RefreshTopicList`: Skyrim reads the topics again each time the list shows.
- `SetShowQuestItems`: Skyrim always lists quest items.
- `AddAchievement`: console achievements.
- `ShowClassMenu`: Skyrim's player has no class (skills level by use alone).
- `ShowSpellMaking`: Skyrim has no spell making.
- `PickIdle`, `Reset3DState`, `EnableLinkedPathPoints` / `DisableLinkedPathPoints`, `TrapUpdate`: Skyrim's idle system,
  models and navmesh do these themselves.

## Approximations

- `IsAnimPlaying` times a sequence by the usual lengths (2 s, 1 s fast, 3 s idles), not the model's own.
- `IsInDangerousWater` is "swimming"; `GetKnockedState` "paralysed, unconscious or bleeding out"; `IsIdlePlaying`
  "an animation drives the actor".
- `GetCurrentAIProcedure` is Oblivion's number for what the actor is doing (combat, dialogue, sleep) or else its
  package's usual procedure.
- `SetRigidBodyMass` pins an object (0) or frees it; actors' mass can't be set in Skyrim.
- `EssentialDeathReload`: the message, then the player dies (Skyrim then loads the last save).
- Arrest is Skyrim's (its guards' crime dialogue: pay, jail, resist), not Oblivion's; each city's guards take the
  player to that city's castle prison (see "Crime and jail" below).

## Moving parts

Oblivion's animated objects (secret walls, gates, portcullises, levers, drawbridges, cell doors, the Oblivion gates'
forming) keep their animation (`ObAnimation`). Oblivion keeps a model's animations as Skyrim does: a controller
manager on the root with named controller sequences (Forward, Backward, Open, Close, Idle, ...), each binding a
transform interpolator and its keyframe data to a node by name through one multi-target transform controller. The
converted model keeps the node tree (shapes in their node's space) and the sequences block for block: interpolators
and keyframe data as they are, the controlled blocks named directly (Skyrim) instead of through a string palette
(Oblivion), text keys cut to start and end, the object palette pointing at the new nodes, manager and controller
flags as vanilla's (MineSecretDoor01). Only transform animation is kept. A model whose only sequence is an Idle loop
gets it as controllers on its nodes. Collision on a moving node is keyframed on the animated-static layer (layer 2,
keyframed motion, on the node itself, as vanilla); the still parts keep static collision.

- A door that doesn't teleport but has Open and Close sequences is a Skyrim door (Skyrim plays them), not the
  swinging activator other doors are; the navmesh goes through it.
- A static with moving parts is a movable static (Skyrim runs controllers on those).
- nifly's wrapper can't reach a sequence's controlled blocks, an object palette's entries or text keys;
  `NiflyRaw` reaches them through the pointers the wrapper does expose (their string references or block pointers
  point into each element). New sequences and palettes come from a template whose blank elements are written into
  the file's bytes.
- `ob-mesh --file a;b` lists what a converted model plays; `nif-anim --file x.nif` dumps a model's animation blocks.

## Effects nifly can't read

Twenty of the models the last build reported ("External component has thrown an exception": FXDustCloud01,
FXPlasmaCloud01, the four fireflies, ARWelkydClusterFX01, ARGreatWelkydStoneItem01, the channel-spike and evil-stone
traps, the dust falls and puffs) are Gamebryo 10.1/10.2 effects. `ob-nif10-bisect` found the cause (each model cut
after 1, 2, ... blocks and loaded by nifly in a child process; then a block that reads fine elsewhere spliced in right
after the suspect): nifly reads a 10.1/10.2 **NiPSysData** in Gamebryo 20.0's layout, loses its place and fails on
the next block (NiPSysAgeDeathModifier in all of them); and it can't read a 10.1.0.106 **NiTransformInterpolator**.
It ends the process (0xC0000409), so `Ob10Nif` recognises those files from their header and reads them itself (every
block type in them field by field, checked by landing on the footer; `ob-nif10` reads every such model in the game).

Each particle system becomes a Skyrim one laid out as vanilla effects (and MwVfx's) are, with Oblivion's modifiers
one for one (age and death; box, sphere or cylinder emitter at its emitter node, a mesh emitter as a box over its
mesh; spawn; grow and fade; colour over life; gravity; drag; rotation; position; bounds), its texture in an effect
shader with Oblivion's alpha. Emission loops at Oblivion's birth rate, or, where the effect's sequences drive it (the
dust clouds and plasma a script sets off with `PlayGroup Forward`, the traps' spray), those sequences become Skyrim
ones under a controller manager, laid out as vanilla's (Lumbermill01WaterWheel01: blend interpolators on the emitter
controller, links setting BirthRate and EmitterActive). The shapes are baked as other models' are; these models'
moving parts (the channel-spike trap's spikes, ScampSwitch01) stay still.

## Furniture

Beds, chairs, benches and thrones are Skyrim furniture (`ObFurniture`). Oblivion marks where an actor stands to get
in (at floor height, facing the furniture; references 1-4 beds, 11 left, 12 right, 13/14 front) and its animation
then moves the actor about 55 units in; Skyrim marks where the actor sits or lies (at the seat or mattress surface,
heading the way a seated actor faces, a bed from head to feet) and the sides it may be entered from. So each front
entry of a seat and each side entry of a bed gives a place 55 units in, at the surface found straight under it in
the model; side entries of seats and a bed's foot entry become entry sides; the two sides of a narrow bed are one
place. The record sits or sleeps as Oblivion's marker flags say (bit 30 sit, bit 31 sleep, as vanilla Skyrim's chairs
and beds also have them), enables the places whose Oblivion markers are enabled, and disables the sides only
disabled markers give (a left-hand bed's right side). `ob-seats --file a;b [--sleep]` lists a model's places.

## Crime and jail

Arrest goes through Skyrim's own crime system (`ObCrime`). Each land has a crime faction that tracks crime with
Oblivion's values (murder 1000, assault 40, trespass 5, pickpocket 25, jail break 50): OBCrimeCyrodiil, and
OBCrimeShiveringIsles for the Isles' people (editor IDs SE...). Every Oblivion person reports to their land's.
Oblivion's guards (their class has Oblivion's guard flag) join Skyrim's IsGuardFaction and GuardDialogueFaction, so
Skyrim's guard crime dialogue (DialogueCrimeGuards: pay the fine, go to jail, resist) arrests the player. Oblivion's
prison markers become Skyrim's; each land's jail is one of Oblivion's prisons (the Imperial City's, in the Bastion,
for Cyrodiil; Aichan prison for the Isles; else the land's first): its cell gets the faction's belongings and
stolen-goods chests (Skyrim puts the player at the prison marker of the belongings chest's cell), out of sight under
the floor, and the player is let out where the jail's load door leads (a prison marker and a follower wait marker
there; in the jail itself when that cell isn't built). TMObState keeps TMOB_PCCrimeGold as Skyrim's bounty and sets
TMOB_PCInJail while the player is in a jail.

Cities: Skyrim gives each crime faction one jail, so each of Oblivion's cities has its own, OBCrime&lt;City&gt; (Anvil,
Bravil, Bruma, Cheydinhal, Chorrol, Leyawiin, Skingrad), with Cyrodiil's values. A guard in a city's Oblivion guard
faction (AnvilGuards, BrumaGuardFaction, ...) reports to that city's faction, and its jail is the city's castle
dungeon. Chorrol has no prison marker, so its guards take the player to Cyrodiil's jail (the Bastion). Oblivion's
bounty is one for the whole land, so TMObState (`SyncCityBounty`, before each jail check) adds up what changed in
any of the land's factions since the last sync and sets that total in all of them. Paying or serving time in one city
clears it everywhere.