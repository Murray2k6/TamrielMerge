ScriptName TMDfQuestPlayer extends ReferenceAlias
{The player, for the quest machine: entering a town (where "local" quest places are) and casting spells ("cast spell
do").}

Event OnLocationChange(Location akOldLoc, Location akNewLoc)
  (GetOwningQuest() as TMDfQuestMachine).LocationChanged(akNewLoc)
EndEvent

Event OnSpellCast(Form akSpell)
  Spell s = akSpell as Spell
  If s
    (GetOwningQuest() as TMDfQuestMachine).SpellCast(s)
  EndIf
EndEvent
