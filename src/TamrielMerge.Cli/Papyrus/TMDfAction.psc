ScriptName TMDfAction extends ObjectReference
{A Daggerfall dungeon action object (lever, switch, lift, platform, secret wall, trapdoor, portcullis, wheel,
action door...), following Daggerfall Unity's DaggerfallAction: when triggered it first passes the trigger on to
the next object in its chain, then performs its own action. Movement toggles between the start pose and the end
pose on each trigger. All poses are absolute Skyrim positions/angles precomputed by the converter.}

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
Int Property ActionHurtRandom = 21 AutoReadOnly
Int Property ActionPoison = 26 AutoReadOnly
Int Property ActionDrainMagicka = 28 AutoReadOnly
Int Property ActionDoorText = 99 AutoReadOnly

; Daggerfall trigger types (Daggerfall Unity DFBlock.RdbTriggerFlags).
Int Property TriggerNone = 0 AutoReadOnly
Int Property TriggerWalkOn = 1 AutoReadOnly
Int Property TriggerDirect = 2 AutoReadOnly
Int Property TriggerWalkInto = 3 AutoReadOnly
Int Property TriggerAttack = 5 AutoReadOnly
Int Property TriggerDirect6 = 6 AutoReadOnly
Int Property TriggerMulti = 8 AutoReadOnly
Int Property TriggerCollision09 = 9 AutoReadOnly

Int Property ActionType Auto
Int Property TriggerType Auto
ObjectReference Property NextObject Auto
{Next object in the action chain (another TMDfAction), or the teleport destination.}
Float Property Duration = 1.0 Auto
{Seconds to move between poses.}
Float[] Property EndPose Auto
{Absolute end pose: X, Y, Z, AngleX, AngleY, AngleZ (degrees).}
Float Property Magnitude Auto
{Damage per player level for hurt actions; magicka for drain.}
Spell Property TrapSpell Auto
{Fired at the player by cast-spell actions.}
Bool Property TrapOnPlayer Auto
{Caster-only spells (Levitate, Heal...) take effect on the player, as Daggerfall readies them for the player for free.}
String Property Text = "" Auto
{What a text action (11) shows, or a door's notice (99, shown the first time it is used).}
Message Property Riddle Auto
{A riddle (12): only the right answer (RiddleAnswer, a button) passes the trigger on.}
Int Property RiddleAnswer = 0 Auto

Float[] startPose
Bool atEnd = False
Bool busy = False
Bool told = False

Event OnInit()
  startPose = new Float[6]
  startPose[0] = GetPositionX()
  startPose[1] = GetPositionY()
  startPose[2] = GetPositionZ()
  startPose[3] = GetAngleX()
  startPose[4] = GetAngleY()
  startPose[5] = GetAngleZ()
EndEvent

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

; Called by a TMDfTrigger box when the player walks into or onto this object.
Function Collided(Bool walkedOn)
  If (walkedOn && TriggerType == TriggerWalkOn) || (!walkedOn && (TriggerType == TriggerWalkInto || TriggerType == TriggerMulti || TriggerType == TriggerCollision09))
    Trigger()
  EndIf
EndFunction

; Triggered by the previous object in a chain: no trigger-type check, as in Daggerfall.
Function Receive()
  Trigger()
EndFunction

Function Trigger()
  If busy
    Return
  EndIf
  busy = True
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
  If ActionType != ActionTeleport && NextObject
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
  If ActionType == ActionMove && EndPose.Length == 6
    Float[] target = EndPose
    If atEnd
      target = startPose
    EndIf
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
    TranslateTo(target[0], target[1], target[2], target[3], target[4], target[5], speed, turnSpeed)
    atEnd = !atEnd
  ElseIf ActionType == ActionTeleport && NextObject
    player.MoveTo(NextObject)
  ElseIf ActionType == ActionLockDoor
    Lock(True)
    SetLockLevel(25)
  ElseIf ActionType == ActionUnlockDoor
    Lock(False)
  ElseIf ActionType == ActionOpenDoor
    Lock(False)
    SetOpen(True)
  ElseIf ActionType == ActionCloseDoor
    SetOpen(False)
  ElseIf ActionType >= ActionHurtRandom && ActionType < ActionPoison
    player.DamageActorValue("Health", Magnitude * player.GetLevel())
  ElseIf ActionType == ActionDrainMagicka
    player.DamageActorValue("Magicka", Magnitude)
  ElseIf ActionType == ActionShowText && Text != ""
    Debug.MessageBox(Text)
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
