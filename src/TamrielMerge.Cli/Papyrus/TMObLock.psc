ScriptName TMObLock extends ObjectReference
{An Oblivion lock on a converted door or container. As in Oblivion, activating it without its key offers to pick
the lock by hand (Skyrim's lockpicking, the nearest thing to Oblivion's tumblers) or to Auto Attempt: every tumbler
not yet set falls to one roll each, at (min(Security, 100) x fLockPickAutoDifficulty + fLockPickAutoBase) %
(Oblivion.esm: 0.3 and 45; reverse-engineered on UESP's Oblivion talk:Security), and a failure breaks the pick.
A lock has one tumbler per difficulty tier (Very Easy 1 ... Very Hard 5); level 100 needs a key. Skyrim's
Lockpicking stands for Security. Doors and containers keep their Skyrim lock (keys and AI see it); a swinging door
(TMSwingDoor, an activator) keeps it here and asks LetIn before it opens.}

Int Property LockLevel = 0 Auto
Bool Property Locked = False Auto
{The lock of a swinging door.}
Bool Property Swing = False Auto
Key Property DoorKey Auto
Message Property Choice Auto
{"Pick the lock", "Auto Attempt", "Leave".}
MiscObject Property Lockpick Auto

Event OnInit()
  If !Swing
    BlockActivation(True)
  EndIf
EndEvent

Event OnLoad()
  If !Swing
    BlockActivation(True)
  EndIf
EndEvent

Event OnActivate(ObjectReference akActionRef)
  If !Swing && LetIn(akActionRef)
    Activate(akActionRef, True)
  EndIf
EndEvent

Bool Function IsShut()
  If Swing
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
  If who != player || !IsShut()
    Return !IsShut()
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
    ; Skyrim's own lockpicking on the door's Skyrim lock.
    Activate(player, True)
    Return False
  ElseIf button == 1
    Return AutoAttempt(player)
  EndIf
  Return False
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
  Int left = Tumblers()
  While left > 0
    If Utility.RandomFloat(0.0, 100.0) >= chance
      player.RemoveItem(Lockpick, 1, True)
      Debug.Notification("Your lockpick broke.")
      Return False
    EndIf
    left -= 1
  EndWhile
  Unlock()
  Game.AdvanceSkill("Lockpicking", 2.0)
  Debug.Notification("You picked the lock.")
  Return True
EndFunction
