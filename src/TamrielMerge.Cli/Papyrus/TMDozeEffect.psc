ScriptName TMDozeEffect extends ActiveMagicEffect
{A guard asleep on night watch (TMNightDanger): unconscious, so they see and hear nothing, until dawn takes the ability
away, or until they are struck or spoken to.}

Spell Property Doze Auto

Event OnEffectStart(Actor akTarget, Actor akCaster)
  akTarget.SetUnconscious(True)
EndEvent

Event OnHit(ObjectReference akAggressor, Form akSource, Projectile akProjectile, Bool abPowerAttack, Bool abSneakAttack, Bool abBashAttack, Bool abHitBlocked)
  Wake()
EndEvent

; Spoken to wakes them; a sneaking hand (a pickpocket) does not.
Event OnActivate(ObjectReference akActionRef)
  Actor who = akActionRef as Actor
  If who && !who.IsSneaking()
    Wake()
  EndIf
EndEvent

Function Wake()
  Actor guard = GetTargetActor()
  If guard
    guard.SetUnconscious(False)
    If Doze
      guard.RemoveSpell(Doze)
    EndIf
  EndIf
EndFunction

Event OnEffectFinish(Actor akTarget, Actor akCaster)
  akTarget.SetUnconscious(False)
EndEvent
