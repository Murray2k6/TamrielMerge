ScriptName TMCreatureSpawner extends ObjectReference
{Procedural creatures (CreatureSpawns): a hidden marker that places a creature from its habitat's leveled list (mixed
from every converted game) the first time its cell loads, and puts a new one there once that one is dead and
RespawnHours have passed: 0 means whenever the player comes back (Daggerfall resets a dungeon when you leave it),
below 0 never (the game doesn't bring creatures back). Death is noticed when the player leaves the cell.}

ActorBase Property Creature Auto
Float Property RespawnHours = -1.0 Auto

Actor spawned
Bool everSpawned = False
Float deadSince = -1.0

Float Function Now()
  Return Utility.GetCurrentGameTime() * 24.0
EndFunction

Event OnCellAttach()
  If !everSpawned
    Spawn()
    Return
  EndIf
  If spawned && !spawned.IsDead()
    Return
  EndIf
  If RespawnHours < 0.0
    Return
  EndIf
  If deadSince < 0.0
    deadSince = Now()
  EndIf
  If Now() - deadSince >= RespawnHours
    If spawned
      spawned.Delete()
    EndIf
    Spawn()
  EndIf
EndEvent

Event OnCellDetach()
  If spawned && spawned.IsDead() && deadSince < 0.0
    deadSince = Now()
  EndIf
EndEvent

Function Spawn()
  If !Creature
    Return
  EndIf
  spawned = PlaceActorAtMe(Creature)
  everSpawned = True
  deadSince = -1.0
EndFunction
