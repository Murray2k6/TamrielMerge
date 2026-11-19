ScriptName TMObMasteryPlayer extends ReferenceAlias
{The player, for TMObMastery: a loaded game has the mastery ranks checked again (the quest's update registration
survives a save; this makes sure of it).}

Event OnInit()
  TMObMastery mastery = GetOwningQuest() as TMObMastery
  If mastery
    mastery.Loaded()
  EndIf
EndEvent

Event OnPlayerLoadGame()
  TMObMastery mastery = GetOwningQuest() as TMObMastery
  If mastery
    mastery.Loaded()
  EndIf
EndEvent
