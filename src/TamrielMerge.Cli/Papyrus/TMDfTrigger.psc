ScriptName TMDfTrigger extends ObjectReference
{Invisible box around and over a Daggerfall action object whose trigger is walking onto or into it (Daggerfall Unity
DaggerfallActionCollision, MIT): while the player touches it and moves (a movement key held, or the player moved), the
object is told every 0.12 seconds, as walking on it when the player's feet are on its top, else as walking into it;
the object decides whether that counts for its trigger type.}

TMDfAction Property Target Auto
Float Property TopZ Auto
{The top of the object (Skyrim Z): a contact under the player (feet at or over it) is walking on it.}

Float Property Timeout = 0.12 AutoReadOnly

Bool inside = False
Float lastX
Float lastY
Float lastZ

Event OnTriggerEnter(ObjectReference akActionRef)
  If akActionRef == Game.GetPlayer() && Target
    inside = True
    Touch(True)
  EndIf
EndEvent

Event OnTriggerLeave(ObjectReference akActionRef)
  If akActionRef == Game.GetPlayer()
    inside = False
    UnregisterForUpdate()
  EndIf
EndEvent

Event OnUnload()
  inside = False
  UnregisterForUpdate()
EndEvent

Event OnUpdate()
  If inside
    Touch(False)
  EndIf
EndEvent

Function Touch(Bool entered)
  Actor player = Game.GetPlayer()
  Float ox = player.GetPositionX()
  Float oy = player.GetPositionY()
  Float oz = player.GetPositionZ()
  Bool moved = entered || (ox - lastX) * (ox - lastX) + (oy - lastY) * (oy - lastY) + (oz - lastZ) * (oz - lastZ) > 1.0
  lastX = ox
  lastY = oy
  lastZ = oz
  If moved || MovementHeld()
    Target.Collided(oz >= TopZ - 8.0)
  EndIf
  If inside
    RegisterForSingleUpdate(Timeout)
  EndIf
EndFunction

; Daggerfall Unity: only an active move counts (forwards, backwards, left or right held; up, down and jumping don't).
Bool Function MovementHeld()
  Return Input.IsKeyPressed(Input.GetMappedKey("Forward")) || Input.IsKeyPressed(Input.GetMappedKey("Back")) || Input.IsKeyPressed(Input.GetMappedKey("Strafe Left")) || Input.IsKeyPressed(Input.GetMappedKey("Strafe Right"))
EndFunction
