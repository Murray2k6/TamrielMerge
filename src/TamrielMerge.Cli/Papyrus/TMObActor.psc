ScriptName TMObActor extends Actor
{Every converted Oblivion person and creature (TamrielMerge): an attack on a peaceful one by the player is assault,
killing a peaceful person murder, as Oblivion counted them (GetCrimeKnown, GetPCFactionAttack / Murder, IsPCAMurderer,
OnAlarm blocks of whoever sees it).}

TMObState Property ObState Auto
Int[] Property StatBase Auto
{Its Oblivion attributes (0-7) and skills (8-28): the record's, or, for one whose stats Oblivion works out, those at
level 1 (ObStats).}
Float[] Property StatRate Auto
{What each rises a level after the first, for one whose stats Oblivion works out (none for the others).}
Quest Property Conversations Auto
{OBDialogue (TMObConversations), for one of Oblivion's people (not its creatures): it talks with others there.}
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

Event OnUnload()
  If Conversations
    (Conversations as TMObConversations).Unregister(Self)
  EndIf
EndEvent

Event OnDeath(Actor akKiller)
  If Conversations
    (Conversations as TMObConversations).Unregister(Self)
  EndIf
  Actor player = Game.GetPlayer()
  If akKiller == player && peaceful && !TMObState.IsCreature(Self)
    ObState.Crime(4, player, Self)
  EndIf
EndEvent

Event OnLoad()
  assaulted = False
  peaceful = True
  ObState.MirrorActor(Self)
  If Conversations
    (Conversations as TMObConversations).Register(Self)
  EndIf
EndEvent

; Its Oblivion stat at its level as it is (rounded, at most 100 when worked out), or -1 if it has none.
Float Function OblivionStat(Int aiStat)
  If !StatBase || aiStat < 0 || aiStat >= StatBase.Length
    Return -1.0
  EndIf
  Float v = StatBase[aiStat]
  If StatRate && aiStat < StatRate.Length && StatRate[aiStat] != 0.0
    Int level = GetLevel()
    If level < 1
      level = 1
    EndIf
    v = Math.Floor(v + (level - 1) * StatRate[aiStat] + 0.5)
    If v > 100.0
      v = 100.0
    EndIf
  EndIf
  Return v
EndFunction
