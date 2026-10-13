ScriptName TMDfDaytime extends Quest
{Daggerfall's street population is out only by day (Daggerfall Unity PopulationManager: WorldTime IsDay, 6:00 to
18:00). Every wanderer in every town has Marker as its enable parent; this enables it at dawn and disables it at dusk,
checking every few game minutes.}

ObjectReference Property Marker Auto
GlobalVariable Property GameHour Auto
Float Property DawnHour = 6.0 Auto
Float Property DuskHour = 18.0 Auto

Event OnInit()
  Check()
EndEvent

Event OnUpdateGameTime()
  Check()
EndEvent

Function Check()
  If Marker && GameHour
    Float hour = GameHour.GetValue()
    Bool day = hour >= DawnHour && hour < DuskHour
    If day && Marker.IsDisabled()
      Marker.Enable()
    ElseIf !day && !Marker.IsDisabled()
      Marker.Disable()
    EndIf
  EndIf
  RegisterForSingleUpdateGameTime(0.1)
EndFunction
