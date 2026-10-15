ScriptName TMRgUseItem extends ActiveMagicEffect
{Using one of Redguard's usable items (a potion's single effect): Redguard ran the item's use script (ITEM.INI
use_script) when the player used it, and that script decides whether the item is used up (DropItem). So the item
Skyrim just took is given back and the converted use script starts beside the player (Shell: an activator carrying
it, which deletes itself when done).}

Activator Property Shell Auto
Form Property Item Auto

Event OnEffectStart(Actor akTarget, Actor akCaster)
  If Item
    akTarget.AddItem(Item, 1, True)
  EndIf
  If Shell
    akTarget.PlaceAtMe(Shell)
  EndIf
EndEvent
