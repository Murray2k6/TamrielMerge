ScriptName TMRgLaw extends Quest
{Redguard's law (docs/progression.md, "Crime by game and area"): Stros M'Kai keeps no bounty. The Empire's guards there
attack Cyrus when he goes about with a drawn weapon where one of them sees him (within 2048 units, in sight), and stop
once it is put away, unless he has hurt that guard (its health is below what it had when it drew: a guard nicked by
someone else earlier stands down too; 2026-10-29, it used to stand down only at full health); whoever he strikes answers
by themselves.}

Actor[] Property Guards Auto
{Every guard Redguard places (RgLaw).}
Actor Property PlayerRef Auto

Float[] startHealth   ; by guard: its health when it drew on the player (-1 none)

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
  If !startHealth || startHealth.Length != Guards.Length
    startHealth = Utility.CreateFloatArray(Guards.Length, -1.0)
  EndIf
  Bool drawn = PlayerRef.IsWeaponDrawn()
  Int i = 0
  While i < Guards.Length
    Actor g = Guards[i]
    If g && g.Is3DLoaded() && !g.IsDead()
      If drawn
        If !g.IsInCombat() && g.GetDistance(PlayerRef) < 2048.0 && g.HasLOS(PlayerRef)
          startHealth[i] = g.GetActorValue("Health")
          g.StartCombat(PlayerRef)
        EndIf
      ElseIf g.IsInCombat() && g.GetCombatTarget() == PlayerRef
        ; Not hurt since it drew (half a point of leeway for regeneration rounding): it stands down.
        If startHealth[i] < 0.0 || g.GetActorValue("Health") >= startHealth[i] - 0.5
          g.StopCombat()
          g.StopCombatAlarm()
          startHealth[i] = -1.0
        EndIf
      EndIf
    EndIf
    i += 1
  EndWhile
EndFunction
