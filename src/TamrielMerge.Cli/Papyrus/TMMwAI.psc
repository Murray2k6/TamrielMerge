ScriptName TMMwAI extends Quest
{Morrowind's AI packages for the scripts TamrielMerge translates from Morrowind, on the quest MWScriptAI:
AITravel and AIEscort walk the actor to the place (PathToReference, on a thread of its own so the calling script goes
on, as Morrowind's does) and hold it there; AIFollow follows the player (Skyrim's follow package, through an alias) or
keeps close to another actor; AIWander lets it wander about where it stands (sandbox aliases); AIActivate walks to a
thing and uses it. GetAIPackageDone is true once a travel, escort or activation arrives; GetCurrentAIPackage gives
Morrowind's numbers (0 wander, 1 travel, 2 escort, 3 follow, 4 activate, -1 none).}

ReferenceAlias[] Property FollowSlots Auto
{Aliases with Skyrim's DefaultFollowPlayer package.}
ReferenceAlias[] Property HoldSlots Auto
{Aliases holding position where the actor is (DefaultHoldPositionCurrentLoc128).}
ReferenceAlias[] Property SandboxSlots Auto
{Aliases wandering about where the actor is (DefaultSandboxCurrentLocation256).}
ReferenceAlias[] Property WideSandboxSlots Auto
{The same, wider (DefaultSandboxCurrentLocation1024).}
Static Property XMarker Auto
{Placed as travel destinations.}

Int Property PackageWander = 0 AutoReadOnly
Int Property PackageTravel = 1 AutoReadOnly
Int Property PackageEscort = 2 AutoReadOnly
Int Property PackageFollow = 3 AutoReadOnly
Int Property PackageActivate = 4 AutoReadOnly

Actor[] actors
Int[] packages
Bool[] arrived
ObjectReference[] destinations
Int[] tokens
Int[] pending
Int pendingCount

Function Setup()
  If !actors
    actors = new Actor[64]
    packages = new Int[64]
    arrived = new Bool[64]
    destinations = new ObjectReference[64]
    tokens = new Int[64]
    pending = new Int[64]
  EndIf
EndFunction

; The actor's slot (made when asked).
Int Function SlotOf(Actor a, Bool abMake)
  Setup()
  Int i = actors.Find(a)
  If i >= 0 || !abMake
    Return i
  EndIf
  i = actors.Find(None)
  If i < 0
    ; Full: the oldest dead or unloaded actor's slot is reused.
    i = 0
    While i < actors.Length && actors[i] && !actors[i].IsDead() && actors[i].Is3DLoaded()
      i += 1
    EndWhile
    If i >= actors.Length
      i = 0
    EndIf
    Release(actors[i])
  EndIf
  actors[i] = a
  packages[i] = -1
  arrived[i] = False
  Return i
EndFunction

; Out of every alias, and no longer keeping close to anyone.
Function Release(Actor a)
  If !a
    Return
  EndIf
  ClearFrom(FollowSlots, a)
  ClearFrom(HoldSlots, a)
  ClearFrom(SandboxSlots, a)
  ClearFrom(WideSandboxSlots, a)
  a.ClearKeepOffsetFromActor()
EndFunction

Function ClearFrom(ReferenceAlias[] slots, Actor a)
  Int i = 0
  While slots && i < slots.Length
    If slots[i].GetActorReference() == a
      slots[i].Clear()
    EndIf
    i += 1
  EndWhile
EndFunction

; Into a free alias of the group (or the first, taken from whoever had it).
Function Put(ReferenceAlias[] slots, Actor a)
  If !slots || slots.Length == 0
    Return
  EndIf
  Int i = 0
  While i < slots.Length && slots[i].GetReference()
    i += 1
  EndWhile
  If i >= slots.Length
    i = 0
  EndIf
  slots[i].ForceRefTo(a)
  a.EvaluatePackage()
EndFunction

; AIWander range: wander about where it stands (range 0: stand there).
Function Wander(Actor a, Float afRange)
  If !a
    Return
  EndIf
  Int i = SlotOf(a, True)
  tokens[i] = tokens[i] + 1
  Release(a)
  packages[i] = PackageWander
  arrived[i] = False
  If afRange <= 0.0
    Put(HoldSlots, a)
  ElseIf afRange <= 512.0
    Put(SandboxSlots, a)
  Else
    Put(WideSandboxSlots, a)
  EndIf
EndFunction

; AITravel x y z: walk there, then stand.
Function Travel(Actor a, Float x, Float y, Float z)
  Go(a, None, x, y, z, PackageTravel)
EndFunction

; AIEscort target duration x y z: lead the way there.
Function Escort(Actor a, Actor akTarget, Float afDuration, Float x, Float y, Float z)
  Go(a, akTarget, x, y, z, PackageEscort)
EndFunction

; AIFollow target duration x y z: follow the player (or keep close to another actor) until told otherwise.
Function Follow(Actor a, Actor akTarget, Float afDuration, Float x, Float y, Float z)
  If !a || !akTarget
    Return
  EndIf
  Int i = SlotOf(a, True)
  tokens[i] = tokens[i] + 1
  Release(a)
  packages[i] = PackageFollow
  arrived[i] = False
  If akTarget == Game.GetPlayer()
    Put(FollowSlots, a)
  Else
    a.KeepOffsetFromActor(akTarget, 0.0, -96.0, 0.0, afCatchUpRadius = 400.0, afFollowRadius = 160.0)
  EndIf
EndFunction

; AIActivate target: walk to it and use it.
Function ActivateRef(Actor a, ObjectReference akTarget)
  If !a || !akTarget
    Return
  EndIf
  Int i = SlotOf(a, True)
  tokens[i] = tokens[i] + 1
  Release(a)
  packages[i] = PackageActivate
  arrived[i] = False
  destinations[i] = akTarget
  Queue(i)
EndFunction

Function Go(Actor a, Actor akLeader, Float x, Float y, Float z, Int aiPackage)
  If !a
    Return
  EndIf
  Int i = SlotOf(a, True)
  tokens[i] = tokens[i] + 1
  Release(a)
  packages[i] = aiPackage
  arrived[i] = False
  ; The destination: a marker of this slot's, moved there (placed beside the actor, so in its world).
  ObjectReference marker = destinations[i]
  If !marker || marker as Actor || !(marker.GetBaseObject() == XMarker)
    marker = a.PlaceAtMe(XMarker, 1, True)
    destinations[i] = marker
  EndIf
  marker.MoveTo(a)
  marker.SetPosition(x, y, z)
  Queue(i)
EndFunction

Function Queue(Int aiSlot)
  If pendingCount < pending.Length
    pending[pendingCount] = aiSlot
    pendingCount += 1
  EndIf
  RegisterForSingleUpdate(0.01)
EndFunction

; Each queued walk on a thread of its own: the next update takes the next one while this one walks.
Event OnUpdate()
  If pendingCount <= 0
    Return
  EndIf
  Int i = pending[0]
  Int k = 1
  While k < pendingCount
    pending[k - 1] = pending[k]
    k += 1
  EndWhile
  pendingCount -= 1
  If pendingCount > 0
    RegisterForSingleUpdate(0.01)
  EndIf
  Actor a = actors[i]
  ObjectReference target = destinations[i]
  Int token = tokens[i]
  If !a || !target
    Return
  EndIf
  Float speed = 0.5
  If packages[i] == PackageEscort
    speed = 0.3
  EndIf
  Bool reached = a.PathToReference(target, speed)
  If tokens[i] != token
    Return   ; told to do something else meanwhile
  EndIf
  ; A path the navmesh can't give: Morrowind's actors get there regardless.
  If !reached && a.GetDistance(target) > 256.0 && !a.IsDead()
    a.MoveTo(target)
  EndIf
  arrived[i] = True
  If packages[i] == PackageActivate
    target.Activate(a)
  EndIf
  Put(HoldSlots, a)
EndEvent

Bool Function Done(Actor a)
  Int i = SlotOf(a, False)
  Return i >= 0 && arrived[i]
EndFunction

Int Function Current(Actor a)
  Int i = SlotOf(a, False)
  If i < 0
    Return -1
  EndIf
  Return packages[i]
EndFunction
