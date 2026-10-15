ScriptName TMDfSensesPlayer extends ReferenceAlias
{The player, for TMDfSensesControl: menu registrations don't survive loading a save, so they are made again.}

Event OnPlayerLoadGame()
  (GetOwningQuest() as TMDfSensesControl).Setup()
EndEvent
