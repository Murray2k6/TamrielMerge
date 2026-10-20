ScriptName TMClimbing extends Quest
{Daggerfall's climbing (docs/progression.md; Daggerfall Unity's ClimbingMotor and CalculateClimbingChance), for the
Climbing skill. Skyrim can't climb, so the climb is the script's: jump at a wall you are walking into (you stay where
you were: something is in the way) and, if your grip holds, you climb while you hold forward, until you can step
forward again (over the top), let go, run out of stamina or lose your hold. Each grip check is one use of the skill:
starting 70%, holding on 50% once a second, regaining a hold 20% four times a second (each from its base towards 100%
with the skill, Khajiit +30, Handholds +10 a rank, plus a tenth of Luck). The hidden climbing effect is on while
climbing, and a moment after: Open Animation Replacer plays the climbing animation on it (Skyrim.Animations.
ClimbAnimation) and SafeFall's perk takes away the fall's damage. On a quest of its own (TMClimbingQuest): its quick loop
would otherwise wake every script of the progression quest.}

Actor Property PlayerRef Auto
Quest Property Progression Auto
{TMProgression, whose TMSkills and TMPassives this asks.}
Spell Property ClimbingAbility Auto
Perk Property SafeFall Auto
Race[] Property BonusRaces Auto
{Races climbing better (Daggerfall's Khajiit), and by how much.}
Int[] Property BonusAmount Auto
Float Property Tick = 0.05 AutoReadOnly
Float Property ClimbSpeed = 45.0 AutoReadOnly
{Units a second (Daggerfall: a third of walking speed).}
Float Property StaminaPerSecond = 4.0 AutoReadOnly

Int skill = -1
Bool climbing = False
Bool pendingStart = False
Bool slipping = False
Bool releasing = False
Float jumpX
Float jumpY
Float lastX
Float lastY
Float checkTimer = 0.0
Float slipTimer = 0.0
Float slipTime = 0.0

TMSkills Function Skills()
  Return Progression as TMSkills
EndFunction

; At the start and after each load (TMProgression.Setup).
Function Setup()
  TMSkills skills = Skills()
  If !skills || SKSE.GetVersion() <= 0
    Return
  EndIf
  skill = skills.Index("Climbing")
  If skill < 0
    Return
  EndIf
  If SafeFall && !PlayerRef.HasPerk(SafeFall)
    PlayerRef.AddPerk(SafeFall)
  EndIf
  If climbing || releasing
    ; A save made while climbing: back on the ground.
    climbing = False
    releasing = False
    PlayerRef.RemoveSpell(ClimbingAbility)
  EndIf
  RegisterForAnimationEvent(PlayerRef, "JumpUp")
EndFunction

Bool Function ForwardHeld()
  Return Input.IsKeyPressed(Input.GetMappedKey("Forward")) || Input.IsKeyPressed(Input.GetMappedKey("Forward", 2))
EndFunction

Float Function Moved(Float x, Float y)
  Float dx = PlayerRef.GetPositionX() - x
  Float dy = PlayerRef.GetPositionY() - y
  Return Math.sqrt(dx * dx + dy * dy)
EndFunction

; A grip check from its base: towards 100% with the skill (5-95), plus a tenth of Luck; each is a use of the skill.
Bool Function Check(Int base)
  TMSkills skills = Skills()
  Int level = skills.LevelOf(skill)
  Int i = BonusRaces.Find(PlayerRef.GetRace())
  If i >= 0
    level += BonusAmount[i]
  EndIf
  If skills.TreeTaken(skill, 0)
    level += 10
  EndIf
  If skills.TreeTaken(skill, 1)
    level += 10
  EndIf
  If level < 5
    level = 5
  ElseIf level > 95
    level = 95
  EndIf
  Int luck = 50
  TMPassives passives = Progression as TMPassives
  If passives && passives.RuleGame() >= 0
    luck = passives.AttributeValue(7)
  EndIf
  Float chance = base + (100 - base) * level / 100.0 + luck / 10.0
  skills.AddUse(skill, 1.0)
  Return Utility.RandomFloat(0.0, 100.0) < chance
EndFunction

Event OnAnimationEvent(ObjectReference akSource, String asEventName)
  If asEventName == "JumpUp" && !climbing && skill >= 0 && ForwardHeld()
    jumpX = PlayerRef.GetPositionX()
    jumpY = PlayerRef.GetPositionY()
    pendingStart = True
    RegisterForSingleUpdate(0.15)
  EndIf
EndEvent

Event OnUpdate()
  If pendingStart
    pendingStart = False
    ; Something in the way: the jump didn't carry you forward.
    If ForwardHeld() && Moved(jumpX, jumpY) < 6.0 && !PlayerRef.IsSwimming() && !PlayerRef.IsOnMount() && !Utility.IsInMenuMode()
      If Check(70)
        StartClimbing()
      Else
        Debug.Notification("You can't find a grip.")
      EndIf
    EndIf
    Return
  EndIf
  If climbing
    ClimbTick()
    If climbing
      RegisterForSingleUpdate(Tick)
    EndIf
    Return
  EndIf
  If releasing
    ; A moment after the climb, the effect (and the safe landing) goes.
    releasing = False
    PlayerRef.RemoveSpell(ClimbingAbility)
  EndIf
EndEvent

Function StartClimbing()
  If PlayerRef.IsWeaponDrawn()
    PlayerRef.SheatheWeapon()
  EndIf
  PlayerRef.AddSpell(ClimbingAbility, False)
  climbing = True
  slipping = False
  releasing = False
  checkTimer = 0.0
  slipTimer = 0.0
  lastX = PlayerRef.GetPositionX()
  lastY = PlayerRef.GetPositionY()
  RegisterForSingleUpdate(Tick)
EndFunction

Function ClimbTick()
  If !ForwardHeld()
    StopClimbing(False, "You let go.")
    Return
  EndIf
  ; Over the top: forward goes forward again.
  If Moved(lastX, lastY) > 6.0
    StopClimbing(True, "")
    Return
  EndIf
  TMSkills skills = Skills()
  Float cost = StaminaPerSecond * Tick
  If skills.TreeTaken(skill, 3)
    cost *= 0.5
  EndIf
  PlayerRef.DamageActorValue("Stamina", cost)
  If PlayerRef.GetActorValue("Stamina") <= 0.0
    StopClimbing(False, "You are too tired to hold on.")
    Return
  EndIf
  If slipping
    slipTimer += Tick
    slipTime += Tick
    If slipTimer >= 0.25
      slipTimer = 0.0
      slipping = !Check(20)
    EndIf
    If slipping && slipTime >= 1.5
      StopClimbing(False, "You lose your grip.")
    EndIf
    Return
  EndIf
  checkTimer += Tick
  If checkTimer >= 1.0
    checkTimer = 0.0
    If !Check(50)
      slipping = True
      slipTime = 0.0
      slipTimer = 0.0
      Debug.Notification("You slip!")
      Return
    EndIf
  EndIf
  Float speed = ClimbSpeed * (0.75 + skills.LevelOf(skill) / 400.0)
  If skills.TreeTaken(skill, 2)
    speed *= 1.3
  EndIf
  lastX = PlayerRef.GetPositionX()
  lastY = PlayerRef.GetPositionY()
  PlayerRef.SetPosition(lastX, lastY, PlayerRef.GetPositionZ() + speed * Tick)
EndFunction

; The climb over: at the top, a step onto it; the climbing effect stays a moment (the landing is safe).
Function StopClimbing(Bool top, String msg)
  climbing = False
  slipping = False
  If top
    Float a = PlayerRef.GetAngleZ()
    PlayerRef.SetPosition(PlayerRef.GetPositionX() + 30.0 * Math.sin(a), PlayerRef.GetPositionY() + 30.0 * Math.cos(a), PlayerRef.GetPositionZ() + 20.0)
  EndIf
  If msg != ""
    Debug.Notification(msg)
  EndIf
  releasing = True
  RegisterForSingleUpdate(1.5)
EndFunction
