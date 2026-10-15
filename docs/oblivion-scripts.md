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
| `PlayGroup` / `LoopGroup` / `IsAnimPlaying` | the model's controller sequence of that name (`PlayGamebryoAnimation`), and how long it plays (2 s, 1 s fast, 3 s idles) |
| `AddScriptPackage` / `RemoveScriptPackage` / `GetCurrentAIPackage` / `GetCurrentAIProcedure` / `SetPackDuration` / `Wait` | TMObState's package aliases (one per use of each script package): the actor is put in the alias running the package; Oblivion's packages are converted from vanilla packages of the same kind (Travel, Sandbox, Follow, Escort, Sleep, Eat, Flee, Hold position) aimed where Oblivion's were |
| Crimes: `GetCrimeKnown`, `GetPCFactionMurder/Steal/Attack` (and `Set…`), `IsPCAMurderer`, `GetAmountSoldStolen` | TMObActor on every Oblivion person and creature reports assaults and murders by the player; TMObPlayer (the player's alias) theft, pickpocketing, trespass and selling stolen goods |
| `GetCrimeGold` / `ModCrimeGold` / `SetCrimeGold` | the player's: global TMOB_PCCrimeGold (Oblivion has one bounty); another actor's: kept by TMObState, and a bounty makes it attacked on sight |
| `GetPCMiscStat` / `ModPCMiscStat` | Skyrim's own stat where it keeps one (days passed, locks picked, books read…) plus what scripts and the tracked crimes add |
| `GetDestroyed`, `GetRestrained`, `GetTalkedToPC`, `IsTalking`, `SetNoRumors` | kept by TMObState (Skyrim can set but not read these) |
| `CloseCurrentOblivionGate` / `ForceCloseOblivionGate` | the gate the player went through (a gate's script, activated by the player) is destroyed and the player is back in front of it |
| `GetPlayerInSEWorld` / `SetPlayerInSEWorld`, `IsPlayerInJail` | globals TMOB_PCInSEWorld, TMOB_PCInJail |
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
| furniture with a script (beds, chairs, thrones), static with a script | an activator |
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

- `PlayGroup` plays the model's own animation when the converted model has it; converted models are static today
  (`ObMeshConverter` keeps no controllers), so animated activators (secret walls, gates, levers) don't move yet, while
  their scripts' timing (`IsAnimPlaying`) works.
- Furniture with a script is an activator: its script runs when it's used, but it can't be sat or slept in.
- `IsInDangerousWater` is "swimming"; `GetKnockedState` "paralysed, unconscious or bleeding out"; `IsIdlePlaying`
  "an animation drives the actor".
- `GetCurrentAIProcedure` is Oblivion's number for what the actor is doing (combat, dialogue, sleep) or else its
  package's usual procedure.
- `SetRigidBodyMass` pins an object (0) or frees it; actors' mass can't be set in Skyrim.
- `EssentialDeathReload`: the message, then the player dies (Skyrim then loads the last save).
- Oblivion's arrest isn't converted, so `IsPlayerInJail` is only what TMOB_PCInJail says.
