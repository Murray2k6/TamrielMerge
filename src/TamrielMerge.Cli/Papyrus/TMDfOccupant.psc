ScriptName TMDfOccupant extends Actor
{One of the people of a shared Daggerfall building interior (buildings with the same layout share one interior between
towns). They are placed temporary, not persistent: Skyrim keeps every persistent reference in memory from the start,
and with a persistent person in every interior of every town the game ran out of room while its data loaded
(2026-10-23). When they load they take the name the town the player came in from gives them: the street door left
its names on the way quest (TMDfStreetDoor: CurrentNames, for CurrentCell), and this is name Index of them (SKSE
SetDisplayName; without SKSE they keep their own).}

TMDfTalk Property Way Auto
Int Property Index = -1 Auto

Event OnLoad()
  If !Way || Index < 0 || SKSE.GetVersion() <= 0
    Return
  EndIf
  String[] names = Way.CurrentNames
  If names && Index < names.Length && Way.CurrentCell == GetParentCell() && names[Index] != ""
    SetDisplayName(names[Index], True)
  EndIf
EndEvent
