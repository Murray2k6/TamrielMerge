ScriptName TMTesGrantPower extends ActiveMagicEffect
{A Morrowind or Oblivion "cast when used" enchantment (TesMagic): while the item is worn its wearer has a lesser
power that casts the enchantment's effects.}

Spell Property Power Auto

Event OnEffectStart(Actor akTarget, Actor akCaster)
  akTarget.AddSpell(Power, False)
EndEvent

Event OnEffectFinish(Actor akTarget, Actor akCaster)
  akTarget.RemoveSpell(Power)
EndEvent
