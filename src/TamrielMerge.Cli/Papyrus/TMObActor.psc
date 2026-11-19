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

; Where and when the actor took up its current package (TMObDoor: someone sent on and held where it started, at a shut
; door, wants through it), and when its 3D last loaded (the package it begins a cell with is no sending on).
Float packageStartTime
Float packageStartX
Float packageStartY
Float loadedTime

; For the log (when Papyrus logging is on): the packages an Oblivion person takes up, so a scene can be followed
; without the console (which package a guard is on when he stands at a gate, 2026-11-14).
Event OnPackageStart(Package akNewPackage)
  packageStartTime = Utility.GetCurrentRealTime()
  packageStartX = GetPositionX()
  packageStartY = GetPositionY()
  Debug.Trace("TMOb package " + Self + " starts " + akNewPackage + " at " + packageStartX + " " + packageStartY)
EndEvent

; Sent on by a new package and held where it took it up: the package began afMin to afMax seconds ago, at least 2 s
; after the actor's 3D loaded, and the actor is still within afMove of where it was then.
Bool Function HeldSinceNewPackage(Float afMin, Float afMax, Float afMove)
  If packageStartTime <= 0.0 || packageStartTime < loadedTime + 2.0
    Return False
  EndIf
  Float since = Utility.GetCurrentRealTime() - packageStartTime
  If since < afMin || since > afMax
    Return False
  EndIf
  Float dx = GetPositionX() - packageStartX
  Float dy = GetPositionY() - packageStartY
  Return dx * dx + dy * dy < afMove * afMove
EndFunction

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
  loadedTime = Utility.GetCurrentRealTime()
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
