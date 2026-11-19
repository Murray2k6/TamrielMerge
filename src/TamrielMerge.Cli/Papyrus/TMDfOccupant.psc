ScriptName TMDfOccupant extends Actor
{One of the people of a shared Daggerfall building interior (buildings with the same layout share one interior between
towns). They are placed temporary, not persistent: Skyrim keeps every persistent reference in memory from the start,
and with a persistent person in every interior of every town the game ran out of room while its data loaded
(2026-10-23). Each building's people are its own, as in Daggerfall: the street door the player came in by left its
names on the way quest (TMDfStreetDoor: CurrentNames, for CurrentCell, LastDoor), and this one takes name Index of them
(SKSE SetDisplayName; without SKSE they keep their own) when it loads and whenever a street door lets the player in
(the TMDfEntered event: Skyrim keeps the last interiors visited loaded, so going into the same layout in the next town
loads no one again, and until 2026-11-03 they kept the last town's names; user: "npcs are being duplicated when they
suppose to be new"). Someone killed is dead in that building only: the doors they died behind are remembered here, and
behind any other they are its own living person.}

TMDfTalk Property Way Auto
Int Property Index = -1 Auto

; The form ID of the street door whose person this one is now (0: none yet).
Int doorHere = 0
; The street doors (form IDs, "|" between) behind which this person was killed.
String diedBehind = "|"

Event OnLoad()
  RegisterForModEvent("TMDfEntered", "OnEntered")
  TakeBuilding()
EndEvent

Event OnEntered(String eventName, String strArg, Float numArg, Form sender)
  TakeBuilding()
EndEvent

Event OnDeath(Actor akKiller)
  If doorHere != 0
    diedBehind += doorHere + "|"
  EndIf
EndEvent

; Becomes the person of the building the player last came into, when that building is this interior.
Function TakeBuilding()
  If !Way || Index < 0 || SKSE.GetVersion() <= 0 || !Way.LastDoor || Way.CurrentCell != GetParentCell()
    Return
  EndIf
  Int entered = Way.LastDoor.GetFormID()
  If entered == doorHere
    Return
  EndIf
  doorHere = entered
  String[] names = Way.CurrentNames
  If names && Index < names.Length && names[Index] != ""
    SetDisplayName(names[Index], True)
  EndIf
  If StringUtil.Find(diedBehind, "|" + entered + "|") >= 0
    ; Killed here: the body where it fell, or no one when another building's person has been here since.
    If !IsDead()
      Disable()
    EndIf
  Else
    If IsDisabled()
      Enable()
    EndIf
    If IsDead()
      Resurrect()
    EndIf
  EndIf
EndFunction
