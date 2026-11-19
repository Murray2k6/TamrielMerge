ScriptName TMMwActorAI extends Actor
{Morrowind's AI settings on one of its people or creatures (docs/morrowind-scripts.md, "AI settings"): Hello, Fight,
Flee and Alarm (0-100) as Morrowind keeps them, from the actor's record (AIDT) and changed by the scripts (SetFight...),
and what Morrowind's engine does with them, as OpenMW does (mwmechanics: combat.cpp, actors.cpp, aicombataction.cpp):
- Fight: while loaded and out of combat, the actor attacks the player when its fight term reaches 100: Fight +
  iFightDistanceBase - fFightDistanceMultiplier x distance (level for one that flies or swims) + (50 - disposition) x
  fFightDispMult for a person, + iWerewolfFightMod against a werewolf, and it can see and has noticed the player
  (Skyrim's own aggression is off for these actors: this decides).
- Flee: in combat, it runs when (1 - health share) x fAIFleeHealthMult + Flee x fAIFleeFleeMult + the distance term
  reaches 100 (Flee 100 or more: always), as Skyrim does with Confidence 0, and stands again when it falls short.
- Alarm: 100 or more reports crimes it sees, as Skyrim's witnesses do whose Morality forbids every crime (3); less,
  none (0).
- Hello: its greeting as OpenMW gives it (actors.cpp updateGreetingState, every quarter second): wandering, travelling or
  idle and not fighting or swimming, once the player has been within Hello x iGreetDistanceMultiplier, alive, seen and
  noticed, for two checks, it says Hello (MWVoiceHello) and faces the player for iGreetDuration seconds, and greets
  again only once the player has gone fGreetDistanceReset away; idle remarks (playIdleDialogue) at fVoiceIdleOdds x 6 a
  10,000th a second within 3000 units, with Hello above 0, not fighting, following or escorting.
- Voices (MWVoices, TMMwVoices.Speak): Attack as a fight begins and, at iVoiceAttackOdds, as each attack starts
  (aicombat.cpp); Hit as a person is hurt, at iVoiceHitOdds (npc.cpp onHit); Flee as it turns to run.
- Disposition (a person's): OpenMW's derived one (mechanicsmanagerimp.cpp getDerivedDisposition), kept in Variable09
  for the dialogue: the base (its record's, as scripts change it) + fDispRaceMod for the player's race +
  fDispPersonalityMult x (the player's Personality - fDispPersonalityBase) + (fDispFactionRankMult x rank +
  fDispFactionRankBase) x fDispFactionMod x the faction's reaction (to itself when the player is of it, else the lowest
  to the player's factions) - fDispCrimeMod x the bounty + fDispDiseaseMod if the player is diseased + fDispWeaponDrawn
  with the player's weapon out; Charm on top (its effect raises Variable09); the whole clamped to 0-100.
Each is also mirrored in an actor value the dialogue's conditions read (Skyrim's conditions read actor values, not
script properties): Variable05 Hello, Variable06 Fight, Variable07 Flee, Variable08 Alarm, Variable04 Reputation; and the states the dialogue
asks of it (OpenMW filter.cpp): Variable01 friendly hits (the player's hits while not fighting it, at most 4),
Variable02 its combat target (1 a creature, 2 a werewolf), Variable03 attacked (struck by the player, until the
player's crime is dealt with),
Variable10 whether it would attack the player now (isAggressive).}

Int Property Hello Auto
Int Property Fight Auto
Int Property Flee Auto
Int Property Alarm Auto
Int Property Reputation Auto
{The actor's reputation (NPDT; GetReputation on it), the player's being a global.}
Bool Property IsPerson Auto
Bool Property Flies Auto
Bool Property Swims Auto
Float Property FightDistanceBase = 10.0 Auto
Float Property FightDistanceMultiplier = 0.005 Auto
Float Property FightDispMult = 0.2 Auto
Float Property WerewolfFightMod = 100.0 Auto
Float Property FleeHealthMult = 7.0 Auto
Float Property FleeFleeMult = 0.3 Auto
Float Property MeleeArmorMult = 1.0 Auto
Float Property MeleeWeaponMult = 2.0 Auto
Int Property RecordPackage = -1 Auto
{Its record's AI package (-1 none, 0 wander, 1 travel, 2 escort, 3 follow, 4 activate), a script's (TMMwAI) above it.}
Float Property GreetDistanceMultiplier = 6.0 Auto
Float Property GreetDuration = 4.0 Auto
Float Property GreetDistanceReset = 512.0 Auto
Float Property VoiceIdleOdds = 10.0 Auto
Float Property VoiceHitOdds = 30.0 Auto
Float Property VoiceAttackOdds = 10.0 Auto
Int Property BaseDisposition = 50 Auto
{Its record's disposition; the scripts' Set/ModDisposition change it (dispBase).}
Float Property DispRaceMod = 5.0 Auto
Float Property DispPersonalityMult = 0.5 Auto
Float Property DispPersonalityBase = 50.0 Auto
Float Property DispFactionRankMult = 0.5 Auto
Float Property DispFactionRankBase = 1.0 Auto
Float Property DispFactionMod = 3.0 Auto
Float Property DispCrimeMod = 0.0 Auto
Float Property DispDiseaseMod = -10.0 Auto
Float Property DispWeaponDrawn = -5.0 Auto
FormList Property CommonDiseases Auto
FormList Property BlightDiseases Auto
GlobalVariable Property WerewolfKills Auto
{The player's kills as a werewolf (GetWerewolfKills, the dialogue's PCWerewolfKills).}

Float ownConfidence = -1.0
Bool fleeing = False

FormList Property CreatureGems Auto
{A creature whose soul the scripts ask for (HasSoulGem): its own gems by size (petty..grand, Azura's Star).}
FormList Property GenericGems Auto
{Skyrim's filled gems in the same order: soul trapping it fills one, which is swapped for its own.}
GlobalVariable Property LoadCount Auto
{Games loaded so far (TMMwPlayerState): friendly hits aren't kept in a save in Morrowind (OpenMW creaturestats.cpp), so
they start again from 0 after each load.}

Int seenLoads = -1
Int greetState = 0
Int greetTimer = 0
Float greetAskedAt = 0.0
Float lastTick = -1.0
Bool dispSet = False
Float dispBase = 50.0
; When the disposition was last worked out (real seconds), and this actor's Morrowind faction, looked up once.
Float lastDisp = -100.0
; When its own next update is due (real seconds), and whether one is running: an update reaches every script on the
; actor, so the Morrowind script beside this one (a chargen guard's, Processus Vitellius's) woke it at that script's rate
; on top of its own (2026-11-02, a user's log: 13,552 of its runs waiting at once on one actor).
Float nextUpdate = 0.0
Bool updating = False
Faction myFaction = None
Bool factionKnown = False
Bool wasInCombat = False
Bool Property Saying = False Auto Hidden
{Saying a script's Say line (TMMwSayLine), for SayDone.}
Float Property SaidAt = 0.0 Auto Hidden
{When it began (a line cut off before its end counts as said after a minute: no line runs so long).}

; ---- its record's AI packages (AI_W, AI_T, AI_E, AI_F, AI_A, in the record's order) ----
; Morrowind runs them one after another and, where the record says so, round again: a miner's or a guard's walk from
; point to point, a retainer following its lord. Only records with more than one plain wander have them here (a lone
; wander is the actor's own Skyrim package). A package a script gives the actor ends them, as Morrowind's script
; commands clear the actor's packages.
Int[] Property RecordKinds Auto
Float[] Property RecordX Auto
Float[] Property RecordY Auto
Float[] Property RecordZ Auto
Float[] Property RecordHours Auto
{An AI_W's or AI_F's/AI_E's duration in hours (0: no end).}
Float[] Property RecordRange Auto
{An AI_W's distance.}
Bool Property RecordRepeat = False Auto
ObjectReference Property RecordTarget Auto
{Whom an AI_F follows or an AI_E leads (or what an AI_A uses).}
Bool Property RecordTargetIsPlayer = False Auto
Int recordStep = -1   ; the package running (-1 none yet, -2 over for good)

; ---- a wander's idles (AI_W's and AIWander's eight chances, Idle2 to Idle9) ----
; OpenMW aiwander.cpp getRandomIdle (read 2026-11-11): one roll of 0-1 over fIdleChanceMultiplier means no idle; else
; each of the eight rolls 0-100, those at or under their chance stand, the highest roll among them wins; the group
; plays once and the choice is made again when it ends. No idle means a walk (the Skyrim package's own wandering).
Int[] Property IdleChances Auto
{Its wander's eight chances now: its record's first AI_W, then whatever AIWander or its record's packages set.}
Int[] Property RecordIdles Auto
{Eight chances for each of its record's packages (zeros for one that isn't a wander).}
Float idleUntil = 0.0

Function SetIdles(Int a2, Int a3, Int a4, Int a5, Int a6, Int a7, Int a8, Int a9)
  If !IdleChances || IdleChances.Length < 8
    IdleChances = new Int[8]
  EndIf
  IdleChances[0] = a2
  IdleChances[1] = a3
  IdleChances[2] = a4
  IdleChances[3] = a5
  IdleChances[4] = a6
  IdleChances[5] = a7
  IdleChances[6] = a8
  IdleChances[7] = a9
EndFunction

Function WanderIdle(Float afNow)
  If afNow < idleUntil || !IdleChances || IdleChances.Length < 8 || CurrentPackage() != 0
    Return
  EndIf
  If greetState == 1 || Saying || IsInDialogueWithPlayer() || GetSitState() != 0 || GetSleepState() != 0 || IsSwimming() || IsWeaponDrawn() || IsOnMount()
    Return
  EndIf
  Quest q = Quest.GetQuest("MWScriptAI")
  If !q
    Return
  EndIf
  TMMwAI ai = q as TMMwAI
  If !ai.WanderIdles || ai.WanderIdles.Length < 8
    Return
  EndIf
  If Utility.RandomFloat(0.0, 1.0) > ai.IdleChanceMultiplier
    idleUntil = afNow + ai.WalkSeconds
    Return
  EndIf
  Int chosen = -1
  Float best = 0.0
  Int i = 0
  While i < 8
    Float roll = Utility.RandomFloat(0.0, 100.0)
    If roll <= IdleChances[i] && roll > best
      chosen = i
      best = roll
    EndIf
    i += 1
  EndWhile
  If chosen < 0
    idleUntil = afNow + ai.WalkSeconds
    Return
  EndIf
  If ai.WanderIdles[chosen] && PlayIdle(ai.WanderIdles[chosen])
    idleUntil = afNow + ai.IdleSeconds
  Else
    idleUntil = afNow + 1.0
  EndIf
EndFunction

Function RunRecord()
  If recordStep == -2 || !RecordKinds || RecordKinds.Length == 0
    Return
  EndIf
  Quest q = Quest.GetQuest("MWScriptAI")
  If !q
    Return
  EndIf
  TMMwAI ai = q as TMMwAI
  If recordStep >= 0
    Int mine = ai.IsRecord(Self)
    If mine == 0
      recordStep = -2   ; a script's package took its place
      Return
    EndIf
    If mine == 1
      If !ai.Done(Self)
        Return
      EndIf
      recordStep += 1
    EndIf
    ; mine == -1: its slot went to another actor while it was away; the same package again
  Else
    recordStep = 0
  EndIf
  If recordStep >= RecordKinds.Length
    If !RecordRepeat
      recordStep = -2
      Return
    EndIf
    recordStep = 0
  EndIf
  Int kind = RecordKinds[recordStep]
  ObjectReference target = RecordTarget
  If RecordTargetIsPlayer
    target = Game.GetPlayer()
  EndIf
  If kind == 0
    ai.Wander(Self, RecordRange[recordStep], RecordHours[recordStep])
    Int at = recordStep * 8
    If RecordIdles && RecordIdles.Length >= at + 8
      SetIdles(RecordIdles[at], RecordIdles[at + 1], RecordIdles[at + 2], RecordIdles[at + 3], RecordIdles[at + 4], RecordIdles[at + 5], RecordIdles[at + 6], RecordIdles[at + 7])
    EndIf
  ElseIf kind == 1
    ai.Travel(Self, RecordX[recordStep], RecordY[recordStep], RecordZ[recordStep])
  ElseIf kind == 2 && target as Actor
    ai.Escort(Self, target as Actor, RecordHours[recordStep], RecordX[recordStep], RecordY[recordStep], RecordZ[recordStep])
  ElseIf kind == 3 && target as Actor
    ai.Follow(Self, target as Actor, RecordHours[recordStep], RecordX[recordStep], RecordY[recordStep], RecordZ[recordStep])
  ElseIf kind == 4 && target
    ai.ActivateRef(Self, target)
  Else
    ; Nothing to follow, lead or use (its target isn't in the world): the next package at the next look.
    recordStep += 1
    If recordStep >= RecordKinds.Length && !RecordRepeat
      recordStep = -2
    EndIf
    Return
  EndIf
  ai.MarkRecord(Self)
EndFunction

Event OnInit()
  ApplyAlarm()
  Mirror()
  Listen()
EndEvent

Event OnLoad()
  ApplyAlarm()
  Mirror()
  Listen()
  CheckLoads()
  If ownConfidence < 0.0
    ownConfidence = GetActorValue("Confidence")
  EndIf
  RegisterForAnimationEvent(Self, "weaponSwing")
  RegisterForAnimationEvent(Self, "weaponLeftSwing")
  RegisterForAnimationEvent(Self, "bowDraw")
  lastTick = -1.0
  updating = False
  Float first = Utility.RandomFloat(0.2, 0.6)
  nextUpdate = Utility.GetCurrentRealTime() + first
  RegisterForSingleUpdate(first)
EndEvent

TMMwVoices Function Voices()
  Quest q = Quest.GetQuest("MWVoices")
  If q
    Return q as TMMwVoices
  EndIf
  Return None
EndFunction

; Its AI package now: a script's (TMMwAI) or its record's.
Int Function CurrentPackage()
  Quest q = Quest.GetQuest("MWScriptAI")
  If q
    Int p = (q as TMMwAI).Current(Self)
    If p >= 0
      Return p
    EndIf
  EndIf
  Return RecordPackage
EndFunction

; isTargetMagicallyHidden: invisible, or chameleon above 75. Skyrim has no chameleon actor value (asking for one is an
; error each time): the converted chameleon spells are invisibility (TesMagic), so invisibility is both.
Bool Function Hidden(Actor akPlayer)
  Return akPlayer.GetActorValue("Invisibility") > 0.0
EndFunction

Function ResetGreeting()
  If greetState == 1
    ClearLookAt()
  EndIf
  greetState = 0
  greetTimer = 0
EndFunction

; updateGreetingState (each quarter second).
Function UpdateGreeting(Actor akPlayer)
  Int pkg = CurrentPackage()
  If IsInCombat() || IsSwimming() || (pkg != -1 && pkg != 0 && pkg != 1)
    ResetGreeting()
    Return
  EndIf
  Float helloDistance = Hello * GreetDistanceMultiplier
  Float distance = GetDistance(akPlayer)
  If greetState == 0
    If distance <= helloDistance && !akPlayer.IsDead() && GetActorValue("Paralysis") <= 0.0 && !Hidden(akPlayer) && HasLOS(akPlayer) && akPlayer.IsDetectedBy(Self)
      greetTimer += 1
    EndIf
    If greetTimer >= 2
      greetState = 1
      greetAskedAt = Utility.GetCurrentRealTime()
      TMMwVoices v = Voices()
      If !v || !v.Speak(Self, v.HelloTopic)
        greetState = 2
      EndIf
      greetTimer = 0
    EndIf
  ElseIf greetState == 1
    greetTimer += 1
    ; No line of Hello's held for it (say() found none): the greeting is over.
    If greetTimer == 2 && !(Saying && SaidAt >= greetAskedAt)
      greetState = 2
      greetTimer = 0
    ElseIf greetTimer <= (GreetDuration / 0.25) as Int
      SetLookAt(akPlayer)
    Else
      ClearLookAt()
      greetState = 2
      greetTimer = 0
    EndIf
  EndIf
  If greetState != 0 && distance >= GreetDistanceReset
    ResetGreeting()
  EndIf
EndFunction

; playIdleDialogue, for the time since the last check.
Function IdleRemark(Actor akPlayer, Float afSeconds)
  If Hello == 0 || IsInCombat() || IsSwimming()
    Return
  EndIf
  Int pkg = CurrentPackage()
  If pkg == 2 || pkg == 3 || GetDistance(akPlayer) >= 3000.0
    Return
  EndIf
  If Utility.RandomFloat(0.0, 1.0) * 10000.0 < VoiceIdleOdds * afSeconds * 6.0 && akPlayer.HasLOS(Self)
    TMMwVoices v = Voices()
    If v
      v.Speak(Self, v.IdleTopic)
    EndIf
  EndIf
EndFunction

; Attacks starting (AiCombatStorage::startAttackIfReady): Attack at iVoiceAttackOdds.
Event OnAnimationEvent(ObjectReference akSource, String asEventName)
  If IsInCombat() && Utility.RandomInt(0, 99) < VoiceAttackOdds
    TMMwVoices v = Voices()
    If v
      v.Speak(Self, v.AttackTopic)
    EndIf
  EndIf
EndEvent

; ---- disposition ----

Float Function DerivedDisposition()
  If !dispSet
    dispSet = True
    dispBase = BaseDisposition
  EndIf
  Actor player = Game.GetPlayer()
  Float disp = dispBase
  If GetRace() == player.GetRace()
    disp += DispRaceMod
  EndIf
  Quest stateQuest = Quest.GetQuest("MWScriptState")
  TMMwState st = None
  If stateQuest
    st = stateQuest as TMMwState
  EndIf
  Float personality = 50.0
  If st
    personality = st.GetStat(player, 6, -1)
  EndIf
  disp += DispPersonalityMult * (personality - DispPersonalityBase)
  Float reaction = 0.0
  Int rank = 0
  ; Its own Morrowind faction, looked up once (it changes only by a script's PCJoinFaction-like calls, not for an NPC).
  If st && !factionKnown
    myFaction = st.MwFactionOf(Self)
    factionKnown = True
  EndIf
  Faction mine = myFaction
  Int myRank = -1
  If mine
    myRank = player.GetFactionRank(mine)
  EndIf
  If mine && myRank >= 0
    If !mine.IsPlayerExpelled()
      reaction = st.Reaction(mine, mine)
      rank = myRank
    EndIf
  ElseIf mine
    ; The list read once (each read of another script's property waits its turn on that script).
    Faction[] all = st.MwFactions
    Bool first = True
    Int i = 0
    While i < all.Length
      Faction f = all[i]
      If f
        Int fr = player.GetFactionRank(f)
        If fr >= 0 && !f.IsPlayerExpelled()
          Int r = st.Reaction(mine, f)
          If first || r < reaction
            reaction = r
            rank = fr
            first = False
          EndIf
        EndIf
      EndIf
      i += 1
    EndWhile
  EndIf
  disp += (DispFactionRankMult * rank + DispFactionRankBase) * DispFactionMod * reaction
  Quest lawQuest = Quest.GetQuest("MWLaw")
  If lawQuest && (lawQuest as TMMwLaw).Crime
    disp -= DispCrimeMod * (lawQuest as TMMwLaw).Crime.GetCrimeGold()
  EndIf
  If TMMwScript.HasAny(player, CommonDiseases) || TMMwScript.HasAny(player, BlightDiseases)
    disp += DispDiseaseMod
  EndIf
  If player.IsWeaponDrawn() && player.GetEquippedItemType(1) != 9 && player.GetEquippedItemType(0) != 9
    disp += DispWeaponDrawn
  EndIf
  Int v = disp as Int
  If v < 0
    v = 0
  ElseIf v > 100
    v = 100
  EndIf
  Return v
EndFunction

; Variable09 holds it (less 50) for the dialogue; Charm's effect adds to it on top. Worked out when the player talks to it
; (OnActivate) and on its updates near the player at most once a second: on every update it was most of Papyrus's work
; (2026-11-02, a user's log: 25,000 of the frames in its stack dumps, every actor waiting on the same state quest).
Function UpdateDisposition()
  If IsPerson
    lastDisp = Utility.GetCurrentRealTime()
    SetActorValue("Variable09", DerivedDisposition() - 50.0)
  EndIf
EndFunction

Event OnActivate(ObjectReference akActionRef)
  If akActionRef == Game.GetPlayer()
    UpdateDisposition()
  EndIf
EndEvent

; The scripts' SetDisposition / ModDisposition: the base.
Function SetBaseDisposition(Float afValue)
  dispSet = True
  dispBase = afValue
  UpdateDisposition()
EndFunction

Float Function GetBaseDisposition()
  If !dispSet
    dispSet = True
    dispBase = BaseDisposition
  EndIf
  Return dispBase
EndFunction

Event OnUnload()
  UnregisterForUpdate()
EndEvent

Event OnUpdate()
  Float now = Utility.GetCurrentRealTime()
  ; Not its own update (another script's on the actor), or one already running: nothing, its own is still to come.
  If updating || now < nextUpdate - 0.05
    Return
  EndIf
  If !Is3DLoaded() || IsDead()
    Return
  EndIf
  updating = True
  Actor player = Game.GetPlayer()
  Float seconds = 0.0
  If lastTick >= 0.0 && now > lastTick
    seconds = now - lastTick
  EndIf
  lastTick = now
  CountGems()
  Float distance = GetDistance(player)
  If distance < 1500.0 && now - lastDisp >= 1.0
    UpdateDisposition()
  EndIf
  If !IsInCombat()
    UpdateGreeting(player)
    IdleRemark(player, seconds)
    RunRecord()
    If distance < 3000.0
      WanderIdle(now)
    EndIf
  EndIf
  Bool aggressive = ShouldAttack(player)
  SetActorValue("Variable10", aggressive as Float)
  If IsInCombat()
    UpdateFlee(GetCombatTarget())
  Else
    If fleeing
      StopFleeing()
    EndIf
    If aggressive
      StartCombat(player)
    EndIf
  EndIf
  ; Every quarter second where it can greet (OpenMW's sUpdateHelloInterval), every second within 3000 (idle remarks go by
  ; the seconds passed, so as often as before), every three within OpenMW's actors' processing range (7168), every five
  ; further. Four times a second within 3000 kept Papyrus behind (2026-11-02).
  Float wait = 5.0
  If distance < Hello * GreetDistanceMultiplier + 256.0 || greetState != 0
    wait = 0.25
  ElseIf distance < 3000.0
    wait = 1.0
  ElseIf distance < 7168.0
    wait = 3.0
  EndIf
  nextUpdate = Utility.GetCurrentRealTime() + wait
  updating = False
  RegisterForSingleUpdate(wait)
EndEvent

; getAggroDistance: level for one that can move up and down (flying, or swimming in water).
Float Function AggroDistance(ObjectReference akTarget)
  If Flies || (Swims && IsSwimming())
    Float dx = akTarget.GetPositionX() - GetPositionX()
    Float dy = akTarget.GetPositionY() - GetPositionY()
    Return Math.Sqrt(dx * dx + dy * dy)
  EndIf
  Return GetDistance(akTarget)
EndFunction

Float Function DistanceBias(ObjectReference akTarget)
  Return FightDistanceBase - FightDistanceMultiplier * AggroDistance(akTarget)
EndFunction

; getFightTerm and isAggressive against the player.
Bool Function ShouldAttack(Actor akPlayer)
  If !akPlayer || akPlayer.IsDead() || IsPlayerTeammate() || GetActorValue("Paralysis") > 0.0
    Return False
  EndIf
  Float fightTerm = Fight + DistanceBias(akPlayer)
  If IsPerson
    fightTerm += (50.0 - TMMwScript.GetDisposition(Self)) * FightDispMult
  EndIf
  If TMMwScript.IsWerewolf(akPlayer)
    fightTerm += WerewolfFightMod
  EndIf
  If fightTerm < 100.0
    Return False
  EndIf
  Return HasLOS(akPlayer) && akPlayer.IsDetectedBy(Self)
EndFunction

; vanillaRateFlee and makeFleeDecision.
Function UpdateFlee(Actor akEnemy)
  If !akEnemy
    Return
  EndIf
  Float rating = Flee as Float
  If Flee < 100
    rating = (1.0 - GetActorValuePercentage("Health")) * FleeHealthMult + Flee * FleeFleeMult
    If rating != 0.0
      rating += DistanceBias(akEnemy)
    EndIf
  EndIf
  ; makeFleeDecision (OpenMW aicombataction.cpp, read 2026-11-11): a flee rating under 100 counts as none, and the actor
  ; flees only when it is over the rating of what it fights with (vanillaRateWeaponAndAmmo: its armour rating x
  ; fAIMeleeArmorMult + the weapon's damage x its skill / 100 x fAIMeleeWeaponMult; bare hands 0). Skyrim's numbers stand
  ; in for the weapon's damage, the skill and the armour rating (the converted weapon's base damage, the actor's skill
  ; with it, its damage resistance).
  If rating >= 100.0 && rating <= WeaponRating()
    rating = 0.0
  EndIf
  If rating >= 100.0
    If !fleeing
      fleeing = True
      SetActorValue("Confidence", 0.0)
      TMMwVoices v = Voices()
      If v
        v.Speak(Self, v.FleeTopic)
      EndIf
    EndIf
  ElseIf fleeing
    StopFleeing()
  EndIf
EndFunction

Float Function WeaponRating()
  Weapon w = GetEquippedWeapon()
  If !w
    w = GetEquippedWeapon(True)
  EndIf
  If !w
    Return 0.0
  EndIf
  Float skill = GetActorValue("OneHanded")
  Int kind = w.GetWeaponType()
  If kind == 5 || kind == 6
    skill = GetActorValue("TwoHanded")
  ElseIf kind == 7 || kind == 9
    skill = GetActorValue("Marksman")
  EndIf
  Return GetActorValue("DamageResist") * MeleeArmorMult + w.GetBaseDamage() * skill * 0.01 * MeleeWeaponMult
EndFunction

Function StopFleeing()
  fleeing = False
  If ownConfidence >= 0.0
    SetActorValue("Confidence", ownConfidence)
  Else
    SetActorValue("Confidence", 3.0)
  EndIf
EndFunction

Function Listen()
  RegisterForModEvent("TMMwCrimeResolved", "OnCrimeResolved")
  RegisterForModEvent("TMMwGameLoaded", "OnGameLoaded")
EndFunction

; Attacked holds until the player's crime is dealt with (OpenMW actors.cpp updateCrimePursuit clears it then).
Event OnCrimeResolved(String asEventName, String asArg, Float afNum, Form akSender)
  SetActorValue("Variable03", 0.0)
EndEvent

Event OnGameLoaded(String asEventName, String asArg, Float afNum, Form akSender)
  CheckLoads()
  ; The real-time clock starts again with each game started, and a save can hold a run part-way: both start afresh.
  updating = False
  lastDisp = -100.0
  If Is3DLoaded()
    nextUpdate = Utility.GetCurrentRealTime() + 0.5
    RegisterForSingleUpdate(0.5)
  Else
    nextUpdate = 0.0
  EndIf
EndEvent

Function CheckLoads()
  If !LoadCount
    Return
  EndIf
  Int now = LoadCount.GetValueInt()
  If now != seenLoads
    seenLoads = now
    SetActorValue("Variable01", 0.0)
  EndIf
EndFunction

Event OnHit(ObjectReference akAggressor, Form akSource, Projectile akProjectile, Bool abPowerAttack, Bool abSneakAttack, Bool abBashAttack, Bool abHitBlocked)
  ; A person hurt by an attacker: Hit at iVoiceHitOdds (Npc::onHit; creatures say none).
  If IsPerson && akAggressor && akAggressor as Actor && Utility.RandomInt(0, 99) < VoiceHitOdds
    TMMwVoices v = Voices()
    If v
      v.Speak(Self, v.HitTopic)
    EndIf
  EndIf
  If akAggressor != Game.GetPlayer()
    Return
  EndIf
  CountGems()
  SetActorValue("Variable03", 1.0)
  ; A hit while not fighting the player: a friendly hit (FriendHit counts to 4).
  If GetCombatTarget() != Game.GetPlayer() && GetActorValue("Variable01") < 4.0
    SetActorValue("Variable01", GetActorValue("Variable01") + 1.0)
  EndIf
EndEvent

Int[] gemCounts

; The player's filled gems of each size, just before the soul can be trapped (each tick and hit).
Function CountGems()
  If !CreatureGems || !GenericGems
    Return
  EndIf
  If !gemCounts
    gemCounts = new Int[6]
  EndIf
  Actor p = Game.GetPlayer()
  Int i = 0
  While i < 5 && i < GenericGems.GetSize()
    gemCounts[i] = p.GetItemCount(GenericGems.GetAt(i))
    i += 1
  EndWhile
EndFunction

; Soul trapped: the gem Skyrim filled (one more of a size than before) becomes the one holding this creature's soul.
Function SwapTrappedSoul()
  If !CreatureGems || !GenericGems || !gemCounts
    Return
  EndIf
  Utility.Wait(0.5)
  Actor p = Game.GetPlayer()
  Int i = 0
  While i < 5 && i < GenericGems.GetSize() && i < CreatureGems.GetSize()
    If p.GetItemCount(GenericGems.GetAt(i)) > gemCounts[i]
      p.RemoveItem(GenericGems.GetAt(i), 1, True)
      p.AddItem(CreatureGems.GetAt(i), 1, True)
      Return
    EndIf
    i += 1
  EndWhile
EndFunction

Event OnDeath(Actor akKiller)
  SwapTrappedSoul()
  ; A person killed as a werewolf feeds the hunger (Bloodmoon's WereChangeScript: "once an NPC is killed"); a creature doesn't.
  If akKiller && IsPerson && akKiller == Game.GetPlayer() && WerewolfKills && TMMwScript.IsWerewolf(akKiller)
    WerewolfKills.Mod(1.0)
  EndIf
EndEvent

Event OnCombatStateChanged(Actor akTarget, Int aeCombatState)
  If aeCombatState == 0
    wasInCombat = False
    SetActorValue("Variable02", 0.0)
    Return
  EndIf
  ; startCombat's shout: a fight begun.
  If !wasInCombat
    wasInCombat = True
    ResetGreeting()
    TMMwVoices v = Voices()
    If v
      v.Speak(Self, v.AttackTopic)
    EndIf
  EndIf
  ; CreatureTarget: 2 a werewolf, 1 a creature (not a person), else 0.
  Float kind = 0.0
  If akTarget
    If TMMwScript.IsWerewolf(akTarget)
      kind = 2.0
    ElseIf !akTarget.GetRace().IsPlayable()
      kind = 1.0
    EndIf
  EndIf
  SetActorValue("Variable02", kind)
EndEvent

Function Mirror()
  SetActorValue("Variable04", Reputation)
  SetActorValue("Variable05", Hello)
  SetActorValue("Variable06", Fight)
  SetActorValue("Variable07", Flee)
  SetActorValue("Variable08", Alarm)
EndFunction

Function SetReputationValue(Int aiValue)
  Reputation = aiValue
  SetActorValue("Variable04", Reputation)
EndFunction

Function ApplyAlarm()
  If Alarm >= 100
    SetActorValue("Morality", 3.0)
  Else
    SetActorValue("Morality", 0.0)
  EndIf
EndFunction

; ---- the scripts' Get/Set (TMMwScript.GetAi / SetAi) ----

Int Function GetSetting(String asWhich)
  If asWhich == "hello"
    Return Hello
  ElseIf asWhich == "fight"
    Return Fight
  ElseIf asWhich == "flee"
    Return Flee
  EndIf
  Return Alarm
EndFunction

Function SetSetting(String asWhich, Float afValue)
  Int v = afValue as Int
  If v < 0
    v = 0
  ElseIf v > 100
    v = 100
  EndIf
  If asWhich == "hello"
    Hello = v
  ElseIf asWhich == "fight"
    Fight = v
    ; A change takes at once, as the next frame's check would in Morrowind.
    If Is3DLoaded() && !IsInCombat() && ShouldAttack(Game.GetPlayer())
      StartCombat(Game.GetPlayer())
    EndIf
  ElseIf asWhich == "flee"
    Flee = v
  Else
    Alarm = v
    ApplyAlarm()
  EndIf
  Mirror()
EndFunction
