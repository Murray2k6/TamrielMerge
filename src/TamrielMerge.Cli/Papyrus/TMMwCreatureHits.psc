ScriptName TMMwCreatureHits extends ActiveMagicEffect
{A Morrowind creature struck as in its game (OpenMW 0.48 Creature::onHit): every blow from someone that lands and hurts it
makes it flinch (its graph's TMHit: one of its hits at random), unless it is knocked down - its agility times
fKnockDownMult at most the blow's damage, and a roll of 0..99 at least its agility times iKnockDownOddsMult / 100 plus
iKnockDownOddsBase (TMKnockDown). Knocked out while its fatigue (stamina) is below zero or it has none (character.cpp's
refreshHitRecoilAnims: TMKnockOut, bTMKnockedOut held until it comes to). Tells its graph which of its model's idle sets
it picks idles by (iTMIdleSet) and whether it is fighting (bTMInCombat: it runs, in water too). Its race's ability.}

; The creatures of its race, each one's agility (-1: no knockdowns) and idle set.
ActorBase[] Property Bases Auto
Int[] Property Agility Auto
Int[] Property IdleSet Auto
Float Property KnockDownMult Auto
Int Property KnockDownOddsMult Auto
Int Property KnockDownOddsBase Auto

Actor Me
Int Index = -1
Float LastHealth
Bool Out

Event OnEffectStart(Actor akTarget, Actor akCaster)
  Me = akTarget
  Index = Bases.Find(Me.GetLeveledActorBase())
  If Index < 0
    Index = Bases.Find(Me.GetActorBase())
  EndIf
  LastHealth = Me.GetActorValue("Health")
  Tell()
EndEvent

; Its graph starts afresh each time its 3D loads.
Event OnLoad()
  If Me
    LastHealth = Me.GetActorValue("Health")
    Tell()
  EndIf
EndEvent

Function Tell()
  If Index >= 0
    Me.SetAnimationVariableInt("iTMIdleSet", IdleSet[Index])
  EndIf
  Me.SetAnimationVariableBool("bTMInCombat", Me.IsInCombat())
  Out = False
  Me.SetAnimationVariableBool("bTMKnockedOut", False)
  CheckOut()
EndFunction

Event OnCombatStateChanged(Actor akTarget, Int aeCombatState)
  If Me
    Me.SetAnimationVariableBool("bTMInCombat", aeCombatState == 1)
    Watch()
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

; Knocked out while its fatigue is below zero or it has none; it comes to when its fatigue is back.
Function CheckOut()
  Bool now = Me.GetActorValue("Stamina") < 0.0 || Me.GetBaseActorValue("Stamina") <= 0.0
  If now != Out && !Me.IsDead()
    Out = now
    Me.SetAnimationVariableBool("bTMKnockedOut", now)
    If now
      Debug.SendAnimationEvent(Me, "TMKnockOut")
    EndIf
  EndIf
  Watch()
EndFunction

; While it fights or lies knocked out: its fatigue looked at four times a second (to come to), its health once a second
; (so a blow's damage is that blow's, not magic's since).
Function Watch()
  If Out
    RegisterForSingleUpdate(0.25)
  ElseIf Me.IsInCombat()
    RegisterForSingleUpdate(1.0)
  EndIf
EndFunction

Event OnUpdate()
  If Me && !Me.IsDead() && Me.Is3DLoaded()
    If !Out
      LastHealth = Me.GetActorValue("Health")
    EndIf
    CheckOut()
  EndIf
EndEvent
