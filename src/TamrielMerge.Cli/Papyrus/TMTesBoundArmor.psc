ScriptName TMTesBoundArmor extends ActiveMagicEffect
{Bound armour (TesMagic): Skyrim has none, so the effect puts a Daedric piece on the target for as long as it lasts
(it can't be taken off meanwhile) and takes it away at the end.}

Armor Property Piece Auto

Event OnEffectStart(Actor akTarget, Actor akCaster)
  akTarget.AddItem(Piece, 1, True)
  akTarget.EquipItem(Piece, True, True)
EndEvent

Event OnEffectFinish(Actor akTarget, Actor akCaster)
  akTarget.UnequipItem(Piece, False, True)
  akTarget.RemoveItem(Piece, akTarget.GetItemCount(Piece), True)
EndEvent
