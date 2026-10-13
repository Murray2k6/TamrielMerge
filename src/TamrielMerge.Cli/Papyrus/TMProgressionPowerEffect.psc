ScriptName TMProgressionPowerEffect extends ActiveMagicEffect
{The "Tamriel" power: opens the Tamriel menu (TMProgression).}

TMProgression Property Progression Auto

Event OnEffectStart(Actor akTarget, Actor akCaster)
  Progression.OpenMenu()
EndEvent
