ScriptName TMDfDoorSpellEffect extends ActiveMagicEffect
{Daggerfall's Open and Wizard Lock. Acts on the nearest Daggerfall door: Open succeeds with Daggerfall's chance and
cannot open doors that need a key; Wizard Lock shuts the door and locks it at master level.}

FormList Property DoorBases Auto
Bool Property LockDoor Auto
Float Property Reach = 400.0 Auto
Float Property Chance = 100.0 Auto
{Daggerfall's chance for Open at the reference caster level.}

Event OnEffectStart(Actor akTarget, Actor akCaster)
  ObjectReference target = Game.FindClosestReferenceOfAnyTypeInListFromRef(DoorBases, akCaster, Reach)
  If !target
    Debug.Notification("There is no target near enough.")
  ElseIf LockDoor
    target.SetOpen(False)
    target.Lock(True)
    target.SetLockLevel(100)
  ElseIf !target.IsLocked()
    Debug.Notification("That target is not locked.")
  ElseIf target.GetLockLevel() >= 255
    Debug.Notification("This lock cannot be opened by magic.")
  ElseIf Utility.RandomFloat(0.0, 100.0) < Chance
    target.Lock(False)
    target.SetOpen(True)
  Else
    Debug.Notification("The spell failed to open the lock.")
  EndIf
EndEvent
