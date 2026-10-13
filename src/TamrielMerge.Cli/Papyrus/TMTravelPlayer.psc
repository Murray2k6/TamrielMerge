ScriptName TMTravelPlayer extends ReferenceAlias
{The player, for TMTravel: mod events and keys don't survive loading a save, so they are registered again; Skyrim's map
travel priced as Daggerfall's; the journey's walk (its own thread: the game's pathing waits until the player arrives);
movement keys handing the journey back to the player.}

ObjectReference walkTarget
Bool keysOn = False

TMTravel Function Travel()
  Return GetOwningQuest() as TMTravel
EndFunction

Event OnPlayerLoadGame()
  Travel().Setup()
  If keysOn
    WatchKeys(True)
  EndIf
EndEvent

Event OnPlayerFastTravelEnd(Float afTravelGameTimeHours)
  Travel().FastTravelled(afTravelGameTimeHours)
EndEvent

; Walks the player (driven by the game's AI) to a reference; TMTravel hears how it ended.
Function WalkTo(ObjectReference target)
  walkTarget = target
  RegisterForSingleUpdate(0.1)
EndFunction

Event OnUpdate()
  ObjectReference target = walkTarget
  walkTarget = None
  If !target
    Return
  EndIf
  Bool reached = GetActorRef().PathToReference(target, 1.0)
  Travel().WalkEnded(reached)
EndEvent

Function WatchKeys(Bool on)
  UnregisterForAllKeys()
  keysOn = on
  If !on
    Return
  EndIf
  String[] moves = new String[5]
  moves[0] = "Forward"
  moves[1] = "Back"
  moves[2] = "Strafe Left"
  moves[3] = "Strafe Right"
  moves[4] = "Jump"
  Int i = 0
  While i < moves.Length
    Int code = Input.GetMappedKey(moves[i])
    If code > 0
      RegisterForKey(code)
    EndIf
    i += 1
  EndWhile
EndFunction

Event OnKeyDown(Int keyCode)
  If !Utility.IsInMenuMode()
    Travel().KeyPressed()
  EndIf
EndEvent
