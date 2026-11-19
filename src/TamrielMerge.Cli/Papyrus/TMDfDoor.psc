ScriptName TMDfDoor extends ObjectReference
{A Daggerfall door as Daggerfall's own model (DoorPlacer; user, 2026-11-10: "some doors arent against walls", "can we use
daggerfalls doors instead of a skyrim one?"). The model stands where Daggerfall put it, its origin on its hinge, and has
no animation of Skyrim's: using it swings it a quarter turn about the hinge and back, as Daggerfall Unity's
DaggerfallActionDoor does (OpenAngle -90, about the vertical). A lock (TMDfLock) or a door notice (TMDfAction) blocks
its activation and opens it themselves when it should open (Use); the scripts that opened Skyrim's doors with SetOpen
ask through SetDoor and DoorState.}

Float Property OpenTurn = -90.0 Auto
{Degrees about the vertical from shut to open.}
Float Property Seconds = 0.6 Auto
Sound Property OpenSound Auto
Sound Property CloseSound Auto

Bool isOpen
Bool busy
Bool known
Float shutX
Float shutY
Float shutZ

Bool Function IsOpenNow()
  Return isOpen
EndFunction

Event OnActivate(ObjectReference akActionRef)
  ; Blocked: its lock or its notice decides, and calls Toggle itself.
  If IsActivationBlocked() || IsLocked()
    Return
  EndIf
  Swing(!isOpen)
EndEvent

Function Swing(Bool abOpen)
  If busy || abOpen == isOpen
    Return
  EndIf
  busy = True
  If !known
    shutX = GetAngleX()
    shutY = GetAngleY()
    shutZ = GetAngleZ()
    known = True
  EndIf
  isOpen = abOpen
  Float turnZ = shutZ
  If abOpen
    turnZ += OpenTurn
    If OpenSound
      OpenSound.Play(Self)
    EndIf
  ElseIf CloseSound
    CloseSound.Play(Self)
  EndIf
  Float turnSpeed = 90.0
  If Seconds > 0.0
    turnSpeed = Math.Abs(OpenTurn) / Seconds
  EndIf
  If Is3DLoaded()
    TranslateTo(GetPositionX(), GetPositionY(), GetPositionZ(), shutX, shutY, turnZ, 1.0, turnSpeed)
  Else
    SetAngle(shutX, shutY, turnZ)
    busy = False
  EndIf
EndFunction

Event OnTranslationComplete()
  busy = False
EndEvent

Event OnTranslationFailed()
  ; Cut short (its cell unloaded): where it was going.
  If known
    Float turnZ = shutZ
    If isOpen
      turnZ += OpenTurn
    EndIf
    SetAngle(shutX, shutY, turnZ)
  EndIf
  busy = False
EndEvent

Event OnLoad()
  ; As it was left: a door swung open stays open.
  busy = False
  If known
    Float turnZ = shutZ
    If isOpen
      turnZ += OpenTurn
    EndIf
    SetAngle(shutX, shutY, turnZ)
  EndIf
EndEvent

; ---- for the scripts that open and shut doors ----

; A use that goes through (its lock picked or opened by its key, or never locked): what Skyrim's own door does then.
Function Use(ObjectReference akDoor, ObjectReference akUser) Global
  TMDfDoor d = akDoor as TMDfDoor
  If d
    d.Swing(!d.IsOpenNow())
  Else
    akDoor.Activate(akUser, True)
  EndIf
EndFunction

Function SetDoor(ObjectReference akDoor, Bool abOpen) Global
  If !akDoor
    Return
  EndIf
  TMDfDoor d = akDoor as TMDfDoor
  If d
    d.Swing(abOpen)
  Else
    akDoor.SetOpen(abOpen)
  EndIf
EndFunction

; Skyrim's open states: 1 open, 3 shut (a Daggerfall door is never between the two for long).
Int Function DoorState(ObjectReference akDoor) Global
  TMDfDoor d = akDoor as TMDfDoor
  If d
    If d.IsOpenNow()
      Return 1
    EndIf
    Return 3
  EndIf
  Return akDoor.GetOpenState()
EndFunction

; The nearest Daggerfall door to a reference, within reach, in its cell (SKSE's cell references: the doors are records of
; whichever plugin holds their dungeon, so no list names them all).
ObjectReference Function Nearest(ObjectReference akFrom, Float afReach) Global
  Cell c = akFrom.GetParentCell()
  If !c
    Return None
  EndIf
  ObjectReference best = None
  Float bestDistance = afReach
  Int n = c.GetNumRefs(29)
  Int i = 0
  While i < n
    ObjectReference r = c.GetNthRef(i, 29)
    If r && r as TMDfDoor
      Float d = r.GetDistance(akFrom)
      If d <= bestDistance
        best = r
        bestDistance = d
      EndIf
    EndIf
    i += 1
  EndWhile
  Return best
EndFunction
