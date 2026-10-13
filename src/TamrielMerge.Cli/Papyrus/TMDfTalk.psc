ScriptName TMDfTalk extends Quest
{Daggerfall's conversations (DFTalk quest). Where is...: for each kind of building, whether this town has one and the
compass direction to the nearest (0 north, 1 north-east ... 7 north-west), measured from where the player stands, or
from the street door of the building they are in. Dialogue conditions read Present[i] and Direction[i].
Towns: TownLocations lists each town's location; for town t and building kind k, DoorsByKind.GetAt(t * KindCount + k)
is a FormList of that kind's street doors. Indoors (buildings share interiors between towns) the town and the
starting point are the street door the player came in by (LastDoor, LastTown, set by TMDfStreetDoor).}

FormList Property TownLocations Auto
FormList Property DoorsByKind Auto
GlobalVariable[] Property Present Auto
GlobalVariable[] Property Direction Auto
Int Property KindCount Auto
Actor Property PlayerRef Auto
ObjectReference Property LastDoor Auto Hidden
Int Property LastTown = -1 Auto Hidden

Int Function TownIndex()
  If PlayerRef.IsInInterior()
    Return LastTown
  EndIf
  ; A town's streets carry its location (FormList.Find is native, so this stays quick with thousands of towns).
  Location here = PlayerRef.GetCurrentLocation()
  If !here
    Return -1
  EndIf
  Return TownLocations.Find(here)
EndFunction

; Where the player is, as a point on the town's streets.
ObjectReference Function Origin(Int town)
  If PlayerRef.IsInInterior()
    Return LastDoor
  EndIf
  Return PlayerRef
EndFunction

; Compass sector of (dx, dy), Skyrim's +Y being north: 0 N, 1 NE, 2 E, 3 SE, 4 S, 5 SW, 6 W, 7 NW.
Int Function Sector(Float dx, Float dy)
  Float angle
  If dy == 0.0
    If dx >= 0.0
      angle = 90.0
    Else
      angle = 270.0
    EndIf
  Else
    angle = Math.atan(dx / dy)
    If dy < 0.0
      angle += 180.0
    ElseIf dx < 0.0
      angle += 360.0
    EndIf
  EndIf
  Int s = ((angle + 22.5) / 45.0) as Int
  Return s % 8
EndFunction

Function UpdateDirections()
  Int town = TownIndex()
  ObjectReference origin = None
  If town >= 0
    origin = Origin(town)
  EndIf
  Int k = 0
  While k < KindCount
    Present[k].SetValue(0)
    If origin
      FormList doors = DoorsByKind.GetAt(town * KindCount + k) as FormList
      Float best = -1.0
      ObjectReference nearest = None
      Int i = 0
      While doors && i < doors.GetSize()
        ObjectReference d = doors.GetAt(i) as ObjectReference
        Float dx = d.GetPositionX() - origin.GetPositionX()
        Float dy = d.GetPositionY() - origin.GetPositionY()
        Float dist = dx * dx + dy * dy
        If best < 0.0 || dist < best
          best = dist
          nearest = d
        EndIf
        i += 1
      EndWhile
      If nearest
        Present[k].SetValue(1)
        Direction[k].SetValue(Sector(nearest.GetPositionX() - origin.GetPositionX(), nearest.GetPositionY() - origin.GetPositionY()))
      EndIf
    EndIf
    k += 1
  EndWhile
EndFunction
