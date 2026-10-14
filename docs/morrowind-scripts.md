# Morrowind's scripts

Every Morrowind script runs in the converted game (user, 2026-10-14: "all scripts need to work"): its object scripts,
its global and start scripts, and the result scripts of its dialogue, translated from Morrowind's script language
(MWScript) into Papyrus and compiled with the Creation Kit's compiler when `mw-build` converts Morrowind.

## How a script is translated

`Morrowind/MwScript.cs` parses a script (SCPT's source text, SCTX) or a dialogue response's result (BNAM) into a tree;
`Morrowind/MwPapyrus*.cs` writes it as Papyrus; `Morrowind/MwScriptSet.cs` does it for the whole game;
`Morrowind/MwScriptsAttach.cs` puts the scripts on the records.

- **Object scripts** (on activators, containers, doors, lights, items, people, creatures) become `MW_<name>`, extending
  Actor when only people and creatures carry them, else ObjectReference. Morrowind runs a script every frame while its
  object is in an active cell; Papyrus runs the script's body (`MwTick`) on a timer while the object's 3D is loaded,
  or the player carries it: every 0.5 s, or 0.05 s for scripts that move or turn things (Move, Rotate, SetPos,
  SetAngle). A script whose every statement waits on an event (`if ( OnActivate )`, `if ( OnDeath )`…) has no timer:
  the event runs it.
- **Events** set a flag the script's own function reads once, as Morrowind's do, and run the script at once:
  OnActivate (a script with OnActivate blocks the default activation, and `Activate` performs it — through the
  object's Morrowind lock, TMMwLock, and a swinging door's swing, TMSwingDoor), OnDeath, OnMurder (killed by the player),
  OnKnockout (bleeding out), OnPCAdd, OnPCDrop, OnPCEquip (the function, and the special locals OnPCEquip and
  PCSkipEquip), OnPCHitMe. GetPCSleep is true from Skyrim's sleep start to its end; the script runs as the sleep
  starts, so an ambush placed then is there on waking (Skyrim can't interrupt sleep: WakeUpPC does nothing).
- **Global scripts** — Morrowind's Main, the start scripts Tribunal and Bloodmoon list (SSCR), and every script named
  by StartScript, StopScript or ScriptRunning anywhere — become `MWG_<name>` on a quest of their own (`MWScript<name>`),
  run every 0.5 s while the quest runs. StartScript starts it (a reference's StartScript sets the script's implicit
  reference, `MwSelf`), StopScript stops it, ScriptRunning is IsRunning. Start scripts start with the game; every
  global script runs only while the player is in Morrowind's lands (a cell of Morrowind.esm), since Morrowind's story
  comes after Redguard's and Daggerfall's (TMStoryOrder) and its timers and ambushes (Tribunal's Dark Brotherhood
  attack on sleeping) belong there. A script both carried and started gets both versions. Morrowind's engine sets
  CharGenState to 1 on a new game: the converted global starts at 1, so Main runs the character generation (the prison
  ship, the census office) when the player first arrives.
- **Dialogue results** become `MWR_<hash>` TopicInfo fragments run as the response ends, the speaker their implicit
  reference; responses with the same translated result share one script (3,700 scripts for 5,550 responses). They
  extend TMMwInfo, so persuasion's disposition, gold and roll still apply. Results that only end the conversation,
  offer choices or add topics need no script (Goodbye is the response's flag, Choice its linked topics). A result
  script that didn't compile falls back to TMMwInfo's fixed set (journal, items, globals, factions, disposition).
- **Locals** become properties (`v_<name>`) so other scripts read and set them (`"Ralen Hlaalo".state`, through the
  script the object's record carries). A result script's undeclared names are its speaker's locals: with one speaker,
  through its script; otherwise each script on people that declares the local is tried. Locals the dialogue's
  conditions test are mirrored as the actor's rank in a hidden faction `MWLocal<name>`, and the converted conditions
  (filter type 3, "local", and C, "not local") read that rank (a speaker without the local isn't in the faction, so
  the response isn't theirs, as in Morrowind). A name a script uses without declaring it (its declaration lost before
  the game shipped) becomes a local of its own.
- **Names** become properties the converter fills: a reference by ID (`r_`, the first reference of that ID, placed
  persistent, as Morrowind's `ID->` finds it), a base object (`f_`), a global (`g_`; GameHour, Day, Month, Year,
  DaysPassed and TimeScale are Skyrim's own), a journal's quest (`q_`), a global script's quest (`s_`), a faction,
  spell, sound, race and so on. The records the scripts need are made for them: a Message per message box with buttons,
  a topic per Say line, a marker at the origin of each cell PositionCell names, the AI quest, the mirror factions,
  globals for faction reputation, a crime faction, lists of cells, diseases and effects.
- **Shared helpers** are TamrielMerge's own scripts: `TMMwScript` (global functions for what is more than one Papyrus
  call) and `TMMwAI` (the AI packages, on the quest MWScriptAI).

Scripted activators become Activator records (unscripted ones stay statics). Scripts and globals have their own
namespaces (Tribunal has globals named like scripts: MatchMakeFons). Morrowind's activation rule is kept: an object is
held back from activating while its script checks OnActivate, and let go when the script calls Activate (so a door
whose script stops checking after its first use opens normally afterwards). The special local `nolore` (people without
the generic topics) works through the mirrored locals, since Morrowind's own dialogue tests it; `companion` (sharing
inventory) does not, Skyrim's followers trade through their own dialogue.

## Functions

Morrowind's functions (all of Morrowind's, Tribunal's and Bloodmoon's; `MwFunctions.cs` lists them with their
arguments) map to Papyrus and SKSE. Where Skyrim has no counterpart the nearest behaviour is used:

| Morrowind | Skyrim |
|---|---|
| Journal, SetJournalIndex, GetJournalIndex | SetStage / GetStage of the journal's quest (MwDialogue's: a stage per index). Skyrim's GetStage is the highest stage set, so a journal can't go back; an index scripts set that no entry has gets a stage without a log entry. A journal the game doesn't have reads 0 and does nothing |
| AddItem, RemoveItem, GetItemCount, Drop, Equip, HasItemEquipped | the same; coins are Skyrim's gold (Gold_005 counts 5) |
| Enable, Disable, GetDisabled, SetDelete, Lock, Unlock, GetLocked, Activate | the same, through a swinging door's or Morrowind lock's script where there is one |
| PositionCell, Position, CenterOnCell, CenterOnExterior, PlaceItemCell, PlaceItem | MoveTo / PlaceAtMe by a marker at the cell's origin (the exterior's at the worldspace's), the coordinates as offsets |
| PlaceAtPC, PlaceAtMe | PlaceAtMe, moved that far ahead, behind, left or right |
| GetPos, SetPos, GetAngle, SetAngle, GetStartingPos, GetStartingAngle, SetAtStart | the same (the start is where the object was when first loaded) |
| Move, MoveWorld, Rotate, RotateWorld | TranslateTo over the frame time, so the motion glides |
| GetDistance | GetDistance; across cells, Morrowind's huge distance |
| MessageBox with buttons, GetButtonPressed | a Message record shown with the values (Skyrim formats %.Nf); the choice is returned once. Without buttons a script reads, Debug.MessageBox; ^Action names are written as words |
| Say, SayDone | a topic of the quest MWScriptSay with one response per speaker, carrying Morrowind's recording (the voice planner packs it in the speaker's voice) and the subtitle; SayDone after the line's length reckoned from its text. Said by the player or a creature (Azura speaking to the player), the recording plays as a sound and the text is shown |
| PlaySound, PlaySound3D(VP), PlayLoopSound3D(VP), StopSound, GetSoundPlaying | a sound marker per Morrowind sound (loops on a copy of a looping descriptor); GetSoundPlaying for the file's length |
| AIWander, AITravel, AIEscort, AIFollow(Cell), AIEscortCell, AIActivate, GetAIPackageDone, GetCurrentAIPackage | TMMwAI: travel and escort walk the actor there (PathToReference on a thread of its own; moved there if the navmesh has no way) and hold it there; follow the player through an alias with DefaultFollowPlayer (another actor: KeepOffsetFromActor); wander through sandbox aliases about where it stands (range 0: hold position). Durations are not kept |
| StartCombat, StopCombat, ForceGreeting, Resurrect | the same; ForceGreeting is the player activating the person (the conversation opens) |
| GetDisposition, SetDisposition, ModDisposition | Variable09 (disposition minus 50), as TMMwInfo and the converted dialogue keep it |
| Get/Set/ModHello, Fight, Flee, Alarm | Morrowind's numbers kept in Variable05-08, and Fight set on Aggression (80+ attacks), Flee on Confidence, Alarm on Assistance |
| Get/Set/Mod skills, Health, Magicka, Fatigue, resistances | Skyrim's actor values (Fatigue is Stamina; Morrowind's skills on Skyrim's nearest) |
| Get/Set/Mod attributes, Athletics, Acrobatics, Unarmored, some effects | no counterpart: read as 30 + the actor's level (up to 100), so checks pass or fail as for a character of that level; setting them does nothing |
| AddSpell, RemoveSpell, GetSpell, Cast, ExplodeSpell, GetSpellEffects, RemoveSpellEffects | the converted spell (MwMagic) |
| GetEffect, RemoveEffects | the converted magic effects of that Morrowind effect (a list per effect) |
| GetCommonDisease, GetBlightDisease | any of Morrowind's converted diseases of that kind |
| GetPCRank, PCJoinFaction, PCRaiseRank, PCLowerRank, PCExpell, PCClearExpelled, RaiseRank, LowerRank | faction ranks and expulsion (a faction may be named by its name: "House Telvanni") |
| GetPCFacRep, ModPCFacRep, Get/Set/ModReputation | globals (Skyrim keeps no reputation) |
| Get/Set/ModFactionReaction | Skyrim's enemy / neutral / friend / ally by the sign and size |
| GetPCCrimeLevel, SetPCCrimeLevel, ModPCCrimeLevel, PayFine, PayFineThief, GoToJail | a crime faction for Morrowind (MWCrimeFaction); jail is the bounty served as days passing |
| GetPCCell | the player's cell in a list of the cells of that name (interiors starting with it, the exterior cells named so) |
| CellChanged | the player's cell differs from the script's last look |
| GetStandingPC, GetCollidingPC, HurtStandingActor, HurtCollidingActor | the player within the object's bounds (on top of it) |
| StartScript, StopScript, ScriptRunning | the global script's quest |
| DisablePlayerControls, Fighting, Magic, ViewSwitch, Looking and their Enable | Game.Disable/EnablePlayerControls for that part; jumping and the vanity camera have no switch |
| DisableTeleporting, EnableTeleporting | fast travel |
| EnableRaceMenu, NameMenu, ClassMenu, BirthMenu, StatReviewMenu | nothing: the player made their character at the start of the journey (TMStoryOrder) |
| ShowMap | the town's map marker added to the map |
| ShowRestMenu | the Wait key tapped (SKSE) |
| FadeIn, FadeOut, FadeTo | Game.FadeOutGame |
| PlayBink | Morrowind's own Bink video, copied to Data\Video\TamrielMerge_MW_… |
| PlayGroup, LoopGroup | the model's sequence of that name on objects; on people, Skyrim's nearest idle for knockouts |
| ChangeWeather, GetCurrentWeather | Skyrim's weather for Morrowind's weather type (the regions' table) |
| BecomeWerewolf, UndoWerewolf, IsWerewolf, GetWerewolfKills | Skyrim's beast form (its change spell, its shift-back timer, its race, its stat) |
| HasSoulGem, RemoveSoulGem, AddSoulGem | Skyrim's soul gems hold a size, not a creature: filled gems carried; Morrowind's gem as Skyrim's filled gem of that size |
| MenuMode, GetSecondsPassed, Random, GetSquareRoot, Xbox | Utility.IsInMenuMode, the time since the script last ran, RandomInt, Math.Sqrt, 0 |
| AddTopic, ClearInfoActor, StreamMusic, ModRegion, Fall, ForceRun/Jump, SetWerewolfAcrobatics, TurnMoon…, console commands | nothing to do in Skyrim (its converted topics need no unlocking, the music plays from its own system, its actors fall and run by themselves): kept as comments, counted as translated |
| OnPCSoulGemUse, OnRepair, HitOnMe, HitAttemptOnMe, RepairedOnMe, GetPCInJail, GetPCTraveling | Skyrim has no event or state for them: always false |

The original scripts' slips are read as Morrowind's compiler read them: `if doonce = 0`, `<==`, IDs with hyphens
(`Ingred_Hackle-Lo_Leaf_01`), `"Torasa Aram". == 2` (a missing variable: 0), an extra endif (ignored), an elseif left
after an extra endif (a new if), an else without its if (never runs, as the compiler jumps over it), a condition line
without its if (nothing), `GetDisabled "Cimber Valerius"` (the reference meant), `Player->fakeiswere` (the script's own
local), `GetStat Level` (the level), a cell named first in PositionCell, and names of cells or journals the game
doesn't have (nothing, as in Morrowind).

## Coverage and testing

`TamrielMerge.Cli mw-script-translate [--out DIR] [--compile] [--id REGEX] [--plugins a,b]` translates every script of
the converter's plugins into `out/mwscripts/Source` (default) and writes `coverage.txt`: per plugin the share of
statements translated, what is missing most and the scripts with the most left. `--compile` compiles them with the
Creation Kit's compiler against Skyrim's, SKSE's and TamrielMerge's sources (batches of 400) into `Scripts`, the log in
`compile.txt`. `mw-build --no-scripts` leaves the scripts out.

Results (2026-10-14, Morrowind + Tribunal + Bloodmoon + the official plugins the converter selects): 1,215 scripts
become 1,089 object scripts and 138 global scripts (5 start scripts), and the 5,553 of 16,081 dialogue results that
do something become 3,703 result scripts; 33,374 of 33,374 statements translated (100%), and all 4,930 Papyrus
scripts compile (about 17 minutes in batches of 400). `mw-script-attach-test` runs the builder's script steps on the
prison ship and the census office with the dialogue: 922 scripts attached (709 people), 7,387 responses with their
result script, 18,517 properties filled (the 1,850 left are references in cells that test doesn't build).

Not checked in the game yet: everything above is verified by compiling and by the builder's bookkeeping only.
