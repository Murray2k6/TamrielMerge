ScriptName TMDfExitDoor extends ObjectReference
{The way out of a shared Daggerfall building interior: back to the street door the player came in through
(TMDfStreetDoor), standing in front of it and facing away from it.}

TMDfTalk Property Talk Auto

Event OnActivate(ObjectReference akActionRef)
  Actor player = Game.GetPlayer()
  If akActionRef != player || !Talk.LastDoor
    Return
  EndIf
  ObjectReference streetDoor = Talk.LastDoor
  ; The door's front faces its local -Y: in the world that is (-sin a, -cos a) for its Z angle a.
  Float a = streetDoor.GetAngleZ()
  player.MoveTo(streetDoor, -Math.sin(a) * 160.0, -Math.cos(a) * 160.0, 8.0)
  player.SetAngle(0.0, 0.0, a + 180.0)
EndEvent
