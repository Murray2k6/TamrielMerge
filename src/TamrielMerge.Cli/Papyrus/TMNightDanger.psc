ScriptName TMNightDanger extends Quest
{Nights in town (docs/progression.md, "Nights in town"), for every converted game that has towns:
- Guards (each game's "Guards" tag) may doze on night watch: once a night each guard near the player has a DozeChance%
  chance to nod off (TMDozeEffect: unconscious, so they witness nothing) until dawn, or until struck or spoken to.
- Morrowind's and Oblivion's towns ("TownCentres" markers, within TownRadius) are dangerous after dark: every Interval
  seconds outdoors there is a SpawnChance% chance that one of Skyrim's thieves or assassins comes out of the dark behind
  the player (at most MaxStalkers at a time); those still lurking at dawn slip away. Daggerfall's towns carry their own
  night stalkers from its encounter tables (TownBuilder).
Night is 20:00 to 6:00. TMNightDangerOn (1) turns it all off at 0.}

Actor Property PlayerRef Auto
GlobalVariable Property Enabled Auto
GlobalVariable Property GameHour Auto
Spell Property Doze Auto
String[] Property GuardPlugins Auto
Int[] Property GuardIds Auto
String[] Property TownPlugins Auto
Int[] Property TownIds Auto
Float Property TownRadius = 8000.0 Auto
Float Property Interval = 40.0 Auto
Int Property SpawnChance = 20 Auto
Int Property DozeChance = 30 Auto
Int Property MaxStalkers = 2 Auto
Float Property DuskHour = 20.0 Auto
Float Property DawnHour = 6.0 Auto

Faction[] guards
FormList[] towns
ActorBase[] stalkers
Actor[] lurking
Actor[] dozing
Actor[] decided
Int decidedNext = 0
Bool wasNight = False

; After each load (TMProgression.Setup): find the converted games' guards and towns, and start the rounds.
Function Setup()
  guards = new Faction[8]
  Int n = 0
  Int i = 0
  While GuardIds && i < GuardIds.Length && n < 8
    Faction f = Game.GetFormFromFile(GuardIds[i], GuardPlugins[i]) as Faction
    If f
      guards[n] = f
      n += 1
    EndIf
    i += 1
  EndWhile
  towns = new FormList[8]
  n = 0
  i = 0
  While TownIds && i < TownIds.Length && n < 8
    FormList l = Game.GetFormFromFile(TownIds[i], TownPlugins[i]) as FormList
    If l
      towns[n] = l
      n += 1
    EndIf
    i += 1
  EndWhile
  ; Skyrim's world-encounter thieves and assassins.
  stalkers = new ActorBase[4]
  stalkers[0] = Game.GetFormFromFile(0x1034E7, "Skyrim.esm") as ActorBase   ; LvlWEThief
  stalkers[1] = Game.GetFormFromFile(0x1051F1, "Skyrim.esm") as ActorBase   ; LvlWEThiefRanged
  stalkers[2] = Game.GetFormFromFile(0x1051FC, "Skyrim.esm") as ActorBase   ; LvlWEAssassin
  stalkers[3] = Game.GetFormFromFile(0x1051FD, "Skyrim.esm") as ActorBase   ; LvlWEAssassinRanged
  If !lurking
    lurking = new Actor[4]
    dozing = new Actor[16]
    decided = new Actor[32]
  EndIf
  RegisterForSingleUpdate(Interval)
EndFunction

Bool Function IsNight()
  Float hour = GameHour.GetValue()
  Return hour >= DuskHour || hour < DawnHour
EndFunction

Event OnUpdate()
  If Enabled && Enabled.GetValue() > 0 && GameHour && guards
    Bool night = IsNight()
    If night
      If !Utility.IsInMenuMode()
        NodOff()
        Lurk()
      EndIf
    ElseIf wasNight
      Dawn()
    EndIf
    wasNight = night
  EndIf
  RegisterForSingleUpdate(Interval)
EndEvent

; ---- Guards ----

Bool Function IsGuard(Actor a)
  Int i = 0
  While i < guards.Length
    If guards[i] && a.IsInFaction(guards[i])
      Return True
    EndIf
    i += 1
  EndWhile
  Return False
EndFunction

Function NodOff()
  Int tries = 0
  While tries < 3
    Actor a = Game.FindRandomActorFromRef(PlayerRef, 4000.0)
    If a && a != PlayerRef && !a.IsDead() && !a.IsInCombat() && decided.Find(a) < 0 && IsGuard(a)
      decided[decidedNext] = a
      decidedNext = (decidedNext + 1) % decided.Length
      Int slot = dozing.Find(None)
      If slot >= 0 && Utility.RandomInt(1, 100) <= DozeChance && !a.HasSpell(Doze)
        a.AddSpell(Doze, False)
        dozing[slot] = a
      EndIf
    EndIf
    tries += 1
  EndWhile
EndFunction

; ---- The town in the dark ----

; The town centre the player is near, of any converted game's town lists, or None.
ObjectReference Function NearTown()
  WorldSpace here = PlayerRef.GetWorldSpace()
  Int i = 0
  While i < towns.Length
    If towns[i]
      Int j = 0
      Int count = towns[i].GetSize()
      While j < count
        ObjectReference centre = towns[i].GetAt(j) as ObjectReference
        If centre && centre.GetWorldSpace() == here && PlayerRef.GetDistance(centre) < TownRadius
          Return centre
        EndIf
        j += 1
      EndWhile
    EndIf
    i += 1
  EndWhile
  Return None
EndFunction

Function Lurk()
  If PlayerRef.IsInInterior() || PlayerRef.IsInCombat() || Utility.RandomInt(1, 100) > SpawnChance
    Return
  EndIf
  Int alive = 0
  Int free = -1
  Int i = 0
  While i < lurking.Length
    If lurking[i] && !lurking[i].IsDead()
      alive += 1
    ElseIf free < 0
      free = i
    EndIf
    i += 1
  EndWhile
  If alive >= MaxStalkers || free < 0 || !NearTown()
    Return
  EndIf
  ActorBase who = stalkers[Utility.RandomInt(0, 3)]
  Form marker = Game.GetFormFromFile(0x00003B, "Skyrim.esm")
  If !who || !marker
    Return
  EndIf
  ; Out of the dark behind the player.
  Float a = PlayerRef.GetAngleZ()
  ObjectReference spot = PlayerRef.PlaceAtMe(marker)
  spot.MoveTo(PlayerRef, -Math.Sin(a) * 1400.0, -Math.Cos(a) * 1400.0, 0.0)
  Actor stalker = spot.PlaceAtMe(who) as Actor
  spot.Delete()
  If stalker
    stalker.StartCombat(PlayerRef)
    lurking[free] = stalker
    Debug.Trace("[TamrielMerge] night: " + stalker + " comes for the player")
  EndIf
EndFunction

; Dawn: the dozers wake, the night's people who are still about slip away.
Function Dawn()
  Int i = 0
  While i < dozing.Length
    If dozing[i]
      dozing[i].RemoveSpell(Doze)
      dozing[i] = None
    EndIf
    i += 1
  EndWhile
  i = 0
  While i < lurking.Length
    If lurking[i] && !lurking[i].IsDead() && !lurking[i].IsInCombat()
      lurking[i].Disable()
      lurking[i].Delete()
    EndIf
    lurking[i] = None
    i += 1
  EndWhile
  i = 0
  While i < decided.Length
    decided[i] = None
    i += 1
  EndWhile
EndFunction
