ScriptName TMMwSunDamage extends ActiveMagicEffect
{Morrowind's Sun Damage magic effect (MGEF 135; "vampire sun damage" carries 5 points of it): each second outdoors
the actor loses the effect's magnitude times how much sun there is (OpenMW spelleffects.cpp applyActorMagicEffect,
SunDamage: damage = magnitude * clamp(max(sunVisibility, fMagicSunBlockedMult) * sunRisen, 0, 1), per second of real
time; nothing in an interior). The sun risen (OpenMW weather.cpp getSunPercentage) is 0 before Morrowind.ini's Sunrise
Time and after Sunset Time + Sunset Duration, climbs over the Sunrise Duration, falls over the Sunset Duration, and is
1 in between; the sun's visibility is the weather's Glare View (Morrowind.ini: Clear and Cloudy 1, Foggy 0.25, the
rest 0; a Skyrim weather of its own by its kind). The properties are Morrowind's values, read when the plugin is built.}

GlobalVariable Property GameHour Auto
Float Property SunriseTime = 6.0 Auto
{Morrowind.ini [Weather] Sunrise Time.}
Float Property SunsetTime = 18.0 Auto
{Sunset Time.}
Float Property SunriseDuration = 2.0 Auto
{Sunrise Duration.}
Float Property SunsetDuration = 2.0 Auto
{Sunset Duration.}
Float Property BlockedMult = 0.5 Auto
{fMagicSunBlockedMult: the least of the sun that gets through any weather.}
Weather[] Property Weathers Auto
{Morrowind's weathers (MWWeatherList's order), with their Glare View.}
Float[] Property GlareView Auto
Float Property Tick = 1.0 AutoReadOnly

Actor host
Float magnitude
Float lastReal

Event OnEffectStart(Actor akTarget, Actor akCaster)
  host = akTarget
  magnitude = GetMagnitude()
  lastReal = Utility.GetCurrentRealTime()
  RegisterForSingleUpdate(Tick)
EndEvent

Event OnUpdate()
  If !host || host.IsDead()
    Return
  EndIf
  Float now = Utility.GetCurrentRealTime()
  Float seconds = now - lastReal
  lastReal = now
  ; A long pause (a menu, a load) isn't hours of sun.
  If seconds < 0.0
    seconds = 0.0
  ElseIf seconds > 5.0
    seconds = 5.0
  EndIf
  If !host.IsInInterior()
    Float scale = SunScale()
    If scale > 0.0 && seconds > 0.0
      host.DamageActorValue("Health", magnitude * scale * seconds)
    EndIf
  EndIf
  RegisterForSingleUpdate(Tick)
EndEvent

; getSunPercentage: 0 at night, 1 by day, the sunrise and sunset in between.
Float Function SunRisen()
  If !GameHour
    Return 1.0
  EndIf
  Float hour = GameHour.GetValue()
  Float nightEnd = SunriseTime
  Float dayStart = SunriseTime + SunriseDuration
  Float dayEnd = SunsetTime
  Float nightStart = SunsetTime + SunsetDuration
  If hour <= nightEnd || hour >= nightStart
    Return 0.0
  ElseIf hour <= dayStart
    Return (hour - nightEnd) / SunriseDuration
  ElseIf hour > dayEnd
    Return 1.0 - ((hour - dayEnd) / SunsetDuration)
  EndIf
  Return 1.0
EndFunction

; getSunVisibility: the weather's Glare View (a Skyrim weather: clear and cloudy skies 1, rain and snow 0, as
; Morrowind's rain and snow have).
Float Function SunVisibility()
  Weather w = Weather.GetCurrentWeather()
  If !w
    Return 1.0
  EndIf
  If Weathers && GlareView
    Int i = Weathers.Find(w)
    If i >= 0 && i < GlareView.Length
      Return GlareView[i]
    EndIf
  EndIf
  Int kind = w.GetClassification()
  If kind == 2 || kind == 3
    Return 0.0
  EndIf
  Return 1.0
EndFunction

Float Function SunScale()
  Float risen = SunRisen()
  If risen <= 0.0
    Return 0.0
  EndIf
  Float visible = SunVisibility()
  If visible < BlockedMult
    visible = BlockedMult
  EndIf
  Float scale = visible * risen
  If scale > 1.0
    scale = 1.0
  ElseIf scale < 0.0
    scale = 0.0
  EndIf
  Return scale
EndFunction

Event OnEffectFinish(Actor akTarget, Actor akCaster)
  UnregisterForUpdate()
EndEvent
