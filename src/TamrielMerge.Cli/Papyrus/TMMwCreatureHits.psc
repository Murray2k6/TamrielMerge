ScriptName TMMwCreatureHits extends ActiveMagicEffect
{A Morrowind creature as its game runs it (OpenMW 0.48's reading), telling its own behaviour graph what Skyrim's engine
doesn't. Its race's ability.
- Struck: every blow from someone that lands and hurts it makes it flinch (TMHit: one of its hits at random), unless it
  is knocked down (Creature::onHit: its agility times fKnockDownMult at most the blow's damage, and a roll of 0..99 at
  least its agility times iKnockDownOddsMult / 100 plus iKnockDownOddsBase: TMKnockDown).
- Knocked out (TMKnockOut, bTMKnockedOut held) once its fatigue is spent - Skyrim's stamina stops at empty where
  Morrowind's fatigue goes below zero - or when it has none, until it comes back.
- Its idles by its own chances (iTMIdleSet), whether it fights (bTMInCombat) or runs (bTMRunning: Morrowind runs a fight,
  else as its AI says), the range of the spell it holds (iTMCastRange: Morrowind's cast animation is the spell's first
  effect's), and facing into a storm (bTMStorm: Morrowind's IdleStorm when the storm blows within 60 degrees of straight at
  it, an ash or blight storm from Red Mountain, any other along +Y).
- A group a script plays (TMMwScript.PlayGroup) counted through its loops (iTMLoops); dying while one plays waits for it
  to end (Morrowind: "Do not interrupt scripted animation by death").
- How fast it turns while it turns (fTMTurnRate, degrees a second, from its own facing ten times a second): its graph
  plays its turn that much faster, as OpenMW does (1 to 1.5 times, by the turn over half a turn a second).}

; The creatures of its race, each one's agility (-1: no knockdowns) and idle set.
ActorBase[] Property Bases Auto
Int[] Property Agility Auto
Int[] Property IdleSet Auto
Float Property KnockDownMult Auto
Int Property KnockDownOddsMult Auto
Int Property KnockDownOddsBase Auto
; Its spells by Morrowind's range (the rest are Target), and when it can face into a storm (it has IdleStorm) its game's
; storms: ash and blight (from Red Mountain), and the rest (the blizzard: along +Y).
Spell[] Property SelfSpells Auto
Spell[] Property TouchSpells Auto
FormList Property AshStorms Auto
FormList Property OtherStorms Auto

Actor Me
Int Index = -1
Float LastHealth
Bool Out
Bool Moving
Bool DeathWaits
Bool Turning
Float TurnAngle
Float TurnTime

Event OnEffectStart(Actor akTarget, Actor akCaster)
  Me = akTarget
  Index = Bases.Find(Me.GetLeveledActorBase())
  If Index < 0
    Index = Bases.Find(Me.GetActorBase())
  EndIf
  LastHealth = Me.GetActorValue("Health")
  RegisterForAnimationEvent(Me, "moveStart")
  RegisterForAnimationEvent(Me, "moveStop")
  RegisterForAnimationEvent(Me, "TMPlayCycle")
  RegisterForAnimationEvent(Me, "TMPlayDone")
  RegisterTurns()
  Tell()
EndEvent

Function RegisterTurns()
  RegisterForAnimationEvent(Me, "turnLeft")
  RegisterForAnimationEvent(Me, "turnRight")
  RegisterForAnimationEvent(Me, "turnStop")
  ; Its own graph's, entering and leaving a turn (sent to scripts whatever the engine's are).
  RegisterForAnimationEvent(Me, "TMTurnStart")
  RegisterForAnimationEvent(Me, "TMTurnEnd")
  RegisterForAnimationEvent(Me, "moveStart")
EndFunction

; Its graph starts afresh each time its 3D loads.
Event OnLoad()
  If Me
    RegisterForAnimationEvent(Me, "moveStart")
    RegisterForAnimationEvent(Me, "moveStop")
    RegisterForAnimationEvent(Me, "TMPlayCycle")
    RegisterForAnimationEvent(Me, "TMPlayDone")
    RegisterTurns()
    LastHealth = Me.GetActorValue("Health")
    Tell()
  EndIf
EndEvent

Function Tell()
  If Index >= 0
    Me.SetAnimationVariableInt("iTMIdleSet", IdleSet[Index])
  EndIf
  Me.SetAnimationVariableBool("bTMInCombat", Me.IsInCombat())
  Me.SetAnimationVariableBool("bTMRunning", Me.IsRunning())
  Out = False
  Me.SetAnimationVariableBool("bTMKnockedOut", False)
  Range(Me.GetEquippedSpell(0))
  Range(Me.GetEquippedSpell(1))
  CheckOut()
EndFunction

Event OnCombatStateChanged(Actor akTarget, Int aeCombatState)
  If Me
    Me.SetAnimationVariableBool("bTMInCombat", aeCombatState == 1)
    Watch()
  EndIf
EndEvent

; A spell readied: its Morrowind range for its cast (0 self, 1 touch, 2 target).
Event OnObjectEquipped(Form akBaseObject, ObjectReference akReference)
  Range(akBaseObject as Spell)
EndEvent

Function Range(Spell s)
  If s && Me
    Int r = 2
    If SelfSpells && SelfSpells.Find(s) >= 0
      r = 0
    ElseIf TouchSpells && TouchSpells.Find(s) >= 0
      r = 1
    EndIf
    Me.SetAnimationVariableInt("iTMCastRange", r)
  EndIf
EndFunction

Event OnAnimationEvent(ObjectReference akSource, String asEventName)
  If !Me
    Return
  EndIf
  If asEventName == "turnLeft" || asEventName == "turnRight" || asEventName == "TMTurnStart"
    If !Turning
      Turning = True
      TurnAngle = Me.GetAngleZ()
      TurnTime = Utility.GetCurrentRealTime()
      RegisterForSingleUpdate(0.1)
    EndIf
    Return
  ElseIf asEventName == "turnStop" || asEventName == "TMTurnEnd"
    Turning = False
    Return
  EndIf
  If asEventName == "moveStart"
    Turning = False
    Moving = True
    Me.SetAnimationVariableBool("bTMRunning", Me.IsRunning())
    Watch()
  ElseIf asEventName == "moveStop"
    Moving = False
  ElseIf asEventName == "TMPlayCycle"
    ; One more time through a played group's loop part (LoopGroup's count; PlayGroup's is endless: -1).
    Int n = Me.GetAnimationVariableInt("iTMLoops")
    If n > 0
      Me.SetAnimationVariableInt("iTMLoops", n - 1)
    EndIf
  ElseIf asEventName == "TMPlayDone"
    If DeathWaits
      DeathWaits = False
      Debug.SendAnimationEvent(Me, "DeathAnimation")
    EndIf
  EndIf
EndEvent

Event OnDying(Actor akKiller)
  If Me && Me.GetAnimationVariableBool("bTMScripted")
    DeathWaits = True
  EndIf
EndEvent

Event OnHit(ObjectReference akAggressor, Form akSource, Projectile akProjectile, Bool abPowerAttack, Bool abSneakAttack, Bool abBashAttack, Bool abHitBlocked)
  If !Me
    Return
  EndIf
  ; The blow's damage: what it took off its health since the last blow or look (its race heals none by itself).
  Float health = Me.GetActorValue("Health")
  Float damage = LastHealth - health
  LastHealth = health
  ; A blow from someone that landed and hurt: a weapon's, a fist's or claw's, an arrow's (not a spell's: Morrowind's spells
  ; neither stagger nor knock down).
  Bool blow = akSource == None || (akSource as Weapon) != None
  If blow && akAggressor != None && !abHitBlocked && damage > 0.0 && !Me.IsDead()
    Int agi = -1
    If Index >= 0
      agi = Agility[Index]
    EndIf
    If agi >= 0 && agi * KnockDownMult <= damage && (agi * KnockDownOddsMult) * 0.01 + KnockDownOddsBase <= Utility.RandomInt(0, 99)
      Debug.SendAnimationEvent(Me, "TMKnockDown")
    Else
      Debug.SendAnimationEvent(Me, "TMHit")
    EndIf
  EndIf
  CheckOut()
EndEvent

; Knocked out once its fatigue is spent or it has none; it comes to when its fatigue comes back.
Function CheckOut()
  Bool now = Me.GetActorValue("Stamina") <= 0.0 || Me.GetBaseActorValue("Stamina") <= 0.0
  If now != Out && !Me.IsDead()
    Out = now
    Me.SetAnimationVariableBool("bTMKnockedOut", now)
    If now
      Debug.SendAnimationEvent(Me, "TMKnockOut")
    EndIf
  EndIf
  Watch()
EndFunction

; Facing into a storm: the storm's way (from Red Mountain for ash and blight, else +Y) against its facing, more than 120
; degrees apart (OpenMW's updateIdleStormState: their dot product below -0.5); not in water.
Bool Function InStorm()
  If !AshStorms || Me.IsSwimming()
    Return False
  EndIf
  Weather now = Weather.GetCurrentWeather()
  Bool ash = AshStorms.HasForm(now)
  If !ash && !(OtherStorms && OtherStorms.HasForm(now))
    Return False
  EndIf
  Float x = 0.0
  Float y = 1.0
  If ash
    Actor player = Game.GetPlayer()
    x = player.GetPositionX() - 25000.0
    y = player.GetPositionY() - 70000.0
    Float dist = Math.Sqrt(x * x + y * y)
    If dist <= 0.0
      Return False
    EndIf
    x = x / dist
    y = y / dist
  EndIf
  Float a = Me.GetAngleZ()
  Return Math.Sin(a) * x + Math.Cos(a) * y < -0.5
EndFunction

; While it moves, fights, lies knocked out or might face a storm: whether it runs four times a second, its fatigue (to
; come to) and storm, and its health once a second (so a blow's damage is that blow's, not magic's since).
Function Watch()
  If Turning
    RegisterForSingleUpdate(0.1)
  ElseIf Out || Moving
    RegisterForSingleUpdate(0.25)
  ElseIf Me.IsInCombat() || AshStorms
    RegisterForSingleUpdate(1.0)
  EndIf
EndFunction

Event OnUpdate()
  If Me && !Me.IsDead() && Me.Is3DLoaded()
    If !Out
      LastHealth = Me.GetActorValue("Health")
    EndIf
    If Moving
      Me.SetAnimationVariableBool("bTMRunning", Me.IsRunning())
    EndIf
    If Turning
      ; Degrees turned since the last look (the shorter way round) over the seconds between.
      Float now = Utility.GetCurrentRealTime()
      Float angle = Me.GetAngleZ()
      Float turned = angle - TurnAngle
      If turned > 180.0
        turned -= 360.0
      ElseIf turned < -180.0
        turned += 360.0
      EndIf
      If now > TurnTime
        Me.SetAnimationVariableFloat("fTMTurnRate", Math.Abs(turned) / (now - TurnTime))
      EndIf
      TurnAngle = angle
      TurnTime = now
    EndIf
    If AshStorms
      Me.SetAnimationVariableBool("bTMStorm", InStorm())
    EndIf
    CheckOut()
  EndIf
EndEvent
