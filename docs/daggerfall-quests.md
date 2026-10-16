# Daggerfall quests

Daggerfall's 232 quests (the QBN/QRC pairs in ARENA2) are converted from the player's own files into Daggerfall.esm,
data-driven: one Papyrus interpreter runs every quest's own QBN program. Daggerfall Unity (MIT,
github.com/Interkarma/daggerfall-unity) is the reference for what each operation does; its text quests are only used, as
a dev aid, to borrow symbol names when comparing (`df-quest-dump --names`).

## The files

- QBN (UESP "Quest hacking guide", "Quest pseudo-codes"; checked on all 232): a 60-byte header (bytes 16-35 ten UInt16
  record counts, 36-57 eleven UInt16 section offsets), then sections 0 items (19 bytes a record), 3 people (20), 4 places
  (24), 6 clocks (33), 7 foes (14), 8 operations (87), 9 states (8), and in seven files 10 symbol names (27). Records name
  their text symbol by hash: `h = (h << 1) + c` over the name without its underscores.
- An operation: UInt16 opcode, flags, operand count, five 15-byte operands (Not byte, pointer = section << 8 | record or
  0x12345678 for a constant, section, value), a message id shown when it runs (0xFFFF none). The first operand is a state:
  for a trigger the state it sets, for an action the state it waits for (none: the start-up).
- Person byte 2 is local (0) / remote (1) / unspecified, byte 3 the face; the "kind" word is the building type of their
  trade (0-20, 21 the questor), -8 a named individual at home, -1 a faction, -2 a faction type, -3..-7 social groups.
- Place: scope 0 fixed (P1 the Daggerfall location id), 1 this town, 2 elsewhere in the region; P1 0 building, 1 dungeon;
  P2 the building or dungeon type (-1 any); P3 1 any house, 2 any shop. Item: reward 0 an item, 1 a random reward, 2/3
  gold (category/index = max/min, 0xFFFF computed); group/index are Daggerfall Unity's ItemGroups. Clock minutes; a
  clock's state is the state with its hash.
- QRC: a TEXT.RSC-style database (DfText); 1000 offer, 1001 refused, 1002 accepted, 1004 done, 1010+ log and messages.

## Opcodes

`DfQuestOps` names all 69 opcodes after Daggerfall Unity's commands, found by lining up every opcode with the lines of
the Daggerfall Unity task its state hashes to (then checked with `df-quest-dump --names`): triggers 0x01 toting and
clicked, 0x02 killed, 0x03 clicked item, 0x15 injured, 0x1C clicked npc, 0x47 clicked with gold, 0x4D level; always-on
0x05 dropped at, 0x1F daily from (minutes after midnight), 0x34/0x35 when and/or, 0x4E individual available, 0x4F repute
at least; the rest are actions (place/create/give/take/log/say/prompt/clock/reveal/repute ...). What the line-up settled
beyond UESP's guesses: 0x04 is "give pc" with the quest-complete message (success), 0x4C "give pc ... notify", 0x1A "get
item", 0x24 "take from pc", 0x23 "start task" (cycle), 0x3F/0x40 add/drop foe face, 0x45 cast spell on foe, 0x47
"clicked and at least N gold otherwise do", 0x51 "create npc" / "create npc at", 0x53 "transfer pc inside".

`df-quest-check`: all 232 quests have every operation supported (5655 operations). 0x11 (two uses, A0C10Y05 and
B0C00Y13, in the start-up: a place and two numbers), which UESP has no meaning for and Daggerfall Unity leaves out, marks
the place on the map as reveal does. `df-quest-check --places` lists where every fixed place resolves.

## Records (DfQuestBuilder, in Daggerfall.esm)

- One quest per Daggerfall quest (`DFQ<name>`, e.g. DFQM0B00Y15) with the shared script TMDfQuest: its program (9 Ints
  per operation: code, kind, Not bits, message, five operands), states linked to the 64 global variables
  (DFQuestGlobal<n>, DfQuestGlobals, shared with the dungeon actions), clocks, items (Skyrim's or renamed stand-ins,
  DfQuestItems; letters are a "Letter" book whose words show when read), people, places, foes (CreatureBuilder.Spawn).
- Every QRC message variant is a Message record owned by its quest; symbols (`_x_`, `__x_`, `___x_`, `____x_`, `=x_`,
  `==x_`) and macros (%pcf, %g pronouns of the last person named, %ra, %reg, %god, %qdt, %cn ...) become alias text
  replacement: each distinct name is a name-tag alias (T<k>) whose reference the script renames (SKSE SetDisplayName)
  before a message shows. Prompts and the offer get Yes/No buttons.
- The log: one objective per log message; "log N step S" shows it in step S, replacing the step's last.
- "Tell me about <name>." topics for people, places and items with an answer (anyInfo), opened by "add dialog"/"dialog
  link" (stages 100+), answered by any Daggerfall townsperson (asking about a place marks it on the map); "Have you heard
  any rumors?" for "rumor mill" (stages 200+).
- DFQuestReputation (TMDfReputation): FACTION.TXT (parents, allies, enemies, types) on the DFReact<id> globals that
  greetings and TMDfGuilds.Reputation read, so quest rewards drive guild promotion (10 x rank). Changes follow Daggerfall
  Unity's ChangeReputation: allies +half, enemies -half, knightly orders via the generic order, otherwise the whole tree
  from the root (the faction and roots fully, the rest half), gods also the generic temple; -100..100.
- DFQuestMachine (TMDfQuestMachine): the catalogue (letter, membership, minimum rank or level, one-time), the
  "Do you have any work for me?" topic, the people quests make, item lists per item group, Daggerfall's spells, cures
  and diseases, its sounds, the covens' summoning, the escorts, the holding cell for quest items and name tags.
- Quest people are made as the town people are (TownPeople, Daggerfall Unity StaticNPC): the person's faction's race
  (FACTION.TXT, TMDfReputation.Races), else the region's Breton or Redguard; a vanilla face of that race and sex
  (`DFQuestPerson000`..: 9 races x 2 sexes x 6 dresses x 2 faces); dressed for their trade (the building type: barkeep,
  smith, merchant, robes, noble) or else their faction's social group; a Daggerfall name of that race.
- Sounds: "play sound N" is DAGGER.SND's record with id N (Daggerfall Unity PlaySound: SoundReader.GetSoundIndex):
  `DfSound` writes each sound the quests use (19, 386) as a 16-bit 44.1 kHz WAV under `Sound\fx\TamrielMerge\Daggerfall`
  with a descriptor and marker (`DFQuestSound<id>`); "every M minutes K times" stops after K (0: for ever).
- Videos: "play video N" plays `TamrielMerge_DfAnim<N>.bik`, which the videos step makes from ANIM000N.VID
  (docs/videos.md).
- Building names: Daggerfall's own (`DfBuildingNames`, as Daggerfall Unity's FormulaHelper.GenerateBuildingName): the
  town's MAPS.BSA building records of named types handed out in order to its blocks' buildings, each name drawn from
  its record's seed (TavernsB then TavernsA, a shop type's StoresB then StoresA with %cn / %ef, "The Bank of <region>",
  the guild's or the temple order's name, the three castles from TEXT.RSC). TownBuilder puts it on the street door
  (TMDfStreetDoor.BuildingName), the quest sites list it (BName) and `_place_` in a message names the building by it
  ("The Dancing Griffin" rather than "the tavern"). Houses keep "a house" (Daggerfall names them only in quests).
- "add face" / "drop face": Daggerfall Unity shows the face of someone travelling with the player; here the person
  follows the player (DFQuestMachine's aliases Escort0-2, Skyrim's PlayerFollowerPackage) until dropped or the quest
  ends. "add foe face" makes the foes follow as before.
- `TownTalk` and the builder make the same DFFaction<id>, DFReact<id> and DFSocialGroup<group> records (by EditorID), so
  the town parts share Daggerfall.esm's.

## Witches' covens

The 14 covens (MAPS.BSA location type Coven 13: one exterior block each, WITCAA00-13.RMB, no buildings and no dungeon)
are built in "Daggerfall Towns.esm" as the towns are (DaggerfallTowns.All includes them; each its own small worldspace
entered from its spot on the overland). Their witches are the block's flats from TEXTURE.179 ("Witches Coven"), each in
its coven's faction (419 the Glenmoril Witches ... 432 the Witches of Alcaire, FACTION.TXT type 8). The quest sites
register each coven as a town (TMDfQuestMachine.FixedSite prefers the built coven to Daggerfall.esm's map-only entry), so
the quests that send the player to one ($CUREWER, R0C30Y25, R0C40Y23, R0C4XY23, R0C60Y24) mark and reach it.

Talking to a witch (Daggerfall Unity DaggerfallWitchesCovenPopupWindow): "Do you have any work for me?" offers the
covens' quests (Q, by level), and "Summon a Daedra Prince." runs TMDfQuestMachine.SummonDaedra
(DaggerfallQuestPopupWindow, MIT):

- who answers: the Glenmoril Witches always summon Hircine; any other coven a prince picked at random for the day (1-15,
  never Hircine); anyone else only the prince whose day it is (TEXT.RSC 480 otherwise). The days (Daggerfall's day of the
  year, month x 30 + day): Clavicus Vile 1, Meridia 13, Sheogorath 32, Sanguine 46, Hermaeus Mora 65, Azura 81, Peryite
  99, Namira 129, Hircine 155, Vaernima 190, Nocturnal 248, Malacath 278, Mephala 283, Boethiah 302, Mehrunes Dagon 320,
  Molag Bal 350;
- the cost: 200000 - 1000 x the summoner's regard (481 asks, with the cost; 454 when the player is short);
- Sheogorath comes instead 5% of the time (15% in a thunderstorm: the Iliac Bay's storm weathers, DFStormWeathers);
- the chance: 30 + the player's regard with the prince, +30 on the prince's weather (rain for Sanguine, Peryite,
  Boethiah, Nocturnal; a storm for Sheogorath) or always for the others; failing (484) at a coven brings 1-3 Daedra
  (Daedra Lord, Seducer, Daedroth, Fire or Frost Daedra);
- a prince summoned before only speaks (482); the first time the prince's video plays
  (`TamrielMerge_DfDaedra<NAME>.bik`) and they offer their quest (Hircine X0C00Y00, Clavicus Vile V0C00Y00, Mehrunes
  Dagon Y0C00Y00, Molag Bal 20C00Y00, Sanguine 70C00Y00, Peryite 50C00Y00, Malacath 80C0XY00, Hermaeus Mora W0C00Y00,
  Sheogorath 60C00Y00, Boethiah U0C00Y00, Namira 30C00Y00, Meridia 10C00Y00, Vaernima 90C00Y00, Nocturnal 40C00Y00,
  Mephala Z0C00Y00, Azura T0C00Y00); refusing brings 3-5 Daedra.

The Mages Guild's and temples' summoning services (Daggerfall Unity Services 66, 456...) are not offered yet.

## Legal standing (TMDfLaw)

"legal repute" changes the player's standing in the region of the town they were last in (-100..100). TMDfLaw (a second
script on DFQuestMachine) reads it as Daggerfall Unity's PlayerEntity and DaggerfallCourtWindow do:

- below -10, each game minute in that region's town streets has a 5% chance (10% once banished from it) that 2-5 of the
  City Watch come for the player for criminal conspiracy (one group at a time; never indoors or in dungeons);
- when the first reaches the player: come quietly or fight (they attack);
- crimes the region's people see (each region is a Skyrim crime faction, DfLaw; docs/progression.md "Crime by game and
  area") bring the Watch at once in town, and lower the standing by the crime's loss when they confront the player;
- the court, for the crime committed (TEXT.RSC 8050-8064, 550-551): FALL.EXE's penalty table for that crime (per standing
  point, base, least, most; criminal conspiracy: 5 x rep + 500 within 10-1500), a point per 40 of it a 40 gold fine or 3
  days in prison at random, a fine beyond the player's gold served at 40 gold a day. A Dark Brotherhood member (assault,
  murder) or Thieves Guild member (breaking in, trespassing, pickpocketing) may be bribed free (rank against 0-19).
  Below 0, banishment unless a -rep and a -rep/2 chance (75 at most) both fail: pleading guilty then banishes, else
  halves the sentence; pleading not guilty, debating (Etiquette) or lying (Streetwise) frees the player with rep + (skill
  + Personality) / 2 percent (5-95; Skyrim's Speech for Personality, the skills docs/daggerfall-senses.md's), else
  banishes or finds them guilty, rep + d100 under 25 doubling the fine, over 75 halving it. A sentence served gives back
  half the crime's loss less one; prison is served in Daggerfall's prison (the days counted down, then passed), and
  the player is let out at the town's gates four hours later, healed;
- every 112 days each region's standing moves 1 toward 0.

## Run time

- A pass runs the operations in Daggerfall's order with Daggerfall Unity's task rules: a trigger sets its state once
  (again after the state is cleared); an always-on condition's first operation for a state sets and clears it, later ones
  only set it; an action runs once when its state is set and again after the state is cleared and set ("clear" re-arms).
  Passes run every 2 s, on every event, and when the player changes location.
- Events: aliases on the quest's people (activating them is "clicked"), items (OnContainerChanged: picked up from the
  world is "clicked item"; OnRead/OnEquipped "used"), the named individuals "when individual is available" waits for;
  foes are polled (dead, wounded); the machine's player alias reports locations and spells cast.
- Offering: "Do you have any work for me?" to a guild's people offers that guild's quests (Mages N, Fighters M, Thieves O,
  Dark Brotherhood L, knightly orders B, temples C and their own letter), members by rank and reputation, non-members only
  0-rank ones (Daggerfall Unity QuestListsManager); to nobles R, merchants K, other townsfolk A, by level; covens Q,
  vampire clans P. The quest is set up (places, people, items), then offered with message 1000; yes 1002, no 1001.
- Places: TMDfQuestSites quests (QuestSites) register at game start: per plugin its towns (a few buildings of each type
  with the street door, arrival marker and the interior's 199.11/199.18 quest markers, now placed as XMarkers by
  DungeonCellBuilder), dungeons (doors, start marker, quest markers), map markers and named individuals. Daggerfall.esm
  registers Privateer's Hold, its own town and dungeon, and the covens quests name (map marker only); each town part and
  dungeon part registers its own. Local = the town the player was last in; remote = another town of its region; dungeons
  by type in the region (falling back to any type, then anywhere). Buildings of one layout share an interior, so "pc at"
  a building also checks the town the player came in from.
- Ending: +5 to the quest's faction when it gave its reward, -2 otherwise; what the quest made is removed (kept items and
  rewards stay). The main quest: `_TUTOR__` and `_BRISIEN` start on the first arrival in the Iliac Bay; "start quest N"
  starts S000000N; an ending finishing (S0000008, the Totem, starts S0000100-S0000104 or S0000106; S0000107 is only a
  reputation change S0000005 makes) calls TMStoryOrder.StoryCompleted(0).

## Dev commands

- `df-quest-dump --quest NAME[,NAME] [--raw] [--names DIR] [--messages]`: a quest as readable commands (or its raw
  records); DIR is a folder of Daggerfall Unity's text quests, only for symbol names.
- `df-quest-check [--places] [--simulate[ NAME]]`: coverage per quest; fixed places and their locations; the pass rules
  run with nothing happening in the world (prompts answered yes): 230 quests wait for the player, and the two that end on
  their own are S0000106 and S0000107, reputation changes that are meant to.
- `df-quest-build [--out FILE] [--scripts]`: a scratch plugin with only the quest records (no sites), checking every
  TMDf script property against the Papyrus sources; `--scripts` compiles the scripts beside it.
- `df-town-test --towns ... --quests`: one or more towns with the quests and their quest sites (Daggerfall: 45 sites,
  29 with a spawn marker, 36 with an item marker, Castle Daggerfall with 4 individuals), the buildings' names, and for
  covens (`--towns "Glenmorial Coven,Coven of the Tide"`) how many witches are in a coven faction and that the
  summoning topic answers all 14 covens.
- `locations --list Coven`: the covens with their location ids, blocks and buildings; `df-flat-census --archives 346,178
  --list`: every flat of some archives in the blocks (the castle courts' people).

## Not done yet / approximations

- Untested in game. Arrays over 128 entries come from script properties (catalogue, factions, sites); Papyrus allows
  that for properties, but it wants checking in game, as does the name-tag text replacement.
- The covens' summoning, the court and the escorts are untested in game. A coven's summoning only from its witches:
  the Mages Guild's and temples' summoning services aren't offered.
- "mute npc" and the questor role have nothing to act on in Skyrim's dialogue; prompts and messages are message boxes
  (not voiced).
- Clocks to a place use 90 minutes per map pixel of the longer axis, x2.5, at least a day, instead of Daggerfall Unity's
  travel calculator. Houses have no names of their own ("a house"); a town's streets as a quest place have no spawn
  marker of their own (people and items placed "at" a town go to its map marker).
- Daggerfall's quest sounds are made only for the ids the converted quests use; the %rt macro in shop names is "King".

## Research notes (Daggerfall Unity)

Source: Daggerfall Unity (github.com/Interkarma/daggerfall-unity, MIT), master. Raw base:
`https://raw.githubusercontent.com/Interkarma/daggerfall-unity/master/`.

### Files and lists

- `Assets/StreamingAssets/Quests/*.txt`: 265 files; 243 from classic Daggerfall (34 `S0000xxx` main quest, `_BRISIEN`,
  `_TUTOR__`, `$CUREVAM`, `$CUREWER`, 17 Daedra quests, guild/social quests). Each holds QRC (text) and QBN (logic).
- Name: char 1 who offers (A commoners, B knights, C temples, K merchants, L Dark Brotherhood, M Fighters, N Mages,
  O Thieves, P vampires, Q witches, R nobles, S main; temple letters 0 D E F G H I J; Daedra 1-9, T-Z); char 3 A
  prospect / B member / C non-member; char 4 minimum rank; char 5 0 normal / 1 one-time / X adult.
- `Tables/QuestList-Classic.txt`: `name, group, membership, minReq, flag`; 187 active rows (Nobility 28, HolyOrder 24,
  Fighters 20, Commoners 20, Mages 18, Knightly 17, Thieves 15, DB 13, Merchants 12, Vampires 10, Witches 10).
- Selection (`QuestListsManager.cs`): membership matches, and (non-member and minReq 0) or (minReq < 10 and <= rank) or
  (minReq >= 10 and <= reputation); not adult; one-time not repeated; uniform random pick. Offer = message 1000 yes/no.

### Format

- QRC: `Name: [id]` / `Message: id` blocks; fixed 1000 offer, 1001 refuse, 1002 accept, 1003 fail, 1004 complete,
  1005-1007 rumours, 1008/1009 questor after, 1010 log, 1045 time lapse; 1011+ free.
- Macros: `_sym_` name, `__sym_` building/town, `___sym_` town/dungeon, `____sym_` region, `=sym_` clock days / foe
  name; `%pcn %pcf %qdt %ra %god %g %di %cn %reg` (MacroHelper.cs).
- QBN declarations: `Item` (gold [range], named, artifact, class/subclass, key), `Person` (group/named/faction/
  factiontype, male/female, local/remote, atHome), `Place` (local/remote/permanent/randompermanent), `Clock`
  (dd.hh:mm, flag bits 1 set task, 8 repeat, 16 travel-time x2.5), `Foe _s_ is [n] <type>`, `variable`, global links.
- Tasks: `_s_ task:`, `until _s_ performed:`, `variable _s_`; the headless startup runs first. A task fires on a
  trigger, `start task`/`setvar`, or its same-named clock; `clear`/`unset` re-arm.

### Commands (82 classes in `Questing/Actions`; uses = lines/quests)

Triggers: when (780/207), clicked npc (258/178), killed foe (211/120), toting item and clicked (207/113), injured foe
(166/95), pc at (90/72), item used (76/55), clicked item (64/43), have item (56/47), repute exceeds (41/18), daily from
(23/16), level completed (12/6), dropped at (8/5), repute is at least (8/2), npc is available (7/1), cast spell (3/3),
enters/exits (1/1).

Actions: say (696), end quest (528), start/stop timer (408), log (377), give pc (288, success), create foe (233),
place foe (215), change repute (204), get item (200), clear (199), reveal (182), make permanent (135), place npc (117),
dialog link / add dialog (173), place item (102), prompt (98), pick one of (93), hide/restore/destroy/mute npc, give
item to foe (63), take item (44), create npc (43), start task (43), cast spell on foe (41), start quest (40), add/drop
face (70), restrain foe (33), unset (30), create npc at (24), remove foe (21), remove log (13), legal repute (12),
play video, rumor mill, teleport pc, diseases, play sound.

### Resolving

- Place: building of a type (Quests-Places.txt: types, guild halls by faction 40/41, house/shop/random), dungeon of a
  type, fixed named places; local = this town, remote = another town / a dungeon in the region. Markers: archive 199
  records 11 (spawn) and 18 (item) in interiors and dungeon blocks; sites without markers are skipped.
- Person: career group (home building by career), Questor (the giver), named Individual (FACTION.TXT), faction.
- Foe: Quests-Foes.txt (Daggerfall mobile ids). Item: Quests-Items.txt.

### Main quest

`_TUTOR__` and `_BRISIEN` start a new game; Brisienna's letters (7-14 days) lead to `S0000999` (backbone state machine:
"npc is available", "repute is at least", "level completed" triggers, `start quest N N`, global variables such as
LiftedCurse, *GotTotem), which runs S0000001-S0000022, S0000100-107 (endings), S0000500-503, S0000977, S0000988.

### Rewards

- Guild/social quests: on end, faction +5 if any `give pc` ran (success), else -2 (Quest.cs).
- Gold: `range a to b`, else `Random(150pm, 200pm) * (regionPrice+500)/1000 * (factionPower+50)/100`, pm = rank+1
  (guilds) or level/2+1, max 10.
- `change repute with _p_ by N` propagates to allies/enemies.

Local copies of the files and C# sources were downloaded to the session scratchpad during research (not kept).
