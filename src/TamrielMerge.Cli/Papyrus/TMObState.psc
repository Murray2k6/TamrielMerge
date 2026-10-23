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
  ; What follows in Skyrim's AI: Aggression as the conversion gives it (80 and up very aggressive), Energy as it is.
  If aiStat == 29
    If afValue >= 80.0
      a.SetActorValue("Aggression", 2.0)
    Else
      a.SetActorValue("Aggression", 0.0)
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

Function MenuOpened()
  If menusOpen <= 0
    menusOpen = 0
    pausedSince = Utility.GetCurrentRealTime()
  EndIf
  menusOpen += 1
EndFunction

Function MenuClosed()
  menusOpen -= 1
  If menusOpen <= 0 && pausedSince >= 0.0
    pausedTotal += Utility.GetCurrentRealTime() - pausedSince
    pausedSince = -1.0
    menusOpen = 0
  EndIf
EndFunction

; A save loaded or a new game begun (TMObPlayer): the clock starts again, GetGameLoaded is true once to each script.
Function GameLoaded()
  pausedTotal = 0.0
  pausedSince = -1.0
  menusOpen = 0
  ClockLoads += 1
  If LaunchMarker && LaunchMarker.GetName() != "TamrielMerge started"
    Launches += 1
    LaunchMarker.SetName("TamrielMerge started")
  EndIf
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
  akRef.PlayGamebryoAnimation(asGroup, aiFlag == 0)
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

; The next OnUpdate (LoopGroup's replays, ForceSubtitle's lines that never started): as soon as the soonest wants it.
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
  If !akSpeaker
    Return
  EndIf
  Init()
  Int i = SpeakerSlot(akSpeaker)
  If speakers[i] != akSpeaker
    speakLooking[i] = False
  EndIf
  speakers[i] = akSpeaker
  speakLines[i] = akLine
  speakStarts[i] = Utility.GetCurrentRealTime()
  speakTalking[i] = True
EndFunction

Function LineEnded(Actor akSpeaker, TopicInfo akLine)
  If !akSpeaker
    Return
  EndIf
  Init()
  Int i = speakers.Find(akSpeaker)
  If i < 0 || speakLines[i] != akLine
    Return
  EndIf
  speakTalking[i] = False
  ReleaseSubtitle(i)
  If speakLooking[i]
    speakLooking[i] = False
    akSpeaker.ClearLookAt()
  EndIf
  ; What a line without a recording of its own lasted in this voice.
  If Recorded(akSpeaker, akLine) <= 0.0
    Int k = learnedNext
    learnedLines[k] = akLine
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
    If learnedLines[k] == akLine && learnedVoices[k] == voice
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
  Actor a = akSpeaker as Actor
  If a && (akTarget || abForceSubtitle)
    Int i = SpeakerSlot(a)
    If speakers[i] != a
      speakForced[i] = False
    EndIf
    speakers[i] = a
    speakTalking[i] = False
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
  Int tries = 0
  While tries < 40
    Int i = speakers.Find(a)
    If i >= 0 && speakTalking[i] && speakStarts[i] >= asked
      Return LineLength(a, speakLines[i])
    EndIf
    Utility.Wait(0.025)
    tries += 1
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

Bool Function IsVampire(Actor akActor) Global
  If !akActor
    Return False
  EndIf
  Keyword vampire = Game.GetFormFromFile(0x000A82BB, "Skyrim.esm") as Keyword
  Return (vampire && akActor.HasKeyword(vampire)) || StringUtil.Find(akActor.GetRace().GetName(), "Vampire") >= 0
EndFunction

Function VampireFeed(Actor akVampire, Actor akVictim)
  If akVampire && akVictim
    akVampire.StartVampireFeed(akVictim)
    ownFeeds += 1
  EndIf
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

; 1 knocked down (paralysed, unconscious or bleeding out), else 0.
Int Function KnockedState(Actor akActor) Global
  If akActor && (akActor.IsUnconscious() || akActor.IsBleedingOut() || akActor.GetActorValue("Paralysis") > 0)
    Return 1
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
    If oblivionLock.Swing
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
