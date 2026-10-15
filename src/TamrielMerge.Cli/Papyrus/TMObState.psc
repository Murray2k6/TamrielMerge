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
Int[] crimeTypes
Form[] crimeCriminals
Form[] crimeVictims
Int crimeNext
Int[] factionCrimes
Actor[] bountyActors
Int[] bountyGold
Int[] miscStats
Actor[] speakers
Float[] speakEnds
Int fedSeen = -1
Int ownFeeds

Function Init()
  If !animRefs
    animRefs = new ObjectReference[64]
    animEnds = new Float[64]
    crimeTypes = new Int[64]
    crimeCriminals = new Form[64]
    crimeVictims = new Form[64]
    factionCrimes = new Int[128]
    bountyActors = new Actor[32]
    bountyGold = new Int[32]
    miscStats = new Int[34]
    speakers = new Actor[16]
    speakEnds = new Float[16]
  EndIf
EndFunction

Event OnInit()
  Init()
EndEvent

; ---- animations ----

; Plays an Oblivion animation group (the model's controller sequence of that name, when it has one) and remembers for
; how long it plays, for IsAnimPlaying. aiFlag is Oblivion's (0 start over, 1 start at the next loop, 2 the same).
Function PlayGroup(ObjectReference akRef, String asGroup, Int aiFlag = 0)
  If !akRef
    Return
  EndIf
  Init()
  akRef.PlayGamebryoAnimation(asGroup, aiFlag == 0)
  Float dur = 2.0
  If StringUtil.Find(asGroup, "Fast") == 0
    dur = 1.0
  ElseIf asGroup == "SpecialIdle" || asGroup == "Idle"
    dur = 3.0
  EndIf
  Int i = animRefs.Find(akRef)
  If i < 0
    i = animRefs.Find(None)
  EndIf
  If i < 0
    i = OldestAnim()
  EndIf
  animRefs[i] = akRef
  animEnds[i] = Utility.GetCurrentRealTime() + dur
EndFunction

Int Function OldestAnim()
  Int oldest = 0
  Int i = 1
  While i < animEnds.Length
    If animEnds[i] < animEnds[oldest]
      oldest = i
    EndIf
    i += 1
  EndWhile
  Return oldest
EndFunction

Bool Function IsAnimPlaying(ObjectReference akRef)
  Init()
  Int i = animRefs.Find(akRef)
  If i < 0
    Return False
  EndIf
  If Utility.GetCurrentRealTime() < animEnds[i]
    Return True
  EndIf
  animRefs[i] = None
  Return False
EndFunction

; ---- speech ----

; Oblivion's Say returns how long the line lasts, and IsTalking is true meanwhile; Skyrim's says nothing of either, so
; a typical line's length is used.
Float Function Said(ObjectReference akSpeaker)
  Init()
  Actor a = akSpeaker as Actor
  If a
    Int i = speakers.Find(a)
    If i < 0
      i = speakers.Find(None)
    EndIf
    If i < 0
      i = 0
    EndIf
    speakers[i] = a
    speakEnds[i] = Utility.GetCurrentRealTime() + 4.0
  EndIf
  Return 4.0
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
  Return i >= 0 && Utility.GetCurrentRealTime() < speakEnds[i]
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

; Wait / StopWaiting: the actor holds still while that package is its current one.
Function WaitOn(Actor akActor, Package akPackage, Bool abWait)
  If akActor && (!akPackage || akActor.GetCurrentPackage() == akPackage || !abWait)
    akActor.SetDontMove(abWait)
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
    Return PCCrimeGold.GetValueInt()
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
