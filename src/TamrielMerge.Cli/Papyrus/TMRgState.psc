ScriptName TMRgState extends Quest
{What Redguard's converted scripts (TMRgSoup) share, on the always-running quest RGScripts: the lists that name
Redguard's flags (globals, by SOUP386.DEF index), items (by ITEM.INI index), sound effects (MAIN.SFX, by index) and
script lines (spoken by RGLine index), Cyrus's log, the spells that stand in for Redguard's fireballs and dragon
breath, the potion mixing state (GetPotion/SetPotion), the player's selected item and how many scripts hold the
player's controls; and what stands in for Redguard's engine where Skyrim has none of its own: camera shots, group
synchronisation, animation groups as Skyrim's idles, the movies (Bink), the screen pictures (a page showing the
image), the hand item, the player flown along a script's path, and hanging from objects (TMRgMove).}

FormList Property Flags Auto
FormList Property Items Auto
FormList Property Sounds Auto
FormList Property Lines Auto
Quest Property LogQuest Auto
Spell Property Fireball Auto
Spell Property Breath Auto
Spell Property Zap Auto
GlobalVariable[] Property MenuShow Auto
{Which buttons of a script's menu show (RGMenuShow0-9), as Redguard's menuAddItem added them.}
FormList Property Idles Auto
{Redguard's animation groups (SOUP386.DEF order) as Skyrim's idles for people (an empty entry: no counterpart).}
FormList Property PlayerIdles Auto
{The same for Cyrus, whose numbered "misc" groups are his own (drinking, using an item).}
Idle Property IdleStop Auto
{IdleForceDefaultState: back to standing.}
FormList Property Pictures Auto
{The screen pictures (ViewGXA's images) as notes that show the image, by the scripts' picture number.}
TMRgMove Property Mover Auto
{Ledges, ropes and hanging (the quest RGMovement).}
GlobalVariable Property FirstPersonAfterShots Auto
{1: after a cutscene that showed Cyrus in third person, the camera goes back to first person (0: it stays, as
Redguard's own camera did).}

Int[] potions
Int Property SelectedItem = 1 Auto Hidden
Int lockouts = 0
Float Property DialogDue = 0.0 Auto Hidden

Event OnInit()
  potions = new Int[32]
EndEvent

Int Function GetPotion(Int i)
  If !potions
    potions = new Int[32]
  EndIf
  If i < 0 || i >= potions.Length
    Return 0
  EndIf
  Return potions[i]
EndFunction

Function SetPotion(Int i, Int v)
  If !potions
    potions = new Int[32]
  EndIf
  If i >= 0 && i < potions.Length
    potions[i] = v
  EndIf
EndFunction

; Redguard's lockoutPlayer(1/0): scripts nest it, so controls come back when the last one lets go (and a camera shot
; of the cutscene ends with it).
Function Lockout(Bool on)
  If on
    lockouts += 1
    If lockouts == 1
      Game.DisablePlayerControls(True, True, False, False, True, True, True)
    EndIf
  ElseIf lockouts > 0
    lockouts -= 1
    If lockouts == 0
      If !flying && !hangingOn
        Game.EnablePlayerControls()
      EndIf
      EndShot()
    EndIf
  EndIf
EndFunction

Bool Function LockedOut()
  Return lockouts > 0
EndFunction

Function ClearMenu()
  If !MenuShow
    Return
  EndIf
  Int i = 0
  While i < MenuShow.Length
    MenuShow[i].SetValue(0)
    i += 1
  EndWhile
EndFunction

Function ShowMenuItem(Int i)
  If MenuShow && i >= 0 && i < MenuShow.Length
    MenuShow[i].SetValue(1)
  EndIf
EndFunction

Int Function Flag(Int i)
  GlobalVariable g = Flags.GetAt(i) as GlobalVariable
  If g
    Return g.GetValueInt()
  EndIf
  Return 0
EndFunction

Function SetFlag(Int i, Int v)
  GlobalVariable g = Flags.GetAt(i) as GlobalVariable
  If g
    g.SetValueInt(v)
  EndIf
EndFunction

Form Function Item(Int i)
  If i < 0 || i >= Items.GetSize()
    Return None
  EndIf
  Return Items.GetAt(i)
EndFunction

Float Function Atan2(Float y, Float x)
  If x > 0.0
    Return Math.atan(y / x)
  ElseIf x < 0.0
    If y >= 0.0
      Return Math.atan(y / x) + 180.0
    EndIf
    Return Math.atan(y / x) - 180.0
  ElseIf y > 0.0
    Return 90.0
  ElseIf y < 0.0
    Return -90.0
  EndIf
  Return 0.0
EndFunction

; ---- camera shots ----

Bool shotThird = False

; A shot of Cyrus (showCyrus*: the camera somewhere looking at him): Skyrim's third-person camera shows him.
Function ShotOfCyrus()
  If !shotThird
    shotThird = True
    Game.ForceThirdPerson()
  EndIf
EndFunction

; The camera back on the player (showPlayer, XResetCamera, the end of a cutscene).
Function EndShot()
  If shotThird && !flying
    shotThird = False
    If FirstPersonAfterShots && FirstPersonAfterShots.GetValueInt() == 1
      Game.ForceFirstPerson()
    EndIf
  EndIf
EndFunction

; A shot of something else (showObj*, lookObj): the player's view turned to look at it, at its target height.
Function ShotOf(ObjectReference target, Float lookHeight)
  Actor p = Game.GetPlayer()
  If !target || target == p || flying
    Return
  EndIf
  Float dx = target.GetPositionX() - p.GetPositionX()
  Float dy = target.GetPositionY() - p.GetPositionY()
  Float flat = Math.sqrt(dx * dx + dy * dy)
  If flat < 24.0
    Return
  EndIf
  ; The player's eyes are about 120 above their feet.
  Float dz = target.GetPositionZ() + lookHeight - (p.GetPositionZ() + 120.0)
  Float pitch = -Math.atan(dz / flat)
  If pitch > 60.0
    pitch = 60.0
  ElseIf pitch < -60.0
    pitch = -60.0
  EndIf
  p.SetAngle(pitch, 0.0, Atan2(dx, dy))
EndFunction

; ---- group synchronisation (SyncWithGroup) ----

Int[] syncCount
Int[] syncGen

Function SyncSetup()
  If !syncCount
    syncCount = new Int[128]
    syncGen = new Int[128]
  EndIf
EndFunction

; A member reaching a sync point: the barrier's generation it waits on, or -1 when it was the last to arrive (all go on).
Int Function SyncArrive(Int group, Int members)
  SyncSetup()
  If group < 0 || group >= 128
    Return -1
  EndIf
  syncCount[group] = syncCount[group] + 1
  If syncCount[group] >= members
    syncCount[group] = 0
    syncGen[group] = syncGen[group] + 1
    Return -1
  EndIf
  Return syncGen[group]
EndFunction

Int Function SyncGeneration(Int group)
  SyncSetup()
  If group < 0 || group >= 128
    Return 0
  EndIf
  Return syncGen[group]
EndFunction

; Releases everyone waiting (ResetGroupSync, or a member that waited too long for one that isn't running).
Function SyncReset(Int group)
  SyncSetup()
  If group >= 0 && group < 128
    syncCount[group] = 0
    syncGen[group] = syncGen[group] + 1
  EndIf
EndFunction

; ---- animation groups as idles ----

Idle Function IdleFor(Int anim, Bool cyrus)
  FormList l = Idles
  If cyrus && PlayerIdles
    l = PlayerIdles
  EndIf
  If !l || anim < 0 || anim >= l.GetSize()
    Return None
  EndIf
  Return l.GetAt(anim) as Idle
EndFunction

; ---- hand items (HandItem: what Cyrus holds for an animation) ----

Form handForm

; A weapon or torch Skyrim equips; none (item 0's call, an empty hand) takes back what was equipped for it. Potions
; and the rest are Skyrim's own to show (drinking a potion plays its own sound and effect).
Function HandItem(Actor a, Form f)
  If !a
    Return
  EndIf
  If !f
    If handForm && a.IsEquipped(handForm)
      a.UnequipItem(handForm, False, True)
    EndIf
    handForm = None
    Return
  EndIf
  If (f as Weapon) || (f as Light)
    If a.GetItemCount(f) > 0
      a.EquipItem(f, False, True)
      handForm = f
    EndIf
  EndIf
EndFunction

; ---- movies (QueueMovie: MENU.INI's movie_name[n], converted to Bink by the videos step) ----

Function PlayMovie(Int n)
  Game.PlayBink("TamrielMerge_RedguardMovie" + n + ".bik", True, True, True, True)
EndFunction

; ---- screen pictures ----

; ViewGXA: the image full page, as a note the player reads (closing it goes back to the game).
Function ShowPicture(Int i)
  If !Pictures || i < 0 || i >= Pictures.GetSize()
    Return
  EndIf
  Form f = Pictures.GetAt(i)
  If !f
    Return
  EndIf
  Actor p = Game.GetPlayer()
  ObjectReference page = p.PlaceAtMe(f, 1, False, True)
  page.Enable()
  page.Activate(p)
  Utility.Wait(0.2)
  page.Disable()
  page.Delete()
EndFunction

; ShowBitmap/UnShowBitmap: COMMON's status icons while the strength (0) and ironskin (1) potions last, as a line when
; they come and go.
Bool[] statusShown

Function Status(Int i, Bool on)
  If !statusShown
    statusShown = new Bool[8]
  EndIf
  If i < 0 || i >= 8 || statusShown[i] == on
    Return
  EndIf
  statusShown[i] = on
  String what = "Strength potion"
  If i == 1
    what = "Ironskin potion"
  EndIf
  If on
    Debug.Notification(what + ": in effect")
  Else
    Debug.Notification(what + ": worn off")
  EndIf
EndFunction

; ---- the player flown along a script's path (the canah's flight between the shrines) ----

Bool flying = False
Float flyX
Float flyY
Float flyZ
Float flyUntil

Bool Function Flying()
  Return flying
EndFunction

; Redguard set Cyrus's position far away while he was held (a flight): Skyrim's player is carried there along a curve
; over the time the script gives it (a next leg redirects it).
Function FlyTo(Float x, Float y, Float z, Float seconds)
  Actor p = Game.GetPlayer()
  Float dx = x - p.GetPositionX()
  Float dy = y - p.GetPositionY()
  Float dz = z - p.GetPositionZ()
  Float d = Math.sqrt(dx * dx + dy * dy + dz * dz)
  If d < 1.0
    Return
  EndIf
  If seconds < 0.5
    seconds = 0.5
  EndIf
  Float speed = d / seconds
  If speed < 400.0
    speed = 400.0
  EndIf
  If !flying
    Game.DisablePlayerControls(True, True, False, False, True, True, True)
    If !shotThird
      shotThird = True
      Game.ForceThirdPerson()
    EndIf
  EndIf
  flying = True
  flyX = x
  flyY = y
  flyZ = z
  flyUntil = Utility.GetCurrentRealTime() + d / speed + 0.5
  p.SplineTranslateTo(x, y, z, 0.0, 0.0, Atan2(dx, dy), d * 0.3, speed, 0.0)
  RegisterForSingleUpdate(d / speed + 0.3)
EndFunction

Event OnUpdate()
  If flying
    If Utility.GetCurrentRealTime() < flyUntil
      RegisterForSingleUpdate(0.3)
      Return
    EndIf
    Actor p = Game.GetPlayer()
    p.StopTranslation()
    p.SetPosition(flyX, flyY, flyZ)
    flying = False
    If lockouts == 0 && !hangingOn
      Game.EnablePlayerControls()
      EndShot()
    EndIf
  EndIf
EndEvent

; ---- hanging from objects (PlayerHanging; TMRgMove holds the player there) ----

ObjectReference hangingOn

ObjectReference Function HangingOn()
  Return hangingOn
EndFunction

Function SetHanging(ObjectReference r)
  hangingOn = r
EndFunction
