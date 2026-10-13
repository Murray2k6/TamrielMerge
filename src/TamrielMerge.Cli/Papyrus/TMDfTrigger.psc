ScriptName TMDfTrigger extends ObjectReference
{Invisible box around a Daggerfall action object whose trigger is walking into it or onto it. Tells the object
when the player enters; the object decides whether that counts for its trigger type.}

TMDfAction Property Target Auto
Bool Property WalkOn Auto
{True for boxes over the top of the object (walk-on triggers), false for boxes around it (walk-into).}

Event OnTriggerEnter(ObjectReference akActionRef)
  If akActionRef == Game.GetPlayer() && Target
    Target.Collided(WalkOn)
  EndIf
EndEvent
