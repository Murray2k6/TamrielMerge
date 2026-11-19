ScriptName TMObLock extends ObjectReference
{An Oblivion lock on a converted door or container. As in Oblivion, activating it without its key offers to pick
the lock by hand (Skyrim's lockpicking, the nearest thing to Oblivion's tumblers) or to Auto Attempt: every tumbler
not yet set falls to one roll each, at (min(Security, 100) x fLockPickAutoDifficulty + fLockPickAutoBase) %
(Oblivion.esm: 0.3 and 45; reverse-engineered on UESP's Oblivion talk:Security), and a failure breaks the pick.
A lock has one tumbler per difficulty tier (Very Easy 1 ... Very Hard 5); level 100 needs a key. The Security
mastery ranks (TMObMastery.TumblersKept; UESP Oblivion:Security) keep some set tumblers set when a pick breaks:
one at Journeyman, two at Expert, all at Master; the next attempt goes on from them. Skyrim's
Lockpicking stands for Security. Doors and containers keep their Skyrim lock (keys and AI see it); a swinging door
(TMSwingDoor, an activator) keeps it here and asks LetIn before it opens.}

Int Property LockLevel = 0 Auto
Bool Property Locked = False Auto
{The lock of a swinging door.}
Bool Property Swing = False Auto
Bool Property Own = False Auto
{An owned inner door (a person's, a faction's): its lock is kept here, in Locked. Skyrim's people open a door in
their way by themselves, as Oblivion's do, unless Skyrim has it locked or its activation is blocked: then they stand
before it and the game puts them on its far side (the prisoner's cell gate, seen in game 2026-11-14 in some twenty
runs; as a plain door, no lock and no script, the Emperor's party opened it and walked in). Oblivion's owners need
no key for their own doors, so such a door is a plain door to Skyrim while the player is away from it, and gets
Skyrim's lock and this script's say only while the player is near (TMObDoor.Guard).}
Bool Property ScriptActivates = False Auto
{Its own Oblivion script has an OnActivate block, which takes the place of the default activation: the lock holds the
level only.}
Key Property DoorKey Auto
Message Property Choice Auto
{"Pick the lock", "Auto Attempt", "Leave".}
MiscObject Property Lockpick Auto
TMObMastery Property Mastery Auto
{Oblivion's skill mastery (the Security ranks).}

; Tumblers set by a broken attempt that the Security rank kept set.
Int setTumblers = 0

Event OnInit()
  If !Swing && !ScriptActivates && !Own
    BlockActivation(True)
  EndIf
EndEvent

Event OnLoad()
  ; (An owned inner door's activation is left to the game while the player is away from it: TMObDoor.)
  If !Swing && !ScriptActivates && !Own
    BlockActivation(True)
  EndIf
  ; For the log (when Papyrus logging is on): the lock as the game has it.
  Debug.Trace("TMOb lock " + Self + " loaded: locked " + IsLocked() + ", level " + GetLockLevel() + ", key " + GetKey() + " (ours " + DoorKey + "), owner " + GetActorOwner() + " / " + GetFactionOwner() + ", open state " + GetOpenState())
EndEvent

Event OnActivate(ObjectReference akActionRef)
  If akActionRef != Game.GetPlayer()
    Debug.Trace("TMOb lock " + Self + " tried by " + akActionRef + ": locked " + IsLocked() + ", open state " + GetOpenState())
  EndIf
  If Own && !IsActivationBlocked()
    ; A plain door just now (the player away): the game has opened or shut it itself.
    Return
  EndIf
  If !Swing && !ScriptActivates && LetIn(akActionRef)
    Activate(akActionRef, True)
  EndIf
EndEvent

Bool Function IsShut()
  If Swing || Own
    Return Locked
  EndIf
  Return IsLocked()
EndFunction

Function Unlock()
  Locked = False
  If !Swing
    Lock(False)
  EndIf
EndFunction

Bool Function LetIn(ObjectReference who)
  Actor player = Game.GetPlayer()
  If !IsShut()
    Return True
  EndIf
  If who != player
    ; Someone else at a locked door: Oblivion's people go through the locks they hold the key to or own (the thing
    ; theirs, or their faction's), and the lock is then open. Refused whoever it was, Glenroy stood two minutes at
    ; the prisoner's cell gate, the Emperor's faction's and his to open (seen in game 2026-11-14).
    Actor a = who as Actor
    If !a
      Return False
    EndIf
    Bool theirs = DoorKey && a.GetItemCount(DoorKey) > 0
    If !theirs
      ActorBase owner = GetActorOwner()
      Faction owners = GetFactionOwner()
      theirs = (owner && a.GetActorBase() == owner) || (owners && a.IsInFaction(owners))
    EndIf
    If theirs
      ; It stays unlocked, as in Oblivion: what locks it again is the game's own scripting (the prisoner's gate:
      ; BaurusScript closes it and locks it at 100 once the party is in).
      Unlock()
    EndIf
    Return theirs
  EndIf
  If DoorKey && player.GetItemCount(DoorKey) > 0
    Unlock()
    Return True
  EndIf
  If LockLevel >= 100
    Debug.Notification("This lock cannot be picked. You need a key.")
    Return False
  EndIf
  If player.GetItemCount(Lockpick) <= 0
    Debug.Notification("You need a lockpick or the key.")
    Return False
  EndIf
  Int button = Choice.Show(Tumblers())
  If button == 0
    If Swing
      ; A swinging door has no Skyrim lock to pick by hand.
      Return AutoAttempt(player)
    EndIf
    If Own
      ; Skyrim's lock is on the door while the player is at it (TMObDoor): its lockpicking; picked, the lock is off.
      If !IsLocked()
        Lock(True)
        SetLockLevel(SkyrimLevel())
      EndIf
      Activate(player, True)
      Utility.Wait(0.3)
      If !IsLocked()
        Locked = False
      EndIf
      Return False
    EndIf
    ; Skyrim's own lockpicking on the door's Skyrim lock.
    Activate(player, True)
    Return False
  ElseIf button == 1
    Return AutoAttempt(player)
  EndIf
  Return False
EndFunction

; Skyrim's lock level for Oblivion's (the levels Skyrim's lockpicking knows: 1 novice, 25, 50, 75, 100 master).
Int Function SkyrimLevel()
  If LockLevel <= 7
    Return 1
  ElseIf LockLevel <= 20
    Return 25
  ElseIf LockLevel <= 40
    Return 50
  ElseIf LockLevel <= 80
    Return 75
  EndIf
  Return 100
EndFunction

; Oblivion's tiers (UESP Oblivion:Open): Very Easy 0-7, Easy 8-20, Average 21-40, Hard 41-80, Very Hard 81-99.
Int Function Tumblers()
  If LockLevel <= 7
    Return 1
  ElseIf LockLevel <= 20
    Return 2
  ElseIf LockLevel <= 40
    Return 3
  ElseIf LockLevel <= 80
    Return 4
  EndIf
  Return 5
EndFunction

Bool Function AutoAttempt(Actor player)
  Float skill = player.GetActorValue("Lockpicking")
  If skill > 100.0
    skill = 100.0
  EndIf
  Float chance = skill * 0.3 + 45.0
  Int total = Tumblers()
  If setTumblers > total
    setTumblers = total
  EndIf
  Int set = setTumblers
  While set < total
    If Utility.RandomFloat(0.0, 100.0) >= chance
      player.RemoveItem(Lockpick, 1, True)
      ; The tumblers the Security rank keeps set (-1: all of them).
      Int kept = 0
      If Mastery
        kept = Mastery.TumblersKept()
      EndIf
      If kept < 0 || kept > set
        kept = set
      EndIf
      setTumblers = kept
      If kept > 0
        Debug.Notification("Your lockpick broke. " + kept + " tumbler(s) stay set.")
      Else
        Debug.Notification("Your lockpick broke.")
      EndIf
      Return False
    EndIf
    set += 1
  EndWhile
  setTumblers = 0
  Unlock()
  Game.AdvanceSkill("Lockpicking", 2.0)
  Debug.Notification("You picked the lock.")
  Return True
EndFunction
