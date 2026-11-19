ScriptName TMWearTool extends ActiveMagicEffect
{A repair tool used (Morrowind's repair hammers and prongs, Oblivion's repair hammer; ItemWear.Tool): an ingestible whose
single effect runs this, as the alchemy apparatus do (TMObApparatus). The item Skyrim just consumed is given back, and
TMWear offers what can be mended; each game's rules decide what the attempt costs.}

Form Property Item Auto
TMWear Property Wear Auto
Float Property Quality Auto
{Morrowind's RIDT quality; 0 in Oblivion.}
Int Property Uses Auto
{Morrowind's RIDT uses; 0 in Oblivion, whose hammers break by chance.}

Event OnEffectStart(Actor akTarget, Actor akCaster)
  If Item
    akTarget.AddItem(Item, 1, True)
  EndIf
  If Wear && akTarget == Game.GetPlayer()
    Wear.UseTool(Item, Quality, Uses)
  EndIf
EndEvent
