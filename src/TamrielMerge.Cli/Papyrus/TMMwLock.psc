ScriptName TMMwLock extends ObjectReference
{A Morrowind lock and trap on a converted door or container, worked the Morrowind way (TMMwSecurity) instead of
Skyrim's minigame. Activating it with a lockpick in hand tries to pick the lock, with a probe tries to disarm the
trap; carrying its key unlocks it and disarms the trap; otherwise a trap goes off (once), and a locked one stays shut.
Doors and containers keep their Skyrim lock (so keys and AI see it); a swinging door (TMSwingDoor, an activator)
keeps the lock here and asks LetIn before it opens.}

TMMwSecurity Property Security Auto
Int Property LockLevel = 0 Auto
Bool Property Locked = False Auto
{The lock of a swinging door.}
Bool Property Swing = False Auto
Key Property DoorKey Auto
Bool Property Trapped = False Auto
Spell Property TrapSpell Auto
Int Property TrapDamage = 0 Auto
{Health lost to the trap when there is no spell for it.}

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

Bool Property Deferred = False Auto Hidden
{A Morrowind script's OnActivate decides when it opens (TMMwScript.TakeActivation; its Activate calls LetIn).}

Event OnActivate(ObjectReference akActionRef)
  If !Swing && !Deferred && LetIn(akActionRef)
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

; Whether this activation opens it; picking, probing and traps happen here.
Bool Function LetIn(ObjectReference who)
  Actor player = Game.GetPlayer()
  If who != player
    Return !IsShut()
  EndIf
  Weapon tool = player.GetEquippedWeapon()
  If tool && Trapped && Security.Probes.HasForm(tool)
    If Security.Probe(tool)
      Trapped = False
    EndIf
    Return False
  EndIf
  If tool && IsShut() && Security.Picks.HasForm(tool)
    If Security.Pick(tool, LockLevel)
      Unlock()
    EndIf
    Return False
  EndIf
  If DoorKey && player.GetItemCount(DoorKey) > 0 && player.GetItemCount(doorKey) > 0
    Unlock()
    Trapped = False
  EndIf
  If Trapped
    Trapped = False
    If TrapSpell
      TrapSpell.Cast(Self, player)
    Else
      player.DamageActorValue("Health", TrapDamage)
      Debug.Notification("You set off a trap.")
    EndIf
    Return False
  EndIf
  If IsShut()
    Debug.Notification("Locked.")
    Return False
  EndIf
  Return True
EndFunction
