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

Event OnMenuOpen(String asMenuName)
  If asMenuName == "Sleep/Wait Menu"
    If EnemiesNearby(True)
      Input.TapKey(1)   ; closed, as Escape would
      Debug.MessageBox("There are enemies nearby.")
    EndIf
  ElseIf asMenuName == "MapMenu"
    If Game.IsFastTravelControlsEnabled() && EnemiesNearby(False)
      Game.EnableFastTravel(False)
      mapOff = True
      Debug.Notification("You cannot travel with enemies nearby.")
    EndIf
  EndIf
EndEvent

Event OnMenuClose(String asMenuName)
  If asMenuName == "MapMenu" && mapOff
    mapOff = False
    Game.EnableFastTravel(True)
  EndIf
EndEvent
