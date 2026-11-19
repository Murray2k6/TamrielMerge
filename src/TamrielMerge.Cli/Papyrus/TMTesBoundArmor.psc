ScriptName TMTesBoundArmor extends ActiveMagicEffect
{Bound armour (TesMagic): Skyrim has none, so the effect puts the piece on the target for as long as it lasts (the
game's own piece where it names one, as Oblivion's Mythic Dawn robe and hood; else a Daedric one), where it can't be
taken off meanwhile, and takes it away at the end; also when the wearer dies, as bound armour leaves a corpse in
Oblivion (an ability is not dispelled by death in Skyrim, so its end alone left the piece on the body to be looted).}

Armor Property Piece Auto

Actor wearer

Event OnEffectStart(Actor akTarget, Actor akCaster)
  wearer = akTarget
  If akTarget.IsDead()
    Return
  EndIf
  akTarget.AddItem(Piece, 1, True)
  akTarget.EquipItem(Piece, True, True)
EndEvent

Event OnDying(Actor akKiller)
  TakeAway()
EndEvent

Event OnEffectFinish(Actor akTarget, Actor akCaster)
  wearer = akTarget
  TakeAway()
EndEvent

Function TakeAway()
  If !wearer || !Piece
    Return
  EndIf
  Int count = wearer.GetItemCount(Piece)
  If count > 0
    wearer.UnequipItem(Piece, False, True)
    wearer.RemoveItem(Piece, count, True)
  EndIf
EndFunction
