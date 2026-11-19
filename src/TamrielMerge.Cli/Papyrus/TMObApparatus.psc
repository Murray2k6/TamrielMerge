ScriptName TMObApparatus extends ActiveMagicEffect
{One of Oblivion's alchemy apparatus used (TamrielMerge, ObMasteryPerks.ApparatusItem): an ingestible whose single
effect runs this, as Redguard's usable items do (TMRgUseItem). The item Skyrim just consumed is given back; a mortar
and pestle asks TMObMastery to open an alchemy workbench with the apparatus boost: (mortar quality - 10) / 2 % plus a
quarter of the quality of the best calcinator and the best retort carried (Oblivion.esm APPA qualities: Novice 10,
Apprentice 25, Journeyman 50, Expert 75, Master 100). Oblivion brews with a mortar only; an alembic, calcinator or
retort used on its own says so.}

Form Property Item Auto
TMObMastery Property Mastery Auto
Int Property Kind Auto
{0 mortar and pestle, 1 alembic, 2 calcinator, 3 retort (Oblivion's APPA type).}
Float Property Quality Auto
FormList Property Calcinators Auto
{Every calcinator, best first.}
Float[] Property CalcinatorQualities Auto
FormList Property Retorts Auto
{Every retort, best first.}
Float[] Property RetortQualities Auto

Event OnEffectStart(Actor akTarget, Actor akCaster)
  If Item
    akTarget.AddItem(Item, 1, True)
  EndIf
  If Kind != 0
    Debug.Notification("You need a mortar and pestle to make potions.")
    Return
  EndIf
  Float boost = (Quality - 10.0) / 2.0 + Best(akTarget, Calcinators, CalcinatorQualities) / 4.0 + Best(akTarget, Retorts, RetortQualities) / 4.0
  If boost < 0.0
    boost = 0.0
  EndIf
  If Mastery
    Mastery.RequestBrew(boost)
  EndIf
EndEvent

; The quality of the best of a kind the actor carries (the lists are best first), 0 with none.
Float Function Best(Actor akWho, FormList akList, Float[] akQualities)
  If !akList || !akQualities
    Return 0.0
  EndIf
  Int i = 0
  Int n = akList.GetSize()
  While i < n && i < akQualities.Length
    If akWho.GetItemCount(akList.GetAt(i)) > 0
      Return akQualities[i]
    EndIf
    i += 1
  EndWhile
  Return 0.0
EndFunction
