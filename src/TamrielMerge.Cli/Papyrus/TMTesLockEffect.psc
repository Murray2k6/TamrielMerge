ScriptName TMTesLockEffect extends ActiveMagicEffect
{Morrowind's and Oblivion's Open and Lock (TesMagic). Cast on yourself at the door or container under the crosshair
(SKSE): Open unlocks a lock no stronger than the magnitude (Morrowind: lock level; Oblivion: the tier the spell
names, up to Very Hard); Lock locks it at the magnitude. Converted locks (TMMwLock, TMObLock) and swinging doors
keep their own state; other doors and containers use Skyrim's lock.}

Bool Property LockIt = False Auto

Event OnEffectStart(Actor akTarget, Actor akCaster)
  ObjectReference target = Game.GetCurrentCrosshairRef()
  If !target
    Debug.Notification("There is nothing to " + Verb() + ".")
    Return
  EndIf
  Int magnitude = GetMagnitude() as Int
  TMMwLock mwLock = target as TMMwLock
  TMObLock obLock = target as TMObLock
  If LockIt
    If mwLock
      mwLock.LockLevel = magnitude
      mwLock.Locked = True
    ElseIf obLock
      obLock.LockLevel = magnitude
      obLock.Locked = True
    EndIf
    If !(mwLock && mwLock.Swing) && !(obLock && obLock.Swing)
      target.Lock(True)
    EndIf
    Return
  EndIf
  Int level = target.GetLockLevel()
  Bool shut = target.IsLocked()
  If mwLock
    level = mwLock.LockLevel
    shut = mwLock.IsShut()
  ElseIf obLock
    level = obLock.LockLevel
    shut = obLock.IsShut()
  EndIf
  If !shut
    Return
  EndIf
  If level > magnitude || level >= 100 && obLock
    Debug.Notification("The lock is too strong for this spell.")
    Return
  EndIf
  If mwLock
    mwLock.Unlock()
  ElseIf obLock
    obLock.Unlock()
  Else
    target.Lock(False)
  EndIf
EndEvent

String Function Verb()
  If LockIt
    Return "lock"
  EndIf
  Return "open"
EndFunction
