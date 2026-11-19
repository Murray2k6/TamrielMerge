ScriptName TMRgRope extends ObjectReference
{One of Redguard's ropes (MPRP): activating it swings the player across (TMRgMove.Swing) to the landing on the side
they aren't on. The build finds the landings: the walkable places within the rope's reach on either side of the gap
it hangs over (or the floor it hangs from and the ground under it, for a rope to climb).}

TMRgMove Property Mover Auto
Float[] Property Landings Auto
{Two landings, x y z each.}
Float Property RopeLength = 300.0 Auto
{How far the rope hangs below its top (its origin).}

Event OnActivate(ObjectReference akActionRef)
  If akActionRef != Game.GetPlayer() || !Mover
    Return
  EndIf
  If !Landings || Landings.Length < 6
    ; The build found no two landings for this rope (2026-10-29: 11 of 29): said, rather than nothing.
    Debug.Notification("The rope leads nowhere you can reach from here.")
    Return
  EndIf
  Mover.Swing(Self, Landings, RopeLength)
EndEvent
