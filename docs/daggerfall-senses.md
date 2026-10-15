# Daggerfall's enemy senses

User request (2026-10-15): "daggerfall has a enemy detection system that needs in for the daggerfall part only".
Daggerfall's own enemies notice the player as Daggerfall Unity's `EnemySenses` and `EnemyMotor` (MIT) decide, and
"There are enemies nearby" works as its `GameManager.AreEnemiesNearby` does, for resting, waiting and travel. This
applies only in the Iliac Bay and only to Daggerfall's actors. Skyrim's detection settings (fDetection*, iDetection*)
are not changed, and Morrowind's, Oblivion's, Redguard's and Skyrim's actors keep Skyrim's own detection.

## Where it is

- **TMDfSenses** (Actor script) is on every hostile actor CreatureBuilder makes in Daggerfall.esm, 49 in all: 36
  `DFCreature...` monsters (the City Watch among them), and 13 `DFLvl...` actors that stand for Skyrim's leveled lists
  (the enemy classes, plus Werewolf, Vampire and Vampire Ancient). Those last ones carry
  it themselves, so their Use Script template flag is cleared. The `DFLvlEncounter...` actors keep Use Script, so whatever
  their encounter list picks brings its own copy. The lists hold the `DFLvl...` class actors, not Skyrim's lists. Passive
  copies, townsfolk, animals and vanilla records don't get it. Dungeon and town plugins use Daggerfall.esm's actors, so
  they need no copies. "Daggerfall Creatures.esp" deep-copies the actors and keeps the script.
- **TMDfSensesControl** runs on the quest `TMDfSensesQuest`. It is start-game enabled and sits at the fixed number
  0xDF0300 in Daggerfall.esm, so nothing after it is renumbered. A player alias (`TMDfSensesPlayer`) registers the
  menus again after each load. The quest's `IliacBay` property is DFLocIliacBay; with no property set (test plugins)
  the senses work everywhere.
- **TMTravel** has a `DfSenses` property, filled when Daggerfall.esm is in the folder.

`df-senses-check --plugin X [--master Y] [--list]` reports which actors carry the script and with what properties,
checks that every encounter-list pick has the senses, and counts how many placed actors carry it.

## The rules (Daggerfall Unity, with the classic numbers)

Distances are classic units x 2 (BlockMeshBuilder.Scale: Daggerfall is built at 2 Skyrim units per classic unit, which
is 80 per metre).

| Rule | Daggerfall Unity | Here |
|---|---|---|
| Sight | SightRadius 4096 classic units (102.4 m), FieldOfView 180, a raycast from eye to eye | 8192 units, heading within 90 degrees, `HasLOS` |
| Hearing | HearingRadius 25 m, only once it has detected the player and can't see them; static geometry blocks it, doors don't | 2000 units; `HasLOS`, or Skyrim's own detection (`IsDetectedBy`) when the player is behind it |
| Classic spawn area (gates the Stealth check) | within 1094 classic units; inside, by the marker's spawn distance type (its sound index, 0-6): spawn across {1024, 384, 640, 768, 768, 768, 768}, up {128, 128, 128, 384, 768, 128, 256}, down {0 (as up), 0, 0, 0, -128, -768, 0}; once spawned across {1024 x4, 768 x3}, up or down {384 x4, 768 x3} | the same x 2; the type rides on the placed enemy (`TMDfSpawnType`, types 1-6; none is type 0) |
| Language pacification | on first detecting the player (not quest foes): chance out of 200 = Etiquette or Streetwise / 10 + Personality / 5, or a language + Personality / 10; +10 weapons sheathed, else -25; success tallies the skill 3 times and the enemy stops being hostile until struck, failure tallies a language once | the same, Skyrim's Speech for Personality; Daggerfall's skills as globals (below); quest foes are not told apart |
| Rest spawns | resting in a dungeon on alert (an enemy saw the player within 8 hours): on 12 of each 144 minutes a 1 in 36 chance of one of the dungeon type's random encounters, 8-20 m away | each 12 minutes of a wait or sleep a 1 in 36 chance; the encounter actor of the dungeon seen last; the rest ends there (the hours after are given back within the day); placed at an actor of the cell 640-1600 units off |
| Stealth check | within 1024 classic units, once a game minute; the player stays unnoticed with `2 x ((distance x Stealth) >> 10)` percent | 2048 units, `Utility.GetCurrentGameTime()` minutes, Skyrim's Sneak as Stealth |
| Sneak mode | moving at less than half speed (crouching, or standing still): checks only on even minutes; faster, an enemy that has met the player always knows where they are | sneaking (not running or sprinting), or under 60 units a second |
| Stealth skill use | one use a minute in which any enemy made the check | `Game.AdvanceSkill("Sneak", 1.0)` |
| Illusion | invisibility always blocks it, except for monsters that see through it (Imp, Skeletal Warrior, Ghost, Mummy, Wraith, Frost/Fire Daedra, Daedroth, Vampire, Seducer, Vampire Ancient, Daedra Lord, Lich); chameleon and shade are seen through 8% / 4% of updates | the converter's Chameleon and Shadow are Skyrim's Invisibility, so all three block; `SeesInvisible` on those monsters |
| Struck | MakeEnemyHostileToAttacker: it knows the attacker at once | OnHit by the player or a teammate, or any fight with the player that it didn't start (a quest, a spell) |
| Giving up | GiveUpTimer 200 classic updates (12.5 s) without detecting; then it stops | StopCombat after 12.5 s without detecting, then it is unaware again |
| Enemies nearby | an enemy that sees the player, or one within the classic spawn area; resting, an unaware one only within 12 m | the same over the loaded enemies (960 units when resting) |

## What it does in Skyrim

- **Unaware**: Aggression and Assistance are 0 (actor values, set when the actor loads), so Skyrim's own detection, and
  allies calling for help, can't start a fight. Skyrim's AI packages (the template's sandbox or patrol) decide what it
  does meanwhile; in Daggerfall Unity an unaware enemy just stands.
- **Noticed**: its own Aggression and Assistance come back and it gets `StartCombat(player)`. If Skyrim loses the player
  while Daggerfall's rules still detect them (for example the Stealth check when the player walks within 25.6 m),
  combat is started again.
- **Lost**: Skyrim's combat AI chases and searches (it heads for where it last saw the player). Daggerfall's 12.5
  seconds end the search: StopCombat, and it is unaware again. If Skyrim restarts a fight within 3 seconds of that, it
  is stopped.
- **Polling**: only loaded actors poll, and only while hostile. They poll every 0.5 s within 2188 units or while in
  combat, every 1-1.5 s within sight range, and further out at the time a running player needs to come within sight
  (up to 10 s). Only the near ones raycast. Outside the Iliac Bay an actor gets its own values back and drops out.
- **What Skyrim still decides**: who to attack and how once combat has started; sneak attacks and the detection eye on
  the HUD (both Skyrim's detection); the search movement; and a fight that it starts for its own reasons (a spell or a
  quest), which the script takes as the enemy noticing the player.
- **Pacified**: Aggression and Assistance stay 0, any fight with the player Skyrim starts is stopped, and it no longer
  counts among the enemies nearby; struck by the player (or a teammate) it is hostile again and attacks.
- **Not done**: telling quest foes apart (Daggerfall Unity never pacifies them); classic's minute-by-minute alert
  decay inside a rest (here the alert is read when the rest begins).

## Language skills

Daggerfall's Etiquette, Streetwise, Centaurian, Daedric, Dragonish, Giantish, Harpy, Impish, Nymph, Orcish and Spriggan
skills are globals in Daggerfall.esm (`DFSkill<name>`, fixed numbers 0xDF0310-0xDF031A), starting at 5, 0-100.
TMDfSensesControl.Tally raises one as Daggerfall Unity does: a skill rises by 1 when its uses reach
`floor(skill x multiplier x 1.04^level x 2 / 5 + 1)` (FormulaHelper.CalculateSkillUsesForAdvancement; multiplier 1 for
Etiquette and Streetwise, 15 for the languages; Skyrim's level), with a notification. Uses: pacifying (3), a failed
language (1), pleading in court (Etiquette to debate, Streetwise to lie; docs/daggerfall-quests.md, TMDfLaw).

Which language an enemy answers to (Daggerfall Unity GetEnemyEntityLanguageSkill; `TMDfSenses.Language`, set by
DfSenses.LanguageOf): Streetwise for Nightblades, Burglars, Rogues, Acrobats, Thieves and Assassins, Etiquette for the
other classes and for vampires and liches; Orcish (orcs), Harpy, Giantish (giants, gargoyles), Dragonish
(dragonlings), Nymph (nymphs, lamias), Daedric (Frost and Fire Daedra, Daedroth, Seducer, Daedra Lord), Spriggan,
Centaurian, Impish (imps, dreugh); animals and the rest none. The classes stand for Skyrim's leveled lists, so a list
both kinds of class use has an actor per language (`DFLvl<list>` and `DFLvl<list>Streetwise`).

The rest spawns' enemies come from the dungeon types' encounter-list actors (`DFRestEncounters`, 0xDF0320).

`df-senses-check` also prints how many actors answer to each language, the rest encounter lists and how many placed
enemies have each spawn distance type (six dungeons: type 0 73, 1 81, 2 132, 3 31, 4 7, 5 13, 6 59).

## Enemies nearby

- **Sleep/Wait menu** in the Iliac Bay: with an enemy nearby (resting rules), the menu is closed and "There are enemies
  nearby." is shown (TEXT.RSC 354, DaggerfallUI's rest window). This needs SKSE (RegisterForMenu). Skyrim's own check
  still applies.
- **Skyrim's map** in the Iliac Bay: with an enemy nearby, fast travel is switched off while the map is open ("You
  cannot travel with enemies nearby."), and switched back on when it closes. It is only switched off if it was on.
- **The Travel tab** (TMTravel): a journey can't begin or continue with enemies nearby, and one under way stops when they
  come near ("There are enemies nearby."), checked every 2 seconds. Before, it stopped only when a fight began.
