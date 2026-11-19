ScriptName TMMwAfflictions extends ReferenceAlias
{The player, on the quest MWAfflictions: what Morrowind's engine does about the player's vampirism, corprus and
werewolf form that its scripts don't (the scripts - VampireCheck, Vampire_*_PC, Vampire_Cure_PC, vampdreamScript,
WereCheckScript, WereChangeScript, werehungerScript - are translated and carry the rest: the three days, the clan,
the abilities, the cure, the night from 21:00 to 6:00, the hunger).

- Vampire look: while the player has the Vampirism effect (the ability "vampire sun damage") they are Skyrim's
  vampire form of their race (OpenMW npcanimation.cpp: the vampire head while the effect's magnitude is above 0;
  Skyrim's own vampirism swaps the race the same way), swapped back once it is gone. Only a swap made here is undone,
  so Skyrim's own vampires are left as they are.
- Corprus cure: when the "corprus" spell is gone (Divayth Fyr's potion) the drains come back, a day's worth for each
  worsening TMMwCorprus made, and the fortifies stay (OpenMW spelleffects.cpp removeMagicEffect, Corprus: the harmful
  applied-once effects removed once per worsening; the rest kept).
- Werewolf form (Skyrim's beast form, which BecomeWerewolf casts): on the change the night's kills start at none
  (OpenMW NpcStats::setWerewolf), Morrowind's attributes become the fWereWolf* settings' (Player::setWerewolfStats:
  Strength, Agility, Endurance 150, Speed 90, Luck 25, Intelligence, Willpower, Personality 0 in Bloodmoon) and come
  back on the change back (Player::restoreStats), and whoever sees the change (OpenMW mechanicsmanagerimp.cpp
  setWerewolf: a person within fAlarmRadius with a line of sight who notices the player) makes the player a known
  werewolf (PCKnownWerewolf, sWerewolfAlarmMessage), and one who reports crimes (Alarm above 0) adds iWereWolfBounty
  to the bounty, unless Skyrim's own crime (the crime faction's werewolf value, the same setting) already did. The
  beast form's skills, health, speed and claws are Skyrim's own.}

GlobalVariable Property WerewolfKills Auto
{MWWerewolfKills (TMMwActorAI counts them).}
Faction Property Crime Auto
{MWCrimeFaction.}
Quest Property MwState Auto
{MWScriptState (TMMwState): Morrowind's attributes.}
GlobalVariable Property KnownWerewolf Auto
{Bloodmoon's PCKnownWerewolf (None without Bloodmoon).}
String Property AlarmMessage Auto
{sWerewolfAlarmMessage.}
Float Property AlarmRadius = 2000.0 Auto
{fAlarmRadius.}
Int Property WerewolfBounty = 1000 Auto
{iWereWolfBounty.}
Race Property BeastRace Auto
{WerewolfBeastRace.}
Float[] Property WerewolfAttributes Auto
{fWereWolfStrength .. fWereWolfLuck, in TMMwState's order (Strength, Intelligence, Willpower, Agility, Speed,
Endurance, Personality, Luck).}
MagicEffect Property VampirismEffect Auto
{The converted Vampirism effect (TMMwVampirism).}
Race[] Property BaseRaces Auto
{Skyrim's races of Morrowind's, and their vampire forms, by index.}
Race[] Property VampireRaces Auto
Spell Property CorprusDisease Auto
{The converted "corprus".}
GlobalVariable Property CorprusWorsenings Auto
{MWCorprusWorsenings.}
Int[] Property CorprusDrainAttributes Auto
{The corprus record's Drain Attribute effects: attributes and magnitudes.}
Float[] Property CorprusDrainMagnitudes Auto
Float Property Poll = 5.0 AutoReadOnly

Float[] savedAttributes
Bool wolfStats = False
Bool vampireForm = False
Int bountyAtChange = -1

Event OnInit()
  RegisterForSingleUpdate(Poll)
EndEvent

Event OnPlayerLoadGame()
  RegisterForSingleUpdate(Poll)
EndEvent

Event OnUpdate()
  Actor p = GetActorReference()
  If p && !p.IsDead()
    CheckVampire()
    CheckCorprus()
    ; A change missed (the race switched while this script wasn't listening): the stats follow the form.
    Bool beast = BeastRace && p.GetRace() == BeastRace
    If beast != wolfStats
      WolfStats(beast)
    EndIf
  EndIf
  RegisterForSingleUpdate(Poll)
EndEvent

; TMMwScript.BecomeWerewolf, before the change spell is cast: the kills start again (setWerewolf) and the bounty is
; noted, so the alarm can tell whether Skyrim's crime already charged the change.
Function ChangeBegins()
  If WerewolfKills
    WerewolfKills.SetValue(0.0)
  EndIf
  If Crime
    bountyAtChange = Crime.GetCrimeGold()
  Else
    bountyAtChange = -1
  EndIf
EndFunction

Event OnRaceSwitchComplete()
  Actor p = GetActorReference()
  If !p
    Return
  EndIf
  If BeastRace && p.GetRace() == BeastRace
    If WerewolfKills
      WerewolfKills.SetValue(0.0)
    EndIf
    WolfStats(True)
    Witnesses(p)
    bountyAtChange = -1
  ElseIf wolfStats
    WolfStats(False)
  EndIf
EndEvent

; setWerewolfStats / restoreStats on Morrowind's attributes.
Function WolfStats(Bool abOn)
  Actor p = GetActorReference()
  TMMwState mw = MwState as TMMwState
  If !p || !mw
    wolfStats = abOn
    Return
  EndIf
  If abOn
    savedAttributes = new Float[8]
    Int k = 0
    While k < 8
      savedAttributes[k] = mw.GetStat(p, k, -1)
      If WerewolfAttributes && k < WerewolfAttributes.Length
        mw.SetStat(p, k, -1, WerewolfAttributes[k])
      EndIf
      k += 1
    EndWhile
  ElseIf savedAttributes
    Int k = 0
    While k < 8 && k < savedAttributes.Length
      mw.SetStat(p, k, -1, savedAttributes[k])
      k += 1
    EndWhile
  EndIf
  wolfStats = abOn
EndFunction

; MechanicsManager::setWerewolf's witnesses: each Morrowind person within fAlarmRadius with a line of sight who
; notices the player (awarenessCheck) detects the change; one whose Alarm is above 0 reports it.
Function Witnesses(Actor p)
  Cell here = p.GetParentCell()
  If !here
    Return
  EndIf
  Bool detected = False
  Bool reported = False
  Int i = here.GetNumRefs(62)
  While i > 0
    i -= 1
    TMMwActorAI mw = here.GetNthRef(i, 62) as TMMwActorAI
    If mw && mw.IsPerson && mw != p && !mw.IsDead() && mw.GetDistance(p) <= AlarmRadius
      If mw.HasLOS(p) && p.IsDetectedBy(mw)
        detected = True
        If mw.Alarm > 0
          reported = True
        EndIf
      EndIf
    EndIf
  EndWhile
  If !detected
    Return
  EndIf
  If AlarmMessage != ""
    Debug.Notification(AlarmMessage)
  EndIf
  If KnownWerewolf
    KnownWerewolf.SetValueInt(1)
  EndIf
  If reported && Crime && bountyAtChange >= 0 && Crime.GetCrimeGold() <= bountyAtChange
    Crime.ModCrimeGold(WerewolfBounty, False)
  EndIf
EndFunction

; The vampire look: Skyrim's vampire form of the player's race while the Vampirism effect runs, the race back after.
Function CheckVampire()
  Actor p = GetActorReference()
  If !p || !BaseRaces || !VampireRaces || (BeastRace && p.GetRace() == BeastRace)
    Return
  EndIf
  Bool vampire = VampirismEffect && p.HasMagicEffect(VampirismEffect)
  Race r = p.GetRace()
  If vampire && !vampireForm
    Int b = BaseRaces.Find(r)
    If b >= 0 && b < VampireRaces.Length && VampireRaces[b]
      vampireForm = True
      p.SetRace(VampireRaces[b])
    EndIf
  ElseIf !vampire && vampireForm
    vampireForm = False
    Int v = VampireRaces.Find(r)
    If v >= 0 && v < BaseRaces.Length && BaseRaces[v]
      p.SetRace(BaseRaces[v])
    EndIf
  EndIf
EndFunction

; The corprus cure: the spell gone, the drains undone a worsening at a time, the fortifies kept.
Function CheckCorprus()
  If !CorprusWorsenings || !CorprusDisease
    Return
  EndIf
  Int n = CorprusWorsenings.GetValueInt()
  Actor p = GetActorReference()
  If n <= 0 || !p || p.HasSpell(CorprusDisease)
    Return
  EndIf
  TMMwState mw = MwState as TMMwState
  If mw && CorprusDrainAttributes && CorprusDrainMagnitudes
    Int i = 0
    While i < CorprusDrainAttributes.Length && i < CorprusDrainMagnitudes.Length
      mw.ModStat(p, CorprusDrainAttributes[i], -1, CorprusDrainMagnitudes[i] * n)
      i += 1
    EndWhile
  EndIf
  CorprusWorsenings.SetValue(0.0)
EndFunction
