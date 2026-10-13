ScriptName TMProgressionPlayer extends ReferenceAlias
{The player, for TMProgression: key and menu events don't survive loading a save, so they are registered again.}

Event OnPlayerLoadGame()
  (GetOwningQuest() as TMProgression).Setup()
EndEvent
