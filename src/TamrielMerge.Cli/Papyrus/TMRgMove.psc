ScriptName TMRgMove extends Quest
{What Cyrus could do in Redguard and Skyrim's player can't, played by script (the quest RGMovement):
- ledges: Redguard's Cyrus caught any ledge he jumped at and pulled himself up. The build marks the ledges of
  Redguard's places that are too high to jump onto (RGLedgeMarker, at the top edge, facing onto it); jumping at one
  within reach carries the player up onto it (controls held for the moment, TranslateTo).
- ropes: Redguard's ropes (MPRP) are activators (TMRgRope) the player swings on, from where they stand to the
  landing on the other side the build found (or up to the floor the rope hangs from).
- hanging: objects whose scripts test PlayerHanging (the palace chandeliers, the dwarven machines' handles, rope
  bridges) are grabbed by activating them from below; the player hangs under them, carried as they move, until
  activating again (or after 20 seconds) lets go.}

TMRgState Property RG Auto
Static Property LedgeMarker Auto
{The base of the ledge markers.}
GlobalVariable Property ClimbReach Auto
{How far above the player's feet a ledge can be caught (Redguard's reach; default 240).}

Bool busy = False
ObjectReference hanging = None
Float hangX
Float hangY
Float hangZ
Float hangUntil
ObjectReference probe

Event OnInit()
  RegisterForSingleUpdate(2.0)
EndEvent

Event OnUpdate()
  If hanging
    Hold()
    Return
  EndIf
  If busy
    RegisterForSingleUpdate(0.5)
    Return
  EndIf
  Actor p = Game.GetPlayer()
  ; Only where Redguard's ledges are (one within a few cells): quick checks there, slow ones elsewhere.
  If !LedgeMarker || !Game.FindClosestReferenceOfTypeFromRef(LedgeMarker, p, 3000.0)
    RegisterForSingleUpdate(2.0)
    Return
  EndIf
  If p.GetAnimationVariableBool("bInJumpState") && !p.IsSwimming() && !RG.LockedOut()
    ObjectReference ledge = LedgeAhead(p)
    If ledge
      Climb(p, ledge)
    EndIf
  EndIf
  RegisterForSingleUpdate(0.1)
EndEvent

; The ledge the player is jumping at: the nearest marker to a point just ahead of them at reaching height, within
; reach above their feet and in front of them.
ObjectReference Function LedgeAhead(Actor p)
  If !probe
    probe = p.PlaceAtMe(Game.GetFormFromFile(0x0000003B, "Skyrim.esm"), 1, True, False)
  EndIf
  Float a = p.GetAngleZ()
  probe.SetPosition(p.GetPositionX() + Math.sin(a) * 56.0, p.GetPositionY() + Math.cos(a) * 56.0, p.GetPositionZ() + 140.0)
  ObjectReference ledge = Game.FindClosestReferenceOfTypeFromRef(LedgeMarker, probe, 150.0)
  If !ledge
    Return None
  EndIf
  Float reach = 240.0
  If ClimbReach && ClimbReach.GetValue() > 0.0
    reach = ClimbReach.GetValue()
  EndIf
  Float dz = ledge.GetPositionZ() - p.GetPositionZ()
  If dz < 40.0 || dz > reach
    Return None
  EndIf
  Float h = p.GetHeadingAngle(ledge)
  If h > 70.0 || h < -70.0
    Return None
  EndIf
  Return ledge
EndFunction

; Pulled up onto the ledge: hands on its edge, then up and over onto its top.
Function Climb(Actor p, ObjectReference ledge)
  busy = True
  Game.DisablePlayerControls(True, True, False, False, True, False, False)
  Float f = ledge.GetAngleZ()
  Float sx = Math.sin(f)
  Float sy = Math.cos(f)
  Float lx = ledge.GetPositionX()
  Float ly = ledge.GetPositionY()
  Float lz = ledge.GetPositionZ()
  ; Hanging at the edge: body against the wall, hands on the top.
  Float hz = lz - 110.0
  If hz < p.GetPositionZ()
    hz = p.GetPositionZ()
  EndIf
  Float hx = lx - sx * 30.0
  Float hy = ly - sy * 30.0
  Float d = Dist(p, hx, hy, hz)
  p.TranslateTo(hx, hy, hz, 0.0, 0.0, f, 450.0, 0.0)
  Utility.Wait(d / 450.0 + 0.15)
  ; Up and over.
  Float tx = lx + sx * 26.0
  Float ty = ly + sy * 26.0
  Float tz = lz + 6.0
  d = Dist(p, tx, ty, tz)
  p.TranslateTo(tx, ty, tz, 0.0, 0.0, f, 220.0, 0.0)
  Utility.Wait(d / 220.0 + 0.1)
  p.StopTranslation()
  If !RG.LockedOut()
    Game.EnablePlayerControls()
  EndIf
  busy = False
EndFunction

Float Function Dist(ObjectReference r, Float x, Float y, Float z)
  Float dx = x - r.GetPositionX()
  Float dy = y - r.GetPositionY()
  Float dz = z - r.GetPositionZ()
  Return Math.sqrt(dx * dx + dy * dy + dz * dz)
EndFunction

; ---- ropes ----

; Swinging on a rope (TMRgRope): grabbed where the player reaches it, then carried in an arc to the landing farther
; from them (landings: two points, x y z each).
Function Swing(ObjectReference rope, Float[] landings, Float ropeLength)
  If busy || hanging || !landings || landings.Length < 6
    Return
  EndIf
  busy = True
  Actor p = Game.GetPlayer()
  Float ax = landings[0]
  Float ay = landings[1]
  Float az = landings[2]
  Float bx = landings[3]
  Float by = landings[4]
  Float bz = landings[5]
  Float tx = bx
  Float ty = by
  Float tz = bz
  If Dist(p, ax, ay, az) > Dist(p, bx, by, bz)
    tx = ax
    ty = ay
    tz = az
  EndIf
  Game.DisablePlayerControls(True, True, False, False, True, False, False)
  ; The grab: on the rope at reaching height (hands about 100 above the feet), facing the landing.
  Float top = rope.GetPositionZ()
  Float gz = p.GetPositionZ() + 30.0
  If gz > top - 140.0
    gz = top - 140.0
  ElseIf gz < top - ropeLength
    gz = top - ropeLength
  EndIf
  Float heading = RG.Atan2(tx - rope.GetPositionX(), ty - rope.GetPositionY())
  Float gx = rope.GetPositionX() - Math.sin(heading) * 20.0
  Float gy = rope.GetPositionY() - Math.cos(heading) * 20.0
  Float d = Dist(p, gx, gy, gz)
  p.TranslateTo(gx, gy, gz, 0.0, 0.0, heading, 400.0, 0.0)
  Utility.Wait(d / 400.0 + 0.2)
  ; The swing: an arc out over the gap to the landing.
  d = Dist(p, tx, ty, tz + 24.0)
  p.SplineTranslateTo(tx, ty, tz + 24.0, 0.0, 0.0, heading, d * 0.6, 420.0, 0.0)
  Utility.Wait(d / 420.0 + 0.3)
  p.StopTranslation()
  If !RG.LockedOut()
    Game.EnablePlayerControls()
  EndIf
  busy = False
EndFunction

; ---- hanging from objects ----

; Activating an object the player can hang from: from below, the player grabs it (and when already hanging from it,
; lets go). False when the player isn't under it (the activation is the object's own then).
Bool Function HangFrom(ObjectReference obj)
  Actor p = Game.GetPlayer()
  If hanging
    If hanging == obj
      LetGo()
      Return True
    EndIf
    Return False
  EndIf
  If busy
    Return False
  EndIf
  Float dz = obj.GetPositionZ() - p.GetPositionZ()
  If dz < 70.0 || dz > 360.0
    Return False
  EndIf
  Float dx = p.GetPositionX() - obj.GetPositionX()
  Float dy = p.GetPositionY() - obj.GetPositionY()
  Float flat = Math.sqrt(dx * dx + dy * dy)
  If flat > 48.0
    dx = dx * 48.0 / flat
    dy = dy * 48.0 / flat
  EndIf
  hanging = obj
  hangX = dx
  hangY = dy
  hangZ = -120.0
  hangUntil = Utility.GetCurrentRealTime() + 20.0
  RG.SetHanging(obj)
  Game.DisablePlayerControls(True, True, False, False, True, False, False)
  Debug.Notification("Hanging on (activate it again to let go)")
  Hold()
  Return True
EndFunction

; Held under the object, following it as its script moves it.
Function Hold()
  If !hanging
    Return
  EndIf
  If Utility.GetCurrentRealTime() > hangUntil || hanging.IsDisabled()
    LetGo()
    Return
  EndIf
  Actor p = Game.GetPlayer()
  Float x = hanging.GetPositionX() + hangX
  Float y = hanging.GetPositionY() + hangY
  Float z = hanging.GetPositionZ() + hangZ
  p.TranslateTo(x, y, z, 0.0, 0.0, p.GetAngleZ(), 300.0 + Dist(p, x, y, z) * 4.0, 0.0)
  RegisterForSingleUpdate(0.1)
EndFunction

Function LetGo()
  hanging = None
  RG.SetHanging(None)
  Game.GetPlayer().StopTranslation()
  If !RG.LockedOut()
    Game.EnablePlayerControls()
  EndIf
  RegisterForSingleUpdate(0.5)
EndFunction
