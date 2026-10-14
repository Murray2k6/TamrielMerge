ScriptName TMObActor extends Actor
{Every converted Oblivion person and creature (TamrielMerge): an attack on a peaceful one by the player is assault,
killing a peaceful person murder, as Oblivion counted them (GetCrimeKnown, GetPCFactionAttack / Murder, IsPCAMurderer,
OnAlarm blocks of whoever sees it).}

TMObState Property ObState Auto
Bool assaulted
Bool peaceful = True

Event OnHit(ObjectReference akAggressor, Form akSource, Projectile akProjectile, Bool abPowerAttack, Bool abSneakAttack, Bool abBashAttack, Bool abHitBlocked)
  Actor player = Game.GetPlayer()
  If akAggressor != player || assaulted || IsDead()
    Return
  EndIf
  If IsHostileToActor(player) || IsPlayerTeammate()
    peaceful = False
    Return
  EndIf
  assaulted = True
  If !TMObState.IsCreature(Self)
    ObState.Crime(3, player, Self)
  EndIf
EndEvent

Event OnDeath(Actor akKiller)
  Actor player = Game.GetPlayer()
  If akKiller == player && peaceful && !TMObState.IsCreature(Self)
    ObState.Crime(4, player, Self)
  EndIf
EndEvent

Event OnLoad()
  assaulted = False
  peaceful = True
EndEvent
