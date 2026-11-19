ScriptName TMObSunDamage extends ActiveMagicEffect
{Oblivion's Sun Damage effect (SUDG, Oblivion.esm 0018A0) on the Vampirism50/75/100 abilities (1, 4 and 8 points;
Vampirism25 has none): the magnitude in health a second outdoors in daylight. Oblivion's rule (UESP Oblivion:Vampirism):
from 6:00 to 20:00 outside, none indoors nor on the planes of Oblivion; the damage peaks at midday at twice the
magnitude (here 1 + sin over the daylight hours); cloud cover and being under water lessen it without stopping it
(halved here). The damage ignores armour and resistances, as Oblivion's did (Resist Magic 2 in the record, which a
vampire's abilities don't carry).}

FormList Property OblivionPlanes Auto
{The worldspaces of Oblivion's planes (OBOblivionPlanes): no sun there.}
Float Property DawnHour = 6.0 AutoReadOnly
Float Property DuskHour = 20.0 AutoReadOnly

Actor host
Float points

Event OnEffectStart(Actor akTarget, Actor akCaster)
  host = akTarget
  points = GetMagnitude()
  If points > 0.0
    RegisterForSingleUpdate(1.0)
  EndIf
EndEvent

Event OnUpdate()
  If !host
    Return
  EndIf
  If !host.IsDead()
    Float factor = Sunlight()
    If factor > 0.0
      host.DamageActorValue("Health", points * factor)
    EndIf
  EndIf
  RegisterForSingleUpdate(1.0)
EndEvent

; How much of the magnitude the sun does now: 0 indoors, at night or on a plane of Oblivion.
Float Function Sunlight()
  If host.IsInInterior()
    Return 0.0
  EndIf
  WorldSpace here = host.GetWorldSpace()
  If here && OblivionPlanes && OblivionPlanes.HasForm(here)
    Return 0.0
  EndIf
  GlobalVariable hour = Game.GetFormFromFile(0x38, "Skyrim.esm") as GlobalVariable
  If !hour
    Return 0.0
  EndIf
  Float h = hour.GetValue()
  If h < DawnHour || h >= DuskHour
    Return 0.0
  EndIf
  Float factor = 1.0 + Math.sin(180.0 * (h - DawnHour) / (DuskHour - DawnHour))
  Weather now = Weather.GetCurrentWeather()
  If now && now.GetClassification() >= 1
    factor *= 0.5
  EndIf
  If host.IsSwimming()
    factor *= 0.5
  EndIf
  Return factor
EndFunction

Event OnEffectFinish(Actor akTarget, Actor akCaster)
  UnregisterForUpdate()
EndEvent
