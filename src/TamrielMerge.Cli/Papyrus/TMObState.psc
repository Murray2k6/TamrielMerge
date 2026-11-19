ScriptName TMObState extends Quest
{What Oblivion's engine kept track of for its scripts, kept here for the converted Oblivion scripts (OB_*) by
TamrielMerge: animations playing (PlayGroup / IsAnimPlaying), script packages (AddScriptPackage, through this quest's
package aliases), crimes and who knows of them (GetCrimeKnown, OnAlarm, GetPCFactionMurder...), bounties, misc stats,
destroyed and restrained references, who has talked to the player, the Oblivion gate the player came through,
birthsigns, and Oblivion's numbering of AI packages and procedures. Functions that need no state are Global.}

Actor Property PlayerRef Auto
GlobalVariable Property PCCrimeGold Auto
{TMOB_PCCrimeGold: the player's bounty in Oblivion's lands (Oblivion has one bounty, not one per hold).}
GlobalVariable Property PCAmountSoldStolen Auto
{TMOB_PCAmountSoldStolen: gold the player got for stolen goods (GetAmountSoldStolen).}
FormList Property TalkedToPC Auto
FormList Property DestroyedRefs Auto
FormList Property RestrainedActors Auto
FormList Property NoRumors Auto
FormList Property StolenItems Auto
{Items the player took without paying, for GetAmountSoldStolen.}
FormList Property QuestItems Auto
{TMObQuestItems: Oblivion's quest items (its records' flag, SetQuestObject), which the player keeps (TMObPlayer).}
FormList Property QuestItemsCleared Auto
{TMObQuestItemsCleared: those SetQuestObject made ordinary again (a list's own entries can't be taken off it).}
Faction[] Property CrimeFactions Auto
{The factions scripts ask GetPCFactionMurder / Steal / Attack about.}
ReferenceAlias[] Property PackageSlots Auto
{This quest's aliases that run a script package (AddScriptPackage); each has one package.}
Package[] Property SlotPackages Auto
Package[] Property Packages Auto
{Every Oblivion package the scripts name, with its Oblivion type (Find 0, Follow 1, Escort 2, Eat 3, Sleep 4, Wander 5,
Travel 6, Accompany 7, UseItemAt 8, Ambush 9, FleeNotCombat 10, CastMagic 11).}
Int[] Property PackageTypes Auto
Message Property BirthsignPage1 Auto
Message Property BirthsignPage2 Auto
FormList[] Property BirthsignSpells Auto
{Each birthsign's spells and abilities, in the order the scripts number them.}
Faction Property CyrodiilCrime Auto
{OBCrimeCyrodiil: the crime faction of Cyrodiil's people. Skyrim keeps the player's bounty there and arrests through it.}
Faction Property IslesCrime Auto
{OBCrimeShiveringIsles: the Shivering Isles' crime faction.}
Faction[] Property CityCrimes Auto
{OBCrimeAnvil, ...: the crime factions of Cyrodiil's cities' guards, each jailing in its own castle's prison. Their bounty
is kept the same as CyrodiilCrime's (SyncCityBounty): Oblivion has one bounty.}
Int syncedNonViolent = 0
Int syncedViolent = 0
GlobalVariable Property PCInSEWorld Auto
{TMOB_PCInSEWorld: whether the player is in the Shivering Isles (which land's bounty a script's SetCrimeGold sets).}
GlobalVariable Property PCInJail Auto
{TMOB_PCInJail: whether the player is in an Oblivion jail (IsPlayerInJail).}
FormList Property JailMarkers Auto
{The prison markers of Oblivion's jails (TMObJailMarkers).}

Int Property ChosenBirthsign = -1 Auto Hidden
Bool Property PCSleeping = False Auto Hidden
{Set by TMObPlayer while the player sleeps (IsPCSleeping).}
ObjectReference Property CurrentGate Auto Hidden
Float gateX
Float gateY
Float gateZ
Float gateAngle

ObjectReference[] animRefs
Float[] animEnds
String[] animGroups
Int[] crimeTypes
Form[] crimeCriminals
Form[] crimeVictims
Int crimeNext
Int[] factionCrimes
Actor[] bountyActors
Int[] bountyGold
Int[] miscStats
Actor[] speakers
TopicInfo[] speakLines
Float[] speakStarts
Bool[] speakTalking
Bool[] speakLooking
TopicInfo[] learnedLines
VoiceType[] learnedVoices
Float[] learnedLengths
Int learnedNext
Int fedSeen = -1
Int ownFeeds

Function Init()
  If !animRefs
    animRefs = new ObjectReference[64]
    animEnds = new Float[64]
    animGroups = new String[64]
    crimeTypes = new Int[64]
    crimeCriminals = new Form[64]
    crimeVictims = new Form[64]
    factionCrimes = new Int[128]
    bountyActors = new Actor[32]
    bountyGold = new Int[32]
    miscStats = new Int[34]
    speakers = new Actor[16]
  EndIf
  If !animGroups
    animGroups = new String[64]
  EndIf
  If !speakLines
    speakLines = new TopicInfo[16]
    speakStarts = new Float[16]
    speakTalking = new Bool[16]
    speakLooking = new Bool[16]
    learnedLines = new TopicInfo[64]
    learnedVoices = new VoiceType[64]
    learnedLengths = new Float[64]
  EndIf
  If !speakForced
    speakForced = new Bool[16]
    speakForcedAt = new Float[16]
  EndIf
  If !speakScripted
    speakScripted = new Bool[16]
    speakPending = new Bool[16]
    speakResulted = new Bool[16]
    speakLineIds = new Int[16]
    speakLengths = new Float[16]
    speakBegins = new Int[16]
    speakBeforeIds = new Int[16]
    speakBeforeAt = new Float[16]
    learnedLineIds = new Int[64]
  EndIf
EndFunction

Event OnInit()
  Init()
EndEvent

; SetQuestObject: an item made a quest item or not.
Function SetQuestItem(Form akItem, Bool abQuest)
  If !akItem || !QuestItems || !QuestItemsCleared
    Return
  EndIf
  If abQuest
    If QuestItemsCleared.HasForm(akItem)
      QuestItemsCleared.RemoveAddedForm(akItem)
    EndIf
    If !QuestItems.HasForm(akItem)
      QuestItems.AddForm(akItem)
    EndIf
  ElseIf QuestItems.HasForm(akItem) && !QuestItemsCleared.HasForm(akItem)
    QuestItemsCleared.AddForm(akItem)
  EndIf
EndFunction

Bool Function IsQuestItem(Form akItem)
  Return QuestItems && akItem && QuestItems.HasForm(akItem) && !(QuestItemsCleared && QuestItemsCleared.HasForm(akItem))
EndFunction

; ---- Oblivion's attributes and skills ----

; The scripts' GetAV / SetAV / ModAV / ForceAV of an attribute (0-7) or skill (8-28, Oblivion's order) - Oblivion's
; own (user, 2026-10-23: "Keep original stats"). A person's or creature's: its Oblivion value (TMObActor.OblivionStat)
; plus what scripts changed. The player's where it lives: a skill Skyrim keeps (Block, Sneak...) Skyrim's, the others
; the progression's (TMSkills: Athletics, Mysticism...; Blade and Blunt hidden ones), the attributes the progression's
; (TMPassives). An actor not of Oblivion: Skyrim's nearest value (SkyrimStatAv).
String[] Property PlayerStatAv Auto
{Each skill's (and AI value's) Skyrim actor value where Skyrim keeps the player's ("" otherwise).}
String[] Property PlayerStatId Auto
{Each skill's progression skill where the progression keeps the player's ("" otherwise).}
String[] Property SkyrimStatAv Auto
{Each skill's nearest Skyrim actor value, for actors that aren't Oblivion's (the attributes "": Skyrim's have none, 0).}
Int Property StatCount = 33 AutoReadOnly

Actor[] statActors
Int statNext
Float[] sd0
Float[] sd1
Float[] sd2
Float[] sd3
Float[] sd4
Float[] sd5
Float[] sd6
Float[] sd7
Float[] sd8
Float[] sd9
Float[] sd10
Bool warnedProgression

; 32 actors of 33 stats: 1,056 changes in eleven arrays of 100 (Papyrus makes none longer than 128).
Float[] Function StatPage(Int aiPart)
  If !sd10
    sd10 = new Float[100]
  EndIf
  If !sd9
    sd0 = new Float[100]
    sd1 = new Float[100]
    sd2 = new Float[100]
    sd3 = new Float[100]
    sd4 = new Float[100]
    sd5 = new Float[100]
    sd6 = new Float[100]
    sd7 = new Float[100]
    sd8 = new Float[100]
    sd9 = new Float[100]
  EndIf
  If aiPart == 0
    Return sd0
  ElseIf aiPart == 1
    Return sd1
  ElseIf aiPart == 2
    Return sd2
  ElseIf aiPart == 3
    Return sd3
  ElseIf aiPart == 4
    Return sd4
  ElseIf aiPart == 5
    Return sd5
  ElseIf aiPart == 6
    Return sd6
  ElseIf aiPart == 7
    Return sd7
  ElseIf aiPart == 8
    Return sd8
  ElseIf aiPart == 9
    Return sd9
  EndIf
  Return sd10
EndFunction

Float Function StatDelta(Int aiSlot, Int aiStat)
  Int flat = aiSlot * StatCount + aiStat
  Return StatPage(flat / 100)[flat % 100]
EndFunction

Function SetStatDelta(Int aiSlot, Int aiStat, Float afValue)
  Int flat = aiSlot * StatCount + aiStat
  Float[] page = StatPage(flat / 100)
  page[flat % 100] = afValue
EndFunction

; The actor's slot of changes (made when asked: a free one, a dead actor's, else the oldest).
Int Function StatSlot(Actor a, Bool abMake)
  If !statActors
    statActors = new Actor[32]
  EndIf
  Int i = statActors.Find(a)
  If i >= 0 || !abMake
    Return i
  EndIf
  i = statActors.Find(None)
  If i < 0
    i = 0
    While i < statActors.Length && !statActors[i].IsDead()
      i += 1
    EndWhile
    If i >= statActors.Length
      i = statNext
      statNext = (statNext + 1) % statActors.Length
    EndIf
  EndIf
  statActors[i] = a
  Int k = 0
  While k < StatCount
    SetStatDelta(i, k, 0.0)
    k += 1
  EndWhile
  Return i
EndFunction

TMPassives Function Passives()
  Quest q = Quest.GetQuest("TMProgression")
  If q
    Return (q as TMPassives)
  EndIf
  WarnProgression()
  Return None
EndFunction

TMSkills Function Skills()
  Quest q = Quest.GetQuest("TMProgression")
  If q
    Return (q as TMSkills)
  EndIf
  WarnProgression()
  Return None
EndFunction

Function WarnProgression()
  If !warnedProgression
    warnedProgression = True
    Debug.Notification("Oblivion's attributes and skills need TamrielMerge Progression.esp.")
  EndIf
EndFunction

; The player's stat (abBase: without what fortifies it).
Float Function PlayerStat(Actor p, Int aiStat, Bool abBase)
  If aiStat < 8
    TMPassives passives = Passives()
    If passives
      Return passives.AttributeValue(aiStat)
    EndIf
    Return 0.0
  EndIf
  Int s = aiStat - 8
  If PlayerStatAv[s] != ""
    If abBase
      Return p.GetBaseActorValue(PlayerStatAv[s])
    EndIf
    Return p.GetActorValue(PlayerStatAv[s])
  EndIf
  TMSkills skills = Skills()
  If skills
    Return skills.EffectiveLevel(skills.Index(PlayerStatId[s]))
  EndIf
  Return 0.0
EndFunction

Function SetPlayerStat(Actor p, Int aiStat, Float afValue)
  If aiStat < 8
    TMPassives passives = Passives()
    If passives
      passives.ModAttribute(aiStat, (afValue - passives.AttributeValue(aiStat)) as Int)
    EndIf
    Return
  EndIf
  Int s = aiStat - 8
  If PlayerStatAv[s] != ""
    ; The base set, with what fortifies it on top.
    Float fortified = p.GetActorValue(PlayerStatAv[s]) - p.GetBaseActorValue(PlayerStatAv[s])
    p.SetActorValue(PlayerStatAv[s], afValue - fortified)
    Return
  EndIf
  TMSkills skills = Skills()
  If skills
    skills.SetEffectiveLevel(skills.Index(PlayerStatId[s]), afValue as Int)
  EndIf
EndFunction

; ModPCSkill: the player's skill raised as by use (its levels counting towards the character's).
Function RaisePlayerSkill(Int aiStat, Int aiBy)
  If aiStat < 8 || aiBy <= 0
    Return
  EndIf
  Int s = aiStat - 8
  If PlayerStatAv[s] != ""
    Game.IncrementSkillBy(PlayerStatAv[s], aiBy)
    Return
  EndIf
  TMSkills skills = Skills()
  If skills
    Int i = skills.Index(PlayerStatId[s])
    Int k = 0
    While k < aiBy && i >= 0
      skills.RaiseOne(i)
      k += 1
    EndWhile
  EndIf
EndFunction

Float Function StatBaseOf(Actor a, Int aiStat)
  TMObActor ob = a as TMObActor
  If ob
    Float v = ob.OblivionStat(aiStat)
    If v >= 0.0
      Return v
    EndIf
  EndIf
  If SkyrimStatAv[aiStat] == ""
    Return 0.0
  EndIf
  Return a.GetBaseActorValue(SkyrimStatAv[aiStat])
EndFunction

Float Function GetStat(Actor a, Int aiStat, Bool abBase = False)
  If !a || aiStat < 0 || aiStat >= StatCount
    Return 0.0
  EndIf
  If a == PlayerRef
    Return PlayerStat(a, aiStat, abBase)
  EndIf
  Float v = StatBaseOf(a, aiStat)
  Int slot = StatSlot(a, False)
  If slot >= 0
    v += StatDelta(slot, aiStat)
  EndIf
  If v < 0.0
    v = 0.0
  EndIf
  Return v
EndFunction

Function SetStat(Actor a, Int aiStat, Float afValue)
  If !a || aiStat < 0 || aiStat >= StatCount
    Return
  EndIf
  If afValue < 0.0
    afValue = 0.0
  EndIf
  If a == PlayerRef
    SetPlayerStat(a, aiStat, afValue)
    Return
  EndIf
  Int slot = StatSlot(a, True)
  SetStatDelta(slot, aiStat, afValue - StatBaseOf(a, aiStat))
  MirrorStat(a, aiStat)
  ; What follows in Skyrim's AI, as the conversion gives it (ObContent.ObAi): Aggression 0 unaggressive, to 50
  ; aggressive, over 50 very aggressive; Confidence by fifths; Energy as it is.
  If aiStat == 29
    If afValue > 50.0
      a.SetActorValue("Aggression", 2.0)
    ElseIf afValue > 0.0
      a.SetActorValue("Aggression", 1.0)
    Else
      a.SetActorValue("Aggression", 0.0)
    EndIf
  ElseIf aiStat == 30
    If afValue <= 10.0
      a.SetActorValue("Confidence", 0.0)
    ElseIf afValue <= 30.0
      a.SetActorValue("Confidence", 1.0)
    ElseIf afValue <= 60.0
      a.SetActorValue("Confidence", 2.0)
    ElseIf afValue <= 90.0
      a.SetActorValue("Confidence", 3.0)
    Else
      a.SetActorValue("Confidence", 4.0)
    EndIf
  ElseIf aiStat == 31
    a.SetActorValue("Energy", afValue)
  EndIf
EndFunction

Function ModStat(Actor a, Int aiStat, Float afBy)
  SetStat(a, aiStat, GetStat(a, aiStat) + afBy)
EndFunction

; ---- what the dialogue's conditions read (Skyrim's conditions read globals and faction ranks, not scripts) ----

Int[] Property StatMirrors Auto
{The stats (ObStats' numbers) the conditions ask of: each the player's in StatGlobals, another's rank in StatFactions.}
GlobalVariable[] Property StatGlobals Auto
Faction[] Property StatFactions Auto
Int[] Property MiscMirrors Auto
{The misc stats the conditions ask of, each in MiscGlobals.}
GlobalVariable[] Property MiscGlobals Auto
GlobalVariable Property BirthsignGlobal Auto
{TMOB_PCBirthsign: the player's birthsign by this script's numbering (-1 none).}
FormList Property EvilFactions Auto
{Oblivion's evil factions.}
FormList Property OblivionFactions Auto
{All of Oblivion's factions (IsActorEvil: an evil one and no other).}
Faction Property EvilFaction Auto
{OBEvil: those who are.}

; The player's, now (TMObPlayer: every few seconds and as a conversation opens).
Function MirrorPlayer()
  Int i = 0
  While StatMirrors && i < StatMirrors.Length
    If StatGlobals[i]
      StatGlobals[i].SetValue(GetStat(PlayerRef, StatMirrors[i]))
    EndIf
    i += 1
  EndWhile
  i = 0
  While MiscMirrors && i < MiscMirrors.Length
    If MiscGlobals[i]
      MiscGlobals[i].SetValue(MiscStat(MiscMirrors[i]))
    EndIf
    i += 1
  EndWhile
  If BirthsignGlobal
    BirthsignGlobal.SetValue(PlayerBirthsign())
  EndIf
EndFunction

; The player's birthsign by this script's numbering: the one chosen here, else the standing stone the progression
; holds the player to (TMPassives.PlayerSign, its ObSigns), else -1.
Int Function PlayerBirthsign()
  If ChosenBirthsign >= 0
    Return ChosenBirthsign
  EndIf
  Quest q = Quest.GetQuest("TMProgression")
  If q
    TMPassives passives = q as TMPassives
    If passives && passives.ObSigns
      Int sign = passives.PlayerSign()
      If sign >= 0
        Return passives.ObSigns.Find(sign)
      EndIf
    EndIf
  EndIf
  Return -1
EndFunction

; An Oblivion person's or creature's, as it loads and as a script changes them (TMObActor, SetStat).
Function MirrorActor(Actor a)
  If !a
    Return
  EndIf
  Int i = 0
  While StatMirrors && i < StatMirrors.Length
    MirrorStat(a, StatMirrors[i])
    i += 1
  EndWhile
  If EvilFaction && EvilFactions && OblivionFactions
    Bool evil = False
    If !IsCreature(a)
      Faction[] all = a.GetFactions(-128, 127)
      Bool other = False
      Int k = 0
      While all && k < all.Length
        If EvilFactions.HasForm(all[k])
          evil = True
        ElseIf OblivionFactions.HasForm(all[k])
          other = True
        EndIf
        k += 1
      EndWhile
      evil = evil && !other
    EndIf
    If evil
      a.SetFactionRank(EvilFaction, 0)
    ElseIf a.IsInFaction(EvilFaction)
      a.RemoveFromFaction(EvilFaction)
    EndIf
  EndIf
EndFunction

Function MirrorStat(Actor a, Int aiStat)
  If !StatMirrors || a == PlayerRef
    Return
  EndIf
  Int i = StatMirrors.Find(aiStat)
  If i >= 0 && StatFactions[i]
    Int v = GetStat(a, aiStat) as Int
    If v > 127
      v = 127
    EndIf
    a.SetFactionRank(StatFactions[i], v)
  EndIf
EndFunction

; ---- the play clock ----

; Oblivion's scripts count time in frames, which stop in menus and conversations (MenuMode): GetSecondsPassed and
; ScriptEffectElapsedSeconds give a run's share of play time. Real time less the time in those menus (TMObPlayer tells
; of each opening and closing, OnMenuOpen/OnMenuClose); real time starts again from 0 each time the game is loaded,
; counted by ClockLoads.
Float pausedTotal = 0.0
Float pausedSince = -1.0
Int menusOpen = 0
Int Property ClockLoads = 0 Auto Hidden
Int Property Launches = 0 Auto Hidden
{Times the game was started up (GetGameRestarted): LaunchMarker's name, set with SKSE's SetName, which no save keeps,
is the plugin's again after each start.}
MiscObject Property LaunchMarker Auto

GlobalVariable Property ClockPaused Auto
{TMOB_ClockPaused: seconds spent in menus since the game was loaded.}
GlobalVariable Property ClockHeld Auto
{TMOB_ClockHeld: the real time at which the menu now open was opened (the clock stands there); 0 with none open.}
GlobalVariable Property ClockLoadsGlobal Auto
{TMOB_ClockLoads: ClockLoads, for the scripts to read.}

; The play clock from its globals: what PlayTime gives, without this script being asked (every script's every run asked
; it, and they queued here: see ObPapyrus.Clock).
Float Function Clock(GlobalVariable akPaused, GlobalVariable akHeld) Global
  Float held = akHeld.GetValue()
  If held > 0.0
    Return held - akPaused.GetValue()
  EndIf
  Return Utility.GetCurrentRealTime() - akPaused.GetValue()
EndFunction

Function PublishClock()
  If ClockPaused
    ClockPaused.SetValue(pausedTotal)
  EndIf
  If ClockHeld
    If pausedSince >= 0.0
      ; (0 means none open: a menu opened at the very start still holds the clock.)
      If pausedSince > 0.001
        ClockHeld.SetValue(pausedSince)
      Else
        ClockHeld.SetValue(0.001)
      EndIf
    Else
      ClockHeld.SetValue(0.0)
    EndIf
  EndIf
  If ClockLoadsGlobal
    ClockLoadsGlobal.SetValueInt(ClockLoads)
  EndIf
EndFunction

Function MenuOpened()
  If menusOpen <= 0
    menusOpen = 0
    pausedSince = Utility.GetCurrentRealTime()
  EndIf
  menusOpen += 1
  PublishClock()
EndFunction

Function MenuClosed()
  menusOpen -= 1
  If menusOpen <= 0 && pausedSince >= 0.0
    pausedTotal += Utility.GetCurrentRealTime() - pausedSince
    pausedSince = -1.0
    menusOpen = 0
  EndIf
  PublishClock()
EndFunction

; A save loaded or a new game begun (TMObPlayer): the clock starts again, GetGameLoaded is true once to each script.
GlobalVariable Property StageBusy Auto
{TMOB_StageBusy: how many quest stages' result scripts are running now; the converted scripts' frames wait meanwhile.}

; SetStage as Oblivion has it: the stage's result script is run before any other script's next frame (Skyrim's
; SetStage comes back when the stage's fragment is done; the frames wait on TMOB_StageBusy until then).
Bool Function StageTo(Quest akQuest, Int aiStage)
  If !akQuest
    Return False
  EndIf
  If StageBusy
    StageBusy.Mod(1.0)
  EndIf
  ; For the log (when Papyrus logging is on): the stages as they are set, so a scene can be followed without the console.
  Debug.Trace("TMOb stage " + akQuest + " to " + aiStage + " (was " + akQuest.GetStage() + ")")
  Bool done = akQuest.SetStage(aiStage)
  If StageBusy
    StageBusy.Mod(-1.0)
  EndIf
  Return done
EndFunction

Function GameLoaded()
  pausedTotal = 0.0
  pausedSince = -1.0
  menusOpen = 0
  ClockLoads += 1
  PublishClock()
  If LaunchMarker && LaunchMarker.GetName() != "TamrielMerge started"
    Launches += 1
    LaunchMarker.SetName("TamrielMerge started")
  EndIf
EndFunction

; Whether the player is in Oblivion's lands (a cell of Oblivion.esm): its quests' scripts run only there.
Bool Function InOblivion()
  Cell here = Game.GetPlayer().GetParentCell()
  If !here
    Return False
  EndIf
  Int index = Game.GetModByName("Oblivion.esm")
  If index < 0 || index > 254
    Return False
  EndIf
  Return Math.RightShift(here.GetFormID(), 24) == index
EndFunction

; Seconds of play since the game was loaded.
Float Function PlayTime()
  If pausedSince >= 0.0
    Return pausedSince - pausedTotal
  EndIf
  Return Utility.GetCurrentRealTime() - pausedTotal
EndFunction

; ---- animations ----

String[] Property AnimKeys Auto
{"model|group" (lower case) for every animation group of every converted model, by the model path its record carries
(ObScriptRecords.FinishAnimationLengths), with each group's length in seconds and whether it loops.}
Float[] Property AnimLengths Auto
Bool[] Property AnimLoops Auto

String[] Property AnimCueKeys Auto
{"model|group" (lower case) of each sound an animation group plays (Oblivion's "Sound: X" text keys in the model's
sequences, which Skyrim's sequences don't act on: ObScriptRecords.FinishAnimationLengths), a group's together.}
Float[] Property AnimCueTimes Auto
{The seconds into the group each is played at.}
Sound[] Property AnimCueSounds Auto

; Sounds waiting for their second of a group that is playing.
ObjectReference[] cueRefs
Sound[] cueSounds
Float[] cueAt

; A group has started on akRef: its sounds, each at its second (those at the start now). Sounds still waiting from an
; earlier group of akRef's are dropped, as the group they belonged to no longer plays.
Function QueueCues(ObjectReference akRef, String asKey)
  If !AnimCueKeys
    Return
  EndIf
  Int j = 0
  If cueRefs
    While j < cueRefs.Length
      If cueRefs[j] == akRef
        cueRefs[j] = None
      EndIf
      j += 1
    EndWhile
  EndIf
  Int c = AnimCueKeys.Find(asKey)
  If c < 0
    Return
  EndIf
  If !cueRefs
    cueRefs = new ObjectReference[16]
    cueSounds = new Sound[16]
    cueAt = new Float[16]
  EndIf
  Float now = PlayTime()
  Float soonest = -1.0
  While c < AnimCueKeys.Length && AnimCueKeys[c] == asKey
    If AnimCueSounds[c]
      If AnimCueTimes[c] <= 0.05
        AnimCueSounds[c].Play(akRef)
      Else
        j = cueRefs.Find(None)
        If j >= 0
          cueRefs[j] = akRef
          cueSounds[j] = AnimCueSounds[c]
          cueAt[j] = now + AnimCueTimes[c]
          If soonest < 0.0 || AnimCueTimes[c] < soonest
            soonest = AnimCueTimes[c]
          EndIf
        EndIf
      EndIf
    EndIf
    c += 1
  EndWhile
  If soonest >= 0.0
    WakeIn(soonest)
  EndIf
EndFunction

; Plays an Oblivion animation group (the model's controller sequence of that name) and remembers for how long it plays
; (its sequence's length; a loop until another group starts), for IsAnimPlaying. A group the model hasn't plays
; nothing, as in Oblivion. aiFlag is Oblivion's (0 start over, 1 start at the next loop, 2 the same).
Function PlayGroup(ObjectReference akRef, String asGroup, Int aiFlag = 0)
  If !akRef
    Return
  EndIf
  Init()
  Int k = -1
  Form base = akRef.GetBaseObject()
  If base && AnimKeys
    k = AnimKeys.Find(base.GetWorldModelPath() + "|" + asGroup)
  EndIf
  Int i = animRefs.Find(akRef)
  If k < 0
    If i >= 0
      animRefs[i] = None
    EndIf
    Return
  EndIf
  ; From its beginning, whatever the flag (Oblivion's 0 waits for the group now playing to end, 1 starts at once: both
  ; play the group from its start). Until 2026-11-15 flag 1 was passed on as "don't start over": the prison's secret
  ; wall, played "forward 1" by its switch, stood open at once instead of sinking the bed (from 1.1 s) and swinging
  ; (from 4.5 s) as its sequence has it (user: "the wall animation does not play either and just pops open").
  akRef.PlayGamebryoAnimation(asGroup, True)
  QueueCues(akRef, base.GetWorldModelPath() + "|" + asGroup)
  If i < 0
    i = animRefs.Find(None)
  EndIf
  If i < 0
    i = OldestAnim()
  EndIf
  animRefs[i] = akRef
  animGroups[i] = asGroup
  If AnimLoops[k]
    animEnds[i] = -1.0
  Else
    animEnds[i] = PlayTime() + AnimLengths[k]
  EndIf
EndFunction

; LoopGroup: the group played aiCount times over (a sequence that ends, started again as it ends; one that loops by
; itself, left to; IsAnimPlaying true for the count's length either way).
ObjectReference[] loopRefs
String[] loopGroups
Int[] loopLeft
Float[] loopNext
Float[] loopLength

Function LoopGroup(ObjectReference akRef, String asGroup, Int aiCount)
  PlayGroup(akRef, asGroup, 0)
  Int i = animRefs.Find(akRef)
  If i < 0 || aiCount <= 1
    Return
  EndIf
  Int k = AnimKeys.Find(akRef.GetBaseObject().GetWorldModelPath() + "|" + asGroup)
  Float seconds = AnimLengths[k]
  animEnds[i] = PlayTime() + seconds * aiCount
  If AnimLoops[k]
    Return
  EndIf
  If !loopRefs
    loopRefs = new ObjectReference[16]
    loopGroups = new String[16]
    loopLeft = new Int[16]
    loopNext = new Float[16]
    loopLength = new Float[16]
  EndIf
  Int j = loopRefs.Find(akRef)
  If j < 0
    j = loopLeft.Find(0)
  EndIf
  If j < 0
    j = 0
  EndIf
  loopRefs[j] = akRef
  loopGroups[j] = asGroup
  loopLeft[j] = aiCount - 1
  loopLength[j] = seconds
  loopNext[j] = PlayTime() + seconds
  WakeIn(seconds)
EndFunction

; The next OnUpdate (LoopGroup's replays, ForceSubtitle's lines that never started, a group's sounds): as soon as the
; soonest wants it.
Float wakeAt = -1.0

Function WakeIn(Float afSeconds)
  Float at = Utility.GetCurrentRealTime() + afSeconds
  If wakeAt < 0.0 || at < wakeAt
    wakeAt = at
    RegisterForSingleUpdate(afSeconds)
  EndIf
EndFunction

Event OnUpdate()
  wakeAt = -1.0
  Float soonest = -1.0
  ; ForceSubtitle lines that never started.
  If speakForced
    Float real = Utility.GetCurrentRealTime()
    Int s = 0
    While s < speakForced.Length
      If speakForced[s] && !speakTalking[s]
        If real - speakForcedAt[s] >= 2.0
          ReleaseSubtitle(s)
        ElseIf soonest < 0.0 || 2.0 - (real - speakForcedAt[s]) < soonest
          soonest = 2.0 - (real - speakForcedAt[s])
        EndIf
      EndIf
      s += 1
    EndWhile
  EndIf
  ; The playing groups' sounds whose second has come.
  If cueRefs
    Float cueNow = PlayTime()
    Int q = 0
    While q < cueRefs.Length
      If cueRefs[q]
        If cueNow >= cueAt[q] - 0.02
          If cueSounds[q] && cueRefs[q].Is3DLoaded()
            cueSounds[q].Play(cueRefs[q])
          EndIf
          cueRefs[q] = None
        ElseIf soonest < 0.0 || cueAt[q] - cueNow < soonest
          soonest = cueAt[q] - cueNow
        EndIf
      EndIf
      q += 1
    EndWhile
  EndIf
  If !loopRefs
    If soonest >= 0.0
      WakeIn(soonest)
    EndIf
    Return
  EndIf
  Float now = PlayTime()
  Int j = 0
  While j < loopRefs.Length
    If loopLeft[j] > 0 && loopRefs[j]
      If now >= loopNext[j] - 0.01
        loopRefs[j].PlayGamebryoAnimation(loopGroups[j], True)
        loopLeft[j] = loopLeft[j] - 1
        loopNext[j] = now + loopLength[j]
      EndIf
      If loopLeft[j] > 0 && (soonest < 0.0 || loopNext[j] - now < soonest)
        soonest = loopNext[j] - now
      EndIf
    EndIf
    j += 1
  EndWhile
  If soonest >= 0.0
    WakeIn(soonest)
  EndIf
EndEvent

; Reset3DState: the model back as it was first drawn (a pressed button up again, a played sequence's nodes back where
; they began): its 3D unloaded and loaded again. Oblivion runs no OnLoad for it; the converted scripts' OnLoad asks
; IsResetting and leaves that one out.
ObjectReference[] resetRefs
Float[] resetAt

Function Reset3D(ObjectReference akRef)
  If !akRef || !akRef.Is3DLoaded()
    Return
  EndIf
  Init()
  If !resetRefs
    resetRefs = new ObjectReference[8]
    resetAt = new Float[8]
  EndIf
  Int i = animRefs.Find(akRef)
  If i >= 0
    animRefs[i] = None
  EndIf
  Int k = resetRefs.Find(akRef)
  If k < 0
    k = resetRefs.Find(None)
  EndIf
  If k < 0
    k = 0
  EndIf
  resetRefs[k] = akRef
  resetAt[k] = Utility.GetCurrentRealTime()
  akRef.Disable(False)
  akRef.Enable(False)
EndFunction

; Whether akRef is loading again for Reset3DState (once: the OnLoad it brings).
Bool Function IsResetting(ObjectReference akRef)
  If !resetRefs
    Return False
  EndIf
  Int k = resetRefs.Find(akRef)
  If k < 0
    Return False
  EndIf
  resetRefs[k] = None
  Return Utility.GetCurrentRealTime() - resetAt[k] < 10.0
EndFunction

; The slot to reuse when all are taken: one whose animation is over, else the one ending first (a loop last).
Int Function OldestAnim()
  Float now = PlayTime()
  Int oldest = 0
  Int i = 0
  While i < animEnds.Length
    If animEnds[i] >= 0.0 && animEnds[i] <= now
      Return i
    EndIf
    If animEnds[oldest] < 0.0 || (animEnds[i] >= 0.0 && animEnds[i] < animEnds[oldest])
      oldest = i
    EndIf
    i += 1
  EndWhile
  Return oldest
EndFunction

; Whether the reference is playing an animation group (asGroup: that one), started by PlayGroup / LoopGroup.
Bool Function IsAnimPlaying(ObjectReference akRef, String asGroup = "")
  Init()
  Int i = animRefs.Find(akRef)
  If i < 0
    Return False
  EndIf
  If asGroup != "" && animGroups[i] != asGroup
    Return False
  EndIf
  If animEnds[i] < 0.0 || PlayTime() < animEnds[i]
    Return True
  EndIf
  animRefs[i] = None
  Return False
EndFunction

; ---- speech ----

; Oblivion's IsTalking is true from a line's first word to its last, and Say returns how long the line lasts. Every
; converted response tells as it starts and ends (TMObLine's fragments). Its length is its own: Oblivion's recording of
; it in the speaker's voice (TMObLine.Lengths, measured from the recordings; a line Oblivion didn't record, by its
; words at the rate Oblivion's recorded lines are spoken), and once said in that voice what it lasted.
VoiceType[] Property VoiceTypes Auto
{The voices TMObLine.Lengths are given in.}
ReferenceAlias Property Listener Auto
{OBDialogue's listener, whom the responses' "Run on Target" conditions ask of: the one a script's Say or SayTo speaks to.}

; A line a script says (Say, SayTo): Oblivion runs its result script as it is said, not when it is done (ValenDrethScript
; reads the taunt stage its line's result sets right after its Say). speakScripted: asked for by a script and not begun
; yet; speakPending: begun, its result still running (SayTimed waits for it).
; speakResulted: its result has run as it began, not to run again when it is done (kept here: a response's own script
; keeps nothing from its beginning to its end).
Bool[] speakScripted
Bool[] speakPending
Bool[] speakResulted
; The lines by their form IDs: the game gives a response's fragments another script object each time (seen in game
; 2026-11-13: the object kept at a line's beginning was not the one its end came with, and no longer named its
; response), so a kept line never equalled the one ending; and it ends a said line twice, as it begins it twice.
Int[] speakLineIds
Int[] learnedLineIds
; A line's length, taken as it begins (Say's value).
Float[] speakLengths
; How a speaker's line began: 0 not by a script, 1 by a script's Say and begun once, 2 and begun twice.
Int[] speakBegins
; The speaker's scripted line before, while its second beginning is still to come (0 none), and when it began.
Int[] speakBeforeIds
Float[] speakBeforeAt

Int Function SpeakerSlot(Actor akActor)
  Int i = speakers.Find(akActor)
  If i < 0
    i = speakers.Find(None)
  EndIf
  If i < 0
    ; All taken: one not talking now.
    i = speakTalking.Find(False)
  EndIf
  If i < 0
    i = 0
  EndIf
  Return i
EndFunction

Function LineStarted(Actor akSpeaker, TopicInfo akLine)
  LineBegins(akSpeaker, akLine)
EndFunction

; A response begins (TMObLine.Fragment_Begin). 0: said by the game (the player's dialogue, a conversation): its result
; script runs when it is done. 1: said by a script's Say: its result runs now (then ResultRan). 2: this saying of it
; has begun already (the game begins a said line twice: seen in game 2026-11-12, both within the same second).
Int Function LineBegins(Actor akSpeaker, TopicInfo akLine)
  If !akSpeaker
    Return 0
  EndIf
  Init()
  ; Asked first: from here to the line marked begun nothing is called, or the line's second beginning came in between
  ; and both were taken for the first (seen in game 2026-11-13).
  Int id = akLine.GetFormID()
  Float now = Utility.GetCurrentRealTime()
  Int i = SpeakerSlot(akSpeaker)
  ; For the log (when Papyrus logging is on): every beginning as it found the speaker's slot.
  Debug.Trace("TMOb begin " + akLine + " by " + akSpeaker + ": slot " + i + " same speaker " + (speakers[i] == akSpeaker) + ", line before " + speakLineIds[i] + ", begins " + speakBegins[i] + ", scripted " + speakScripted[i] + ", talking " + speakTalking[i] + ", " + (now - speakStarts[i]) + " s since")
  If speakers[i] != akSpeaker
    speakLooking[i] = False
    speakScripted[i] = False
    speakPending[i] = False
    speakBegins[i] = 0
    speakBeforeIds[i] = 0
    speakLineIds[i] = 0
  ElseIf speakLineIds[i] == id && speakBegins[i] == 1 && now - speakStarts[i] < 10.0
    ; A scripted line's second beginning, whatever came since: it came up to 5 seconds late, after the line had ended
    ; ("Yessir.") and after the speaker's script had asked for its next line, and was taken for that line, whose
    ; result then ran in its place (seen in game 2026-11-13: lines of the Emperor's party said twice, others skipped).
    speakBegins[i] = 2
    Return 2
  ElseIf speakBeforeIds[i] == id && now - speakBeforeAt[i] < 10.0
    ; The second beginning of the line before, come after the next line began.
    speakBeforeIds[i] = 0
    Return 2
  ElseIf speakLineIds[i] == id && !speakScripted[i] && (speakTalking[i] || now - speakStarts[i] < 3.0)
    ; (Or done already: a short line had ended before its second beginning came.)
    Return 2
  EndIf
  ; A script's Say that no line answered (none of the topic's held) is not this line's, begun long after.
  Bool scripted = speakScripted[i] && now - speakStarts[i] < 12.0
  speakScripted[i] = False
  speakPending[i] = scripted
  speakResulted[i] = scripted
  speakers[i] = akSpeaker
  speakLines[i] = akLine
  speakBeforeIds[i] = 0
  If speakBegins[i] == 1
    speakBeforeIds[i] = speakLineIds[i]
    speakBeforeAt[i] = speakStarts[i]
  EndIf
  speakLineIds[i] = id
  speakBegins[i] = scripted as Int
  speakStarts[i] = now
  speakTalking[i] = True
  ; Last: asking the speaker's voice lets the line's second beginning in, which must find the line begun.
  speakLengths[i] = 0.0
  speakLengths[i] = LineLength(akSpeaker, akLine)
  If scripted
    Return 1
  EndIf
  Return 0
EndFunction

; The result script of a line a script said has run.
Function ResultRan(Actor akSpeaker)
  Int i = speakers.Find(akSpeaker)
  If i >= 0
    speakPending[i] = False
  EndIf
EndFunction

; A response is done (TMObLine.Fragment_End): is its result script still to run? Not when it ran as the line began.
Bool Function ResultOwed(Actor akSpeaker, TopicInfo akLine)
  If !akSpeaker
    Return True
  EndIf
  Init()
  Int i = speakers.Find(akSpeaker)
  If i < 0 || speakLineIds[i] != akLine.GetFormID()
    Return True
  EndIf
  If speakResulted[i]
    Return False
  EndIf
  ; Owed once: the line's second end finds it run.
  speakResulted[i] = True
  Return True
EndFunction

Function LineEnded(Actor akSpeaker, TopicInfo akLine)
  If !akSpeaker
    Return
  EndIf
  Init()
  Int i = speakers.Find(akSpeaker)
  If i < 0 || speakLineIds[i] != akLine.GetFormID() || !speakTalking[i]
    Return
  EndIf
  speakTalking[i] = False
  ReleaseSubtitle(i)
  If speakLooking[i]
    speakLooking[i] = False
    akSpeaker.ClearLookAt()
  EndIf
  ; A walk-up greeting waits while its greeter is talking (its package's IsTalking condition): looked over again now,
  ; so it starts as the line ends rather than whenever the game next weighs the speaker's packages.
  If speakScripted[i]
    akSpeaker.EvaluatePackage()
  EndIf
  ; What a line without a recording of its own lasted in this voice.
  If Recorded(akSpeaker, akLine) <= 0.0
    Int k = learnedNext
    learnedLines[k] = akLine
    learnedLineIds[k] = akLine.GetFormID()
    learnedVoices[k] = akSpeaker.GetVoiceType()
    learnedLengths[k] = Utility.GetCurrentRealTime() - speakStarts[i]
    learnedNext = (k + 1) % learnedLines.Length
  EndIf
EndFunction

; The line's recording in the speaker's voice: its length; minus its estimate (by its words) when Oblivion recorded none.
Float Function Recorded(Actor akSpeaker, TopicInfo akLine)
  TMObLine line = akLine as TMObLine
  If !line || !line.Lengths || !VoiceTypes
    Return 0.0
  EndIf
  Int v = VoiceTypes.Find(akSpeaker.GetVoiceType())
  If v < 0 || v >= line.Lengths.Length
    Return 0.0
  EndIf
  Return line.Lengths[v]
EndFunction

Float Function LineLength(Actor akSpeaker, TopicInfo akLine)
  Float recorded = Recorded(akSpeaker, akLine)
  If recorded > 0.0
    Return recorded
  EndIf
  VoiceType voice = akSpeaker.GetVoiceType()
  Int k = 0
  While k < learnedLines.Length
    If learnedLineIds[k] == akLine.GetFormID() && learnedVoices[k] == voice
      Return learnedLengths[k]
    EndIf
    k += 1
  EndWhile
  Return -recorded
EndFunction

; Say / SayTo: SayTo's speaker looks at the one it speaks to while it speaks; with ForceSubtitle the line's subtitle
; shows whatever the player's setting (bGeneralSubtitles on until the last such line ends, or none starts).
Bool[] speakForced
Float[] speakForcedAt
Int forcedLines
Bool subtitlesWere

Function SayTopic(ObjectReference akSpeaker, Topic akTopic, Actor akTarget = None, Bool abForceSubtitle = False)
  If !akSpeaker || !akTopic
    Return
  EndIf
  Init()
  ; Whom the line's "Run on Target" conditions ask of: the one spoken to, the player where none is named (Valen Dreth's
  ; taunts are by the player's race). Left empty, the game said a response whose conditions do not hold and ran none of
  ; its fragments (seen in game 2026-11-12: "an Imperial in the Imperial Prison" to a Nord, over and over).
  If Listener
    Actor heard = akTarget
    If !heard
      heard = Game.GetPlayer()
    EndIf
    ; Only when it is another: forced again to the one it holds, the alias was empty as the line was chosen (seen in
    ; game 2026-11-13: the second taunt to a Nord was the Imperial's).
    If Listener.GetReference() != heard
      Listener.ForceRefTo(heard)
      Utility.Wait(0.05)
    EndIf
  EndIf
  Actor a = akSpeaker as Actor
  If a
    Int i = SpeakerSlot(a)
    ; The speaker's line before must be over as the game has it (its End fragment, LineEnded), not only by its
    ; recording's length: asked for the moment that length had passed, the game dropped the new line and the script
    ; waited its 10 seconds out (2026-11-13, the prison: 8 of 22 lines asked for never began, each the first asked
    ; after the same speaker's last; asked again they began within 0.6 s).
    ; Only about that moment: up to 1.5 s past the recording's length (4 s where the length isn't known). A line
    ; whose end never came (cut off by another) held the Emperor's "You... I've seen you" back 4.8 s, and the opening
    ; went on to its next stage meanwhile (seen in game 2026-11-13).
    Float askedAt = Utility.GetCurrentRealTime()
    Float waitUntil = askedAt + 4.0
    If speakers[i] == a && speakLengths[i] > 0.0 && speakStarts[i] + speakLengths[i] + 1.5 < waitUntil
      waitUntil = speakStarts[i] + speakLengths[i] + 1.5
    EndIf
    While speakers[i] == a && speakTalking[i] && Utility.GetCurrentRealTime() < waitUntil
      Utility.Wait(0.05)
    EndWhile
    If speakers[i] != a
      speakForced[i] = False
      speakLooking[i] = False
      speakBegins[i] = 0
      speakBeforeIds[i] = 0
      speakLineIds[i] = 0
    EndIf
    speakers[i] = a
    speakTalking[i] = False
    speakScripted[i] = True
    speakPending[i] = False
    speakStarts[i] = Utility.GetCurrentRealTime()
    If akTarget
      a.SetLookAt(akTarget)
      speakLooking[i] = True
    EndIf
    If abForceSubtitle && !speakForced[i]
      If forcedLines <= 0
        forcedLines = 0
        subtitlesWere = Utility.GetINIBool("bGeneralSubtitles:Interface")
        Utility.SetINIBool("bGeneralSubtitles:Interface", True)
      EndIf
      forcedLines += 1
      speakForced[i] = True
      speakForcedAt[i] = Utility.GetCurrentRealTime()
      WakeIn(2.0)
    EndIf
  EndIf
  Debug.Trace("TMOb ask " + akTopic + " by " + akSpeaker + " to " + akTarget)
  akSpeaker.Say(akTopic)
EndFunction

Function ReleaseSubtitle(Int i)
  If !speakForced[i]
    Return
  EndIf
  speakForced[i] = False
  forcedLines -= 1
  If forcedLines <= 0
    forcedLines = 0
    Utility.SetINIBool("bGeneralSubtitles:Interface", subtitlesWere)
  EndIf
EndFunction

; Say's value: how long the line it says lasts (0 when there is nothing to say). The line starts a moment after (its
; Begin fragment); this waits for it.
Float Function SayTimed(ObjectReference akSpeaker, Topic akTopic, Actor akTarget = None, Bool abForceSubtitle = False)
  Float asked = Utility.GetCurrentRealTime()
  SayTopic(akSpeaker, akTopic, akTarget, abForceSubtitle)
  Actor a = akSpeaker as Actor
  If !a || !akTopic
    Return 0.0
  EndIf
  ; The line begins a moment after (10 seconds given: a speaker's first line began over 3 seconds after it was asked
  ; for, was asked for again and its result ran twice); begun, its result
  ; script is waited for however long it takes (one re-evaluating the speaker's packages took over 4 seconds, the
  ; line was said again and its result ran twice: seen in game 2026-11-13).
  While True
    Int i = speakers.Find(a)
    ; Begun: its beginning took the asking's mark off. Not "talking now": a short line was over before its result
    ; script was, and counted as never begun (Baurus's "No sign of pursuit, sir.", seen in game 2026-11-14).
    Bool begun = i >= 0 && !speakScripted[i] && speakStarts[i] >= asked
    Float now = Utility.GetCurrentRealTime()
    If begun && (!speakPending[i] || now > asked + 20.0)
      ; For the log (when Papyrus logging is on): how long after it was asked for the line began, and its result ended.
      Debug.Trace("TMOb say " + akTopic + " by " + a + ": began after " + (speakStarts[i] - asked) + " s, returned after " + (now - asked) + " s, length " + speakLengths[i])
      Return speakLengths[i]
    EndIf
    If !begun && now > asked + 10.0
      Debug.Trace("TMOb say " + akTopic + " by " + a + ": not begun 10 s after it was asked for")
      If i >= 0
        speakScripted[i] = False
      EndIf
      Return 0.0
    EndIf
    Utility.Wait(0.025)
  EndWhile
  Return 0.0
EndFunction

Bool Function IsTalking(Actor akActor)
  If !akActor
    Return False
  EndIf
  If akActor.IsInDialogueWithPlayer()
    Return True
  EndIf
  Init()
  Int i = speakers.Find(akActor)
  Return i >= 0 && speakTalking[i]
EndFunction

; Seconds since the actor's last line began (a long time when none is known).
Float Function SinceSpoke(Actor akActor)
  If !akActor
    Return 1000.0
  EndIf
  Init()
  Int i = speakers.Find(akActor)
  If i < 0
    Return 1000.0
  EndIf
  Return Utility.GetCurrentRealTime() - speakStarts[i]
EndFunction

; ---- follows that end on arrival ----

; Oblivion's Follow and Accompany packages with a place end when the follower gets there. Skyrim's never complete:
; the converter gives each a "further from the place than this" condition, which drops it on arrival, and lists it
; here, both copies (the scripts' and the actors' own), with its place and that distance.
Package[] Property FollowPackages Auto
ObjectReference[] Property FollowPlaces Auto
Float[] Property FollowReach Auto

; akOldPackage is such a follow and akActor has arrived (a package changed for any other reason hasn't ended).
; The follow package the actor is running now, when it is one that ends on arrival and the actor is at its place.
; A package whose deed is done (TMObUse: the thing was activated), which Oblivion tells the actor's script of
; (OnPackageDone) though Skyrim's package of it never ends: kept until the actor's script asks (FollowPoll, each
; second), which then hears it as its package's end.
Actor[] doneActors
Package[] donePackages

Function PackageDone(Actor akActor, Package akPackage)
  If !akActor || !akPackage
    Return
  EndIf
  If !doneActors
    doneActors = new Actor[32]
    donePackages = new Package[32]
  EndIf
  Int i = doneActors.Find(akActor)
  If i < 0
    i = doneActors.Find(None)
  EndIf
  If i < 0
    i = 0
  EndIf
  doneActors[i] = akActor
  donePackages[i] = akPackage
EndFunction

Package Function FollowPoll(Actor akActor)
  If akActor && doneActors
    Int d = doneActors.Find(akActor)
    If d >= 0
      Package done = donePackages[d]
      doneActors[d] = None
      donePackages[d] = None
      If done
        Return done
      EndIf
    EndIf
  EndIf
  If !akActor || !FollowPackages
    Return None
  EndIf
  Package now = akActor.GetCurrentPackage()
  If !now
    Return None
  EndIf
  Int i = FollowPackages.Find(now)
  If i < 0 || !FollowPlaces[i] || akActor.GetDistance(FollowPlaces[i]) > FollowReach[i] + 48.0
    Return None
  EndIf
  Return now
EndFunction

Bool Function FollowArrived(Actor akActor, Package akOldPackage)
  If !akActor || !akOldPackage || !FollowPackages
    Return False
  EndIf
  Int i = FollowPackages.Find(akOldPackage)
  If i < 0 || !FollowPlaces[i]
    Return False
  EndIf
  Return akActor.GetDistance(FollowPlaces[i]) <= FollowReach[i] + 48.0
EndFunction

; ---- script packages ----

; Oblivion runs one script package per actor, ahead of its own packages, until it's removed or another is added.
Function AddScriptPackage(Actor akActor, Package akPackage)
  If !akActor || !akPackage || !SlotPackages || !PackageSlots
    Return
  EndIf
  RemoveScriptPackage(akActor, False)
  Int free = -1
  Int i = 0
  While i < SlotPackages.Length
    If SlotPackages[i] == akPackage && PackageSlots[i]
      ObjectReference holder = PackageSlots[i].GetReference()
      If !holder && free < 0
        free = i
      EndIf
    EndIf
    i += 1
  EndWhile
  If free < 0
    ; every alias with this package is taken: the one held longest goes to this actor
    free = SlotPackages.Find(akPackage)
    If free < 0
      Return
    EndIf
    Actor previous = PackageSlots[free].GetActorReference()
    PackageSlots[free].Clear()
    If previous
      previous.EvaluatePackage()
    EndIf
  EndIf
  PackageSlots[free].ForceRefTo(akActor)
  akActor.EvaluatePackage()
EndFunction

Function RemoveScriptPackage(Actor akActor, Bool abEvaluate = True)
  If !akActor || !PackageSlots
    Return
  EndIf
  Int i = 0
  While i < PackageSlots.Length
    If PackageSlots[i] && PackageSlots[i].GetReference() == akActor
      PackageSlots[i].Clear()
    EndIf
    i += 1
  EndWhile
  If abEvaluate
    akActor.EvaluatePackage()
  EndIf
EndFunction

Package Function ScriptPackage(Actor akActor)
  If !PackageSlots
    Return None
  EndIf
  Int i = 0
  While i < PackageSlots.Length
    If PackageSlots[i] && PackageSlots[i].GetReference() == akActor
      Return SlotPackages[i]
    EndIf
    i += 1
  EndWhile
  Return None
EndFunction

; Oblivion's package type of what the actor is doing (GetCurrentAIPackage).
Int Function PackageType(Actor akActor)
  If !akActor
    Return -1
  EndIf
  Package current = akActor.GetCurrentPackage()
  If current && Packages
    Int i = Packages.Find(current)
    If i >= 0
      Return PackageTypes[i]
    EndIf
  EndIf
  If akActor.IsPlayerTeammate()
    Return 1
  ElseIf akActor.GetSleepState() >= 3
    Return 4
  EndIf
  Return 5
EndFunction

; Oblivion's AI procedure (GetCurrentAIProcedure): 0 Travel, 2 Acquire, 4 Dialogue, 7 Wander, 8 Sleep, 10 Eat,
; 11 Follow, 12 Escort, 13 Combat, 16 Flee, 25 Cast spell, 29 Use item at, 31 Ambush wait.
Int Function Procedure(Actor akActor)
  If !akActor
    Return -1
  EndIf
  If akActor.IsInCombat()
    Return 13
  ElseIf akActor.IsInDialogueWithPlayer() || IsTalking(akActor)
    Return 4
  ElseIf akActor.GetSleepState() >= 3
    Return 8
  EndIf
  Int type = PackageType(akActor)
  If type == 0
    Return 2
  ElseIf type == 1 || type == 7
    Return 11
  ElseIf type == 2
    Return 12
  ElseIf type == 3
    Return 10
  ElseIf type == 4 || type == 6
    Return 0
  ElseIf type == 8
    Return 29
  ElseIf type == 9
    Return 31
  ElseIf type == 10
    Return 16
  ElseIf type == 11
    Return 25
  EndIf
  Return 7
EndFunction

; SetPackDuration: the current script package ends after this many game hours.
Actor packDurationActor
Function SetPackDuration(Actor akActor, Float afHours)
  If akActor && ScriptPackage(akActor)
    packDurationActor = akActor
    RegisterForSingleUpdateGameTime(afHours)
  EndIf
EndFunction

Event OnUpdateGameTime()
  If packDurationActor
    RemoveScriptPackage(packDurationActor)
    packDurationActor = None
  EndIf
EndEvent

; Wait / StopWaiting (CS wiki, read 2026-10-23): "wait" behaviour for that package, which "works only if PackageID is the
; actor's current package, and if the current package is a Follow or Accompany package": the actor stops where it is
; until StopWaiting resumes the package.
Function WaitOn(Actor akActor, Package akPackage, Bool abWait)
  If !akActor
    Return
  EndIf
  If !abWait
    akActor.SetDontMove(False)
    Return
  EndIf
  Int kind = PackageType(akActor)
  If (!akPackage || akActor.GetCurrentPackage() == akPackage) && (kind == 1 || kind == 7)
    akActor.SetDontMove(True)
  EndIf
EndFunction

; ---- crimes ----

; A crime (0 steal, 1 pickpocket, 2 trespass, 3 attack, 4 murder, 5 steal a horse): logged for GetCrimeKnown,
; flagged on the victim's factions (GetPCFactionSteal / Attack / Murder), counted in the misc stats, and told to
; whoever sees it (OnAlarm blocks, through the TMObAlarm event).
Function Crime(Int aiType, Actor akCriminal, Form akVictim)
  Init()
  crimeTypes[crimeNext] = aiType
  crimeCriminals[crimeNext] = akCriminal
  crimeVictims[crimeNext] = akVictim
  crimeNext = (crimeNext + 1) % crimeTypes.Length
  If akCriminal == PlayerRef
    If aiType == 0
      miscStats[28] = miscStats[28] + 1
    ElseIf aiType == 1
      miscStats[29] = miscStats[29] + 1
    ElseIf aiType == 2
      miscStats[30] = miscStats[30] + 1
    ElseIf aiType == 3
      miscStats[31] = miscStats[31] + 1
    ElseIf aiType == 4
      miscStats[32] = miscStats[32] + 1
    ElseIf aiType == 5
      miscStats[33] = miscStats[33] + 1
    EndIf
    Actor victim = akVictim as Actor
    If victim && CrimeFactions
      Int i = CrimeFactions.Length
      While i > 0
        i -= 1
        Faction f = CrimeFactions[i]
        If f && victim.IsInFaction(f)
          SetPCFactionCrime(f, aiType, 1)
        EndIf
      EndWhile
    EndIf
  EndIf
  Int handle = ModEvent.Create("TMObAlarm")
  If handle
    ModEvent.PushInt(handle, aiType)
    ModEvent.PushForm(handle, akCriminal)
    ModEvent.PushForm(handle, akVictim)
    ModEvent.Send(handle)
  EndIf
EndFunction

; Whether a crime of this kind by the criminal against the victim (a reference, or the owner's base for thefts) is known.
Bool Function CrimeKnown(Int aiType, Actor akCriminal, ObjectReference akVictim)
  Init()
  Form victimBase = None
  If akVictim
    victimBase = akVictim.GetBaseObject()
  EndIf
  Int i = 0
  While i < crimeTypes.Length
    If crimeCriminals[i] && crimeTypes[i] == aiType && crimeCriminals[i] == akCriminal
      If !akVictim || crimeVictims[i] == akVictim || crimeVictims[i] == victimBase
        Return True
      EndIf
    EndIf
    i += 1
  EndWhile
  Return False
EndFunction

; aiKind: 0 steal, 3 attack, 4 murder (GetPCFactionSteal / Attack / Murder).
Int Function PCFactionCrime(Faction akFaction, Int aiKind)
  Init()
  Int i = FactionIndex(akFaction)
  If i < 0
    Return 0
  EndIf
  Return Math.LogicalAnd(Math.RightShift(factionCrimes[i], aiKind), 1)
EndFunction

Function SetPCFactionCrime(Faction akFaction, Int aiKind, Int aiValue)
  Init()
  Int i = FactionIndex(akFaction)
  If i < 0
    Return
  EndIf
  Int bit = Math.LeftShift(1, aiKind)
  If aiValue != 0
    factionCrimes[i] = Math.LogicalOr(factionCrimes[i], bit)
  Else
    factionCrimes[i] = Math.LogicalAnd(factionCrimes[i], Math.LogicalNot(bit))
  EndIf
EndFunction

Int Function FactionIndex(Faction akFaction)
  If !akFaction || !CrimeFactions
    Return -1
  EndIf
  Int i = CrimeFactions.Find(akFaction)
  If i < 0 || i >= factionCrimes.Length
    Return -1
  EndIf
  Return i
EndFunction

Int Function CrimeGold(Actor akActor)
  If !akActor || akActor == PlayerRef
    Return PlayerBounty()
  EndIf
  Init()
  Int i = bountyActors.Find(akActor)
  If i < 0
    Return 0
  EndIf
  Return bountyGold[i]
EndFunction

Function SetCrimeGold(Actor akActor, Int aiGold)
  If aiGold < 0
    aiGold = 0
  EndIf
  If !akActor || akActor == PlayerRef
    SetPlayerBounty(aiGold)
    PCCrimeGold.SetValueInt(aiGold)
    If aiGold > miscStats[4]
      miscStats[4] = aiGold
    EndIf
    Return
  EndIf
  ; An NPC with a bounty is a criminal the guards attack on sight.
  Init()
  Int i = bountyActors.Find(akActor)
  If i < 0
    i = bountyActors.Find(None)
  EndIf
  If i < 0
    i = 0
  EndIf
  bountyActors[i] = akActor
  bountyGold[i] = aiGold
  akActor.SetAttackActorOnSight(aiGold > 0)
EndFunction

; The player's bounty: Oblivion keeps one; Skyrim keeps it in each land's crime faction (its guards arrest through it),
; so it is their sum, kept in TMOB_PCCrimeGold too.
Int Function PlayerBounty()
  Int gold = 0
  If CyrodiilCrime
    gold += CyrodiilCrime.GetCrimeGold()
  EndIf
  If IslesCrime
    gold += IslesCrime.GetCrimeGold()
  EndIf
  If !CyrodiilCrime && !IslesCrime
    Return PCCrimeGold.GetValueInt()
  EndIf
  If PCCrimeGold.GetValueInt() != gold
    PCCrimeGold.SetValueInt(gold)
  EndIf
  Return gold
EndFunction

; A script setting the player's bounty sets the whole of it in the land the player is in (the other land's is cleared:
; Oblivion has one bounty).
Function SetPlayerBounty(Int aiGold)
  Faction here = CyrodiilCrime
  Faction other = IslesCrime
  If IslesCrime && PCInSEWorld && PCInSEWorld.GetValueInt() != 0
    here = IslesCrime
    other = CyrodiilCrime
  EndIf
  If other
    other.SetCrimeGoldViolent(0)
    other.SetCrimeGold(0)
  EndIf
  If here
    here.SetCrimeGoldViolent(0)
    here.SetCrimeGold(aiGold)
  EndIf
EndFunction

; Whether the player is in an Oblivion jail: in a jail cell, near its prison marker (where Skyrim puts the arrested
; player). TMObPlayer asks every few seconds; IsPlayerInJail reads TMOB_PCInJail.
Function CheckJail()
  SyncCityBounty()
  PlayerBounty()
  If !PCInJail || !JailMarkers
    Return
  EndIf
  Bool jailed = False
  Cell here = PlayerRef.GetParentCell()
  Int i = JailMarkers.GetSize()
  While i > 0 && !jailed && here
    i -= 1
    ObjectReference m = JailMarkers.GetAt(i) as ObjectReference
    If m && m.GetParentCell() == here && PlayerRef.GetDistance(m) < 512.0
      jailed = True
    EndIf
  EndWhile
  If jailed != (PCInJail.GetValueInt() != 0)
    PCInJail.SetValueInt(jailed as Int)
  EndIf
EndFunction

; Oblivion's one bounty across Cyrodiil's crime faction and its cities' guards' (each city's guards arrest through their
; own, for its jail): whatever changed in any of them since the last look is made so in all. A crime adds to one, paying
; or serving clears one: the change is added to the bounty, and the bounty set in every one. Non-violent and violent
; gold are kept apart, as Skyrim keeps them.
Function SyncCityBounty()
  If !CyrodiilCrime || !CityCrimes
    Return
  EndIf
  Int dNon = CyrodiilCrime.GetCrimeGoldNonViolent() - syncedNonViolent
  Int dVio = CyrodiilCrime.GetCrimeGoldViolent() - syncedViolent
  Int i = CityCrimes.Length
  While i > 0
    i -= 1
    If CityCrimes[i]
      dNon += CityCrimes[i].GetCrimeGoldNonViolent() - syncedNonViolent
      dVio += CityCrimes[i].GetCrimeGoldViolent() - syncedViolent
    EndIf
  EndWhile
  If dNon == 0 && dVio == 0
    Return
  EndIf
  syncedNonViolent += dNon
  syncedViolent += dVio
  If syncedNonViolent < 0
    syncedNonViolent = 0
  EndIf
  If syncedViolent < 0
    syncedViolent = 0
  EndIf
  CyrodiilCrime.SetCrimeGold(syncedNonViolent)
  CyrodiilCrime.SetCrimeGoldViolent(syncedViolent)
  i = CityCrimes.Length
  While i > 0
    i -= 1
    If CityCrimes[i]
      CityCrimes[i].SetCrimeGold(syncedNonViolent)
      CityCrimes[i].SetCrimeGoldViolent(syncedViolent)
    EndIf
  EndWhile
EndFunction

Function ModCrimeGold(Actor akActor, Int aiGold)
  SetCrimeGold(akActor, CrimeGold(akActor) + aiGold)
EndFunction

; Whether akWitness sees a crime: it's the victim, it detects the criminal, or it can see the victim.
Bool Function Witnessed(Actor akWitness, Form akCriminal, Form akVictim) Global
  If !akWitness || akWitness.IsDead() || akWitness == akCriminal
    Return False
  EndIf
  If akWitness == akVictim || akWitness.GetActorBase() == akVictim
    Return True
  EndIf
  Actor criminal = akCriminal as Actor
  If criminal && criminal.IsDetectedBy(akWitness)
    Return True
  EndIf
  ObjectReference victim = akVictim as ObjectReference
  Return victim && akWitness.HasLOS(victim)
EndFunction

; ---- references ----

Function SetDestroyed(ObjectReference akRef, Bool abDestroyed)
  If !akRef
    Return
  EndIf
  akRef.SetDestroyed(abDestroyed)
  If abDestroyed
    DestroyedRefs.AddForm(akRef)
  Else
    DestroyedRefs.RemoveAddedForm(akRef)
  EndIf
EndFunction

Bool Function IsDestroyed(ObjectReference akRef)
  Return akRef && DestroyedRefs.HasForm(akRef)
EndFunction

Function SetRestrained(Actor akActor, Bool abRestrained)
  If !akActor
    Return
  EndIf
  akActor.SetRestrained(abRestrained)
  If abRestrained
    RestrainedActors.AddForm(akActor)
  Else
    RestrainedActors.RemoveAddedForm(akActor)
  EndIf
EndFunction

Bool Function IsRestrained(Actor akActor)
  Return akActor && RestrainedActors.HasForm(akActor)
EndFunction

Bool Function TalkedToPC(Actor akActor)
  Return akActor && TalkedToPC.HasForm(akActor)
EndFunction

Function SetNoRumors(Actor akActor, Bool abNone)
  If !akActor
    Return
  EndIf
  If abNone
    NoRumors.AddForm(akActor)
  Else
    NoRumors.RemoveAddedForm(akActor)
  EndIf
EndFunction

; ---- misc stats ----

; Oblivion's misc stats (GetPCMiscStat): Skyrim's own count where it keeps one, plus what scripts and the crimes
; tracked here add.
Int Function MiscStat(Int aiStat)
  Init()
  If aiStat < 0 || aiStat >= miscStats.Length
    Return 0
  EndIf
  String name = SkyrimStat(aiStat)
  Int value = miscStats[aiStat]
  If name != ""
    value += Game.QueryStat(name)
  EndIf
  Return value
EndFunction

Function ModMiscStat(Int aiStat, Int aiBy)
  Init()
  If aiStat >= 0 && aiStat < miscStats.Length
    miscStats[aiStat] = miscStats[aiStat] + aiBy
  EndIf
EndFunction

String Function SkyrimStat(Int aiStat) Global
  If aiStat == 0
    Return "Days Jailed"
  ElseIf aiStat == 1
    Return "Days Passed"
  ElseIf aiStat == 2
    Return "Skill Increases"
  ElseIf aiStat == 3
    Return "Training Sessions"
  ElseIf aiStat == 5
    Return "Creatures Killed"
  ElseIf aiStat == 6
    Return "People Killed"
  ElseIf aiStat == 7
    Return "Locations Discovered"
  ElseIf aiStat == 8
    Return "Locks Picked"
  ElseIf aiStat == 9
    Return "Lockpicks Broken"
  ElseIf aiStat == 10
    Return "Souls Trapped"
  ElseIf aiStat == 11
    Return "Ingredients Eaten"
  ElseIf aiStat == 12
    Return "Potions Mixed"
  ElseIf aiStat == 14
    Return "Horses Owned"
  ElseIf aiStat == 15
    Return "Houses Owned"
  ElseIf aiStat == 16
    Return "Stores Invested In"
  ElseIf aiStat == 17
    Return "Books Read"
  ElseIf aiStat == 18
    Return "Skill Books Read"
  ElseIf aiStat == 20
    Return "Hours Slept"
  ElseIf aiStat == 21
    Return "Hours Waiting"
  ElseIf aiStat == 22
    Return "Days as a Vampire"
  ElseIf aiStat == 24
    Return "Necks Bitten"
  ElseIf aiStat == 26
    Return "Diseases Contracted"
  EndIf
  Return ""
EndFunction

Bool Function IsPCAMurderer()
  Return MiscStat(32) + Game.QueryStat("Murders") > 0
EndFunction

; ---- vampires ----
; Oblivion's vampirism is its own records and script: the Vampirism25/50/75/100 abilities (VAMP 25..100, Sun Damage
; 0/1/4/8, Weakness to Fire, Resist Disease/Paralysis/Normal Weapons), the Att/Skills abilities and the four powers,
; swapped by VampireScript (the Vampire quest, translated) on PCVampire: the disease's three days and a sleep, +25%
; a day without feeding at the next sleep or wait, back to 25% on feeding. The VAMP effect runs TMObVampirism, which
; keeps the stage here (Oblivion's "vampirism" actor value, GetAV Vampirism) and gives the player Skyrim's vampire
; look (the race's vampire variant; its Vampire keyword is what GetVampire and the dialogue test). Oblivion's vampire
; people (races Vampire / VampireRace) carry the Vampire keyword from the converter (ObContent).

Float Property PCVampirism = 0.0 Auto Hidden
{The VAMP magnitude on the player (25, 50, 75, 100), 0 when not a vampire.}
Race Property VampireCureRace Auto Hidden
{The player's race before the vampire look, given back on the cure.}

Bool Function IsVampire(Actor akActor) Global
  If !akActor
    Return False
  EndIf
  Keyword vampire = Game.GetFormFromFile(0x000A82BB, "Skyrim.esm") as Keyword
  Return (vampire && akActor.HasKeyword(vampire)) || StringUtil.Find(akActor.GetRace().GetName(), "Vampire") >= 0
EndFunction

; GetAV Vampirism: the player's stage; one of Oblivion's vampire people is a full vampire.
Float Function VampirismOf(Actor akActor) Global
  If !akActor
    Return 0.0
  EndIf
  If akActor == Game.GetPlayer()
    TMObState st = Quest.GetQuest("TMObState") as TMObState
    If st
      Return st.PCVampirism
    EndIf
    Return 0.0
  EndIf
  If IsVampire(akActor)
    Return 100.0
  EndIf
  Return 0.0
EndFunction

; The player's vampirism stage (TMObVampirism): the look comes with the first stage, goes with the cure (0).
; Oblivion's VAMP effect only changes the face (UESP Oblivion:Vampirism); Skyrim's face for it is its vampire race
; of the player's race (<Race>RaceVampire, as its own PlayerVampireQuest.VampireChange swaps), whose Vampire keyword
; the player then carries. A race without a vampire variant keeps its look.
Function SetVampirism(Actor akActor, Float afLevel)
  If akActor != PlayerRef
    Return
  EndIf
  PCVampirism = afLevel
  Keyword vampire = Game.GetFormFromFile(0x000A82BB, "Skyrim.esm") as Keyword
  Race now = akActor.GetRace()
  If afLevel > 0.0
    If now && vampire && !now.HasKeyword(vampire)
      Race look = Race.GetRace(RaceEditorId(now) + "Vampire")
      If look
        VampireCureRace = now
        akActor.SetRace(look)
      EndIf
    EndIf
  ElseIf VampireCureRace
    akActor.SetRace(VampireCureRace)
    VampireCureRace = None
  EndIf
EndFunction

; A Vampirism ability's effect ended (TMObVampirism): the stage changed (the next ability is on by now) or the cure
; took the last one off (the Vampire Cure quest's results remove them; PCVampire -1).
Function VampirismEnded(Actor akActor, MagicEffect akEffect, Perk akFeedPerk)
  If akActor != PlayerRef
    Return
  EndIf
  Utility.Wait(0.5)
  If akEffect && akActor.HasMagicEffect(akEffect)
    Return
  EndIf
  SetVampirism(akActor, 0.0)
  If akFeedPerk && akActor.HasPerk(akFeedPerk)
    akActor.RemovePerk(akFeedPerk)
  EndIf
EndFunction

; Skyrim's vanilla races are named as their EditorIDs without the space ("Dark Elf" is DarkElfRace).
String Function RaceEditorId(Race akRace) Global
  String name = akRace.GetName()
  String id = ""
  Int i = 0
  Int n = StringUtil.GetLength(name)
  While i < n
    String ch = StringUtil.GetNthChar(name, i)
    If ch != " "
      id += ch
    EndIf
    i += 1
  EndWhile
  Return id + "Race"
EndFunction

; Feeding on a sleeper (the Feed choice of OBVampireFeed, Skyrim's VampireFeedBeds perk with TMObVampireFeed; or a
; script's VampireFeed): Skyrim's feeding animation; the victim is unharmed and keeps sleeping. Seen by anyone else,
; it is assault (UESP Oblivion:Vampirism: "reported as a crime with a bounty of 40 gold", iCrimeGoldAttack) and
; being caught wakes the victim.
Function VampireFeed(Actor akVampire, Actor akVictim)
  If !akVampire || !akVictim
    Return
  EndIf
  akVampire.StartVampireFeed(akVictim)
  ownFeeds += 1
  If akVampire != PlayerRef
    Return
  EndIf
  Utility.Wait(3.0)
  Actor witness = FeedWitness(akVictim)
  If witness
    Crime(3, PlayerRef, akVictim)
    akVictim.SendAssaultAlarm()
  EndIf
EndFunction

; Someone awake, alive and not the victim who detects the player.
Actor Function FeedWitness(Actor akVictim)
  Cell here = PlayerRef.GetParentCell()
  If !here
    Return None
  EndIf
  Int i = here.GetNumRefs(62)
  While i > 0
    i -= 1
    Actor other = here.GetNthRef(i, 62) as Actor
    If other && other != PlayerRef && other != akVictim && !other.IsDead() && other.GetSleepState() == 0 && PlayerRef.IsDetectedBy(other)
      Return other
    EndIf
  EndWhile
  Return None
EndFunction

; Whether the player fed since this was last asked (Oblivion's HasVampireFed, which vampirism's progress resets).
Bool Function HasVampireFed()
  Int fed = Game.QueryStat("Necks Bitten") + ownFeeds
  If fedSeen < 0
    fedSeen = fed
  EndIf
  If fed > fedSeen
    fedSeen = fed
    Return True
  EndIf
  Return False
EndFunction

; ---- Oblivion gates ----

; The gate the player went through (its script's OnActivate), and where the player stood then.
Function EnteringGate(ObjectReference akGate)
  CurrentGate = akGate
  gateX = PlayerRef.GetPositionX()
  gateY = PlayerRef.GetPositionY()
  gateZ = PlayerRef.GetPositionZ()
  gateAngle = PlayerRef.GetAngleZ()
EndFunction

; CloseCurrentOblivionGate: the gate the player came through closes, and the player is back in front of it.
Function CloseCurrentGate(Bool abCountIt = True)
  If !CurrentGate
    Return
  EndIf
  Game.FadeOutGame(True, False, 0.0, 1.0)
  SetDestroyed(CurrentGate, True)
  If abCountIt
    ModMiscStat(13, 1)
  EndIf
  PlayerRef.MoveTo(CurrentGate)
  PlayerRef.SetPosition(gateX, gateY, gateZ)
  PlayerRef.SetAngle(0.0, 0.0, gateAngle + 180.0)
  Game.FadeOutGame(False, False, 0.5, 2.0)
  CurrentGate = None
EndFunction

Function ForceCloseGate(ObjectReference akGate)
  SetDestroyed(akGate, True)
  If CurrentGate == akGate
    CurrentGate = None
  EndIf
EndFunction

; ---- birthsigns ----

Function ShowBirthsignMenu()
  If !BirthsignPage1 || !BirthsignSpells
    Return
  EndIf
  Int perPage = 9
  If !BirthsignPage2
    perPage = BirthsignSpells.Length
  EndIf
  Int choice = -1
  Int page = 0
  While choice < 0
    If page == 0
      Int b = BirthsignPage1.Show()
      If BirthsignPage2 && b == perPage
        page = 1
      Else
        choice = b
      EndIf
    Else
      Int b = BirthsignPage2.Show()
      If b >= BirthsignSpells.Length - perPage
        page = 0
      Else
        choice = perPage + b
      EndIf
    EndIf
  EndWhile
  If choice >= BirthsignSpells.Length
    Return
  EndIf
  If ChosenBirthsign >= 0 && ChosenBirthsign < BirthsignSpells.Length
    SpellsOf(ChosenBirthsign, False)
  EndIf
  ChosenBirthsign = choice
  SpellsOf(choice, True)
EndFunction

Function SpellsOf(Int aiSign, Bool abAdd)
  FormList list = BirthsignSpells[aiSign]
  If !list
    Return
  EndIf
  Int i = list.GetSize()
  While i > 0
    i -= 1
    Spell s = list.GetAt(i) as Spell
    If s && abAdd
      PlayerRef.AddSpell(s, False)
    ElseIf s
      PlayerRef.RemoveSpell(s)
    EndIf
  EndWhile
EndFunction

Bool Function IsPlayerBirthsign(Int aiSign)
  Return aiSign >= 0 && ChosenBirthsign == aiSign
EndFunction

; ShowEnchantment: Skyrim enchants at an enchanter's table; one appears beside the player for the menu.
Function ShowEnchanting()
  Form enchantingTable = Game.GetFormFromFile(0x0BAD0D, "Skyrim.esm")
  If !enchantingTable
    Return
  EndIf
  ObjectReference table = PlayerRef.PlaceAtMe(enchantingTable)
  table.Activate(PlayerRef)
  Utility.Wait(1.0)
  While UI.IsMenuOpen("Crafting Menu") || PlayerRef.GetSitState() != 0
    Utility.Wait(0.5)
  EndWhile
  table.Disable()
  table.Delete()
EndFunction

; ---- sleep ----

; WakeUpPC: the sleep ends now. Skyrim has no call for it; the sleep menu is closed as a key press would.
Function WakeUpPC()
  If PCSleeping || UI.IsMenuOpen("Sleep/Wait Menu")
    Input.TapKey(1)
    PCSleeping = False
  EndIf
EndFunction

; ---- the player's controls ----

; Held off by an Oblivion script's DisablePlayerControls (until its EnablePlayerControls or a conversation).
Bool controlsHeld

; DisablePlayerControls: off, as Oblivion has it, until EnablePlayerControls or until the player is in a conversation.
; Oblivion's own scripts rely on the second: the Imperial prison's guards hold the prisoner still at CharacterGen stage
; 14 ("player stay put!") and nothing of Oblivion's turns the controls back on before the prisoner must follow the
; Emperor; the conversation the Emperor opens at stage 17 does. In Skyrim controls stay off through a conversation, and
; the prisoner stood at the window for the rest of the escape (runs r98, r99, 2026-11-16).
Function DisableControls() Global
  Game.DisablePlayerControls()
  TMObState obState = Quest.GetQuest("TMObState") as TMObState
  If obState
    obState.HoldControls()
  EndIf
EndFunction

Function EnableControls() Global
  Game.EnablePlayerControls()
  TMObState obState = Quest.GetQuest("TMObState") as TMObState
  If obState
    obState.ReleaseControls()
  EndIf
EndFunction

Function HoldControls()
  controlsHeld = True
  RegisterForMenu("Dialogue Menu")
EndFunction

Function ReleaseControls()
  controlsHeld = False
  UnregisterForMenu("Dialogue Menu")
EndFunction

Event OnMenuOpen(String asMenuName)
  If asMenuName == "Dialogue Menu" && controlsHeld
    Game.EnablePlayerControls()
    ReleaseControls()
    Debug.Trace("[TamrielMerge] the player's controls on again: a conversation began (Oblivion's DisablePlayerControls lasts until one)")
  EndIf
EndEvent

; ---- without state ----

; SetActorRefraction: the shimmer of an invisible body (Skyrim's invisibility shader).
Function Refraction(Actor akActor, Bool abOn) Global
  EffectShader shimmer = Game.GetFormFromFile(0x02DF92, "Skyrim.esm") as EffectShader
  If !akActor || !shimmer
    Return
  EndIf
  If abOn
    shimmer.Play(akActor)
  Else
    shimmer.Stop(akActor)
  EndIf
EndFunction


Int Function FormID(Form akForm) Global
  If !akForm
    Return 0
  EndIf
  Return akForm.GetFormID()
EndFunction

; Oblivion disposition (0-100) between two people: towards the player Skyrim.Disposition's Variable09 (disposition
; minus 50), between others their relationship rank (-4..4, 12 points a rank).
Int Function Disposition(Actor akFrom, Actor akTo) Global
  If !akFrom || !akTo
    Return 50
  EndIf
  If akTo == Game.GetPlayer()
    Return (50.0 + akFrom.GetActorValue("Variable09")) as Int
  EndIf
  Return 50 + akFrom.GetRelationshipRank(akTo) * 12
EndFunction

Function ModDisposition(Actor akFrom, Actor akTo, Float afBy) Global
  If !akFrom || !akTo
    Return
  EndIf
  If akTo == Game.GetPlayer()
    Float now = akFrom.GetActorValue("Variable09")
    Float target = now + afBy
    If target < -50.0
      target = -50.0
    ElseIf target > 50.0
      target = 50.0
    EndIf
    akFrom.ModActorValue("Variable09", target - now)
    Return
  EndIf
  Float wanted = (Disposition(akFrom, akTo) + afBy - 50.0) / 12.0
  Int rank = Math.Floor(wanted + 0.5)
  If rank < -4
    rank = -4
  ElseIf rank > 4
    rank = 4
  EndIf
  akFrom.SetRelationshipRank(akTo, rank)
EndFunction

; Oblivion's creatures are converted to Skyrim actors of non-playable races.
Bool Function IsCreature(Actor akActor) Global
  Return akActor && !akActor.GetRace().IsPlayable()
EndFunction

; GetKnockedState (CS wiki): 1 knocked out (unconscious, paralysed, or out of fatigue), 2 knocked down, else 0. Down is
; Skyrim's knock state (its classic GetKnockStateEnum, through TamrielMergeNative: 0 normal, else ragdolled or
; getting up).
Int Function KnockedState(Actor akActor) Global
  If !akActor
    Return 0
  EndIf
  If akActor.IsUnconscious() || akActor.IsBleedingOut() || akActor.GetActorValue("Paralysis") > 0 || akActor.GetActorValue("Stamina") <= 0
    Return 1
  EndIf
  If TMNative.Condition("GetKnockStateEnum", akActor) != 0.0
    Return 2
  EndIf
  Return 0
EndFunction

; GetWeaponAnimType (Oblivion: 0 hand to hand, 1 one hand, 2 two hand, 3 staff, 4 bow) from Skyrim's own (its classic
; GetWeaponAnimType: the equipped weapon's animation type, 0 hand to hand, 1-4 one-handed, 5-6 two-handed, 7 bow,
; 8 staff, 12 crossbow; SkyrimSE.exe 1.7.104).
Int Function WeaponAnimType(Actor akActor) Global
  If !akActor
    Return 0
  EndIf
  Int t = TMNative.Condition("GetWeaponAnimType", akActor) as Int
  If t >= 1 && t <= 4
    Return 1
  ElseIf t == 5 || t == 6
    Return 2
  ElseIf t == 8
    Return 3
  ElseIf t == 7 || t == 12
    Return 4
  EndIf
  Return 0
EndFunction

Bool Function IsIdlePlaying(Actor akActor) Global
  Return akActor && (akActor.GetAnimationVariableBool("bAnimationDriven") || akActor.GetAnimationVariableBool("bIsSynced"))
EndFunction

; Total value of the clothing (not armour) the actor wears.
Int Function ClothingValue(Actor akActor) Global
  If !akActor
    Return 0
  EndIf
  Int total = 0
  Int slot = 30
  Form[] seen = new Form[32]
  Int n = 0
  While slot < 62
    Armor worn = akActor.GetWornForm(Armor.GetMaskForSlot(slot)) as Armor
    If worn && worn.GetWeightClass() == 2 && seen.Find(worn) < 0
      seen[n] = worn
      n += 1
      total += worn.GetGoldValue()
    EndIf
    slot += 1
  EndWhile
  Return total
EndFunction

; SetRigidBodyMass: 0 pins an object in place (keyframed), more lets its physics move it again. Skyrim actors' mass
; can't be set; their ragdoll is left as it is.
Function SetMass(ObjectReference akRef, Float afMass) Global
  If !akRef || akRef as Actor
    Return
  EndIf
  If afMass <= 0.0
    akRef.SetMotionType(akRef.Motion_Keyframed, True)
  Else
    akRef.SetMotionType(akRef.Motion_Dynamic, True)
  EndIf
EndFunction

; Whether anyone in the actor's cell detects it (IsActorDetected).
Bool Function IsDetectedByAnyone(Actor akActor) Global
  If !akActor || !akActor.GetParentCell()
    Return False
  EndIf
  Cell here = akActor.GetParentCell()
  Int i = here.GetNumRefs(62)
  While i > 0
    i -= 1
    Actor other = here.GetNthRef(i, 62) as Actor
    If other && other != akActor && !other.IsDead() && akActor.IsDetectedBy(other)
      Return True
    EndIf
  EndWhile
  Return False
EndFunction

Function SetFullName(Actor akActor, String asName) Global
  If akActor
    akActor.GetActorBase().SetName(asName)
    akActor.SetDisplayName(asName, True)
  EndIf
EndFunction

Bool Function ControlsDisabled() Global
  Return !(Game.IsMovementControlsEnabled() && Game.IsFightingControlsEnabled() && Game.IsLookingControlsEnabled() && Game.IsSneakingControlsEnabled() && Game.IsActivateControlsEnabled())
EndFunction

; EssentialDeathReload: the story can't go on; as in Oblivion, the message, then the last save is loaded (Skyrim does
; that when the player dies).
Function EssentialDeathReload(String asMessage) Global
  Debug.MessageBox(asMessage)
  Utility.Wait(0.5)
  Actor player = Game.GetPlayer()
  player.GetActorBase().SetEssential(False)
  player.Kill()
EndFunction

; RemoveMe: the scripted item leaves the container it's in (into another, if given).
Function RemoveMe(ObjectReference akItem, ObjectReference akContainer, ObjectReference akTo) Global
  If !akItem
    Return
  EndIf
  If akContainer
    akContainer.RemoveItem(akItem.GetBaseObject(), 1, True, akTo)
  Else
    akItem.Disable()
    akItem.Delete()
  EndIf
EndFunction

; CreateFullActorCopy: a copy of the actor (of its base) wearing and carrying what it does.
Actor Function FullActorCopy(Actor akActor) Global
  If !akActor
    Return None
  EndIf
  Actor copy = akActor.PlaceActorAtMe(akActor.GetActorBase())
  If !copy
    Return None
  EndIf
  copy.RemoveAllItems()
  Int i = akActor.GetNumItems()
  While i > 0
    i -= 1
    Form item = akActor.GetNthForm(i)
    If item
      copy.AddItem(item, akActor.GetItemCount(item), True)
    EndIf
  EndWhile
  Int slot = 30
  While slot < 62
    Form worn = akActor.GetWornForm(Armor.GetMaskForSlot(slot))
    If worn
      copy.EquipItem(worn, False, True)
    EndIf
    slot += 1
  EndWhile
  If akActor.GetEquippedWeapon()
    copy.EquipItem(akActor.GetEquippedWeapon(), False, True)
  EndIf
  Return copy
EndFunction

; PositionCell / PositionWorld: to coordinates in another cell or worldspace, by way of a marker there.
Function PositionAt(ObjectReference akRef, ObjectReference akAnchor, Float afX, Float afY, Float afZ, Float afAngleZ) Global
  If !akRef || !akAnchor
    Return
  EndIf
  akRef.MoveTo(akAnchor)
  akRef.SetPosition(afX, afY, afZ)
  akRef.SetAngle(0.0, 0.0, afAngleZ)
EndFunction

; GetEquippedObject (OBSE): by Oblivion's biped slot numbers (0 head, 1 hair, 2 upper body, 3 lower body, 4 hand,
; 5 foot, 6 right ring, 7 left ring, 8 amulet, 13 shield, 14 torch; 16 the weapon, 17 the ammunition), on the Skyrim
; slots the converted items wear them in (ObWorn.LookFor, ObContent: 30, 31, 32, 53, 33, 37, 36, 36, 35, 39).
Form Function EquippedObject(Actor akActor, Int aiSlot) Global
  If !akActor
    Return None
  EndIf
  If aiSlot == 16
    Return akActor.GetEquippedWeapon()
  ElseIf aiSlot == 17
    Int i = akActor.GetNumItems()
    While i > 0
      i -= 1
      Form f = akActor.GetNthForm(i)
      If f as Ammo && akActor.IsEquipped(f)
        Return f
      EndIf
    EndWhile
    Return None
  ElseIf aiSlot == 14
    Form left = akActor.GetEquippedObject(0)
    If left as Light
      Return left
    EndIf
    Return None
  EndIf
  Int[] slots = new Int[14]
  slots[0] = 30
  slots[1] = 31
  slots[2] = 32
  slots[3] = 53
  slots[4] = 33
  slots[5] = 37
  slots[6] = 36
  slots[7] = 36
  slots[8] = 35
  slots[13] = 39
  If aiSlot < 0 || aiSlot > 13 || slots[aiSlot] == 0
    Return None
  EndIf
  Return akActor.GetWornForm(Armor.GetMaskForSlot(slots[aiSlot]))
EndFunction

; PlaceAtMe with a distance and direction (Oblivion's 0 ahead, 1 behind, 2 left, 3 right of where akRef faces): each
; of the count placed there; the last returned.
ObjectReference Function PlaceNear(ObjectReference akRef, Form akWhat, Int aiCount, Float afDistance, Int aiDirection) Global
  If !akRef || !akWhat
    Return None
  EndIf
  Float heading = akRef.GetAngleZ()
  If aiDirection == 1
    heading += 180.0
  ElseIf aiDirection == 2
    heading -= 90.0
  ElseIf aiDirection == 3
    heading += 90.0
  EndIf
  Float x = akRef.GetPositionX() + afDistance * Math.Sin(heading)
  Float y = akRef.GetPositionY() + afDistance * Math.Cos(heading)
  ObjectReference made = None
  Int i = 0
  While i < aiCount || (i == 0 && aiCount <= 0)
    made = akRef.PlaceAtMe(akWhat, 1)
    If made
      made.SetPosition(x, y, akRef.GetPositionZ())
    EndIf
    i += 1
  EndWhile
  Return made
EndFunction

; IsSpellTarget: the spell on the actor (an ability or disease it has, or every effect of it active on it; Skyrim can't
; tell which spell an active effect came from).
Bool Function IsSpellTarget(Actor akActor, Spell akSpell) Global
  If !akActor || !akSpell
    Return False
  EndIf
  If akActor.HasSpell(akSpell)
    Return True
  EndIf
  Int n = akSpell.GetNumEffects()
  If n <= 0
    Return False
  EndIf
  Int i = 0
  While i < n
    If !akActor.HasMagicEffect(akSpell.GetNthEffectMagicEffect(i))
      Return False
    EndIf
    i += 1
  EndWhile
  Return True
EndFunction

; HasMagicEffect by Oblivion effect code: any of the converted effects for that code.
Bool Function HasEffectIn(Actor akActor, FormList akEffects) Global
  If !akActor || !akEffects
    Return False
  EndIf
  Int i = akEffects.GetSize()
  While i > 0
    i -= 1
    MagicEffect m = akEffects.GetAt(i) as MagicEffect
    If m && akActor.HasMagicEffect(m)
      Return True
    EndIf
  EndWhile
  Return False
EndFunction

; MenuMode with a menu number: whether one of these Skyrim menus is open.
Bool Function AnyMenuOpen(String a, String b = "", String c = "") Global
  Return UI.IsMenuOpen(a) || (b != "" && UI.IsMenuOpen(b)) || (c != "" && UI.IsMenuOpen(c))
EndFunction

; ---- locks ----

; Lock (Oblivion's level, 1-100; -1 for the level it had): Skyrim's lock at the nearest tier, and the Oblivion lock
; (TMObLock) its tumblers come from.
Function LockAt(ObjectReference akRef, Int aiLevel) Global
  If !akRef
    Return
  EndIf
  TMObLock oblivionLock = akRef as TMObLock
  If aiLevel < 0
    If oblivionLock
      aiLevel = oblivionLock.LockLevel
    Else
      aiLevel = OblivionLockLevel(akRef)
    EndIf
  EndIf
  If oblivionLock
    oblivionLock.LockLevel = aiLevel
    oblivionLock.Locked = True
    If oblivionLock.Swing || oblivionLock.Own
      Return
    EndIf
  EndIf
  akRef.Lock(True)
  akRef.SetLockLevel(SkyrimLockLevel(aiLevel))
EndFunction

Function UnlockRef(ObjectReference akRef) Global
  If !akRef
    Return
  EndIf
  TMObLock oblivionLock = akRef as TMObLock
  If oblivionLock
    oblivionLock.Unlock()
  Else
    akRef.Lock(False)
  EndIf
EndFunction

Bool Function IsLockedRef(ObjectReference akRef) Global
  TMObLock oblivionLock = akRef as TMObLock
  If oblivionLock
    Return oblivionLock.IsShut()
  EndIf
  Return akRef && akRef.IsLocked()
EndFunction

; Oblivion's tiers (Very Easy 0-7, Easy 8-20, Average 21-40, Hard 41-80, Very Hard 81-99, key 100) as Skyrim's.
Int Function SkyrimLockLevel(Int aiLevel) Global
  If aiLevel <= 7
    Return 1
  ElseIf aiLevel <= 20
    Return 25
  ElseIf aiLevel <= 40
    Return 50
  ElseIf aiLevel <= 80
    Return 75
  ElseIf aiLevel < 100
    Return 100
  EndIf
  Return 255
EndFunction

Int Function OblivionLockLevel(ObjectReference akRef) Global
  If !akRef
    Return 0
  EndIf
  TMObLock oblivionLock = akRef as TMObLock
  If oblivionLock
    Return oblivionLock.LockLevel
  EndIf
  Int skyrim = akRef.GetLockLevel()
  If skyrim <= 1
    Return 5
  ElseIf skyrim <= 25
    Return 15
  ElseIf skyrim <= 50
    Return 30
  ElseIf skyrim <= 75
    Return 60
  ElseIf skyrim <= 100
    Return 90
  EndIf
  Return 100
EndFunction

; PlayMagicShaderVisuals / PlayMagicEffectVisuals: the shader on a reference that is there to show it. Oblivion does
; nothing for one that isn't loaded; Skyrim's Play is an error then ("is not in a loaded cell and cannot have an effect
; shader played on it": the Arcane University's teleport pads, used by mages out of sight, 14 errors in a game log of
; 2026-11-10).
; Enable/DisableLinkedPathPoints: the reference's linked path points are navmesh-cutting boxes chained to it by akCut
; links (ObWorld.PlacePathCuts); a point switched off is its box enabled.
Function LinkedPathPoints(ObjectReference akRef, Keyword akCut, Bool abEnable) Global
  If !akRef || !akCut
    Return
  EndIf
  ObjectReference box = akRef.GetLinkedRef(akCut)
  Int guard = 0
  While box && guard < 64
    If abEnable
      box.Disable()
    Else
      box.Enable()
    EndIf
    box = box.GetLinkedRef(akCut)
    guard += 1
  EndWhile
EndFunction

Function PlayShader(EffectShader akShader, ObjectReference akOn, Float afSeconds) Global
  If akShader && akOn && akOn.Is3DLoaded()
    akShader.Play(akOn, afSeconds)
  EndIf
EndFunction

; GetDetectionLevel: Oblivion's four levels of how well the observer has the target: 0 lost (it had them and is
; searching), 1 unseen, 2 noticed (aware of them without seeing them), 3 seen. Skyrim says whether the observer
; detects the target (IsDetectedBy), sees them (HasLOS) and is searching (combat state 2). Until 2026-11-10 only 0 and
; 3 were given, so a test for "unseen" (1) never held.
Int Function DetectionLevel(Actor akObserver, Actor akTarget) Global
  If !akObserver || !akTarget
    Return 1
  EndIf
  If akTarget.IsDetectedBy(akObserver)
    If akObserver.HasLOS(akTarget)
      Return 3
    EndIf
    Return 2
  EndIf
  If akObserver.GetCombatState() == 2
    Return 0
  EndIf
  Return 1
EndFunction
