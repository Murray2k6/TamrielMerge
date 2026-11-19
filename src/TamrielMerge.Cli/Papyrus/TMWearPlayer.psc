ScriptName TMWearPlayer extends ReferenceAlias
{The player, for TMWear: being struck, what is equipped, and loads (animation and mod events don't survive one).}

Event OnPlayerLoadGame()
  (GetOwningQuest() as TMWear).Setup()
EndEvent

Event OnHit(ObjectReference akAggressor, Form akSource, Projectile akProjectile, Bool abPowerAttack, Bool abSneakAttack, Bool abBashAttack, Bool abHitBlocked)
  (GetOwningQuest() as TMWear).Struck(akAggressor, akSource, abHitBlocked)
EndEvent

Event OnObjectEquipped(Form akBaseObject, ObjectReference akReference)
  (GetOwningQuest() as TMWear).Equipped(akBaseObject)
EndEvent

Event OnObjectUnequipped(Form akBaseObject, ObjectReference akReference)
  (GetOwningQuest() as TMWear).ConditionChanged()
EndEvent
