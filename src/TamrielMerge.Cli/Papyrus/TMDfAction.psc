ScriptName TMDfAction extends ObjectReference
{A Daggerfall dungeon action object (lever, switch, lift, platform, secret wall, trapdoor, portcullis, wheel,
action door, action flat...), following Daggerfall Unity's DaggerfallAction (MIT): when triggered it first passes the
trigger on to the next object in its chain, then performs its own action. Triggers are ignored while it, or anything
further down its chain, is still moving. Movement toggles between the start pose and the end pose on each trigger.
All poses are absolute Skyrim positions/angles precomputed by the converter: the script does no set-up work.}

; Daggerfall action types (Daggerfall Unity DFBlock.RdbActionFlags).
Int Property ActionMove = 1 AutoReadOnly
Int Property ActionCastSpell = 9 AutoReadOnly
Int Property ActionShowText = 11 AutoReadOnly
Int Property ActionRiddle = 12 AutoReadOnly
Int Property ActionTeleport = 14 AutoReadOnly
Int Property ActionLockDoor = 16 AutoReadOnly
Int Property ActionUnlockDoor = 17 AutoReadOnly
Int Property ActionOpenDoor = 18 AutoReadOnly
Int Property ActionCloseDoor = 20 AutoReadOnly
Int Property ActionHurt21 = 21 AutoReadOnly
Int Property ActionHurt22 = 22 AutoReadOnly
Int Property ActionHurt25 = 25 AutoReadOnly
Int Property ActionDrainMagicka = 28 AutoReadOnly
Int Property ActionDialogue = 29 AutoReadOnly
Int Property ActionSetGlobal = 31 AutoReadOnly
Int Property ActionUnknown32 = 32 AutoReadOnly
Int Property ActionDoorText = 99 AutoReadOnly
; 26 (poison), 27, 30 (activate), 50 and 100 only pass the trigger on, as in Daggerfall Unity.

; Daggerfall trigger types (Daggerfall Unity DFBlock.RdbTriggerFlags).
Int Property TriggerNone = 0 AutoReadOnly
Int Property TriggerWalkOn = 1 AutoReadOnly
Int Property TriggerDirect = 2 AutoReadOnly
Int Property TriggerWalkInto = 3 AutoReadOnly
Int Property TriggerAttack = 5 AutoReadOnly
Int Property TriggerDirect6 = 6 AutoReadOnly
Int Property TriggerMulti = 8 AutoReadOnly
Int Property TriggerCollision09 = 9 AutoReadOnly

; Daggerfall Unity's "lock door" lock value (16), and the Skyrim level the converter gives it (Adept).
Int Property LockDoorValue = 16 AutoReadOnly
Int Property LockDoorLevel = 50 AutoReadOnly

Int Property ActionType Auto
Int Property TriggerType Auto
ObjectReference Property NextObject Auto
{Next object in the action chain (another TMDfAction), or the teleport destination.}
Float Property Duration = 1.0 Auto
{Seconds to move between poses.}
Float[] Property StartPose Auto
{Absolute start pose: X, Y, Z, AngleX, AngleY, AngleZ (degrees), where the converter placed it.}
Float[] Property EndPose Auto
{Absolute end pose: X, Y, Z, AngleX, AngleY, AngleZ (degrees).}
Float Property Magnitude Auto
{Hurt 21: the low end of the roll; hurt 22-25: damage per player level; drain magicka: magicka.}
Int Property Index Auto
{Hurt 21: the high end of the roll (Daggerfall's sound/index byte).}
Spell Property TrapSpell Auto
{Fired at the player by cast-spell actions.}
Bool Property TrapOnPlayer Auto
{Caster-only spells (Levitate, Heal...) take effect on the player, as Daggerfall readies them for the player for free.}
String Property Text = "" Auto
{What a text action (11) shows, a door's notice (99, shown the first time it is used), or a 32's text.}
Message Property Riddle Auto
{A riddle (12): only the right answer (RiddleAnswer, a button) passes the trigger on.}
Int Property RiddleAnswer = 0 Auto
GlobalVariable Property QuestGlobal Auto
{Set-global actions (31): the Daggerfall quest global variable this sets (DFQuestGlobal<n>).}
Actor Property Speaker Auto
{Dialogue actions (29): the person who talks.}
Int Property DoorLock = 0 Auto
{A door's starting Daggerfall lock, which closing it (20) puts back.}

Bool atEnd = False
Bool moving = False
Bool busy = False
Bool told = False
Int activations = 0

Event OnActivate(ObjectReference akActionRef)
  If akActionRef == Game.GetPlayer() && ActionType == ActionDoorText
    Perform()
  ElseIf akActionRef == Game.GetPlayer() && (TriggerType == TriggerDirect || TriggerType == TriggerDirect6 || TriggerType == TriggerMulti || TriggerType == TriggerCollision09)
    Trigger()
  EndIf
EndEvent

Event OnHit(ObjectReference akAggressor, Form akSource, Projectile akProjectile, Bool abPowerAttack, Bool abSneakAttack, Bool abBashAttack, Bool abHitBlocked)
  If akAggressor == Game.GetPlayer() && (TriggerType == TriggerAttack || TriggerType == TriggerMulti)
    Trigger()
  EndIf
EndEvent

; Called by a TMDfTrigger box when the player walks onto (walkedOn) or into this object. Walk-on: 1 and 9
; ("activated by collision / walking on"); walk-into: 3 and 8.
Function Collided(Bool walkedOn)
  If (walkedOn && (TriggerType == TriggerWalkOn || TriggerType == TriggerCollision09)) || (!walkedOn && (TriggerType == TriggerWalkInto || TriggerType == TriggerMulti))
    Trigger()
  EndIf
EndFunction

; Triggered by the previous object in a chain: no trigger-type check, as in Daggerfall.
Function Receive()
  Trigger()
EndFunction

; This object or one further down its chain is still moving (Daggerfall Unity IsPlaying). Chains may loop: give up
; after a few links.
Bool Function IsPlaying(Int depth = 0)
  If moving
    Return True
  EndIf
  If depth > 16 || !NextObject
    Return False
  EndIf
  TMDfAction nextAction = NextObject as TMDfAction
  If nextAction && nextAction != Self
    Return nextAction.IsPlaying(depth + 1)
  EndIf
  Return False
EndFunction

Function Trigger()
  If busy || IsPlaying()
    Return
  EndIf
  busy = True
  activations += 1
  ; A riddle passes the trigger on only when answered rightly (Daggerfall Unity UserInputHandler).
  If ActionType == ActionRiddle
    If Riddle && Riddle.Show() == RiddleAnswer && NextObject
      TMDfAction answered = NextObject as TMDfAction
      If answered
        answered.Receive()
      EndIf
    EndIf
    busy = False
    Return
  EndIf
  If NextObject
    TMDfAction nextAction = NextObject as TMDfAction
    If nextAction
      nextAction.Receive()
    EndIf
  EndIf
  Perform()
  busy = False
EndFunction

Function Perform()
  Actor player = Game.GetPlayer()
  If ActionType == ActionMove && EndPose.Length == 6 && StartPose.Length != 6 && !atEnd
    ; Games saved before the converter gave the start pose: it is where the object rests now.
    StartPose = new Float[6]
    StartPose[0] = GetPositionX()
    StartPose[1] = GetPositionY()
    StartPose[2] = GetPositionZ()
    StartPose[3] = GetAngleX()
    StartPose[4] = GetAngleY()
    StartPose[5] = GetAngleZ()
  EndIf
  If ActionType == ActionMove && EndPose.Length == 6 && StartPose.Length == 6
    atEnd = !atEnd
    Float[] target = Target()
    Float dx = target[0] - GetPositionX()
    Float dy = target[1] - GetPositionY()
    Float dz = target[2] - GetPositionZ()
    Float distance = Math.Sqrt(dx * dx + dy * dy + dz * dz)
    Float turn = MaxTurn(target)
    Float speed = 1.0
    If distance > 0.0
      speed = distance / Duration
    EndIf
    Float turnSpeed = 1.0
    If turn > 0.0
      turnSpeed = turn / Duration
    EndIf
    moving = True
    TranslateTo(target[0], target[1], target[2], target[3], target[4], target[5], speed, turnSpeed)
  ElseIf ActionType == ActionTeleport && NextObject
    ; The player arrives facing the way the destination faces (Daggerfall Unity Teleport).
    player.MoveTo(NextObject)
    player.SetAngle(0.0, 0.0, NextObject.GetAngleZ())
  ElseIf ActionType == ActionLockDoor
    If !IsLocked()
      Lock(True)
      SetLockLevel(LockDoorLevel)
      TMDfLock dfLock = (Self as ObjectReference) as TMDfLock
      If dfLock
        dfLock.LockValue = LockDoorValue
      EndIf
    EndIf
  ElseIf ActionType == ActionUnlockDoor
    Lock(False)
  ElseIf ActionType == ActionOpenDoor
    Lock(False)
    SetOpen(True)
  ElseIf ActionType == ActionCloseDoor
    ; Closing puts the door's starting lock back (Daggerfall Unity CloseDoor).
    Int openState = GetOpenState()
    If openState == 1 || openState == 2
      SetOpen(False)
      If DoorLock > 0
        Lock(True)
        SetLockLevel(SkyrimLockLevel(DoorLock))
        TMDfLock startLock = (Self as ObjectReference) as TMDfLock
        If startLock
          startLock.LockValue = DoorLock
        EndIf
      EndIf
    EndIf
  ElseIf ActionType == ActionHurt21
    ; Random damage, Magnitude to Index (exclusive) x level, only every 20th trigger (Daggerfall Unity DrainHealth21).
    If activations % 20 == 0
      Int low = Magnitude as Int
      If low < 1
        low = 1
      EndIf
      Int high = Index
      If high < 1
        high = 1
      EndIf
      Int roll = low
      If high > low
        roll = Utility.RandomInt(low, high - 1)
      EndIf
      player.DamageActorValue("Health", roll * Level(player))
    EndIf
  ElseIf ActionType >= ActionHurt22 && ActionType <= ActionHurt25
    player.DamageActorValue("Health", (Magnitude as Int) * Level(player))
  ElseIf ActionType == ActionDrainMagicka
    player.DamageActorValue("Magicka", Magnitude)
  ElseIf ActionType == ActionShowText && Text != ""
    Debug.MessageBox(Text)
  ElseIf ActionType == ActionUnknown32 && Text != ""
    Debug.Notification(Text)
  ElseIf ActionType == ActionDialogue
    If Speaker && !Speaker.IsDead()
      Speaker.Activate(player)
    ElseIf Text != ""
      Debug.MessageBox(Text)
    EndIf
  ElseIf ActionType == ActionSetGlobal && QuestGlobal
    QuestGlobal.SetValue(1.0)
  ElseIf ActionType == ActionDoorText && Text != "" && !told
    told = True
    Debug.MessageBox(Text)
  ElseIf ActionType == ActionCastSpell && TrapSpell
    If TrapOnPlayer
      TrapSpell.Cast(player, player)
    Else
      TrapSpell.Cast(Self, player)
    EndIf
  EndIf
EndFunction

Int Function Level(Actor player)
  Int lvl = player.GetLevel()
  If lvl < 1
    lvl = 1
  EndIf
  Return lvl
EndFunction

; Daggerfall lock strength to Skyrim lock level (the converter's DoorPlacer.LockFor).
Int Function SkyrimLockLevel(Int dfLock)
  If dfLock <= 6
    Return 1
  ElseIf dfLock <= 14
    Return 25
  ElseIf dfLock <= 20
    Return 50
  ElseIf dfLock <= 30
    Return 75
  ElseIf dfLock <= 50
    Return 100
  EndIf
  Return 255
EndFunction

; The pose being moved to: the end pose after an odd number of moves, the start pose after an even number.
Float[] Function Target()
  If atEnd
    Return EndPose
  EndIf
  Return StartPose
EndFunction

Event OnTranslationComplete()
  moving = False
EndEvent

; Not loaded (a chain moved it from afar) or blocked: put it where it was going, so its pose and atEnd agree.
Event OnTranslationFailed()
  If moving
    Snap()
  EndIf
EndEvent

; A move cut short by the cell unloading or a saved game: finish it at once.
Event OnLoad()
  If moving
    StopTranslation()
    Snap()
  EndIf
EndEvent

Function Snap()
  Float[] target = Target()
  If target.Length == 6
    SetPosition(target[0], target[1], target[2])
    SetAngle(target[3], target[4], target[5])
  EndIf
  moving = False
EndFunction

Float Function MaxTurn(Float[] target)
  Float best = 0.0
  Int i = 3
  While i < 6
    Float current = GetAngleX()
    If i == 4
      current = GetAngleY()
    ElseIf i == 5
      current = GetAngleZ()
    EndIf
    Float d = target[i] - current
    While d > 180.0
      d -= 360.0
    EndWhile
    While d < -180.0
      d += 360.0
    EndWhile
    If d < 0.0
      d = -d
    EndIf
    If d > best
      best = d
    EndIf
    i += 1
  EndWhile
  Return best
EndFunction
