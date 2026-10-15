ScriptName TMRgSoup extends ObjectReference
{Redguard's script engine (SOUP) for the converted scripts. Each Redguard object's compiled script becomes a script
extending this one (RgPapyrus): its code cut into blocks, Dispatch(b) running block b, which sets the next block (Go)
and, when Redguard would wait, how long (a task's time, the next frame after "end", a call's time). This script runs
them as Redguard did: a main thread from the script's entry point and, when the script has one, a second thread run
from the start every frame until "endint" (what an actor answers to whatever else it is doing); a task the script
waits on (Wait, RotateByAxis, RTX, ...) holds its thread for its time; a multitask runs alongside (WaitOnTasks waits
for those). Scripts run while their object's cell is loaded, a few times a second near the player and once a second
far away, and react at once to being activated.

Units: Redguard positions are Skyrim units (moves and distances in scripts are 256ths of one), angles 2048ths of a
turn, times tenths of a second. Every S_ function is a SOUP function of the same name acting on Who: this object, or
another one the script names (an object task, Obj), or the player.}

TMRgState Property RG Auto
Int Property EntryPoint = 0 Auto
{The main thread's first block (-1: no main thread).}
Bool Property Interrupt = False Auto
{Runs a second thread from block 0 every frame up to endint.}
Bool Property Talks = False Auto
{Skyrim's dialogue holds this actor's conversations: activating starts those, not the script's own talk.}
Bool Property Shell = False Auto
{An item's use script: runs at once on its own, then deletes itself (KillMe).}
ObjectReference[] Property Objects Auto
{The objects this script names, by the script's own index (RgPapyrus).}
FormList Property Markers Auto
{The scene's markers (MPMK), by index.}
ObjectReference[] Property Places Auto
{Where this script's LoadWorld calls lead, by call.}
Float[] Property Locations Auto
{This object's locations (x, y, z each, Skyrim units): 0 its start, then RALC's offsets.}
Int[] Property UseItems Auto
{Items the script checks the player's selected item against (ActiveItem() = n), and the menu to choose among them.}
Message Property UseMenu Auto
Message[] Property Menus Auto
{The script's menus (a door that answers, a choice outside Skyrim's dialogue), by number: a button per item, shown
when its RG.MenuShow global is 1.}
ObjectReference Property MyLight Auto
Bool Property ActsOnPlayer = False Auto
{Redguard's player scripts (item use scripts, COMMON): "me" is Cyrus.}

ObjectReference Property Who Auto Hidden

Int pc0 = -2
Int pc1 = 0
Int _pc = 0
Int thread = 0
Float due0 = 0.0
Float due1 = 0.0
Float _yield = 0.0
Float multiDue = 0.0
Bool busy = False
Bool loaded = False
Bool activated = False
Float activatedAt = 0.0
Bool hookOn = True
Int[] stack0
Int[] stack1
Int sp0 = 0
Int sp1 = 0
Float startX
Float startY
Float startZ
Float startAX
Float startAY
Float startAZ
Bool started = False
Int ambientId = -1
Int chosenItem = -1
Int[] menuIndex
Int menuSel = -1
Bool killed = False

; ---- the engine ----

Event OnInit()
  Setup()
  If Shell
    loaded = True
    RegisterForSingleUpdate(0.05)
  EndIf
EndEvent

Function Setup()
  If started
    Return
  EndIf
  started = True
  startX = GetPositionX()
  startY = GetPositionY()
  startZ = GetPositionZ()
  startAX = GetAngleX()
  startAY = GetAngleY()
  startAZ = GetAngleZ()
  pc0 = EntryPoint
  pc1 = 0
  stack0 = new Int[16]
  stack1 = new Int[16]
EndFunction

Event OnLoad()
  Begin()
EndEvent

Event OnCellAttach()
  Begin()
EndEvent

Event OnUnload()
  loaded = Shell
EndEvent

Event OnCellDetach()
  loaded = Shell
EndEvent

Function Begin()
  Setup()
  If !loaded && !killed
    loaded = True
    RegisterForSingleUpdate(Utility.RandomFloat(0.1, 0.4))
  EndIf
EndFunction

Event OnActivate(ObjectReference akActionRef)
  If akActionRef != Game.GetPlayer()
    Return
  EndIf
  Actor me = (Self as ObjectReference) as Actor
  If Talks && me && !me.IsDead()
    Return
  EndIf
  activated = True
  activatedAt = Utility.GetCurrentRealTime()
  chosenItem = ChooseItem()
  Tick()
EndEvent

Event OnUpdate()
  If !loaded || killed
    Return
  EndIf
  Tick()
  If killed || (pc0 < 0 && (!Interrupt || pc1 < 0))
    Return
  EndIf
  Float now = Utility.GetCurrentRealTime()
  ; Near the player four times a second; further off less often (what they wait for is the player coming close).
  Float wait = 0.25
  If !Shell
    Float d = Game.GetPlayer().GetDistance(Self)
    If d > 8192.0
      wait = 5.0
    ElseIf d > 3072.0
      wait = 2.0
    ElseIf d > 1024.0
      wait = 0.5
    EndIf
  EndIf
  Float next = due0 - now
  If Interrupt && pc1 >= 0
    next = 0.0
  EndIf
  If next > wait || pc0 < 0
    next = wait
  EndIf
  If next < 0.05
    next = 0.05
  EndIf
  RegisterForSingleUpdate(next)
EndEvent

Function Tick()
  If busy
    Return
  EndIf
  busy = True
  Float now = Utility.GetCurrentRealTime()
  If Interrupt && hookOn && pc1 >= 0 && now >= due1
    thread = 1
    _pc = pc1
    RunThread()
    pc1 = _pc
  EndIf
  If pc0 >= 0 && now >= due0 && !killed
    thread = 0
    _pc = pc0
    RunThread()
    pc0 = _pc
  EndIf
  If activated && Utility.GetCurrentRealTime() - activatedAt > 1.0
    activated = False
  EndIf
  busy = False
EndFunction

Function RunThread()
  _yield = 0.0
  Int steps = 0
  While _pc >= 0 && _yield == 0.0 && steps < 300 && !killed
    Who = Home()
    Dispatch(_pc)
    steps += 1
  EndWhile
  Who = Home()
  If _yield == 0.0 && _pc >= 0
    _yield = 0.01
  EndIf
  If _yield > 0.0
    If thread == 0
      due0 = Utility.GetCurrentRealTime() + _yield
    Else
      due1 = Utility.GetCurrentRealTime() + _yield
    EndIf
  EndIf
EndFunction

; Overridden by each converted script: runs block b.
Function Dispatch(Int b)
  _pc = -1
EndFunction

; Next block now.
Function Go(Int b)
  _pc = b
EndFunction

; "end b": the rest of this frame off, then block b.
Function EndTo(Int b)
  _pc = b
  _yield = 0.01
EndFunction

; After a task of the given time, block b.
Function Task(Float seconds, Int b)
  _pc = b
  If seconds > 0.0
    _yield = seconds
  EndIf
EndFunction

; A multitask: runs alongside; WaitOnTasks waits for it.
Function Multi(Float seconds)
  Float until = Utility.GetCurrentRealTime() + seconds
  If until > multiDue
    multiDue = until
  EndIf
EndFunction

Function Gosub(Int target, Int back)
  If thread == 0
    If sp0 < 16
      stack0[sp0] = back
      sp0 += 1
    EndIf
  ElseIf sp1 < 16
    stack1[sp1] = back
    sp1 += 1
  EndIf
  _pc = target
EndFunction

Function Ret()
  If thread == 0 && sp0 > 0
    sp0 -= 1
    _pc = stack0[sp0]
  ElseIf thread == 1 && sp1 > 0
    sp1 -= 1
    _pc = stack1[sp1]
  Else
    _pc = -1
  EndIf
EndFunction

; The interrupt thread's end: from the start again next frame.
Function EndInt()
  _pc = 0
  sp1 = 0
  _yield = 0.01
EndFunction

Function Halt()
  _pc = -1
EndFunction

; ---- values ----

Int Function Flag(Int i)
  Return RG.Flag(i)
EndFunction

Function SetFlag(Int i, Int v)
  RG.SetFlag(i, v)
EndFunction

Int Function Div(Int a, Int b)
  If b == 0
    Return 0
  EndIf
  Return a / b
EndFunction

Int Function Shl(Int a, Int b)
  Return Math.LeftShift(a, b)
EndFunction

Int Function Shr(Int a, Int b)
  Return Math.RightShift(a, b)
EndFunction

Int Function BitAnd(Int a, Int b)
  Return Math.LogicalAnd(a, b)
EndFunction

Int Function BitOr(Int a, Int b)
  Return Math.LogicalOr(a, b)
EndFunction

Int Function BitXor(Int a, Int b)
  Return Math.LogicalXor(a, b)
EndFunction

Int Function B(Bool v)
  If v
    Return 1
  EndIf
  Return 0
EndFunction

ObjectReference Function Obj(Int i)
  If Objects && i >= 0 && i < Objects.Length
    Return Objects[i]
  EndIf
  Return None
EndFunction

; Object tasks: the next call acts on another object (or the player).
Function On(ObjectReference r)
  If r
    Who = r
  Else
    Who = Home()
  EndIf
EndFunction

ObjectReference Function Home()
  If ActsOnPlayer
    Return Game.GetPlayer()
  EndIf
  Return Self
EndFunction

Function OnPlayer()
  Who = Game.GetPlayer()
EndFunction

Actor Function WhoActor()
  Return Who as Actor
EndFunction

Bool Function Real(ObjectReference r)
  Return r != None
EndFunction

; Redguard's object fields (SOUP386.DEF [refs]) the scripts read and write: 1-3 pos.vx/vy/vz (Redguard's x east, y
; down, z north), 4-6 angle.vx/vy/vz (2048ths), 7 hide, 8 dead, 9 torch_timer; others read 0.
Int Function GetField(ObjectReference r, Int field)
  If !r
    Return 0
  EndIf
  If field == 1
    Return ((r.GetPositionX() + 32768.0) * 1.0) as Int
  ElseIf field == 2
    Return (-r.GetPositionZ()) as Int
  ElseIf field == 3
    Return (r.GetPositionY() + 32768.0) as Int
  ElseIf field == 4
    Return (r.GetAngleX() * 2048.0 / 360.0) as Int
  ElseIf field == 5
    Return (-r.GetAngleZ() * 2048.0 / 360.0) as Int
  ElseIf field == 6
    Return (r.GetAngleY() * 2048.0 / 360.0) as Int
  ElseIf field == 7
    Return B(r.IsDisabled())
  ElseIf field == 8
    Actor a = r as Actor
    Return B(a && a.IsDead())
  ElseIf field == 9
    Actor p = r as Actor
    If p && p.GetEquippedItemType(0) == 11
      Return 60
    EndIf
    Return 0
  EndIf
  Return 0
EndFunction

Function SetField(ObjectReference r, Int field, Int v)
  If !r
    Return
  EndIf
  If field == 1
    r.SetPosition(v - 32768.0, r.GetPositionY(), r.GetPositionZ())
  ElseIf field == 2
    r.SetPosition(r.GetPositionX(), r.GetPositionY(), -v)
  ElseIf field == 3
    r.SetPosition(r.GetPositionX(), v - 32768.0, r.GetPositionZ())
  ElseIf field == 4
    r.SetAngle(v * 360.0 / 2048.0, r.GetAngleY(), r.GetAngleZ())
  ElseIf field == 5
    r.SetAngle(r.GetAngleX(), r.GetAngleY(), -v * 360.0 / 2048.0)
  ElseIf field == 6
    r.SetAngle(r.GetAngleX(), v * 360.0 / 2048.0, r.GetAngleZ())
  ElseIf field == 7
    If v != 0
      r.Disable()
    Else
      r.Enable()
    EndIf
  ElseIf field == 8
    Actor a = r as Actor
    If a && v != 0
      a.Kill()
    EndIf
  EndIf
EndFunction

; ---- rotation (Skyrim's angles: model to world is RotX(-x) RotY(-y) RotZ(-z), row vectors) ----

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

; Skyrim's rotation matrix for angles (degrees), row-major.
Float[] Function Matrix(Float ax, Float ay, Float az)
  Float[] m = new Float[9]
  Float cx = Math.cos(-ax)
  Float sx = Math.sin(-ax)
  Float cy = Math.cos(-ay)
  Float sy = Math.sin(-ay)
  Float cz = Math.cos(-az)
  Float sz = Math.sin(-az)
  ; RotX * RotY
  ; RotX = [1 0 0; 0 cx sx; 0 -sx cx], RotY = [cy 0 -sy; 0 1 0; sy 0 cy], RotZ = [cz sz 0; -sz cz 0; 0 0 1]
  Float a00 = cy
  Float a01 = 0.0
  Float a02 = -sy
  Float a10 = sx * sy
  Float a11 = cx
  Float a12 = sx * cy
  Float a20 = cx * sy
  Float a21 = -sx
  Float a22 = cx * cy
  m[0] = a00 * cz - a01 * sz
  m[1] = a00 * sz + a01 * cz
  m[2] = a02
  m[3] = a10 * cz - a11 * sz
  m[4] = a10 * sz + a11 * cz
  m[5] = a12
  m[6] = a20 * cz - a21 * sz
  m[7] = a20 * sz + a21 * cz
  m[8] = a22
  Return m
EndFunction

; A Redguard local axis rotation (axis 0 x, 1 y (down), 2 z (north); amount in 2048ths) as Skyrim's matrix.
Float[] Function AxisMatrix(Int axis, Int amount)
  Float phi = amount * 360.0 / 2048.0
  If axis == 0
    Return Matrix(phi, 0.0, 0.0)
  ElseIf axis == 2
    Return Matrix(0.0, phi, 0.0)
  EndIf
  Return Matrix(0.0, 0.0, -phi)
EndFunction

Float[] Function Mul(Float[] a, Float[] b)
  Float[] m = new Float[9]
  Int i = 0
  While i < 3
    Int j = 0
    While j < 3
      m[i * 3 + j] = a[i * 3] * b[j] + a[i * 3 + 1] * b[3 + j] + a[i * 3 + 2] * b[6 + j]
      j += 1
    EndWhile
    i += 1
  EndWhile
  Return m
EndFunction

; Angles (degrees) of a rotation matrix: x, y, z in out[0..2].
Float[] Function Angles(Float[] m)
  Float[] e = new Float[3]
  Float s = m[2]
  If s > 1.0
    s = 1.0
  ElseIf s < -1.0
    s = -1.0
  EndIf
  e[1] = Math.asin(s)
  If Math.cos(e[1]) > 0.0001
    e[0] = Atan2(-m[5], m[8])
    e[2] = Atan2(-m[1], m[0])
  Else
    e[0] = 0.0
    e[2] = Atan2(m[3], m[4])
  EndIf
  Return e
EndFunction

; Turns Who to angles over a time (seconds), returning the time.
Float Function TurnTo(Float ax, Float ay, Float az, Float seconds)
  Actor a = Who as Actor
  If a
    a.SetAngle(a.GetAngleX(), a.GetAngleY(), az)
    Return seconds
  EndIf
  If seconds <= 0.05
    Who.SetAngle(ax, ay, az)
    Return 0.0
  EndIf
  Float d = Math.abs(Wrap(az - Who.GetAngleZ()))
  Float dx = Math.abs(Wrap(ax - Who.GetAngleX()))
  Float dy = Math.abs(Wrap(ay - Who.GetAngleY()))
  If dx > d
    d = dx
  EndIf
  If dy > d
    d = dy
  EndIf
  Who.TranslateTo(Who.GetPositionX(), Who.GetPositionY(), Who.GetPositionZ(), ax, ay, az, 1.0, d / seconds + 0.01)
  Return seconds
EndFunction

Float Function Wrap(Float deg)
  While deg > 180.0
    deg -= 360.0
  EndWhile
  While deg < -180.0
    deg += 360.0
  EndWhile
  Return deg
EndFunction

; Moves Who to a point over a time (seconds), returning the time; actors walk there.
Float Function MoveTo3(Float tx, Float ty, Float tz, Float seconds)
  Actor a = Who as Actor
  Float dist = Math.sqrt((tx - Who.GetPositionX()) * (tx - Who.GetPositionX()) + (ty - Who.GetPositionY()) * (ty - Who.GetPositionY()) + (tz - Who.GetPositionZ()) * (tz - Who.GetPositionZ()))
  If a
    ; People keep to Skyrim's AI (they aren't slid about); the time is kept.
    Return seconds
  EndIf
  If seconds <= 0.05 || dist < 0.5
    Who.SetPosition(tx, ty, tz)
    Return 0.0
  EndIf
  Who.TranslateTo(tx, ty, tz, Who.GetAngleX(), Who.GetAngleY(), Who.GetAngleZ(), dist / seconds, 0.0)
  Return seconds
EndFunction

; A Redguard vector (fine units, axis 0 x east, 1 y down, 2 z north) along a local axis of Who in world terms.
Float[] Function LocalAxis(Int axis)
  Float[] m = Matrix(Who.GetAngleX(), Who.GetAngleY(), Who.GetAngleZ())
  Float[] v = new Float[3]
  Int row = 0
  Float sign = 1.0
  If axis == 1
    row = 2
    sign = -1.0
  ElseIf axis == 2
    row = 1
  EndIf
  v[0] = m[row * 3] * sign
  v[1] = m[row * 3 + 1] * sign
  v[2] = m[row * 3 + 2] * sign
  Return v
EndFunction

; ---- SOUP functions: movement (tasks return their time in seconds) ----

Float Function S_Wait(Int t)
  Return t * 0.1
EndFunction

Float Function S_WaitOnTasks()
  Float left = multiDue - Utility.GetCurrentRealTime()
  If left < 0.0
    Return 0.0
  EndIf
  Return left
EndFunction

Float Function S_WaitNonMulti()
  Return 0.0
EndFunction

Float Function S_WaitOnDialog()
  Float left = RG.DialogDue - Utility.GetCurrentRealTime()
  If left < 0.0
    Return 0.0
  EndIf
  Return left
EndFunction

Float Function S_RotateByAxis(Int axis, Int amount, Int t)
  Float[] e = Angles(Mul(AxisMatrix(axis, amount), Matrix(Who.GetAngleX(), Who.GetAngleY(), Who.GetAngleZ())))
  Return TurnTo(e[0], e[1], e[2], t * 0.1)
EndFunction

Float Function S_RotateToAxis(Int axis, Int amount, Int t)
  Float ax = startAX
  Float ay = startAY
  Float az = startAZ
  TMRgSoup other = Who as TMRgSoup
  If Who != Self && other
    ax = other.StartAngle(0)
    ay = other.StartAngle(1)
    az = other.StartAngle(2)
  ElseIf Who != Self
    ax = Who.GetAngleX()
    ay = Who.GetAngleY()
    az = Who.GetAngleZ()
  EndIf
  Float[] e = Angles(Mul(AxisMatrix(axis, amount), Matrix(ax, ay, az)))
  Return TurnTo(e[0], e[1], e[2], t * 0.1)
EndFunction

Float Function StartAngle(Int i)
  Setup()
  If i == 0
    Return startAX
  ElseIf i == 1
    Return startAY
  EndIf
  Return startAZ
EndFunction

Float Function StartPos(Int i)
  Setup()
  If i == 0
    Return startX
  ElseIf i == 1
    Return startY
  EndIf
  Return startZ
EndFunction

Float Function S_MoveByAxis(Int axis, Int amount, Int t)
  Float d = amount / 256.0
  Float px = Who.GetPositionX()
  Float py = Who.GetPositionY()
  Float pz = Who.GetPositionZ()
  If axis == 0
    px += d
  ElseIf axis == 1
    pz -= d
  Else
    py += d
  EndIf
  Return MoveTo3(px, py, pz, t * 0.1)
EndFunction

Float Function S_MoveAlongAxis(Int axis, Int amount, Int t)
  Float[] v = LocalAxis(axis)
  Float d = amount / 256.0
  Return MoveTo3(Who.GetPositionX() + v[0] * d, Who.GetPositionY() + v[1] * d, Who.GetPositionZ() + v[2] * d, t * 0.1)
EndFunction

Float Function S_MoveObjectAxis(Int axis, Int amount, Int t)
  Return S_MoveAlongAxis(axis, amount, t)
EndFunction

Float Function S_MoveForward(Int t)
  Return S_MoveAlongAxis(2, t * 256 * 8, t)
EndFunction

Float Function S_MoveBackward(Int t)
  Return S_MoveAlongAxis(2, -t * 256 * 8, t)
EndFunction

Float Function S_MoveLeft(Int t)
  Return S_MoveAlongAxis(0, -t * 256 * 8, t)
EndFunction

Float Function S_MoveRight(Int t)
  Return S_MoveAlongAxis(0, t * 256 * 8, t)
EndFunction

Float Function S_WalkForward(Int t)
  Return S_MoveForward(t)
EndFunction

Float Function S_WalkBackward(Int t)
  Return S_MoveBackward(t)
EndFunction

Float Function S_MoveToLocation(Int i, Int t)
  Float[] at = LocationOf(i)
  If !at
    Return t * 0.1
  EndIf
  Return MoveTo3(at[0], at[1], at[2], t * 0.1)
EndFunction

Float Function S_WanderToLocation(Int i, Int t)
  Return S_MoveToLocation(i, t)
EndFunction

Float[] Function LocationOf(Int i)
  TMRgSoup other = Who as TMRgSoup
  Float[] locs = Locations
  If Who != Self && other
    locs = other.Locations
  EndIf
  If !locs || i < 0 || i * 3 + 2 >= locs.Length
    If i == 0
      Float[] start = new Float[3]
      start[0] = startX
      start[1] = startY
      start[2] = startZ
      Return start
    EndIf
    Return None
  EndIf
  Float[] p = new Float[3]
  p[0] = locs[i * 3]
  p[1] = locs[i * 3 + 1]
  p[2] = locs[i * 3 + 2]
  Return p
EndFunction

Int Function S_DistanceToLocation(Int i)
  Float[] at = LocationOf(i)
  If !at
    Return 0
  EndIf
  Float dx = at[0] - Who.GetPositionX()
  Float dy = at[1] - Who.GetPositionY()
  Float dz = at[2] - Who.GetPositionZ()
  Return (Math.sqrt(dx * dx + dy * dy + dz * dz) * 256.0) as Int
EndFunction

Int Function S_SetObjectLocation(Int i, Int x, Int y, Int z)
  Return 0
EndFunction

Int Function S_OffsetLocation(Int a, Int b)
  Return 0
EndFunction

ObjectReference Function Marker(Int n)
  If !Markers || n < 0 || n >= Markers.GetSize()
    Return None
  EndIf
  Return Markers.GetAt(n) as ObjectReference
EndFunction

Float Function S_MoveMarker(Int n, Int speed)
  ObjectReference m = Marker(n)
  Actor a = Who as Actor
  If !m
    Return 0.0
  EndIf
  If a
    If a != Game.GetPlayer()
      a.PathToReference(m, 0.5)
    EndIf
    Return 0.0
  EndIf
  Float d = Who.GetDistance(m)
  Float secs = d / 150.0
  Return MoveTo3(m.GetPositionX(), m.GetPositionY(), m.GetPositionZ(), secs)
EndFunction

Float Function S_MoveNodeMarker(Int n, Int speed)
  Return S_MoveMarker(n, speed)
EndFunction

Float Function S_MoveNodeLocation(Int i, Int speed)
  Return S_MoveToLocation(i, 10)
EndFunction

Int Function S_AtNodeMarker(Int n, Int dist)
  ObjectReference m = Marker(n)
  If !m
    Return 1
  EndIf
  Return B(Who.GetDistance(m) * 256.0 <= dist)
EndFunction

Int Function S_AtNodeLocation(Int i, Int dist)
  Return B(S_DistanceToLocation(i) <= dist)
EndFunction

Int Function S_ResetRotation(Int x, Int y, Int z)
  If Who == Self
    Who.SetAngle(startAX, startAY, startAZ)
  Else
    TMRgSoup other = Who as TMRgSoup
    If other
      Who.SetAngle(other.StartAngle(0), other.StartAngle(1), other.StartAngle(2))
    EndIf
  EndIf
  Return 0
EndFunction

Int Function S_ReloadRotation()
  Return S_ResetRotation(1, 1, 1)
EndFunction

Int Function S_ResetPosition(Int x, Int y, Int z)
  If Who == Self
    Who.SetPosition(startX, startY, startZ)
  Else
    TMRgSoup other = Who as TMRgSoup
    If other
      Who.SetPosition(other.StartPos(0), other.StartPos(1), other.StartPos(2))
    EndIf
  EndIf
  Return 0
EndFunction

Float Function S_FacePlayer(Int speed)
  Return FaceRef(Game.GetPlayer())
EndFunction

Float Function S_FacePlayerInertia(Int speed)
  Return FaceRef(Game.GetPlayer())
EndFunction

Float Function S_FaceObject(Int n)
  Return 0.0
EndFunction

Float Function FaceRef(ObjectReference r)
  If !r || r == Who
    Return 0.0
  EndIf
  Float h = Who.GetHeadingAngle(r)
  Return TurnTo(Who.GetAngleX(), Who.GetAngleY(), Who.GetAngleZ() + h, 0.3)
EndFunction

Float Function S_PlayerFaceObj()
  Actor p = Game.GetPlayer()
  p.SetAngle(p.GetAngleX(), p.GetAngleY(), p.GetAngleZ() + p.GetHeadingAngle(Self))
  Return 0.0
EndFunction

Float Function S_PlayerFaceTalk(Int a)
  Return S_PlayerFaceObj()
EndFunction

; The player lined up for an action at this object (Redguard walks Cyrus to a spot): turned to face it.
Float Function S_PlayerLineUp(Int x, Int z, Int angle)
  Return S_PlayerFaceObj()
EndFunction

Float Function S_PlayerPickupLineUp(Int x, Int z, Int angle)
  Return S_PlayerFaceObj()
EndFunction

Float Function S_PlayerParallelLineUp(Int x, Int z, Int angle)
  Return S_PlayerFaceObj()
EndFunction

Int Function S_MovePlayerAboveMe(Int a, Int b, Int c, Int d)
  Return 0
EndFunction

Int Function S_MoveAboveMe()
  Return 0
EndFunction

Int Function S_PushPlayer(Int a)
  Return 0
EndFunction

Int Function S_PopPlayer()
  Return 0
EndFunction

Int Function S_Offset(Int x, Int y, Int z)
  Return 0
EndFunction

Int Function S_UnOffset()
  Return 0
EndFunction

Float Function S_PlayerOffsettask(Int x, Int y, Int z)
  Return 0.0
EndFunction

Int Function S_BounceObject(Int a, Int b, Int c)
  Return 0
EndFunction

; ---- visibility and life ----

Int Function S_HideMe()
  Who.Disable()
  Return 0
EndFunction

Int Function S_ShowMe()
  Who.Enable()
  Return 0
EndFunction

Int Function S_KillMe()
  Actor a = Who as Actor
  If a
    If !a.IsDead() && a != Game.GetPlayer()
      a.Kill()
    EndIf
    Return 0
  EndIf
  If Who == Self
    killed = True
    _pc = -1
    pc0 = -1
    pc1 = -1
    Disable()
    If Shell
      Delete()
    EndIf
  Else
    Who.Disable()
  EndIf
  Return 0
EndFunction

Int Function S_KillMyTasks()
  Who.StopTranslation()
  multiDue = 0.0
  Return 0
EndFunction

Int Function S_EnableHook()
  hookOn = True
  Return 0
EndFunction

Int Function S_DisableHook()
  hookOn = False
  Return 0
EndFunction

; Redguard's object attributes (SOUP386.DEF attr_*): health, strength, armor and invulnerability carry over to
; Skyrim's actor values; the others (movement, animation and collision tuning) have no Skyrim counterpart.
Int Function S_SetAttribute(Int attr, Int v)
  Actor a = Who as Actor
  If !a
    Return 0
  EndIf
  If attr == 3 && v > 0 && a != Game.GetPlayer()
    a.SetActorValue("Health", v)
  ElseIf attr == 4
    a.SetActorValue("DamageResist", v)
  ElseIf attr == 123
    a.SetGhost(v != 0)
  EndIf
  Return 0
EndFunction

; ---- combat and health ----

Int Function S_beginCombat()
  Actor a = Who as Actor
  If a && a != Game.GetPlayer() && !a.IsDead()
    a.StartCombat(Game.GetPlayer())
  EndIf
  Return 0
EndFunction

Int Function S_endCombat()
  Actor a = Who as Actor
  If a && a != Game.GetPlayer()
    a.StopCombat()
  EndIf
  Return 0
EndFunction

Int Function S_isFighting()
  Actor a = Who as Actor
  If a
    Return B(a.IsInCombat())
  EndIf
  Return 0
EndFunction

Int Function S_isDead()
  Actor a = Who as Actor
  If a
    Return B(a.IsDead())
  EndIf
  Return B(Who.IsDisabled())
EndFunction

; Health is Redguard's 0-100 (Cyrus's full health is 100): a share of the actor's whole health.
Float Function MaxHealth(Actor a)
  Float pct = a.GetActorValuePercentage("Health")
  If pct > 0.001
    Return a.GetActorValue("Health") / pct
  EndIf
  Return a.GetBaseActorValue("Health")
EndFunction

Int Function S_adjustHealth(Int delta, Int kill)
  Actor a = Who as Actor
  If !a
    Return 0
  EndIf
  Float amount = MaxHealth(a) * delta / 100.0
  If amount < 0.0
    a.DamageActorValue("Health", -amount)
  ElseIf amount > 0.0
    a.RestoreActorValue("Health", amount)
  EndIf
  Return 0
EndFunction

Int Function S_setHealth(Int v)
  Actor a = Who as Actor
  If !a
    Return 0
  EndIf
  Float full = MaxHealth(a)
  Float now = a.GetActorValue("Health")
  Float want = full * v / 100.0
  If want < now
    a.DamageActorValue("Health", now - want)
  ElseIf want > now
    a.RestoreActorValue("Health", want - now)
  EndIf
  Return 0
EndFunction

Int Function S_health()
  Actor a = Who as Actor
  If a
    Return (a.GetActorValuePercentage("Health") * 100.0) as Int
  EndIf
  Return 0
EndFunction

Int Function S_setStrength(Int v)
  Actor a = Who as Actor
  If a
    a.SetActorValue("AttackDamageMult", 1.0 + v / 100.0)
  EndIf
  Return 0
EndFunction

Int Function S_strength()
  Actor a = Who as Actor
  If a
    Return ((a.GetActorValue("AttackDamageMult") - 1.0) * 100.0) as Int
  EndIf
  Return 0
EndFunction

Int Function S_setArmor(Int v)
  Actor a = Who as Actor
  If a
    a.SetActorValue("DamageResist", v)
  EndIf
  Return 0
EndFunction

Int Function S_armor()
  Actor a = Who as Actor
  If a
    Return a.GetActorValue("DamageResist") as Int
  EndIf
  Return 0
EndFunction

Int Function S_resurrect()
  Actor a = Who as Actor
  If a && a.IsDead()
    a.Resurrect()
  EndIf
  Return 0
EndFunction

; A projectile (fireball, dragon's fire) at the player.
Int Function S_shoot(Int a, Int b, Int c, Int d, Int e, Int f, Int g)
  If RG.Fireball
    RG.Fireball.Cast(Who, Game.GetPlayer())
  EndIf
  Return 0
EndFunction

Int Function S_shootPlayer(Int a, Int b, Int c, Int d)
  Return S_shoot(a, b, c, d, 0, 0, 0)
EndFunction

Int Function S_DragonBreath()
  If RG.Breath
    RG.Breath.Cast(Who, Game.GetPlayer())
  EndIf
  Return 0
EndFunction

Int Function S_DragonBreathTask()
  Return S_DragonBreath()
EndFunction

Int Function S_GolemSteam()
  Return 0
EndFunction

Int Function S_AddFlatEffect(Int a, Int b, Int c, Int d, Int e, Int f)
  Return 0
EndFunction

Int Function S_ZapPlayer(Int v)
  Game.GetPlayer().DamageActorValue("Health", v)
  Return 0
EndFunction

Int Function S_CollidePlayerWeapon(Int a, Int b, Int c, Int d)
  Return 0
EndFunction

Int Function S_projectileAI()
  Return 0
EndFunction

Int Function S_chargeWeapon(Int a)
  Return 0
EndFunction

; ---- the player ----

Int Function S_ACTIVATE(Int label)
  If activated
    activated = False
    Return 1
  EndIf
  Return 0
EndFunction

Int Function S_TorchActivate(Int label)
  Return S_ACTIVATE(label)
EndFunction

Int Function S_PlayerDistance()
  Return (Who.GetDistance(Game.GetPlayer()) * 256.0) as Int
EndFunction

Int Function S_AcuratePlayerDistance()
  Return S_PlayerDistance()
EndFunction

Int Function S_PlayerInSight()
  Actor p = Game.GetPlayer()
  Actor a = Who as Actor
  If a && a != p
    Return B(a.HasLOS(p))
  EndIf
  Return B(p.HasLOS(Who) || Who.GetDistance(p) < 400.0)
EndFunction

; The player looking at the object: within arc (2048ths) of straight at it.
Int Function S_PlayerLooking(Int arc)
  Float h = Game.GetPlayer().GetHeadingAngle(Who)
  Return B(Math.abs(h) <= arc * 360.0 / 2048.0 + 1.0)
EndFunction

; The player within the object's arc (-left .. +right, 2048ths) of its facing.
Int Function S_PlayerArc(Int left, Int right)
  Float h = Who.GetHeadingAngle(Game.GetPlayer())
  Return B(h >= -left * 360.0 / 2048.0 && h <= right * 360.0 / 2048.0)
EndFunction

Int Function S_PlayerArcTan()
  Return (Who.GetHeadingAngle(Game.GetPlayer()) * 2048.0 / 360.0) as Int
EndFunction

Int Function S_PlayerFacing(Int left, Int right)
  Float h = Game.GetPlayer().GetHeadingAngle(Who)
  Return B(h >= -left * 360.0 / 2048.0 && h <= right * 360.0 / 2048.0)
EndFunction

; The player standing on the object: over its footprint and at its top.
Int Function S_PlayerStand()
  Actor p = Game.GetPlayer()
  Float dx = Math.abs(p.GetPositionX() - Who.GetPositionX())
  Float dy = Math.abs(p.GetPositionY() - Who.GetPositionY())
  Float half = Who.GetWidth()
  If Who.GetLength() > half
    half = Who.GetLength()
  EndIf
  half = half / 2.0 + 16.0
  Float dz = p.GetPositionZ() - Who.GetPositionZ()
  Return B(dx <= half && dy <= half && dz > -32.0 && dz <= Who.GetHeight() + 48.0)
EndFunction

Int Function S_PlayerCollide()
  Actor p = Game.GetPlayer()
  Float reach = Who.GetWidth()
  If Who.GetLength() > reach
    reach = Who.GetLength()
  EndIf
  Return B(p.GetDistance(Who) <= reach / 2.0 + 40.0)
EndFunction

Int Function S_PlayerCollideStatic()
  Return S_PlayerCollide()
EndFunction

Int Function S_PlayerHanging()
  Return 0
EndFunction

Int Function S_PlayerOnScape()
  Return B(!Game.GetPlayer().IsInInterior())
EndFunction

Int Function S_PlayerStatus()
  Return 0
EndFunction

; The player inside a rectangle of Redguard's ground plane (x east, z north).
Int Function S_InRectangle(Int x1, Int z1, Int x2, Int z2)
  Actor p = Game.GetPlayer()
  Float rx = p.GetPositionX() + 32768.0
  Float rz = p.GetPositionY() + 32768.0
  If x1 > x2
    Int t = x1
    x1 = x2
    x2 = t
  EndIf
  If z1 > z2
    Int t2 = z1
    z1 = z2
    z2 = t2
  EndIf
  Return B(rx >= x1 && rx <= x2 && rz >= z1 && rz <= z2)
EndFunction

Int Function S_IsHoldingWeapon()
  Actor a = Who as Actor
  If !a
    a = Game.GetPlayer()
  EndIf
  Return B(a.IsWeaponDrawn())
EndFunction

Int Function S_IsInDeepWater()
  Actor a = Who as Actor
  If a
    Return B(a.IsSwimming())
  EndIf
  Return 0
EndFunction

Int Function S_SheathSword()
  Actor a = Who as Actor
  If a
    a.SheatheWeapon()
  EndIf
  Return 0
EndFunction

Int Function S_DrawSword()
  Actor a = Who as Actor
  If a
    a.DrawWeapon()
  EndIf
  Return 0
EndFunction

Int Function S_LightTorch()
  Actor a = Who as Actor
  Form torch = RG.Item(45)
  If a && torch
    If a.GetItemCount(torch) == 0 && a != Game.GetPlayer()
      a.AddItem(torch, 1, True)
    EndIf
    a.EquipItem(torch, False, True)
  EndIf
  Return 0
EndFunction

Int Function S_UnlightTorch()
  Actor a = Who as Actor
  Form torch = RG.Item(45)
  If a && torch
    a.UnequipItem(torch, False, True)
  EndIf
  Return 0
EndFunction

Int Function S_InKey(Int k)
  Return 0
EndFunction

Int Function S_GetFramePassed()
  Return 1
EndFunction

Int Function S_QuickRnd(Int n)
  Return Utility.RandomInt(0, n)
EndFunction

Int Function S_Rnd(Int n)
  Return Utility.RandomInt(0, n)
EndFunction

Int Function S_TestGlobalFlag(Int i)
  Return Flag(i)
EndFunction

Int Function S_SetGlobalFlag(Int i)
  SetFlag(i, 1)
  Return 0
EndFunction

Int Function S_ResetGlobalFlag(Int i)
  SetFlag(i, 0)
  Return 0
EndFunction

; ---- items ----

Form Function ItemForm(Int n)
  Return RG.Item(n)
EndFunction

Int Function S_AddItem(Int n, Int count)
  Form f = ItemForm(n)
  If !f || count == 0
    Return 0
  EndIf
  If count > 0
    Game.GetPlayer().AddItem(f, count)
  Else
    Game.GetPlayer().RemoveItem(f, -count)
  EndIf
  Return 0
EndFunction

Int Function S_HaveItem(Int n)
  Form f = ItemForm(n)
  If !f
    Return 0
  EndIf
  Return Game.GetPlayer().GetItemCount(f)
EndFunction

Int Function S_DropItem(Int n, Int count)
  Form f = ItemForm(n)
  If f && count > 0
    Game.GetPlayer().RemoveItem(f, count)
  EndIf
  Return 0
EndFunction

Int Function S_SubItem(Int n, Int count)
  Return S_DropItem(n, count)
EndFunction

Int Function S_RemoveItem(Int n)
  Form f = ItemForm(n)
  If f
    Game.GetPlayer().RemoveItem(f, Game.GetPlayer().GetItemCount(f))
  EndIf
  Return 0
EndFunction

Int Function S_PopItem(Int n, Int count)
  Return S_DropItem(n, count)
EndFunction

Int Function S_ItemUsed(Int n, Int count)
  Return 0
EndFunction

; Holding an item for an animation (Redguard draws it in Cyrus's hand): Skyrim's animations have no hand item.
Int Function S_HandItem(Int n)
  Return 0
EndFunction

Int Function S_SelectItem(Int n)
  RG.SelectedItem = n
  Return 0
EndFunction

; The item the player uses on this object: chosen on activating (UseMenu when several fit), else the best fitting
; one carried, else the selected item (the sabre).
Int Function S_ActiveItem()
  If chosenItem >= 0
    Return chosenItem
  EndIf
  Return BestItem()
EndFunction

Int Function BestItem()
  Actor p = Game.GetPlayer()
  If UseItems
    Int i = 0
    While i < UseItems.Length
      Int n = UseItems[i]
      If n == 45
        If p.GetEquippedItemType(0) == 11
          Return 45
        EndIf
      ElseIf S_HaveItem(n) > 0
        Return n
      EndIf
      i += 1
    EndWhile
  EndIf
  Return RG.SelectedItem
EndFunction

Int Function ChooseItem()
  If !UseItems || UseItems.Length == 0
    Return -1
  EndIf
  Int owned = 0
  Int last = -1
  Int i = 0
  While i < UseItems.Length
    If (UseItems[i] == 45 && Game.GetPlayer().GetEquippedItemType(0) == 11) || (UseItems[i] != 45 && S_HaveItem(UseItems[i]) > 0)
      owned += 1
      last = UseItems[i]
    EndIf
    i += 1
  EndWhile
  If owned == 0
    Return RG.SelectedItem
  ElseIf owned == 1 || !UseMenu
    Return last
  EndIf
  Int pick = UseMenu.Show()
  If pick >= 0 && pick < UseItems.Length
    RG.SelectedItem = UseItems[pick]
    Return UseItems[pick]
  EndIf
  Return 1
EndFunction

Int Function S_ShowItem(Int n, Int a, Int b, Int c)
  Return 0
EndFunction

Int Function S_ShowItemNoRtx(Int n, Int a, Int b)
  Return 0
EndFunction

; Redguard takes everything from Cyrus when he is caught; Skyrim keeps the player's things.
Int Function S_DropAllItems()
  Return 0
EndFunction

Int Function S_PushAllItems(Int n)
  Return 0
EndFunction

Int Function S_GetPotion(Int i)
  Return RG.GetPotion(i)
EndFunction

Int Function S_SetPotion(Int i, Int v)
  RG.SetPotion(i, v)
  Return 0
EndFunction

; ---- sound and speech ----

Int Function S_Sound(Int n, Int vol, Int pan)
  If n < 0 || n >= RG.Sounds.GetSize()
    Return 0
  EndIf
  Sound s = RG.Sounds.GetAt(n) as Sound
  If s
    s.Play(Who)
  EndIf
  Return 0
EndFunction

Int Function S_FlatSound(Int n, Int vol, Int pan)
  Return S_Sound(n, vol, pan)
EndFunction

Float Function S_AmbientSound(Int n, Int vol, Int pan)
  If n < 0 || n >= RG.Sounds.GetSize()
    Return 0.0
  EndIf
  Sound s = RG.Sounds.GetAt(n) as Sound
  If s
    If ambientId >= 0
      Sound.StopInstance(ambientId)
    EndIf
    ambientId = s.Play(Who)
  EndIf
  Return 0.0
EndFunction

Int Function S_EndMySounds()
  If ambientId >= 0
    Sound.StopInstance(ambientId)
    ambientId = -1
  EndIf
  Return 0
EndFunction

Int Function S_EndMySound(Int n)
  Return S_EndMySounds()
EndFunction

Int Function S_EndSound(Int n)
  Return S_EndMySounds()
EndFunction

Int Function S_StopAllSounds()
  Return S_EndMySounds()
EndFunction

; A line (RTX): said by Who when it is an actor with a voiced topic, else its recording played there (Cyrus's at the
; player) with the text shown. Returns its length.
Float Function Line(Int i, String text, Float seconds, Bool cyrus)
  Form f = None
  If i >= 0 && i < RG.Lines.GetSize()
    f = RG.Lines.GetAt(i)
  EndIf
  Topic t = f as Topic
  Actor a = Who as Actor
  If t && a && !cyrus
    a.Say(t)
  Else
    Sound s = f as Sound
    If s
      If cyrus
        s.Play(Game.GetPlayer())
      Else
        s.Play(Who)
      EndIf
    EndIf
    If text != ""
      Debug.Notification(text)
    EndIf
  EndIf
  Float until = Utility.GetCurrentRealTime() + seconds
  If until > RG.DialogDue
    RG.DialogDue = until
  EndIf
  Return seconds
EndFunction

; ---- the screen, the camera and animation ----

; Redguard's camera moves (show*, look*): the player's own camera stays; the time they take is kept.
Float Function S_Camera()
  Return 0.0
EndFunction

Int Function S_lockoutPlayer(Int on)
  RG.Lockout(on != 0)
  Return 0
EndFunction

; Animations: Redguard's own animation numbers have no Skyrim counterpart; the time they hold the script is kept by
; the frame waits that follow them (12 frames a second).
Float Function S_PushAnimation(Int anim, Int count)
  Return 0.0
EndFunction

Float Function S_PushControlAnimation(Int anim, Int count)
  Return 0.0
EndFunction

Int Function S_PlayAnimation(Int anim, Int count)
  Return 0
EndFunction

Float Function S_WaitAnimFrame(Int anim, Int frame)
  Return frame / 12.0
EndFunction

Float Function S_WaitPlayerAnimFrame(Int anim, Int frame)
  Return frame / 12.0
EndFunction

Int Function S_ShowBitmap(Int a, Int b)
  Return 0
EndFunction

Int Function S_UnShowBitmap(Int a)
  Return 0
EndFunction

Float Function S_ViewGXA(Int a, Int b, Int c)
  Return 0.0
EndFunction

Int Function S_QueueMovie(Int n)
  Return 0
EndFunction

Int Function S_PrintSingleParm(Int v)
  Debug.Trace("[TamrielMerge] Redguard " + Self + ": " + v)
  Return 0
EndFunction

; ---- lights ----

Int Function S_Light(Int radius, Int intensity)
  If MyLight
    MyLight.Enable()
  EndIf
  Return 0
EndFunction

Int Function S_LightOff()
  If MyLight
    MyLight.Disable()
  EndIf
  Return 0
EndFunction

; ---- menus (conversations not in Skyrim's dialogue) ----

Int Function S_menuNew()
  RG.ClearMenu()
  menuIndex = new Int[10]
  Int i = 0
  While i < 10
    menuIndex[i] = -1
    i += 1
  EndWhile
  menuSel = -1
  Return 0
EndFunction

; Item (RTX tag) number button of this script's current menu, answering index when chosen.
Int Function S_menuAddItem(Int button, Int index)
  If !menuIndex
    S_menuNew()
  EndIf
  If button >= 0 && button < 10
    RG.ShowMenuItem(button)
    menuIndex[button] = index
  EndIf
  Return 0
EndFunction

Float Function S_menuProc(Int menu)
  If !Menus || menu < 0 || menu >= Menus.Length || !Menus[menu] || !menuIndex
    Return 0.0
  EndIf
  Int b = Menus[menu].Show()
  If b >= 0 && b < 10
    menuSel = menuIndex[b]
  EndIf
  RG.ClearMenu()
  Return 0.0
EndFunction

Int Function S_menuSelection()
  Return menuSel
EndFunction

; ---- the story ----

Int Function S_LoadWorld(Int slot)
  If Places && slot >= 0 && slot < Places.Length && Places[slot]
    Game.GetPlayer().MoveTo(Places[slot])
  EndIf
  Return 0
EndFunction

Int Function S_AddLog(Int stage)
  If RG.LogQuest && stage > 0
    RG.LogQuest.SetStage(stage)
  EndIf
  Return 0
EndFunction

Int Function S_WonGame()
  TMStoryOrder story = Game.GetFormFromFile(0x0003372B, "Skyrim.esm") as TMStoryOrder
  If story
    story.StoryCompleted(1)
  EndIf
  Return 0
EndFunction

; Functions with nothing to do in Skyrim (AI tuning, sprite flats, synchronisation that Papyrus timing already
; gives): the translator calls this with their name so they stay visible in the source.
Int Function S_None()
  Return 0
EndFunction
