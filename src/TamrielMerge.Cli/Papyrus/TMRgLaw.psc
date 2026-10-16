ScriptName TMRgLaw extends Quest
{Redguard's law (docs/progression.md, "Crime by game and area"): Stros M'Kai keeps no bounty. The Empire's guards there
attack Cyrus when he goes about with a drawn weapon where one of them sees him (within 2048 units, in sight), and stop
once it is put away, unless he has hurt that guard; whoever he strikes answers by themselves.}

Actor[] Property Guards Auto
{Every guard Redguard places (RgLaw).}
Actor Property PlayerRef Auto

Event OnInit()
  RegisterForSingleUpdate(2.0)
EndEvent

Event OnUpdate()
  Tick()
  RegisterForSingleUpdate(1.0)
EndEvent

Function Tick()
  If !Guards
    Return
  EndIf
  Bool drawn = PlayerRef.IsWeaponDrawn()
  Int i = 0
  While i < Guards.Length
    Actor g = Guards[i]
    If g && g.Is3DLoaded() && !g.IsDead()
      If drawn
        If !g.IsInCombat() && g.GetDistance(PlayerRef) < 2048.0 && g.HasLOS(PlayerRef)
          g.StartCombat(PlayerRef)
        EndIf
      ElseIf g.IsInCombat() && g.GetCombatTarget() == PlayerRef && g.GetActorValuePercentage("Health") >= 1.0
        g.StopCombat()
        g.StopCombatAlarm()
      EndIf
    EndIf
    i += 1
  EndWhile
EndFunction
