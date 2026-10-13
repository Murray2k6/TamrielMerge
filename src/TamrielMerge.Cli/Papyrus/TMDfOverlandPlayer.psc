ScriptName TMDfOverlandPlayer extends ReferenceAlias
{The player, for TMDfOverland: mod events don't survive loading a save, so they are registered again.}

Event OnPlayerLoadGame()
  (GetOwningQuest() as TMDfOverland).Setup()
EndEvent
