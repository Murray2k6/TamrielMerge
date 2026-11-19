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
{The script's menus (a door that answers, a choice outside Skyrim's dialogue), their pages of nine items one after
another: a button per item, shown when its RG.MenuShow global is 1, and "More..." on a page with more after it.}
Int[] Property MenuStarts Auto
{Where each menu's first page is in Menus, by menu number.}
ObjectReference Property MyLight Auto
Bool Property ActsOnPlayer = False Auto
{Redguard's player scripts (item use scripts, COMMON): "me" is Cyrus.}
Int[] Property AttrInit Auto
{The object's starting attributes Skyrim uses (RAAT), as pairs of attribute number and value.}
Int Property SyncGroup = -1 Auto
{The synchronisation group (its scene's attr_group) as a barrier number on RG, -1 for none.}
Int Property SyncMembers = 0 Auto
{How many objects of that group take part (SyncWithGroup waits for them all).}
Bool Property Hangable = False Auto
; An indoor scene stands about Skyrim's origin, not where Redguard's numbers put it (RgPluginBuilder.CentreInteriors:
; Skyrim moves what stands 30,000 units from an interior's origin): the move, added wherever a position is worked out
; from Redguard's numbers and taken off where one is given back as them. 0 outdoors.
Float Property OffX = 0.0 Auto
Float Property OffY = 0.0 Auto
{The player can hang from it (its script tests PlayerHanging): activating it from below (TMRgMove).}

; No parameter or local here is named x, y or z: ObjectReference declares Float properties X, Y and Z (its position),
; and the compiler resolves those names to the properties, not the parameter (2026-10-29: LineUpAt(x, z, ...) "type
; mismatch", and every S_ function with such parameters read the object's position instead of its arguments).
ObjectReference Property Who Auto Hidden

Int pc0 = -2
Int pc1 = 0
Int _pc = 0
Int _cur = 0
Bool _retry = False
Int[] attrs
Bool syncWaiting = False
Int syncGen = 0
Float syncSince = 0.0
ObjectReference walkMarker
Int thread = 0
Float due0 = 0.0
Float due1 = 0.0
Float _yield = 0.0
Bool busy = False
Bool loaded = False
Int myLocks = 0
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
  attrs = new Int[128]
  If AttrInit
    Int k = 0
    While k + 1 < AttrInit.Length
      If AttrInit[k] >= 0 && AttrInit[k] < 128
        attrs[AttrInit[k]] = AttrInit[k + 1]
      EndIf
      k += 2
    EndWhile
  EndIf
EndFunction

Event OnLoad()
  Begin()
EndEvent

Event OnCellAttach()
  Begin()
EndEvent

; Hidden by a script (HideMe: disabled, its cell still there) is not unloaded: Redguard's script runs on whether its
; object shows or not. The docks' arrival (ISLAND's BOATSTAR) locks the player out, hides its boat and lets the player
; go 13 steps later: stopped by its own HideMe, it never did (2026-11-10, user: "you cant walk after opening"; the log
; of every run had its lockout taken and none given back).
; An unload that comes while the object is still with the player is not the place being left: its 3D was dropped and
; made again, or the event is a late one from the moment of arriving (events are queued and can come after the
; load's). In a new game the docks' BOATSTAR stopped for good eight seconds into its arrival that way, twice: its
; variables showed loaded False, its next step due and never run, the boat in full view and the player locked out
; (2026-11-12). Its cell could not tell: a persistent object of a worldspace is in the world's persistent cell, which
; never counts as attached.
Bool Function StillHere()
  If Is3DLoaded()
    Return True
  EndIf
  ; A script holding the player locked out runs to its end wherever it stands. The docks' BOATSTAR is a marker out
  ; at sea, 13,425 units from the player on the quay (read in game, 2026-11-12): past any distance limit, it stopped
  ; with the lockout held and the player could never move again.
  If myLocks > 0
    If RG.LockedOut()
      Return True
    EndIf
    ; A map change let every lockout go.
    myLocks = 0
  EndIf
  Actor p = Game.GetPlayer()
  Cell pc = p.GetParentCell()
  Cell c = GetParentCell()
  If pc && pc.IsInterior()
    Return c == pc
  EndIf
  If c && c.IsInterior()
    Return False
  EndIf
  Return GetWorldSpace() == p.GetWorldSpace() && p.GetDistance(Self) < 10000.0
EndFunction

Event OnUnload()
  If StillHere()
    Return
  EndIf
  loaded = Shell
  AmbientRun()
EndEvent

Event OnCellDetach()
  If StillHere()
    Return
  EndIf
  loaded = Shell
  AmbientRun()
EndEvent

Function Begin()
  Setup()
  If !loaded && !killed
    loaded = True
    ; A place's objects all load in the same moment (Stros M'Kai: 440 scripts): those near the player start at once,
    ; the rest over the next seconds, so the first runs don't all stand in line together (Papyrus log, 2026-11-12:
    ; 230 stacks waiting at once on arriving on the island).
    Float first = Utility.RandomFloat(0.1, 0.4)
    If !Shell
      Float d = Game.GetPlayer().GetDistance(Self)
      If d > 3072.0
        first = Utility.RandomFloat(1.0, 6.0)
      ElseIf d > 1024.0
        first = Utility.RandomFloat(0.4, 2.0)
      EndIf
    EndIf
    RegisterForSingleUpdate(first)
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
  ; Something to hang from, above the player: grabbing it (again: letting go).
  If Hangable && RG.Mover && RG.Mover.HangFrom(Self)
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
  AmbientRun()
  Tick()
  If killed || (pc0 < 0 && (!Interrupt || pc1 < 0)) && !AmbientRunning()
    Return
  EndIf
  Float now = Utility.GetCurrentRealTime()
  ; Near the player four times a second; further off less often (what they wait for is the player coming close).
  Float wait = 0.25
  If !Shell
    Float d = Game.GetPlayer().GetDistance(Self)
    If d > 8192.0 || d < 1.0
      wait = 5.0
      If myLocks > 0
        wait = 0.25
      EndIf
      ; Far off (or no distance to be had: another world): still in the loaded world? (An unload is ignored while the cell is attached, above: this is where
      ; an object the player has really left stops.)
      If !StillHere()
        loaded = Shell
        AmbientRun()
        Return
      EndIf
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

; GetFramePassed's ticks: the engine's nominal ticks since the last run (12.14 a second), the fraction carried over.
Float lastTickAt = -1.0
Float tickCarry = 0.0
Int framesPassed = 1

Function Tick()
  If busy
    Return
  EndIf
  busy = True
  Float now = Utility.GetCurrentRealTime()
  If lastTickAt >= 0.0 && now >= lastTickAt
    Float ticks = (now - lastTickAt) * 12.14 + tickCarry
    framesPassed = Math.Floor(ticks)
    tickCarry = ticks - framesPassed
  EndIf
  lastTickAt = now
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
    _cur = _pc
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

; After a task of the given time, block b (or, for a task still waiting (Retry), this block again after that time).
Function Task(Float seconds, Int b)
  _pc = b
  If _retry
    _retry = False
    _pc = _cur
    If seconds < 0.05
      seconds = 0.05
    EndIf
  EndIf
  If seconds > 0.0
    _yield = seconds
  EndIf
EndFunction

; A task that isn't done yet (a group not all there): its block runs again after the poll time (the translator gives
; such tasks a block of their own).
Float Function Retry(Float poll)
  _retry = True
  Return poll
EndFunction

; ---- multitasks (engine/SOUP.html: six task slots an actor's two threads share; a multitask's slot is named by its
; call's address) ----
; A multitask runs alongside its thread for its time, or, for a person walking somewhere, until they get there (the
; walk on a thread of its own: PathToReference holds its caller until arrival). Issued again while it runs it doesn't
; run again (the translator asks TaskRunning first); TaskStop ends it (a walk stops where the person is, a moving
; object stops; an ambient sound's task ends, its sound playing on); WaitOnTasks waits for all of them.

Int[] mSite
Float[] mDue
Actor[] mWalker
ObjectReference[] mMarker
Float[] mPace
Bool[] mWalking
ObjectReference[] mMover
Int[] mGen
Bool nextIsMulti = False
Int nextSlot = -1

Function Slots()
  If mSite
    Return
  EndIf
  mSite = new Int[6]
  mDue = new Float[6]
  mWalker = new Actor[6]
  mMarker = new ObjectReference[6]
  mPace = new Float[6]
  mWalking = new Bool[6]
  mMover = new ObjectReference[6]
  mGen = new Int[6]
  Int k = 0
  While k < 6
    mSite[k] = -1
    k += 1
  EndWhile
  RegisterForModEvent(WalkEvent(), "OnWalk")
EndFunction

String Function WalkEvent()
  Return "TMRgWalk" + GetFormID()
EndFunction

Bool Function SlotBusy(Int k)
  If mSite[k] < 0
    Return False
  EndIf
  If mWalker[k]
    Return mWalking[k]
  EndIf
  Return Utility.GetCurrentRealTime() < mDue[k]
EndFunction

Int Function FreeSlot()
  Int k = 0
  While k < 6
    If !SlotBusy(k)
      Return k
    EndIf
    k += 1
  EndWhile
  Return -1
EndFunction

; Before a multitask's call: the slot it will hold (a walk it starts goes there).
Function MultiBegin()
  Slots()
  nextIsMulti = True
  nextSlot = FreeSlot()
  If nextSlot >= 0
    mSite[nextSlot] = -1
    mWalker[nextSlot] = None
    mWalking[nextSlot] = False
    mMover[nextSlot] = None
    mGen[nextSlot] = mGen[nextSlot] + 1
  EndIf
EndFunction

; After it: the slot holds it for its time (or its walk). With all six taken, Redguard's slots are full: not kept.
Function MultiAt(Int site, Float seconds)
  Slots()
  Int k = nextSlot
  nextIsMulti = False
  nextSlot = -1
  If k < 0
    k = FreeSlot()
  EndIf
  If k < 0
    Return
  EndIf
  mSite[k] = site
  mDue[k] = Utility.GetCurrentRealTime() + seconds
  If !mWalker[k]
    mMover[k] = Who
  EndIf
EndFunction

Bool Function TaskRunning(Int site)
  Slots()
  Int k = mSite.Find(site)
  If k >= 0
    If SlotBusy(k)
      Return True
    EndIf
    mSite[k] = -1
  EndIf
  AmbientSlots()
  Return ambSite.Find(site) >= 0
EndFunction

; TaskStop(label): the task at that call's address ends (its handler not run again: a sound plays on).
Function TaskStop(Int site)
  Slots()
  Int k = mSite.Find(site)
  If k >= 0
    EndSlot(k)
  EndIf
  AmbientSlots()
  Int j = ambSite.Find(site)
  If j >= 0
    ambSite[j] = -1
    ambChan[j] = -1
  EndIf
EndFunction

Function EndSlot(Int k)
  mGen[k] = mGen[k] + 1
  If mWalker[k] && mWalking[k]
    ; The walk's target where the person stands: their PathToReference returns.
    mWalking[k] = False
    If mMarker[k]
      mMarker[k].MoveTo(mWalker[k])
    EndIf
  ElseIf mMover[k] && !(mMover[k] as Actor) && Utility.GetCurrentRealTime() < mDue[k]
    mMover[k].StopTranslation()
  EndIf
  mSite[k] = -1
EndFunction

; The longest timed multitask's time left; -1 while a walk runs (its end unknown).
Float Function MultiLeft()
  If !mSite
    Return 0.0
  EndIf
  Float now = Utility.GetCurrentRealTime()
  Float left = 0.0
  Int k = 0
  While k < 6
    If SlotBusy(k)
      If mWalker[k]
        Return -1.0
      EndIf
      If mDue[k] - now > left
        left = mDue[k] - now
      EndIf
    EndIf
    k += 1
  EndWhile
  Return left
EndFunction

; A multitask walk: the person paths to the point on a thread of their own (OnWalk), at the pace given.
Function WalkAsync(Actor a, Float tx, Float ty, Float tz, Float pace)
  Int k = nextSlot
  If k < 0
    Return
  EndIf
  If !mMarker[k]
    mMarker[k] = a.PlaceAtMe(Game.GetFormFromFile(0x0000003B, "Skyrim.esm"), 1, True, False)
  EndIf
  mMarker[k].SetPosition(tx, ty, tz)
  If a.GetDistance(mMarker[k]) < 16.0
    Return
  EndIf
  mWalker[k] = a
  mPace[k] = pace
  mWalking[k] = True
  SendModEvent(WalkEvent(), "", k)
EndFunction

Event OnWalk(String eventName, String strArg, Float numArg, Form sender)
  Int k = numArg as Int
  If k < 0 || k >= 6 || !mWalker[k] || !mMarker[k]
    Return
  EndIf
  Int gen = mGen[k]
  Actor a = mWalker[k]
  ObjectReference m = mMarker[k]
  Float start = a.GetDistance(m)
  a.PathToReference(m, mPace[k])
  If mGen[k] != gen
    ; Stopped (TaskStop, KillMyTasks).
    Return
  EndIf
  If a.GetDistance(m) > 48.0 && a.GetDistance(m) < start + 64.0
    ; Stuck on the way (no navmesh through a moving part): the rest by a short slide.
    a.TranslateTo(m.GetPositionX(), m.GetPositionY(), m.GetPositionZ(), a.GetAngleX(), a.GetAngleY(), a.GetAngleZ(), 150.0, 0.0)
  EndIf
  mWalking[k] = False
EndEvent

; Skyrim's people walk about 90 units a second and run about 300: the pace a time asks for.
Float Function PaceFor(Float dist, Float seconds)
  Float pace = 0.0
  If seconds > 0.05
    pace = (dist / seconds - 90.0) / 210.0
  EndIf
  If pace < 0.0
    Return 0.0
  ElseIf pace > 1.0
    Return 1.0
  EndIf
  Return pace
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
; down, z north, in 256ths of a unit), 4-6 angle.vx/vy/vz (2048ths), 7 hide, 8 dead, 9 torch_timer; others read 0.
; Positions are the stretched world's (RgWarp): what scripts copy from one object to another stays consistent, and
; the translator stretches the places scripts name by number (SetPos).
Int Function GetField(ObjectReference r, Int field)
  If !r
    Return 0
  EndIf
  If field == 1
    Return ((r.GetPositionX() - OffX + 32768.0) * 256.0) as Int
  ElseIf field == 2
    Return (-r.GetPositionZ() * 256.0) as Int
  ElseIf field == 3
    Return ((r.GetPositionY() - OffY + 32768.0) * 256.0) as Int
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
    ; torch_timer: the player's torch's seconds left (TMRgState); anyone else's torch burns while equipped.
    Actor p = r as Actor
    If p == Game.GetPlayer()
      Return RG.TorchTime()
    EndIf
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
  If field >= 1 && field <= 3
    Float px = r.GetPositionX()
    Float py = r.GetPositionY()
    Float pz = r.GetPositionZ()
    If field == 1
      px = v / 256.0 - 32768.0 + OffX
    ElseIf field == 2
      pz = -v / 256.0
    Else
      py = v / 256.0 - 32768.0 + OffY
    EndIf
    SetPos(r, px, py, pz)
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
  ElseIf field == 9
    If (r as Actor) == Game.GetPlayer()
      RG.SetTorchTime(v)
    EndIf
  EndIf
EndFunction

; An object put somewhere by a script (its pos fields set). Cyrus moved far while a cutscene holds him is the canah's
; flight (and the like): Skyrim's player is flown there along the way the script sets, over the next few seconds.
Function SetPos(ObjectReference r, Float tx, Float ty, Float tz)
  If !r
    Return
  EndIf
  Actor p = Game.GetPlayer()
  If r == p
    Float dx = tx - p.GetPositionX()
    Float dy = ty - p.GetPositionY()
    Float dz = tz - p.GetPositionZ()
    If (RG.LockedOut() || RG.Flying()) && dx * dx + dy * dy + dz * dz > 512.0 * 512.0
      RG.FlyTo(tx, ty, tz, 2.5)
      Return
    EndIf
  EndIf
  r.SetPosition(tx, ty, tz)
EndFunction

; SetPos from Redguard's numbers (pos.vx/vy/vz, 256ths) where the translator couldn't stretch them.
Function SetPosRg(ObjectReference r, Int rx, Int ry, Int rz)
  SetPos(r, rx / 256.0 - 32768.0 + OffX, rz / 256.0 - 32768.0 + OffY, -ry / 256.0)
EndFunction

; ---- rotation (Skyrim's angles: model to world is RotX(-x) RotY(-y) RotZ(-z), row vectors) ----

Float Function Atan2(Float oy, Float ox)
  If ox > 0.0
    Return Math.atan(oy / ox)
  ElseIf ox < 0.0
    If oy >= 0.0
      Return Math.atan(oy / ox) + 180.0
    EndIf
    Return Math.atan(oy / ox) - 180.0
  ElseIf oy > 0.0
    Return 90.0
  ElseIf oy < 0.0
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
  ; Without its 3D (its cell not loaded: a seagull circling out of sight) nothing can be translated: the turn is made at
  ; once and its time still taken, so the script keeps its pace.
  If !Who.Is3DLoaded()
    Who.SetAngle(ax, ay, az)
    Return seconds
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
    ; People walk there (Skyrim's pathing, at the pace the time asks) and stand exactly where the script put them.
    If a != Game.GetPlayer()
      If nextIsMulti && nextSlot >= 0
        ; A multitask walk: alongside the script, until they get there.
        WalkAsync(a, tx, ty, tz, PaceFor(dist, seconds))
        Return seconds
      EndIf
      WalkTo(a, tx, ty, tz, seconds)
      Return 0.0
    EndIf
    Return seconds
  EndIf
  If seconds <= 0.05 || dist < 0.5
    Who.SetPosition(tx, ty, tz)
    Return 0.0
  EndIf
  If !Who.Is3DLoaded()
    Who.SetPosition(tx, ty, tz)   ; out of sight: there at once, the time still taken (as TurnTo)
    Return seconds
  EndIf
  Who.TranslateTo(tx, ty, tz, Who.GetAngleX(), Who.GetAngleY(), Who.GetAngleZ(), dist / seconds, 0.0)
  Return seconds
EndFunction

; A person walking to a point: pathing to a marker put there, at a walk or a run as the time asks (Redguard gives a
; move its time); stopped short of it (no navmesh through a moving part), the rest by a short slide, so scripted
; positions that later puzzles depend on are reached.
Function WalkTo(Actor a, Float tx, Float ty, Float tz, Float seconds)
  If !walkMarker
    walkMarker = a.PlaceAtMe(Game.GetFormFromFile(0x0000003B, "Skyrim.esm"), 1, True, False)
  EndIf
  walkMarker.SetPosition(tx, ty, tz)
  Float dist = a.GetDistance(walkMarker)
  If dist < 16.0
    Return
  EndIf
  a.PathToReference(walkMarker, PaceFor(dist, seconds))
  If a.GetDistance(walkMarker) > 48.0 && a.GetDistance(walkMarker) < dist + 64.0
    ; Stuck on the way (no navmesh through a moving part): the rest by a short slide.
    a.TranslateTo(tx, ty, tz, a.GetAngleX(), a.GetAngleY(), a.GetAngleZ(), 150.0, 0.0)
  EndIf
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

; The six task slots are shared by the main body and the entry part (the thread run to endint): WaitOnTasks waits
; while any other slot is taken, WaitNonMulti while another blocking one is (Redguard Preservation, engine/SOUP.html
; "Wait, WaitOnTasks, and WaitNonMulti"). Here the other thread holds a blocking slot while it waits on a task (its
; next run due later than the next frame).
Bool Function OtherThreadBlocked()
  Float now = Utility.GetCurrentRealTime()
  If thread == 0
    Return Interrupt && pc1 >= 0 && due1 > now + 0.02
  EndIf
  Return pc0 >= 0 && due0 > now + 0.02
EndFunction

Float Function S_WaitOnTasks()
  Float left = MultiLeft()
  If left < 0.0 || left == 0.0 && (AmbientRunning() || OtherThreadBlocked())
    ; A walk lasts until the person arrives, an ambient sound's task until the listener leaves its range.
    Return Retry(0.25)
  EndIf
  Return left
EndFunction

Float Function S_WaitNonMulti()
  If OtherThreadBlocked()
    Return Retry(0.1)
  EndIf
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

; SetObjectLocation(i, x, y, z): this object's location i set (256ths, Redguard's x east, y down, z north, as the
; position fields are read: a position copied from an object is the stretched world's). OffsetLocation(i, j):
; location i moved to location j's place.
Int Function S_SetObjectLocation(Int i, Int ox, Int oy, Int oz)
  If !Locations || i < 0 || i * 3 + 2 >= Locations.Length
    Return 0
  EndIf
  Locations[i * 3] = ox / 256.0 - 32768.0 + OffX
  Locations[i * 3 + 1] = oz / 256.0 - 32768.0 + OffY
  Locations[i * 3 + 2] = -oy / 256.0
  Return 0
EndFunction

Int Function S_OffsetLocation(Int a, Int b)
  If !Locations || a < 0 || b < 0 || a * 3 + 2 >= Locations.Length || b * 3 + 2 >= Locations.Length
    Return 0
  EndIf
  Locations[a * 3] = Locations[b * 3]
  Locations[a * 3 + 1] = Locations[b * 3 + 1]
  Locations[a * 3 + 2] = Locations[b * 3 + 2]
  Return 0
EndFunction

ObjectReference Function Marker(Int n)
  If !Markers || n < 0 || n >= Markers.GetSize()
    Return None
  EndIf
  Return Markers.GetAt(n) as ObjectReference
EndFunction

; MoveMarker(n, speed) / MoveNodeMarker: an actor walks to the marker (speed 0, the patrols' usual: its own walk;
; any other, the cutscenes' 64-16384: a run); a prop slides there at 150 a second.
Float Function Pace(Int speed)
  If speed > 0
    Return 1.0
  EndIf
  Return 0.5
EndFunction

Float Function S_MoveMarker(Int n, Int speed)
  ObjectReference m = Marker(n)
  If !m
    Return 0.0
  EndIf
  Return GoTo(m.GetPositionX(), m.GetPositionY(), m.GetPositionZ(), Pace(speed), m)
EndFunction

Float Function GoTo(Float tx, Float ty, Float tz, Float pace, ObjectReference m)
  Actor a = Who as Actor
  If a
    If a != Game.GetPlayer()
      If nextIsMulti && nextSlot >= 0 || !m
        WalkAsync(a, tx, ty, tz, pace)
      Else
        a.PathToReference(m, pace)
      EndIf
    EndIf
    Return 0.0
  EndIf
  Float dx = tx - Who.GetPositionX()
  Float dy = ty - Who.GetPositionY()
  Float dz = tz - Who.GetPositionZ()
  Float secs = Math.sqrt(dx * dx + dy * dy + dz * dz) / 150.0
  Return MoveTo3(tx, ty, tz, secs)
EndFunction

Float Function S_MoveNodeMarker(Int n, Int speed)
  Return S_MoveMarker(n, speed)
EndFunction

; MoveNodeLocation(i, speed): to the object's own location i (the thugs back to their places at 4096: a run).
Float Function S_MoveNodeLocation(Int i, Int speed)
  Float[] at = LocationOf(i)
  If !at
    Return 0.0
  EndIf
  Return GoTo(at[0], at[1], at[2], Pace(speed), None)
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

; ResetRotation(x, y, z): the axes flagged go back to the start angle, the others stay (Redguard's x, y, z are
; Skyrim's x, z, y).
Int Function S_ResetRotation(Int ox, Int oy, Int oz)
  Float ax = Who.GetAngleX()
  Float ay = Who.GetAngleY()
  Float az = Who.GetAngleZ()
  Float sx = startAX
  Float sy = startAY
  Float sz = startAZ
  If Who != Self
    TMRgSoup other = Who as TMRgSoup
    If !other
      Return 0
    EndIf
    sx = other.StartAngle(0)
    sy = other.StartAngle(1)
    sz = other.StartAngle(2)
  EndIf
  If ox != 0
    ax = sx
  EndIf
  If oz != 0
    ay = sy
  EndIf
  If oy != 0
    az = sz
  EndIf
  Who.SetAngle(ax, ay, az)
  Return 0
EndFunction

Int Function S_ReloadRotation()
  Return S_ResetRotation(1, 1, 1)
EndFunction

; ResetPosition(x, y, z): the axes flagged go back to the start position, the others stay (Redguard's y is up,
; Skyrim's z).
Int Function S_ResetPosition(Int ox, Int oy, Int oz)
  Float px = Who.GetPositionX()
  Float py = Who.GetPositionY()
  Float pz = Who.GetPositionZ()
  Float sx = startX
  Float sy = startY
  Float sz = startZ
  If Who != Self
    TMRgSoup other = Who as TMRgSoup
    If !other
      Return 0
    EndIf
    sx = other.StartPos(0)
    sy = other.StartPos(1)
    sz = other.StartPos(2)
  EndIf
  If ox != 0
    px = sx
  EndIf
  If oz != 0
    py = sy
  EndIf
  If oy != 0
    pz = sz
  EndIf
  Who.SetPosition(px, py, pz)
  Return 0
EndFunction

; FacePlayer(duration): a task turning Who toward the player, done when duration - 1 ticks have passed
; (engine/SOUP.html; the game's calls give 12 and 8: 0.9 s and 0.58 s).
Float Function S_FacePlayer(Int duration)
  Float seconds = 0.3
  If duration > 1
    seconds = (duration - 1) / 12.14
  EndIf
  Actor p = Game.GetPlayer()
  If !p || p == Who
    Return 0.0
  EndIf
  TurnTo(Who.GetAngleX(), Who.GetAngleY(), Who.GetAngleZ() + Who.GetHeadingAngle(p), seconds)
  Return seconds
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

; The player lined up for an action at this object: Redguard walks Cyrus to a spot at a distance (c, 256ths) on a
; side (a, 2048ths of a turn from its front) of it, facing it; here he steps there (when it's near) and turns to it.
Float Function S_PlayerLineUp(Int a, Int b, Int c)
  Actor p = Game.GetPlayer()
  If c <= 0 || Who == p
    Return S_PlayerFaceObj()
  EndIf
  Float h = Who.GetAngleZ() + a * 360.0 / 2048.0
  Float tx = Who.GetPositionX() + Math.sin(h) * c / 256.0
  Float ty = Who.GetPositionY() + Math.cos(h) * c / 256.0
  Float dx = tx - p.GetPositionX()
  Float dy = ty - p.GetPositionY()
  Float d = Math.sqrt(dx * dx + dy * dy)
  If d < 16.0 || d > 256.0
    Return S_PlayerFaceObj()
  EndIf
  Float face = h + 180.0
  p.TranslateTo(tx, ty, p.GetPositionZ() + 2.0, 0.0, 0.0, face, 180.0, 360.0)
  Return d / 180.0 + 0.1
EndFunction

; PlayerPickupLineUp(x, z, angle): Cyrus stepped to the spot (x, z) (256ths, Redguard's x east and z north, in the
; object's frame) beside this object, turned to face it; PlayerParallelLineUp the same spot, turned to the object's
; heading plus angle (2048ths), as for standing beside it. Far away (over 256 units) he only turns.
Float Function LineUpAt(Int ox, Int oz, Float face)
  Actor p = Game.GetPlayer()
  If Who == p
    Return 0.0
  EndIf
  Float h = Who.GetAngleZ()
  Float fx = ox / 256.0
  Float fz = oz / 256.0
  Float tx = Who.GetPositionX() + fx * Math.cos(h) + fz * Math.sin(h)
  Float ty = Who.GetPositionY() - fx * Math.sin(h) + fz * Math.cos(h)
  Float dx = tx - p.GetPositionX()
  Float dy = ty - p.GetPositionY()
  Float d = Math.sqrt(dx * dx + dy * dy)
  If d > 256.0
    Return S_PlayerFaceObj()
  EndIf
  If d < 8.0
    p.SetAngle(p.GetAngleX(), p.GetAngleY(), face)
    Return 0.1
  EndIf
  p.TranslateTo(tx, ty, p.GetPositionZ() + 2.0, 0.0, 0.0, face, 180.0, 360.0)
  Return d / 180.0 + 0.1
EndFunction

Float Function S_PlayerPickupLineUp(Int ox, Int oz, Int angle)
  Actor p = Game.GetPlayer()
  Float dx = Who.GetPositionX() - p.GetPositionX()
  Float dy = Who.GetPositionY() - p.GetPositionY()
  Float face = Math.atan(dx / dy)
  If dy < 0.0
    face += 180.0
  EndIf
  Return LineUpAt(ox, oz, face + angle * 360.0 / 2048.0)
EndFunction

Float Function S_PlayerParallelLineUp(Int ox, Int oz, Int angle)
  Return LineUpAt(ox, oz, Who.GetAngleZ() + angle * 360.0 / 2048.0)
EndFunction

; A teleporter: Cyrus put at an offset from this object (256ths: x east, y down, z north), facing d (2048ths).
; MovePlayerAboveMe(x, y, z, angle) (engine/SOUP.html): the player placed at an offset (256ths, Redguard's x east,
; y down, z north) turned by Who's orientation and added to its position.
Int Function S_MovePlayerAboveMe(Int a, Int b, Int c, Int d)
  Actor p = Game.GetPlayer()
  Float[] ax = LocalAxis(0)
  Float[] ay = LocalAxis(1)
  Float[] az = LocalAxis(2)
  Float ox = (ax[0] * a + ay[0] * b + az[0] * c) / 256.0
  Float oy = (ax[1] * a + ay[1] * b + az[1] * c) / 256.0
  Float oz = (ax[2] * a + ay[2] * b + az[2] * c) / 256.0
  p.SetPosition(Who.GetPositionX() + ox, Who.GetPositionY() + oy, Who.GetPositionZ() + oz)
  p.SetAngle(0.0, 0.0, -d * 360.0 / 2048.0)
  Return 0
EndFunction

; The player inside a rectangle the translator stretched into Skyrim's world (x, y bounds).
Int Function S_InRectangleSk(Int x1, Int y1, Int x2, Int y2)
  Actor p = Game.GetPlayer()
  Float px = p.GetPositionX()
  Float py = p.GetPositionY()
  Return B(px >= x1 && px <= x2 && py >= y1 && py <= y2)
EndFunction

Int Function S_MoveAboveMe()
  Return 0
EndFunction

; PushPlayer('gremlin') / PopPlayer: the player as the gremlin and back (TMRgState; the argument, a script string,
; always names the gremlin: 27 and 30 uses, all of it).
Int Function S_PushPlayer(Int a)
  RG.PushPlayer()
  Return 0
EndFunction

Int Function S_PopPlayer()
  RG.PopPlayer()
  Return 0
EndFunction

; Offset(x, y, z) / UnOffset / PlayerOffsettask(x, y, z): the model drawn at an offset from its place. Every call in the
; game is a zero offset (dump 2026-10-29: Offset(0, 0, 0) 18, UnOffset 8, PlayerOffsettask(0, 0, 0) 4): nothing to do.
Int Function S_Offset(Int ox, Int oy, Int oz)
  Return 0
EndFunction

Int Function S_UnOffset()
  Return 0
EndFunction

Float Function S_PlayerOffsettask(Int ox, Int oy, Int oz)
  Return 0.0
EndFunction

; BounceObject(power, speed, steer): the object launched up and forward. Its one use (NECRTOWR's NC_BOUNC, when the
; tower's explosion flag is set and Cyrus is within 256 units: BounceObject(160, 40, 1)) throws Cyrus; here he is
; knocked away from the object (Skyrim's push, power 160 as a force of 4).
Int Function S_BounceObject(Int power, Int speed, Int steer)
  Actor p = Game.GetPlayer()
  If p.GetDistance(Who) < 512.0
    Who.PushActorAway(p, power / 40.0)
  EndIf
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
  Slots()
  Int k = 0
  While k < 6
    If mSite[k] >= 0
      EndSlot(k)
    EndIf
    k += 1
  EndWhile
  AmbientEnd()
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

; Redguard's object attributes (SOUP386.DEF attr_*, 0-126): every one is kept on the object (GetAttribute reads it
; back) and these act in Skyrim: 2 strength, 3 health, 4 armor and 120 invulnerable as actor values; 29 group (the
; barrier SyncWithGroup waits at); 44 panic group, the idle an actor stands in (Skyrim's idle for it); 47-49 the
; camera's target offset (how high a shot looks at the object); 58 aggression. The rest tune Redguard's own movement,
; animation and collision.
Int[] Function AttrsOf(ObjectReference r)
  TMRgSoup other = r as TMRgSoup
  If r != Self && other
    Return other.Attributes()
  EndIf
  If !attrs
    Setup()
  EndIf
  Return attrs
EndFunction

Int[] Function Attributes()
  Setup()
  Return attrs
EndFunction

Int Function S_SetAttribute(Int attr, Int v)
  If attr < 0 || attr >= 128
    Return 0
  EndIf
  Int[] at = AttrsOf(Who)
  If at
    at[attr] = v
  EndIf
  Actor a = Who as Actor
  If !a
    Return 0
  EndIf
  If attr == 2
    S_setStrength(v)
  ElseIf attr == 3 && v > 0 && a != Game.GetPlayer()
    S_setHealth(v)
  ElseIf attr == 4
    a.SetActorValue("DamageResist", v)
  ElseIf attr == 44
    StandIdle(a, v)
  ElseIf attr == 58 && a != Game.GetPlayer()
    If v > 0
      a.SetActorValue("Aggression", 1)
    Else
      a.SetActorValue("Aggression", 0)
    EndIf
  ElseIf attr == 120
    a.SetGhost(v != 0)
  EndIf
  Return 0
EndFunction

Int Function S_GetAttribute(Int attr)
  Int[] at = AttrsOf(Who)
  If !at || attr < 0 || attr >= 128
    Return 0
  EndIf
  Return at[attr]
EndFunction

Int Function S_GetMyAttr(Int attr)
  Return S_GetAttribute(attr)
EndFunction

Int Function S_SetMyAttr(Int attr, Int v)
  Return S_SetAttribute(attr, v)
EndFunction

; How high above an object's origin its camera target is (attr_camera_target_offset_y, Redguard's up; people's
; heads by default).
Float Function LookHeight(ObjectReference r)
  Int[] at = AttrsOf(r)
  Float h = 0.0
  If at
    h = at[48]
  EndIf
  If h == 0.0 && (r as Actor)
    h = 110.0
  EndIf
  Return h
EndFunction

; An actor's standing animation (its panic group): Skyrim's idle for it, or back to plain standing.
Function StandIdle(Actor a, Int anim)
  If !a || a.IsDead() || a.IsInCombat()
    Return
  EndIf
  Idle i = RG.IdleFor(anim, a == Game.GetPlayer())
  If i
    a.PlayIdle(i)
  ElseIf anim == 0 && RG.IdleStop
    a.PlayIdle(RG.IdleStop)
  EndIf
EndFunction

; ---- group synchronisation ----

; SyncWithGroup: the group's members wait for one another at each sync point (Redguard's machines turning together),
; a barrier on RG; one that waits more than 10 seconds for a member that isn't running (unloaded, gone) lets them go.
Float Function S_SyncWithGroup(Int point)
  If SyncGroup < 0 || SyncMembers <= 1
    Return 0.0
  EndIf
  If !syncWaiting
    Int g = RG.SyncArrive(SyncGroup, SyncMembers)
    If g < 0
      Return 0.0
    EndIf
    syncWaiting = True
    syncGen = g
    syncSince = Utility.GetCurrentRealTime()
    Return Retry(0.05)
  EndIf
  If RG.SyncGeneration(SyncGroup) != syncGen
    syncWaiting = False
    Return 0.0
  EndIf
  If Utility.GetCurrentRealTime() - syncSince > 10.0
    RG.SyncReset(SyncGroup)
    syncWaiting = False
    Return 0.0
  EndIf
  Return Retry(0.05)
EndFunction

Float Function S_SyncWithUser(Int point)
  Return S_SyncWithGroup(point)
EndFunction

Int Function S_ResetGroupSync()
  If SyncGroup >= 0
    RG.SyncReset(SyncGroup)
  EndIf
  syncWaiting = False
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

; adjustHealth(delta, kill): delta of Redguard's 0-100 health; kill 0 keeps the actor alive (the game's 137 damaging
; calls pass 1, Arkay's heal 0).
Int Function S_adjustHealth(Int delta, Int kill)
  Actor a = Who as Actor
  If !a
    Return 0
  EndIf
  Float amount = MaxHealth(a) * delta / 100.0
  If amount < 0.0
    If kill == 0
      Float now = a.GetActorValue("Health")
      If -amount >= now
        amount = -(now - 1.0)
      EndIf
    EndIf
    If amount < 0.0
      a.DamageActorValue("Health", -amount)
    EndIf
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

; ZapPlayer(v): v of Redguard's 0-100 health off the player (as adjustHealth and setHealth scale it; 2026-10-29, it
; took v raw points of Skyrim's health).
Int Function S_ZapPlayer(Int v)
  Actor p = Game.GetPlayer()
  Float amount = MaxHealth(p) * v / 100.0
  If amount > 0.0
    p.DamageActorValue("Health", amount)
  ElseIf amount < 0.0
    p.RestoreActorValue("Health", -amount)
  EndIf
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

; ACTIVATE(label) / TorchActivate(label) (Redguard Preservation engine/hud.html): true on the call made while the
; player uses the object (here: activates it in Skyrim, consumed by the first call), unless the player is locked
; out, has a sword drawn, or the activation hold-off runs; ACTIVATE needs no torch burning, TorchActivate a burning one
; (a torch in the left hand). Firing starts the hold-off for every object: 22 ticks (1.8 s). A label without an RTX
; entry never activates (the translator writes 0).
Int Function S_ACTIVATE(Int label)
  Return Activation(False)
EndFunction

Int Function S_TorchActivate(Int label)
  Return Activation(True)
EndFunction

Int Function Activation(Bool torch)
  If !activated
    Return 0
  EndIf
  activated = False
  Actor p = Game.GetPlayer()
  Float now = Utility.GetCurrentRealTime()
  If RG.LockedOut() || p.IsWeaponDrawn() || (p.GetEquippedItemType(0) == 11) != torch || now < RG.ActivateHold
    Return 0
  EndIf
  RG.ActivateHold = now + 22.0 / 12.14
  Return 1
EndFunction

; Deactivate: the hold-off ends (engine/hud.html).
Int Function S_Deactivate()
  RG.ActivateHold = 0.0
  Return 0
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

; Cyrus hanging from this object (TMRgMove holds him under it).
Int Function S_PlayerHanging()
  ObjectReference h = RG.HangingOn()
  Return B(h && h == Who)
EndFunction

Int Function S_PlayerOnScape()
  Return B(!Game.GetPlayer().IsInInterior())
EndFunction

; PlayerStatus() (engine/SOUP.html): the class of the first of its rules the player meets, 0 for none: queued motion or a
; jump (1-4), falling (5-7), sliding (8), living states (9-12), animation groups (15-20), moved since the update began
; (21). The game's scripts test only for 0 (2 of them: standing still on something). Here: in the air 5, moved since this
; actor last ran 21, else 0.
Float statusX
Float statusY
Float statusZ
Bool statusSeen = False

Int Function S_PlayerStatus()
  Actor p = Game.GetPlayer()
  Float ox = p.GetPositionX()
  Float oy = p.GetPositionY()
  Float oz = p.GetPositionZ()
  Bool moved = statusSeen && (ox - statusX) * (ox - statusX) + (oy - statusY) * (oy - statusY) + (oz - statusZ) * (oz - statusZ) > 1.0
  statusX = ox
  statusY = oy
  statusZ = oz
  statusSeen = True
  If p.GetAnimationVariableBool("bInJumpState")
    Return 5
  ElseIf moved
    Return 21
  EndIf
  Return 0
EndFunction

; The player inside a rectangle of Redguard's ground plane (x east, z north; 256ths, as its positions are).
Int Function S_InRectangle(Int x1, Int z1, Int x2, Int z2)
  Actor p = Game.GetPlayer()
  Float rx = (p.GetPositionX() - OffX + 32768.0) * 256.0
  Float rz = (p.GetPositionY() - OffY + 32768.0) * 256.0
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
    RG.TorchLit(a)
  EndIf
  Return 0
EndFunction

Int Function S_UnlightTorch()
  Actor a = Who as Actor
  Form torch = RG.Item(45)
  If a && torch
    a.UnequipItem(torch, False, True)
    If a == Game.GetPlayer()
      RG.TorchOut()
    EndIf
  EndIf
  Return 0
EndFunction

; InKey(code) (engine/SOUP.html "InKey"): 1 while the key of the low byte's character is down, with each modifier the
; next three bytes name held (s Shift, c Control, a Alt); a character with no key 0.
Int Function S_InKey(Int code)
  Int dx = KeyOf(Math.LogicalAnd(code, 255))
  If dx < 0 || !Input.IsKeyPressed(dx)
    Return 0
  EndIf
  Int m = 1
  While m < 4
    Int c = Math.LogicalAnd(Math.RightShift(code, 8 * m), 255)
    If c == 115 || c == 83
      If !Input.IsKeyPressed(42) && !Input.IsKeyPressed(54)
        Return 0
      EndIf
    ElseIf c == 99 || c == 67
      If !Input.IsKeyPressed(29) && !Input.IsKeyPressed(157)
        Return 0
      EndIf
    ElseIf c == 97 || c == 65
      If !Input.IsKeyPressed(56) && !Input.IsKeyPressed(184)
        Return 0
      EndIf
    EndIf
    m += 1
  EndWhile
  Return 1
EndFunction

; A character's DirectInput key (letters either case, digits, space), or -1.
Int Function KeyOf(Int c)
  If c >= 65 && c <= 90
    c += 32
  EndIf
  If c >= 97 && c <= 122
    Int[] letters = new Int[26]
    letters[0] = 30
    letters[1] = 48
    letters[2] = 46
    letters[3] = 32
    letters[4] = 18
    letters[5] = 33
    letters[6] = 34
    letters[7] = 35
    letters[8] = 23
    letters[9] = 36
    letters[10] = 37
    letters[11] = 38
    letters[12] = 50
    letters[13] = 49
    letters[14] = 24
    letters[15] = 25
    letters[16] = 16
    letters[17] = 19
    letters[18] = 31
    letters[19] = 20
    letters[20] = 22
    letters[21] = 47
    letters[22] = 17
    letters[23] = 45
    letters[24] = 21
    letters[25] = 44
    Return letters[c - 97]
  ElseIf c >= 49 && c <= 57
    Return c - 47
  ElseIf c == 48
    Return 11
  ElseIf c == 32
    Return 57
  EndIf
  Return -1
EndFunction

; GetFramePassed (engine/SOUP.html): the whole nominal ticks of the current update, the engine's about 12.14 a second
; (normal_frame_rate 12 per 18 BIOS ticks): those since this actor's last run here, the fraction carried.
Int Function S_GetFramePassed()
  Return framesPassed
EndFunction

; QuickRnd(mask): one draw of the shared stream, masked (engine/SOUP.html "Rnd and QuickRnd").
Int Function S_QuickRnd(Int mask)
  Return Math.LogicalAnd(RG.Draw(), mask)
EndFunction

; Rnd(bound): one draw masked with the smallest of 1, 3, 7 .. 16383 that holds bound values (none past 16384), then the
; remainder by the bound: 0 to bound - 1. A bound of 0 or less still draws, and gives 0.
Int Function S_Rnd(Int bound)
  Int draw = RG.Draw()
  If bound <= 0
    Return 0
  EndIf
  If bound <= 16384
    Int mask = 1
    While mask + 1 < bound
      mask = mask * 2 + 1
    EndWhile
    draw = Math.LogicalAnd(draw, mask)
  EndIf
  Return draw % bound
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

; Holding an item for an animation (Redguard draws it in Cyrus's hand): a weapon or torch Skyrim equips, item 0 (an
; empty hand) takes it back; potions and the rest show as Skyrim's own use shows them (TMRgState.HandItem).
; HandItem(n): the item in the hand: a weapon or the torch as itself, any other item with a hand model as its twin
; (RGHandItems), the rest (potions) Skyrim's own use.
Int Function S_HandItem(Int n)
  Actor a = Who as Actor
  If !a
    Return 0
  EndIf
  If n <= 0
    RG.HandItem(a, None)
    Return 0
  EndIf
  Form f = ItemForm(n)
  If !(f as Weapon) && !(f as Light)
    Form twin = RG.HandTwin(n)
    If twin
      f = twin
    EndIf
  EndIf
  RG.HandItem(a, f)
  Return 0
EndFunction

Int Function S_DisplayHandItem(Int n, Int a, Int b, Int c)
  Return S_HandItem(n)
EndFunction

Int Function S_DisplayHiltItem(Int n, Int a, Int b, Int c)
  Return S_HandItem(n)
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

; ShowItem's text (the translator's: the item's description or the call's RTX line) as a line.
Int Function S_ShowText(String text)
  Debug.Notification(text)
  Return 0
EndFunction

Int Function S_ShowItemNoRtx(Int n, Int a, Int b)
  Return 0
EndFunction

; DropAllItems (engine/SOUP.html): DropItem of every item Cyrus owns, by item number, then no item selected. Its one
; caller is ITEMLIST (the catacombs' prison: Cyrus wakes up stripped, counts first, and gets everything back when he
; finds his things, CatacombsFoundStuff). Only Redguard's items (RGItems) go; the player's own Skyrim things stay.
Int Function S_DropAllItems()
  Actor p = Game.GetPlayer()
  Int n = RG.Items.GetSize()
  Int i = 0
  While i < n
    Form f = ItemForm(i)
    If f
      Int c = p.GetItemCount(f)
      If c > 0
        p.RemoveItem(f, c, True)
      EndIf
    EndIf
    i += 1
  EndWhile
  RG.SelectedItem = 0
  Return 0
EndFunction

; OpenShell(name): the scene's program of that name run on its own, as an item's use script is (a shell placed at the
; player: RGShells, by the translator's number).
Int Function S_OpenShell(Int i)
  If RG.Shells && i >= 0 && i < RG.Shells.GetSize()
    Form f = RG.Shells.GetAt(i)
    If f as Activator
      Game.GetPlayer().PlaceAtMe(f)
    EndIf
  EndIf
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

; ---- sound (Redguard Preservation, engine/sound.html; the channels on RG) ----
; Sound(effect, volume offset, rate offset): placed at Who, its volume Redguard's line over distance (each offset's
; own output model: RgScripts.BuildSounds), started even where that is 0. FlatSound: centred, at the effect's volume
; plus the offset. Both at the effect's rate plus the rate offset, owned by this object, from channel 6.

; The entry for a play, and the gain left to apply when no entry was built for its exact offset (a variable's).
Float soundGain = 1.0

Int Function EntryFor(Int kind, Int n, Int vol, Int rate)
  soundGain = 1.0
  Int e = RG.SoundEntry(kind, n, vol, rate)
  If e >= 0
    Return e
  EndIf
  e = RG.SoundEntry(kind, n, 0, rate)
  If e < 0
    e = RG.SoundEntry(kind, n, 0, 0)
  EndIf
  If e < 0 && kind == 1
    e = RG.SoundEntry(1, n, 0, 0)
  EndIf
  soundGain = (127 + vol) / 127.0
  Return e
EndFunction

Int Function S_Sound(Int n, Int vol, Int rate)
  If n < 0 || 127 + vol <= 0
    Return 0
  EndIf
  RG.PlayEntry(EntryFor(0, n, vol, rate), n, Self, Who, 6, soundGain)
  Return 0
EndFunction

Int Function S_FlatSound(Int n, Int vol, Int rate)
  If n < 0
    Return 0
  EndIf
  RG.PlayEntry(EntryFor(1, n, vol, rate), n, Self, Game.GetPlayer(), 6, soundGain)
  Return 0
EndFunction

; AmbientSound(effect, volume offset, rate offset): a multitask per call site (called again while it runs, it doesn't
; run again). Each run: past the distance where its volume is 0, or with this object no longer updated (its cell
; unloaded), its sound stops and the task ends; otherwise its sound starts if it isn't playing (a one-shot effect
; again when it ends). The volume and pan follow the listener (Skyrim places it at Who with the offset's curve).
; Ending the task (KillMyTasks) leaves its sound playing.
Int[] ambSite
Int[] ambEntry
Int[] ambEffect
Int[] ambChan
ObjectReference[] ambWho

Function AmbientSlots()
  If ambSite
    Return
  EndIf
  ambSite = new Int[6]
  ambEntry = new Int[6]
  ambEffect = new Int[6]
  ambChan = new Int[6]
  ambWho = new ObjectReference[6]
  Int k = 0
  While k < 6
    ambSite[k] = -1
    ambChan[k] = -1
    k += 1
  EndWhile
EndFunction

; In range of the listener, in a cell that is loaded (the emitter is often an XMarker, which has no 3D of its own).
Bool Function Heard(ObjectReference r, Int entry)
  If !r
    Return False
  EndIf
  Cell c = r.GetParentCell()
  Return c && c.IsAttached() && Game.GetPlayer().GetDistance(r) < RG.SoundReachOf(entry)
EndFunction

Float Function S_AmbientSound(Int n, Int vol, Int rate, Int site)
  AmbientSlots()
  If ambSite.Find(site) >= 0 || n < 0 || 127 + vol <= 0
    Return 0.0
  EndIf
  Int e = EntryFor(0, n, vol, rate)
  If e < 0 || !Heard(Who, e)
    Return 0.0
  EndIf
  Int k = ambSite.Find(-1)
  If k < 0
    Return 0.0
  EndIf
  ambSite[k] = site
  ambEntry[k] = e
  ambEffect[k] = n
  ambWho[k] = Who
  ambChan[k] = RG.PlayEntry(e, n, Self, Who, 6, soundGain)
  Return 0.0
EndFunction

; Each run of the ambient tasks (every update while the cell is loaded).
Function AmbientRun()
  If !ambSite
    Return
  EndIf
  Int k = 0
  While k < 6
    If ambSite[k] >= 0
      If !loaded || !Heard(ambWho[k], ambEntry[k])
        RG.StopChannel(ambChan[k])
        ambSite[k] = -1
        ambChan[k] = -1
      ElseIf !RG.ChannelPlaying(ambChan[k])
        ambChan[k] = RG.PlayEntry(ambEntry[k], ambEffect[k], Self, ambWho[k], 6, 1.0)
      EndIf
    EndIf
    k += 1
  EndWhile
EndFunction

Bool Function AmbientRunning()
  If !ambSite
    Return False
  EndIf
  Int k = 0
  While k < 6
    If ambSite[k] >= 0
      Return True
    EndIf
    k += 1
  EndWhile
  Return False
EndFunction

; Ends the ambient tasks without stopping their sounds (KillMyTasks; TaskStop).
Function AmbientEnd()
  If ambSite
    Int k = 0
    While k < 6
      ambSite[k] = -1
      ambChan[k] = -1
      k += 1
    EndWhile
  EndIf
EndFunction

Int Function S_EndMySounds()
  RG.EndSounds(-1, Self)
  Return 0
EndFunction

Int Function S_EndMySound(Int n)
  If n >= 0
    RG.EndSounds(n, Self)
  EndIf
  Return 0
EndFunction

Int Function S_EndSound(Int n)
  If n >= 0
    RG.EndSounds(n, None)
  EndIf
  Return 0
EndFunction

Int Function S_StopAllSounds()
  RG.StopAllSounds()
  Return 0
EndFunction

; A line (RTX): said by Who when it is an actor with a voiced topic, else its recording played there (Cyrus's at the
; player) with the text shown, and a gesture of the line's animation groups when Skyrim has an idle for one. Returns
; its length.
Float Function Line(Int i, String text, Float seconds, Bool cyrus, Int gesture)
  If gesture >= 0
    If cyrus
      PlayGroup(Game.GetPlayer(), gesture)
    Else
      PlayGroup(Who as Actor, gesture)
    EndIf
  EndIf
  Form f = None
  If i >= 0 && i < RG.Lines.GetSize()
    f = RG.Lines.GetAt(i)
  EndIf
  Topic t = f as Topic
  Actor a = Who as Actor
  If t && a && !cyrus
    a.Say(t)
  Else
    ; Speech takes a channel from 0 with no owner (StopAllSounds stops it, EndMySounds doesn't).
    Sound s = f as Sound
    If s
      If cyrus
        RG.PlaySound(s, seconds, -1, None, Game.GetPlayer(), 0, 1.0)
      Else
        RG.PlaySound(s, seconds, -1, None, Who, 0, 1.0)
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

; AmbientRTX(label): the line's recording as a sound, centred (at the player), owned by this object from channel 0,
; its text not shown; a multitask lasting the recording (not restarted).
Float Function AmbientLine(Int i, Float seconds)
  If i >= 0 && i < RG.Lines.GetSize()
    RG.PlaySound(RG.Lines.GetAt(i) as Sound, seconds, -1, Self, Game.GetPlayer(), 0, 1.0)
  EndIf
  Return seconds
EndFunction

; ---- the screen, the camera and animation ----

; Redguard's camera moves (show*, look*) as Skyrim's camera allows (it can't be placed): a shot of an object turns the
; player's view to it, at its camera target height; a shot of Cyrus from somewhere is Skyrim's third-person camera
; on him; showPlayer, XResetCamera and the cutscene's end give the camera back. The time they take is kept.
Float Function S_Camera()
  Return 0.0
EndFunction

Float Function ShotOfWho()
  If Who == Game.GetPlayer()
    RG.ShotOfCyrus()
  Else
    RG.ShotOf(Who, LookHeight(Who))
  EndIf
  Return 0.0
EndFunction

; A shot with its camera placed (TMRgState.PlaceShot): of the script's own object, or of Cyrus (who isn't shown while
; he holds a weapon, as Redguard's showCyrus forms return at once then). The time left for the task to wait: none
; when the camera was placed (a pan has taken its time already), the pan's when Skyrim's own camera stood in.
Float Function PlacedShot(Bool abCyrus, Int ox, Int oy, Int oz, Bool abTurned, Int t)
  ObjectReference subject = Who
  If abCyrus
    Actor p = Game.GetPlayer()
    If p.IsWeaponDrawn()
      Return 0.0
    EndIf
    subject = p
  EndIf
  If RG.PlaceShot(subject, LookHeight(subject), ox, oy, oz, abTurned, PanTime(t))
    Return 0.0
  EndIf
  If abCyrus
    RG.ShotOfCyrus()
  Else
    ShotOfWho()
  EndIf
  Return PanTime(t)
EndFunction

Float Function S_showObj(Int ox, Int oy, Int oz)
  Return PlacedShot(False, ox, oy, oz, False, 0)
EndFunction

Float Function S_showObjLoc(Int ox, Int oy, Int oz)
  Return PlacedShot(False, ox, oy, oz, True, 0)
EndFunction

; The pans (showObjPan*, showCyrusPan*, showPlayerPan) take t ticks (12.14 a second: START's SCENEST waits 50 after a
; pan of 48, the item scripts 4 after one of 6); the shot is Skyrim's at once, the task waits the pan's time.
Float Function PanTime(Int t)
  If t <= 0
    Return 0.0
  EndIf
  Return t / 12.14
EndFunction

Float Function S_showObjPan(Int ox, Int oy, Int oz, Int t)
  Return PlacedShot(False, ox, oy, oz, False, t)
EndFunction

Float Function S_showObjPanLoc(Int ox, Int oy, Int oz, Int t)
  Return PlacedShot(False, ox, oy, oz, True, t)
EndFunction

Float Function S_showObjRot(Int a, Int b, Int c, Int d, Int e, Int f, Int g, Int h)
  Return ShotOfWho()
EndFunction

Float Function S_lookObj(Int a)
  Return ShotOfWho()
EndFunction

Float Function S_showCyrus(Int ox, Int oy, Int oz)
  Return PlacedShot(True, ox, oy, oz, False, 0)
EndFunction

Float Function S_showCyrusLoc(Int ox, Int oy, Int oz)
  Return PlacedShot(True, ox, oy, oz, True, 0)
EndFunction

Float Function S_showCyrusPan(Int ox, Int oy, Int oz, Int t)
  Return PlacedShot(True, ox, oy, oz, False, t)
EndFunction

Float Function S_showCyrusPanLoc(Int ox, Int oy, Int oz, Int t)
  Return PlacedShot(True, ox, oy, oz, True, t)
EndFunction

Float Function S_lookCyrus(Int a)
  RG.ShotOfCyrus()
  Return 0.0
EndFunction

Float Function S_showPlayer()
  RG.EndShot()
  Return 0.0
EndFunction

Float Function S_showPlayerPan(Int t)
  RG.EndShot()
  Return PanTime(t)
EndFunction

Int Function S_XResetCamera()
  RG.EndShot()
  Return 0
EndFunction

Int Function S_updatePlayerViewAttrib()
  Return 0
EndFunction

Int Function S_cameraController()
  Return 0
EndFunction

Int Function S_lockoutPlayer(Int on)
  If on != 0
    myLocks += 1
  ElseIf myLocks > 0
    myLocks -= 1
  EndIf
  RG.Lockout(on != 0, Self)
  Return 0
EndFunction

; Animations: Redguard's animation groups that Skyrim has an idle for play it (picking up, levers, talking, drinking,
; standing); the time they hold the script is kept by the frame waits that follow them (12 frames a second).
Float animStarted = -1.0   ; real time the last group started (frame waits count from it)
Int animGroup = -1
Int animCount = 1

Float Function S_PushAnimation(Int anim, Int count)
  PlayGroup(Who as Actor, anim, count)
  Return 0.0
EndFunction

Float Function S_PushControlAnimation(Int anim, Int count)
  PlayGroup(Who as Actor, anim, count)
  Return 0.0
EndFunction

Int Function S_PlayAnimation(Int anim, Int count)
  PlayGroup(Who as Actor, anim, count)
  Return 0
EndFunction

; A group played count times (Redguard loops it; Skyrim's idle plays once, then again as the waits reach the next
; loop: WaitAnimFrame past the group's length restarts it), its start remembered for the frame waits.
Function PlayGroup(Actor a, Int anim, Int count = 1)
  animStarted = Utility.GetCurrentRealTime()
  animGroup = anim
  If count < 1
    count = 1
  EndIf
  animCount = count
  If !a || a.IsDead() || a.IsInCombat()
    Return
  EndIf
  Idle i = RG.IdleFor(anim, a == Game.GetPlayer())
  If i
    a.PlayIdle(i)
  EndIf
EndFunction

; WaitAnimFrame(anim, frame): until the group reaches that frame (12.14 ticks a second: SOUP), measured from the
; group's start; a frame past the group's end is in its next loop, so the idle is played again.
Float Function S_WaitAnimFrame(Int anim, Int frame)
  Float due = frame / 12.14
  If animStarted >= 0.0 && anim == animGroup
    Float left = animStarted + due - Utility.GetCurrentRealTime()
    If left < 0.0
      left = 0.0
    EndIf
    Return left
  EndIf
  Return due
EndFunction

Float Function S_WaitPlayerAnimFrame(Int anim, Int frame)
  Return S_WaitAnimFrame(anim, frame)
EndFunction

; COMMON's potion status icons: a line when one comes or goes.
Int Function S_ShowBitmap(Int a, Int b)
  RG.Status(a, True)
  Return 0
EndFunction

Int Function S_UnShowBitmap(Int a)
  RG.Status(a, False)
  Return 0
EndFunction

; ViewGXA(image, frame, count): a task showing one GXA image over the world for count - 1 ticks of the engine's
; 12.14 a second (engine/SOUP.html); the observatory telescope's star fields (count 36: 2.9 s). Picture number a is
; the translator's, from the image's file name.
Float Function S_ViewGXA(Int a, Int b, Int c)
  Float seconds = 0.0
  If c > 1
    seconds = (c - 1) / 12.14
  EndIf
  RG.ShowPictureFor(a, seconds)
  Return seconds
EndFunction

; A movie (MENU.INI movie_name[n]) played as Skyrim plays Bink: Redguard queued it for the next map change, which
; follows the call at once in every script that does.
Int Function S_QueueMovie(Int n)
  RG.PlayMovie(n)
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

; LightFlicker(a, b): the light's flicker is in its record (set by the build for the lights whose scripts call it).
Int Function S_LightFlicker(Int a, Int b)
  Return 0
EndFunction

; LightIntensity(v): Skyrim can't change a placed light's brightness: the light is on above 0 (OB_CON01 ramps its light
; up in 9s to 100 and down again before LightOff), off at 0 and below.
Int Function S_LightIntensity(Int v)
  If !MyLight
    Return 0
  EndIf
  If v > 0
    MyLight.Enable()
  Else
    MyLight.Disable()
  EndIf
  Return 0
EndFunction

; LightOffset(x, y, z): the light moved to an offset from this object (256ths; Redguard's x east, y down, z north).
Int Function S_LightOffset(Int ox, Int oy, Int oz)
  If !MyLight
    Return 0
  EndIf
  MyLight.SetPosition(Who.GetPositionX() + ox / 256.0, Who.GetPositionY() + oz / 256.0, Who.GetPositionZ() - oy / 256.0)
  Return 0
EndFunction

; ---- menus (conversations not in Skyrim's dialogue) ----

Int Function S_menuNew()
  RG.ClearMenu()
  menuIndex = new Int[63]
  Int i = 0
  While i < 63
    menuIndex[i] = -1
    i += 1
  EndWhile
  menuSel = -1
  Return 0
EndFunction

; Item slot (the translator's: the item's place in this menu) added, answering index when chosen.
Int Function S_menuAddItem(Int slot, Int index)
  If !menuIndex
    S_menuNew()
  EndIf
  If slot >= 0 && slot < menuIndex.Length
    menuIndex[slot] = index
  EndIf
  Return 0
EndFunction

; The menu's pages, nine items each (RgScripts.MenuPages): a page shows the items added on it and, when items were
; added on a later page, "More..." (its tenth button), which turns to the next page.
Float Function S_menuProc(Int menu)
  If !Menus || !menuIndex || menu < 0
    Return 0.0
  EndIf
  Int first = menu
  Int pages = 1
  If MenuStarts && menu < MenuStarts.Length
    first = MenuStarts[menu]
    If menu + 1 < MenuStarts.Length
      pages = MenuStarts[menu + 1] - first
    Else
      pages = Menus.Length - first
    EndIf
  EndIf
  Int p = 0
  While p < pages && first + p < Menus.Length
    RG.ClearMenu()
    Int b = 0
    While b < 9
      Int slot = p * 9 + b
      If slot < menuIndex.Length && menuIndex[slot] >= 0
        RG.ShowMenuItem(b)
      EndIf
      b += 1
    EndWhile
    Bool more = False
    Int s = (p + 1) * 9
    While s < menuIndex.Length && !more
      more = menuIndex[s] >= 0
      s += 1
    EndWhile
    If more && p + 1 < pages
      RG.ShowMenuItem(9)
    EndIf
    Int pick = -1
    If Menus[first + p]
      pick = Menus[first + p].Show()
    EndIf
    RG.ClearMenu()
    If pick == 9 && more && p + 1 < pages
      p += 1
    Else
      If pick >= 0 && pick < 9 && p * 9 + pick < menuIndex.Length
        menuSel = menuIndex[p * 9 + pick]
      EndIf
      Return 0.0
    EndIf
  EndWhile
  Return 0.0
EndFunction

Int Function S_menuSelection()
  Return menuSel
EndFunction

; ---- the story ----

Int Function S_LoadWorld(Int slot)
  If Places && slot >= 0 && slot < Places.Length && Places[slot]
    RG.MapChanged()   ; the map left behind holds the player no more
    Game.GetPlayer().MoveTo(Places[slot])
  EndIf
  Return 0
EndFunction

Int Function S_AddLog(Int stage)
  RG.AddLog(stage)
  Return 0
EndFunction

Int Function S_WonGame()
  ; The ending (MENU.INI movie 4, WIN.SMK), as Redguard played it on winning.
  RG.PlayMovie(4)
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
