ScriptName TMDfAction extends ObjectReference
{A Daggerfall dungeon action object (lever, switch, lift, platform, secret wall, trapdoor, portcullis, wheel,
action door, action flat...), following Daggerfall Unity's DaggerfallAction (MIT): when triggered it first passes the
trigger on to the next object in its chain, plays its sound, then performs its own action. Triggers are ignored while it,
or anything further down its chain, is still moving. Movement toggles between the start pose and the end pose on each
trigger. On a door (DaggerfallActionDoor, PlayerActivate) the player's opening or closing it is a door trigger and the
click a direct one; a door notice keeps the door shut on its first use.
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
Int Property ActionDoorText = 99 AutoReadOnly
; 26 (poison), 27, 30 (activate), 32, 50 and 100 only pass the trigger on and play their sound, as in Daggerfall Unity
; (no handler of their own).

; Daggerfall trigger types (Daggerfall Unity DFBlock.RdbTriggerFlags).
Int Property TriggerNone = 0 AutoReadOnly
Int Property TriggerWalkOn = 1 AutoReadOnly
Int Property TriggerDirect = 2 AutoReadOnly
Int Property TriggerWalkInto = 3 AutoReadOnly
Int Property TriggerAttack = 5 AutoReadOnly
Int Property TriggerDirect6 = 6 AutoReadOnly
Int Property TriggerMulti = 8 AutoReadOnly
Int Property TriggerCollision09 = 9 AutoReadOnly
Int Property TriggerDoor = 10 AutoReadOnly

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
Float[] Property SpinPoses Auto
{A wheel's turn (Daggerfall's WHE, a full turn that ends where it began): poses a quarter turn apart, six floats
each, the last its start pose. Each trigger turns it through them in Duration.}
Float[] Property FlatShift Auto
{A flat's move (Daggerfall Unity moves its billboard): the Skyrim offset its end pose is from its start, in Duration
(0: at once).}
ObjectReference[] Property Riders Auto
{What stands for the flat (a torch, an item, a marker): moved by FlatShift.}
Float Property Magnitude Auto
{Hurt 21: the low end of the roll; hurt 22-25: damage per player level; drain magicka: magicka.}
Int Property Index Auto
{Hurt 21: the high end of the roll (Daggerfall's sound/index byte).}
Spell Property TrapSpell Auto
{Fired at the player by cast-spell actions.}
Bool Property TrapOnPlayer Auto
{Caster-only spells (Levitate, Heal...) take effect on the player, as Daggerfall readies them for the player for free.}
String Property Text = "" Auto
{What a text action (11) shows, or a door's notice (99, shown at the top of the screen the first time it is used).}
Bool Property Trespass = False Auto
{A door notice (99) whose raw axis value is over 5: using the door after its notice turns the area's enemies hostile
(Daggerfall Unity DoorText, GameManager.MakeEnemiesHostile).}
Quest Property Senses Auto
{Daggerfall's enemy senses (TMDfSensesQuest, TMDfSensesControl): the enemies a trespass turns hostile.}
Sound Property ActionSound Auto
{Daggerfall's sound for this object (DAGGER.SND by the sound/index byte), played on each trigger as Daggerfall Unity does.}
String Property RiddleText = "" Auto
{A riddle (12): its text, asked in the typing menu (Interface\TamrielMerge\riddle.swf); only a typed answer that is one
of RiddleAnswers (case ignored) passes the trigger on (Daggerfall Unity ShowTextWithInput, UserInputHandler).}
String[] Property RiddleAnswers Auto
String Property RiddleMenu = "TamrielMerge/riddle" AutoReadOnly
GlobalVariable Property QuestGlobal Auto
{Set-global actions (31): the Daggerfall quest global variable this sets (DFQuestGlobal<n>).}
Actor Property Speaker Auto
{Dialogue actions (29): the person who talks.}
Int Property DoorLock = 0 Auto
{A door's starting Daggerfall lock, which closing it (20) puts back.}

Bool atEnd = False
Bool moving = False
Int spinStep = -1
Bool busy = False
Int activations = 0
Float cooldown = 0.0
Bool asking = False

Event OnInit()
  HoldDoor()
EndEvent

Event OnLoad()
  HoldDoor()
  ; A move cut short by the cell unloading or a saved game: finish it at once.
  If moving
    StopTranslation()
    Snap()
  EndIf
EndEvent

; A door with a notice to show stays shut the first time it is used (Daggerfall Unity DaggerfallActionDoor.Open: the
; notice is shown and the door isn't opened); Skyrim's door opens on use unless its activation is blocked.
Bool Function HoldsDoor()
  Return ActionType == ActionDoorText && Text != "" && activations == 0 && (TriggerType == TriggerDoor || TriggerType == TriggerDirect || TriggerType == TriggerMulti) && GetBaseObject() as Door
EndFunction

Function HoldDoor()
  If HoldsDoor()
    BlockActivation(True)
  EndIf
EndFunction

Event OnActivate(ObjectReference akActionRef)
  If GetBaseObject() as Door && (Self as ObjectReference) as TMDfLock
    ; Daggerfall's lock on the same door passes the use on (TMDfLock calls Used first), so the two agree on whether
    ; the door opens.
    Return
  EndIf
  Used(akActionRef)
EndEvent

; The player using this object; true when a door must stay shut this time. A door (Daggerfall Unity PlayerActivate:
; the door is toggled first, then the click is passed on as a direct one): opening or closing it is a door trigger (10;
; DaggerfallActionDoor.ExecuteActionOnToggle, not when it is locked and stays shut), then the click a direct one.
; Anyone else opening a door triggers nothing.
Bool Function Used(ObjectReference akActionRef)
  If akActionRef != Game.GetPlayer()
    Return False
  EndIf
  Bool direct = TriggerType == TriggerDirect || TriggerType == TriggerDirect6 || TriggerType == TriggerMulti || TriggerType == TriggerCollision09
  If GetBaseObject() as Door
    If HoldsDoor()
      ; The first use: the notice (through whichever of the two triggers its trigger type takes), the door shut.
      If !((Self as ObjectReference) as TMDfLock)
        BlockActivation(False)
      EndIf
      Trigger()
      Return True
    EndIf
    Int openState = GetOpenState()
    Bool shutAndLocked = (openState == 3 || openState == 4) && IsLocked()
    If TriggerType == TriggerDoor && !shutAndLocked
      Trigger()
    EndIf
  EndIf
  If direct
    Trigger()
  EndIf
  Return False
EndFunction

Event OnHit(ObjectReference akAggressor, Form akSource, Projectile akProjectile, Bool abPowerAttack, Bool abSneakAttack, Bool abBashAttack, Bool abHitBlocked)
  If akAggressor == Game.GetPlayer() && (TriggerType == TriggerAttack || TriggerType == TriggerMulti)
    Trigger()
  EndIf
EndEvent

; Called by a TMDfTrigger box when the player walks onto (walkedOn) or into this object (Daggerfall Unity Receive):
; walking on counts for 1; walking into for 3, 8 and 9.
Function Collided(Bool walkedOn)
  If (walkedOn && TriggerType == TriggerWalkOn) || (!walkedOn && (TriggerType == TriggerWalkInto || TriggerType == TriggerMulti || TriggerType == TriggerCollision09))
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
  ; Daggerfall Unity Play: the next object is triggered (not by a riddle), the sound played, then the action done.
  If ActionType != ActionRiddle && NextObject
    TMDfAction nextAction = NextObject as TMDfAction
    If nextAction
      nextAction.Receive()
    EndIf
  EndIf
  If ActionSound
    ActionSound.Play(Self)
  EndIf
  ; A riddle passes the trigger on only when answered rightly (Daggerfall Unity UserInputHandler): OnRiddleAnswer.
  If ActionType == ActionRiddle
    ; (Asking with no menu open: a game saved while it was open was loaded.)
    If RiddleText != "" && (!asking || !UI.IsMenuOpen("CustomMenu"))
      asking = True
      RegisterForModEvent("TM_RiddleReady", "OnRiddleReady")
      RegisterForModEvent("TM_RiddleAnswer", "OnRiddleAnswer")
      UI.OpenCustomMenu(RiddleMenu)
    EndIf
    busy = False
    Return
  EndIf
  Perform()
  busy = False
EndFunction

; The typing menu has loaded: the riddle's text.
Event OnRiddleReady(String eventName, String strArg, Float numArg, Form sender)
  If asking
    UI.InvokeString("CustomMenu", "_root.Ask", RiddleText)
  EndIf
EndEvent

; What the player typed (numArg -1: the menu was closed with Escape). An exact match with one of the answers, case
; ignored (Papyrus compares strings without case), triggers the next object.
Event OnRiddleAnswer(String eventName, String strArg, Float numArg, Form sender)
  If !asking
    Return
  EndIf
  asking = False
  UnregisterForModEvent("TM_RiddleReady")
  UnregisterForModEvent("TM_RiddleAnswer")
  If numArg < 0.0 || !RiddleAnswers || !NextObject
    Return
  EndIf
  Int i = 0
  While i < RiddleAnswers.Length
    If strArg == RiddleAnswers[i]
      TMDfAction answered = NextObject as TMDfAction
      If answered
        answered.Receive()
      EndIf
      Return
    EndIf
    i += 1
  EndWhile
EndEvent

Function Perform()
  Actor player = Game.GetPlayer()
  If ActionType == ActionMove && FlatShift.Length == 3 && Riders.Length > 0
    MoveRiders()
    Return
  EndIf
  If ActionType == ActionMove && SpinPoses.Length >= 6
    ; A wheel: once round, a quarter at a time (each step short enough that it turns forward).
    spinStep = 0
    moving = True
    SpinTo(0)
    Return
  EndIf
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
  ElseIf ActionType == ActionDialogue
    If Speaker && !Speaker.IsDead()
      Speaker.Activate(player)
    ElseIf Text != ""
      Debug.MessageBox(Text)
    EndIf
  ElseIf ActionType == ActionSetGlobal && QuestGlobal
    QuestGlobal.SetValue(1.0)
  ElseIf ActionType == ActionDoorText
    ; Daggerfall Unity DoorText: the notice at the top of the screen on the first trigger; after it (or from the first,
    ; with none to show) a door whose raw axis value is over 5 turns the area's enemies hostile.
    If activations == 1 && Text != ""
      Debug.Notification(Text)
    ElseIf Trespass
      TMDfSensesControl control = Senses as TMDfSensesControl
      If control
        control.MakeEnemiesHostile()
      EndIf
    EndIf
  ElseIf ActionType == ActionCastSpell && TrapSpell
    ; Daggerfall Unity CastSpell: each trigger takes 45.454546 off a cooldown that starts at 0; at 0 or under the spell
    ; goes off and the cooldown is 1000 again (the first trigger casts, then every 22nd).
    cooldown -= 45.454546
    If cooldown <= 0.0
      If TrapOnPlayer
        TrapSpell.Cast(player, player)
      Else
        TrapSpell.Cast(Self, player)
      EndIf
      cooldown = 1000.0
    EndIf
  EndIf
EndFunction

; A flat's move: its riders to the end pose, or back to the start, each trigger (Daggerfall Unity Move); at once, or in
; Duration (playing until then, as a moving object is).
Float[] riderStart

Function MoveRiders()
  If riderStart.Length != Riders.Length * 3
    riderStart = Utility.CreateFloatArray(Riders.Length * 3)
    Int s = 0
    While s < Riders.Length
      If Riders[s]
        riderStart[s * 3] = Riders[s].GetPositionX()
        riderStart[s * 3 + 1] = Riders[s].GetPositionY()
        riderStart[s * 3 + 2] = Riders[s].GetPositionZ()
      EndIf
      s += 1
    EndWhile
  EndIf
  atEnd = !atEnd
  Float k = 0.0
  If atEnd
    k = 1.0
  EndIf
  Float distance = Math.Sqrt(FlatShift[0] * FlatShift[0] + FlatShift[1] * FlatShift[1] + FlatShift[2] * FlatShift[2])
  Int i = 0
  While i < Riders.Length
    ObjectReference r = Riders[i]
    If r
      Float x = riderStart[i * 3] + FlatShift[0] * k
      Float y = riderStart[i * 3 + 1] + FlatShift[1] * k
      Float z = riderStart[i * 3 + 2] + FlatShift[2] * k
      If Duration <= 0.0 || !r.Is3DLoaded()
        r.SetPosition(x, y, z)
      Else
        r.TranslateTo(x, y, z, r.GetAngleX(), r.GetAngleY(), r.GetAngleZ(), distance / Duration, 0.0)
      EndIf
    EndIf
    i += 1
  EndWhile
  If Duration > 0.0
    moving = True
    RegisterForSingleUpdate(Duration)
  EndIf
EndFunction

; The riders' move has had its time.
Event OnUpdate()
  If moving && FlatShift.Length == 3
    moving = False
  EndIf
EndEvent

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
  If spinStep >= 0 && (spinStep + 1) * 6 < SpinPoses.Length
    spinStep += 1
    SpinTo(spinStep)
    Return
  EndIf
  spinStep = -1
  moving = False
EndEvent

; The wheel's step n: its pose, in the step's share of Duration.
Function SpinTo(Int n)
  Int i = n * 6
  Float[] pose = new Float[6]
  Int k = 0
  While k < 6
    pose[k] = SpinPoses[i + k]
    k += 1
  EndWhile
  Float steps = (SpinPoses.Length / 6) as Float
  Float turnSpeed = MaxTurn(pose) / (Duration / steps)
  If turnSpeed <= 0.0
    turnSpeed = 1.0
  EndIf
  TranslateTo(pose[0], pose[1], pose[2], pose[3], pose[4], pose[5], 1.0, turnSpeed)
EndFunction

; Not loaded (a chain moved it from afar) or blocked: put it where it was going, so its pose and atEnd agree.
Event OnTranslationFailed()
  If moving
    Snap()
  EndIf
EndEvent

Function Snap()
  If FlatShift.Length == 3 && riderStart.Length == Riders.Length * 3
    ; A flat's riders cut short: where they were going.
    Float k = 0.0
    If atEnd
      k = 1.0
    EndIf
    Int i = 0
    While i < Riders.Length
      If Riders[i]
        Riders[i].StopTranslation()
        Riders[i].SetPosition(riderStart[i * 3] + FlatShift[0] * k, riderStart[i * 3 + 1] + FlatShift[1] * k, riderStart[i * 3 + 2] + FlatShift[2] * k)
      EndIf
      i += 1
    EndWhile
    moving = False
    Return
  EndIf
  If spinStep >= 0 && SpinPoses.Length >= 6
    ; A turn cut short: back where it began (its last pose).
    Int last = SpinPoses.Length - 6
    SetPosition(SpinPoses[last], SpinPoses[last + 1], SpinPoses[last + 2])
    SetAngle(SpinPoses[last + 3], SpinPoses[last + 4], SpinPoses[last + 5])
    spinStep = -1
    moving = False
    Return
  EndIf
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
