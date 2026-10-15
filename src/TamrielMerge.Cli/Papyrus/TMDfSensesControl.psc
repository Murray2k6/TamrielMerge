ScriptName TMDfSensesControl extends Quest
{Daggerfall's enemy senses (docs/daggerfall-senses.md; TMDfSenses on each of Daggerfall's hostile actors), for the
Iliac Bay only:
- what every enemy asks about the player, kept twice a second while any is loaded: in the Iliac Bay or not, moving at
  less than half speed (sneaking, or nearly standing still: Daggerfall Unity's IsMovingLessThanHalfSpeed), inside or
  out, invisible;
- the Stealth skill's use: one use of Sneak a game minute in which any enemy made the Stealth check on the player
  (PlayerEntity.TallySkill(Stealth, 1));
- Daggerfall Unity's GameManager.AreEnemiesNearby over the loaded enemies: "There are enemies nearby." closes the
  Sleep/Wait menu (DaggerfallUI's rest window; resting, an unaware enemy counts within 12 m), Skyrim's map travel is
  switched off while the map is open near them, and TMTravel asks before and during a journey ("cannotTravelWithEnemies
  Nearby"). The menu checks need SKSE (RegisterForMenu); without it Skyrim's own rule is all there is.}

Actor Property PlayerRef Auto
Location Property IliacBay Auto
{Daggerfall's world (DFLocIliacBay): the senses work where it is the player's location or a parent of it. None: everywhere
(a test plugin).}
Float Property SlowSpeed = 60.0 Auto
{Units a second under which the player counts as standing still (Skyrim's walk is ~100-130).}
Float Property StealthUses = 1.0 Auto
{Sneak skill uses for a minute of Stealth checks (Daggerfall's one).}
GlobalVariable[] Property Skills Auto
{Daggerfall's language skills (DFSkill<name>, 0-100): Etiquette, Streetwise, Centaurian, Daedric, Dragonish, Giantish,
Harpy, Impish, Nymph, Orcish, Spriggan.}
String[] Property SkillNames Auto
Int[] Property SkillAdvancement Auto
{Daggerfall Unity's advancement multipliers: 1 Etiquette and Streetwise, 15 the languages.}
FormList Property Encounters Auto
{The dungeon types' random encounter actors: what may come while the player rests in a dungeon.}
Float Property AlertHours = 8.0 Auto
{An enemy that saw the player keeps the dungeon alert this long (Daggerfall Unity EnemyAlertActive).}

Int[] skillUses
Float lastAlert = -100.0
Float restStart = -1.0
Form restBase
Cell restCell

Actor[] enemies
Int count = 0
Bool running = False
Bool inBay = False
Bool slow = False
Bool inside = False
Bool invisible = False
Float lastX
Float lastY
Float lastClock
Int lastTally = -1
Int ticks = 0
Bool mapOff = False

Event OnInit()
  Setup()
EndEvent

; At the start and after each load (TMDfSensesPlayer): menus are registered again and enemies no longer loaded let go.
Function Setup()
  If !enemies
    enemies = new Actor[128]
  EndIf
  RegisterForMenu("Sleep/Wait Menu")
  RegisterForMenu("MapMenu")
  RegisterForSleep()
  Prune()
  running = False
  If count > 0
    Wake()
  EndIf
EndFunction

; ---- the loaded enemies ----

Function Register(Actor a)
  If !enemies
    enemies = new Actor[128]
  EndIf
  If enemies.Find(a) >= 0
    If !running
      Wake()
    EndIf
    Return
  EndIf
  Int i = enemies.Find(None)
  If i >= 0
    enemies[i] = a
    count += 1
  EndIf
  ; One of the dungeon's random encounters: what a rest here may bring (its list picks again).
  Form b = a.GetActorBase()
  If Encounters && b && Encounters.HasForm(b)
    restBase = b
    restCell = a.GetParentCell()
  EndIf
  If !running
    Wake()
  EndIf
EndFunction

Function Unregister(Actor a)
  If !enemies
    Return
  EndIf
  Int i = enemies.Find(a)
  If i >= 0
    enemies[i] = None
    count -= 1
  EndIf
EndFunction

Function Prune()
  count = 0
  Int i = 0
  While i < enemies.Length
    Actor a = enemies[i]
    If a && (a.IsDead() || !a.Is3DLoaded())
      enemies[i] = None
    ElseIf a
      count += 1
    EndIf
    i += 1
  EndWhile
EndFunction

Function Wake()
  running = True
  lastClock = 0.0
  Refresh()
  RegisterForSingleUpdate(0.5)
EndFunction

Event OnUpdate()
  ticks += 1
  If ticks % 20 == 0
    Prune()
  EndIf
  If count <= 0
    running = False
    Return
  EndIf
  Refresh()
  RegisterForSingleUpdate(0.5)
EndEvent

; ---- the player ----

Bool Function InDaggerfall()
  Return !IliacBay || PlayerRef.IsInLocation(IliacBay)
EndFunction

Function Refresh()
  inBay = InDaggerfall()
  inside = PlayerRef.IsInInterior()
  invisible = PlayerRef.GetActorValue("Invisibility") > 0.0
  Float now = Utility.GetCurrentRealTime()
  Float x = PlayerRef.GetPositionX()
  Float y = PlayerRef.GetPositionY()
  Float dt = now - lastClock
  Float speed = 0.0
  If lastClock > 0.0 && dt > 0.05 && dt < 5.0
    speed = Math.sqrt((x - lastX) * (x - lastX) + (y - lastY) * (y - lastY)) / dt
  EndIf
  lastX = x
  lastY = y
  lastClock = now
  slow = speed < SlowSpeed || (PlayerRef.IsSneaking() && !PlayerRef.IsRunning() && !PlayerRef.IsSprinting())
EndFunction

; What an enemy asks each time it looks: 1 in the Iliac Bay, 2 moving at less than half speed, 4 inside, 8 invisible.
Int Function Status()
  If !running
    Wake()
  EndIf
  Int st = 0
  If inBay
    st += 1
  EndIf
  If slow
    st += 2
  EndIf
  If inside
    st += 4
  EndIf
  If invisible
    st += 8
  EndIf
  Return st
EndFunction

; An enemy made the Stealth check on the player this game minute.
Function StealthUsed(Int minute)
  If minute != lastTally
    lastTally = minute
    Game.AdvanceSkill("Sneak", StealthUses)
  EndIf
EndFunction

; ---- enemies nearby ----

; GameManager.AreEnemiesNearby: any of Daggerfall's hostile enemies that sees the player or would be spawned in classic
; around them (resting: an unaware one only within 12 m). Never outside the Iliac Bay.
Bool Function EnemiesNearby(Bool resting)
  If !enemies || !InDaggerfall()
    Return False
  EndIf
  Int i = 0
  While i < enemies.Length
    TMDfSenses s = enemies[i] as TMDfSenses
    If s && s.NearPlayer(resting)
      Return True
    EndIf
    i += 1
  EndWhile
  Return False
EndFunction

; ---- language skills (Daggerfall Unity FormulaHelper, EnemySenses; MIT) ----

Int Function SkillValue(Int i)
  If !Skills || i < 0 || i >= Skills.Length || !Skills[i]
    Return 0
  EndIf
  Return Skills[i].GetValueInt()
EndFunction

; TallySkill and RaiseSkills: a skill rises once its uses reach
; floor(skill x multiplier x 1.04^level x 2 / 5 + 1) (CalculateSkillUsesForAdvancement).
Function Tally(Int i, Int uses)
  If !Skills || i < 0 || i >= Skills.Length || !Skills[i]
    Return
  EndIf
  If !skillUses
    skillUses = new Int[16]
  EndIf
  skillUses[i] = skillUses[i] + uses
  If skillUses[i] > 20000
    skillUses[i] = 20000
  EndIf
  Int value = Skills[i].GetValueInt()
  Float levelMod = Math.pow(1.04, PlayerRef.GetLevel())
  Int needed = Math.Floor(value * SkillAdvancement[i] * levelMod * 2.0 / 5.0 + 1.0)
  If value < 100 && skillUses[i] >= needed
    skillUses[i] = 0
    Skills[i].SetValueInt(value + 1)
    Debug.Notification("Your " + SkillNames[i] + " skill has increased to " + (value + 1) + ".")
  EndIf
EndFunction

; CalculateEnemyPacification on first meeting: Etiquette or Streetwise skill / 10 + Personality / 5, a language
; skill + Personality / 10 (Skyrim's Speech stands for Personality); +10 with weapons sheathed, else -25; out of 200.
; Success tallies the skill 3 times, a failed language once.
Bool Function TryPacify(Actor enemy, Int lang)
  If lang < 0 || !Skills || lang >= Skills.Length
    Return False
  EndIf
  Int skill = SkillValue(lang)
  Int personality = PlayerRef.GetActorValue("Speechcraft") as Int
  Int chance
  If lang <= 1
    chance = skill / 10 + personality / 5
  Else
    chance = skill + personality / 10
  EndIf
  If PlayerRef.IsWeaponDrawn()
    chance -= 25
  Else
    chance += 10
  EndIf
  If Utility.RandomInt(0, 199) < chance
    Debug.Notification(enemy.GetDisplayName() + " pacified using " + SkillNames[lang] + " skill.")
    Tally(lang, 3)
    Return True
  ElseIf lang > 1
    Tally(lang, 1)
  EndIf
  Return False
EndFunction

; ---- rest in a dungeon (PlayerEntity.IntermittentEnemySpawn) ----

; An enemy saw the player: the dungeon is on alert for AlertHours.
Function Alert()
  lastAlert = Utility.GetCurrentGameTime()
EndFunction

; A rest that ended: in a dungeon on alert, every 12 minutes of it gave a 1 in 36 chance that one of the dungeon's
; random encounters came; the rest ends when it does (the hours after it are given back) and the enemy is 8-20 m off.
Function RestEnded()
  Float now = Utility.GetCurrentGameTime()
  Float start = restStart
  restStart = -1.0
  If start < 0.0 || now <= start || !InDaggerfall() || !PlayerRef.IsInInterior() || !restBase || PlayerRef.GetParentCell() != restCell
    Return
  EndIf
  If start - lastAlert > AlertHours / 24.0
    Return
  EndIf
  Int rolls = ((now - start) * 1440.0 / 12.0) as Int
  Int k = 1
  Int at = -1
  While k <= rolls && at < 0
    If Utility.RandomInt(0, 35) == 0
      at = k
    EndIf
    k += 1
  EndWhile
  If at < 0
    Return
  EndIf
  ; Hours after the enemy came are given back (within the same day).
  GlobalVariable hour = Game.GetFormFromFile(0x38, "Skyrim.esm") as GlobalVariable
  Float back = (now - start) * 24.0 - at * 0.2
  If hour && back > 0.0 && hour.GetValue() - back >= 0.0
    hour.SetValue(hour.GetValue() - back)
  EndIf
  ObjectReference spot = SpawnSpot(640.0, 1600.0)
  Actor foe = spot.PlaceAtMe(restBase, 1, False, False) as Actor
  If foe
    Debug.Notification("You are awakened by an enemy.")
    foe.StartCombat(PlayerRef)
  EndIf
EndFunction

; A place 8-20 m from the player where something already stands (an actor of the dungeon, alive or dead), else the
; player's own spot.
ObjectReference Function SpawnSpot(Float near, Float far)
  Cell c = PlayerRef.GetParentCell()
  If c
    Int n = c.GetNumRefs(62)
    Int tries = 0
    While n > 0 && tries < 24
      ObjectReference r = c.GetNthRef(Utility.RandomInt(0, n - 1), 62)
      If r && r != PlayerRef
        Float d = r.GetDistance(PlayerRef)
        If d >= near && d <= far
          Return r
        EndIf
      EndIf
      tries += 1
    EndWhile
  EndIf
  Return PlayerRef
EndFunction

Event OnMenuOpen(String asMenuName)
  If asMenuName == "Sleep/Wait Menu"
    If EnemiesNearby(True)
      Input.TapKey(1)   ; closed, as Escape would
      Debug.MessageBox("There are enemies nearby.")
    Else
      restStart = Utility.GetCurrentGameTime()
    EndIf
  ElseIf asMenuName == "MapMenu"
    If Game.IsFastTravelControlsEnabled() && EnemiesNearby(False)
      Game.EnableFastTravel(False)
      mapOff = True
      Debug.Notification("You cannot travel with enemies nearby.")
    EndIf
  EndIf
EndEvent

; Sleeping passes the time after the menu has closed.
Event OnSleepStart(Float afSleepStartTime, Float afDesiredSleepEndTime)
  restStart = afSleepStartTime
EndEvent

Event OnSleepStop(Bool abInterrupted)
  RestEnded()
EndEvent

Event OnMenuClose(String asMenuName)
  If asMenuName == "Sleep/Wait Menu"
    RestEnded()
  EndIf
  If asMenuName == "MapMenu" && mapOff
    mapOff = False
    Game.EnableFastTravel(True)
  EndIf
EndEvent
