ScriptName TMDfExitDoor extends ObjectReference
{The way out of a shared Daggerfall building interior: back to the street door the player came in through
(TMDfStreetDoor), standing in front of it and facing away from it. When the player didn't come in through a street door
of this building (no remembered door after coc or in an older save, or one whose way in is another interior), it
falls back to Fallback: the street door of the first town with this building that leads to this exit.}

TMDfTalk Property Talk Auto
ObjectReference Property Fallback Auto
{A street door of this building (the converter's choice: the first town's, nearest this exit).}

Event OnActivate(ObjectReference akActionRef)
  Actor player = Game.GetPlayer()
  If akActionRef != player
    Return
  EndIf
  ObjectReference streetDoor = None
  If Talk
    streetDoor = Talk.LastDoor
  EndIf
  ; The remembered door must lead into this interior.
  TMDfStreetDoor came = streetDoor as TMDfStreetDoor
  If !came || !came.Arrival || came.Arrival.GetParentCell() != GetParentCell()
    streetDoor = Fallback
  EndIf
  If !streetDoor
    Debug.Notification("This door leads nowhere you know.")
    Return
  EndIf
  ; The door's front faces its local -Y: in the world that is (-sin a, -cos a) for its Z angle a.
  Float a = streetDoor.GetAngleZ()
  player.MoveTo(streetDoor, -Math.sin(a) * 160.0, -Math.cos(a) * 160.0, 8.0)
  player.SetAngle(0.0, 0.0, a + 180.0)
EndEvent
