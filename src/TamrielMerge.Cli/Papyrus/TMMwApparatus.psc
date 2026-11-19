ScriptName TMMwApparatus extends ActiveMagicEffect
{One of Morrowind's alchemy apparatus used (TamrielMerge, MwApparatus): an ingestible whose single effect runs this, as
Oblivion's do (TMObApparatus). The item Skyrim just consumed is given back, and TMMwAlchemy opens the alchemy bench as
Morrowind opens its alchemy window for any apparatus.}

Form Property Item Auto
TMMwAlchemy Property Alchemy Auto

Event OnEffectStart(Actor akTarget, Actor akCaster)
  If Item
    akTarget.AddItem(Item, 1, True)
  EndIf
  If Alchemy
    Alchemy.RequestBrew()
  EndIf
EndEvent
