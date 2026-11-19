Scriptname TMObDoor extends ObjectReference
{Oblivion's rule for people and doors, on a converted door that leads nowhere (a gate, a room's door): someone
coming at a shut door opens it if it isn't locked, or if it is theirs to open (the lock's key in hand, or the door
their own or their faction's: Oblivion's people need no key for what they own), and a lock opened that way stays
open, as in Oblivion (what locks it again is the game's own scripting: the prisoner's cell gate is shut and locked by
BaurusScript once the party is in).

Skyrim's own people don't do this with these doors: measured in game on the prisoner's cell gate, 2026-11-14, in ten
runs with the party's places taken every two seconds, they stood before it some seconds and were then put on its
far side by the game with the gate still shut, whatever was changed about the gate (its height over the floor, its
model's flags, the way it faces, its bounds, its lock's script, Skyrim's own door model in its record). So the rule
is here, looked at twice a second while the door's cell is loaded.}

Float Property Reach = 100.0 Auto
{How near the door someone must have come, turned to it, for it to open.}

Bool watching
Int ticks
; Skyrim's lock and the lock script's say put on an owned door because the player is near it.
Bool guarded

Bool Property Verbose = False Auto
{For the log, each second: the door's state, the people of its cell and the nearest one's distance and turn.}

Function Report()
  Cell here = GetParentCell()
  Int n = 0
  If here
    n = here.GetNumRefs(43)
  EndIf
  Actor player = Game.GetPlayer()
  Actor nearest = None
  Float best = 100000.0
  Int i = n
  While i > 0
    i -= 1
    Actor a = here.GetNthRef(i, 43) as Actor
    If a && a != player
      Float d = a.GetDistance(Self)
      If d < best
        best = d
        nearest = a
      EndIf
    EndIf
  EndWhile
  If nearest
    Debug.Trace("TMOb door " + Self + ": state " + GetOpenState() + ", locked " + IsLocked() + ", " + n + " people; nearest " + nearest + " at " + best + ", turn " + nearest.GetHeadingAngle(Self) + ", speed " + nearest.GetAnimationVariableFloat("Speed") + ", dead " + nearest.IsDead() + ", 3D " + nearest.Is3DLoaded())
  Else
    Debug.Trace("TMOb door " + Self + ": state " + GetOpenState() + ", locked " + IsLocked() + ", " + n + " people, none but the player")
  EndIf
EndFunction

Event OnLoad()
  Watch()
EndEvent

Event OnCellAttach()
  Watch()
EndEvent

Function Watch()
  If !watching
    watching = True
    RegisterForSingleUpdate(0.5)
  EndIf
EndFunction

Event OnUnload()
  watching = False
  UnregisterForUpdate()
EndEvent

Event OnUpdate()
  If !Is3DLoaded()
    watching = False
    Return
  EndIf
  ; 3: shut (1 open, 2 opening, 4 closing).
  ticks += 1
  If Verbose && ticks % 2 == 0
    Report()
  EndIf
  Guard()
  If GetOpenState() == 3
    Actor who = Comer()
    If who && MayOpen(who)
      Debug.Trace("TMOb door " + Self + " opened by " + who)
      SetOpen(True)
    EndIf
  EndIf
  RegisterForSingleUpdate(0.5)
EndEvent

; An owned, locked door (TMObLock.Own): to Skyrim a plain door, which its people open on their way. While the player
; is near, its activation is the lock script's (Oblivion's choices: "you need a key", or the pick); that comes off
; again when the player has gone. Someone of the house who walks up to it meanwhile is let in by the rule below, and
; the lock is then off for good, as in Oblivion.
Function Guard()
  TMObLock oblivionLock = (Self as ObjectReference) as TMObLock
  If !oblivionLock || !oblivionLock.Own
    Return
  EndIf
  ; Held shut for the player by the script alone (TMObLock answers the try: "you need a key", or the pick), never by
  ; Skyrim's own lock. Until 2026-11-15 a "needs a key" lock was put on while the player stood near and taken off
  ; when they left: after that the game still found no way through for its people (run r71: with the lock off for
  ; an hour of game time, Glenroy stood 13 s at 123 units from the shut gate and was then set down 220 units on,
  ; inside the cell; Baurus later 443 units: user, "theres some teleport marker on the prison cell door"). With no
  ; lock of Skyrim's ever on it the party crossed at a walk (run r54).
  Bool near = oblivionLock.Locked && GetOpenState() == 3 && Game.GetPlayer().GetDistance(Self) < 320.0
  If near && !guarded
    guarded = True
    BlockActivation(True)
    Debug.Trace("TMOb door " + Self + " held shut for the player, who is near")
  ElseIf guarded && (!oblivionLock.Locked || Game.GetPlayer().GetDistance(Self) > 400.0)
    guarded = False
    BlockActivation(False)
    If IsLocked()
      ; A lock of Skyrim's put on for the player's own picking (TMObLock) and left unpicked.
      Lock(False)
    EndIf
    Debug.Trace("TMOb door " + Self + " a plain door again (the player gone, or its lock off): state " + GetOpenState())
  EndIf
EndFunction

; Someone come up to the door: within reach and turned towards it. Not the player, who opens doors by hand.
Actor Function Comer()
  Cell here = GetParentCell()
  If !here
    Return None
  EndIf
  Actor player = Game.GetPlayer()
  ; 43: references whose base is a person (SKSE filters a cell's references by their base's type: asked for 62, the
  ; reference type, it found none: measured in game 2026-11-14).
  Int n = here.GetNumRefs(43)
  While n > 0
    n -= 1
    Actor a = here.GetNthRef(n, 43) as Actor
    If a && a != player && !a.IsDead() && a.Is3DLoaded()
      Float d = a.GetDistance(Self)
      Float turn = a.GetHeadingAngle(Self)
      If d <= Reach
        ; Up to the door and turned to it. (Not "under way within reach": Glenroy coming to his marker 122 units
        ; from the cell gate, on his way nowhere near it, had it opened two stages early: seen in game 2026-11-14.)
        If turn > -60.0 && turn < 60.0
          Return a
        EndIf
      ElseIf d <= 200.0 && turn > -90.0 && turn < 90.0
        ; Or sent on by a new package and held where it took it up, near the shut door and facing its way: the door
        ; is what holds it. Skyrim's people stand at these doors rather than come up to them (Glenroy, sent through
        ; the cell gate at stage 14, stood 13 s at his marker 123 units off, turned to it, and was then set down on its
        ; far side by the game: runs r79 to r85, 2026-11-15, whatever was changed about the gate).
        TMObActor oblivionActor = a as TMObActor
        If oblivionActor && oblivionActor.HeldSinceNewPackage(1.5, 30.0, 24.0)
          Return a
        EndIf
      EndIf
    EndIf
  EndWhile
  Return None
EndFunction

; Oblivion's rule: not locked; or the lock theirs to open (TMObLock holds Oblivion's lock: its key, the door's
; owner), which then stays open.
Bool Function MayOpen(Actor who)
  TMObLock oblivionLock = (Self as ObjectReference) as TMObLock
  If oblivionLock
    Return oblivionLock.LetIn(who)
  EndIf
  If !IsLocked()
    Return True
  EndIf
  Key own = GetKey()
  Bool theirs = own && who.GetItemCount(own) > 0
  If !theirs
    ActorBase owner = GetActorOwner()
    Faction owners = GetFactionOwner()
    theirs = (owner && who.GetActorBase() == owner) || (owners && who.IsInFaction(owners))
  EndIf
  If theirs
    Lock(False)
  EndIf
  Return theirs
EndFunction
