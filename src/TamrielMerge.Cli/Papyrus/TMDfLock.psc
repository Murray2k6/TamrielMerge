ScriptName TMDfLock extends ObjectReference
{Daggerfall's locks (rules from Daggerfall Unity, MIT) on a converted door. No minigame: sneaking (Daggerfall's Steal
mode) and activating a locked door tries to pick it, with chance 5 x (level - lock) + Lockpicking (5-95%); a failure
can't be retried until the Lockpicking skill has risen. Locks of 20 and more are magically held: only magic opens
them. Hitting a locked door with a weapon may bash it open (20 - lock in %). The Skyrim lock stays on the door, so
Open spells, keys and AI see it as locked.}

Int Property LockValue = 0 Auto
{Daggerfall's lock strength.}
Int failedAtSkill = -1

Event OnLoad()
  BlockActivation(True)
EndEvent

Event OnActivate(ObjectReference akActionRef)
  Actor player = Game.GetPlayer()
  If akActionRef != player || !IsLocked()
    Activate(akActionRef, True)
    Return
  EndIf
  Key doorKey = GetKey()
  If doorKey && player.GetItemCount(doorKey) > 0
    Lock(False)
    Activate(player, True)
    Return
  EndIf
  If LockValue >= 20
    Debug.Notification("This is a magically held lock.")
    Return
  EndIf
  Int skill = player.GetActorValue("Lockpicking") as Int
  Int chance = 5 * (player.GetLevel() - LockValue) + skill
  If chance < 5
    chance = 5
  ElseIf chance > 95
    chance = 95
  EndIf
  If !player.IsSneaking()
    Debug.Notification("This door is locked. (" + Difficulty(chance) + " Sneak to pick it.)")
    Return
  EndIf
  If skill <= failedAtSkill
    Debug.Notification("You can't pick this lock until your skill improves.")
    Return
  EndIf
  Game.AdvanceSkill("Lockpicking", 2.0)
  If Utility.RandomInt(1, 100) <= chance
    Debug.Notification("You pick the lock.")
    Lock(False)
    Activate(player, True)
  Else
    Debug.Notification("You fail to pick the lock.")
    failedAtSkill = skill
  EndIf
EndEvent

Event OnHit(ObjectReference akAggressor, Form akSource, Projectile akProjectile, Bool abPowerAttack, Bool abSneakAttack, Bool abBashAttack, Bool abHitBlocked)
  If akAggressor != Game.GetPlayer() || !IsLocked() || LockValue >= 20 || !(akSource as Weapon)
    Return
  EndIf
  If Utility.RandomInt(1, 100) <= 20 - LockValue
    Lock(False)
    SetOpen(True)
    Debug.Notification("You bash the door open.")
  EndIf
EndEvent

; How hard the lock looks, as Daggerfall describes it.
String Function Difficulty(Int chance)
  If chance >= 95
    Return "The lock is trivial."
  ElseIf chance >= 70
    Return "The lock looks easy."
  ElseIf chance >= 45
    Return "The lock looks average."
  ElseIf chance >= 30
    Return "The lock looks difficult."
  EndIf
  Return "The lock looks very difficult."
EndFunction
