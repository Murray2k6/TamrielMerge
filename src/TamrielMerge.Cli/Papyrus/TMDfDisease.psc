ScriptName TMDfDisease extends ActiveMagicEffect
{One of Daggerfall's 17 diseases on the player (Daggerfall Unity DiseaseEffect, MIT; the table is FALL.EXE's, DfDiseases):
the day it is caught it incubates; from the next day on, each day passed rolls its damage (MinDamage to MaxDamage) and
takes the roll times each stat's factor off Strength, Intelligence, Willpower, Agility, Endurance, Personality, Speed and
Luck (the attributes of the levelling, TMPassives, given back when the disease ends), health, stamina (Daggerfall's
fatigue) and magicka, then says "You feel somewhat bad.". A disease with days of symptoms (DaysMin-DaysMax) ends after
them; one without (255) lasts until cured. Caught below level 2 it doesn't take. A Skyrim disease: Skyrim's cures end it.}

Int[] Property Factors Auto
{STR INT WIL AGI END PER SPD LUC HEA FAT SPL.}
Int Property MinDamage Auto
Int Property MaxDamage Auto
Int Property DaysMin = 255 Auto
Int Property DaysMax = 255 Auto
Spell Property Disease Auto
{The disease spell itself, taken off the player when its days are over.}

Int Property Never = 255 AutoReadOnly

Actor host
Int[] drained
Int lastDay = -1
Int daysLeft = -1
Bool ended = False

; Daggerfall Unity's stat order onto the levelling's attributes (Strength, Intelligence, Willpower, Agility, Speed,
; Endurance, Personality, Luck).
Int Function Attribute(Int stat)
  If stat == 4
    Return 5
  ElseIf stat == 5
    Return 6
  ElseIf stat == 6
    Return 4
  EndIf
  Return stat
EndFunction

Int Function Today()
  Return Math.Floor(Utility.GetCurrentGameTime())
EndFunction

Event OnEffectStart(Actor akTarget, Actor akCaster)
  host = akTarget
  ; Only the player catches one, and not at level 1 (DiseaseEffect.Start).
  If host != Game.GetPlayer() || host.GetLevel() < 2
    End()
    Return
  EndIf
  drained = new Int[8]
  lastDay = Today()
  If DaysMin != Never
    daysLeft = Utility.RandomInt(DaysMin, DaysMax)
  EndIf
  RegisterForUpdateGameTime(1.0)
EndEvent

; UpdateDisease: each new day, its effects once for every day passed (a long rest or a journey can pass several).
Event OnUpdateGameTime()
  If ended || !host
    Return
  EndIf
  Int today = Today()
  Int past = today - lastDay
  If past <= 0
    Return
  EndIf
  Int k = 0
  While k < past
    Daily()
    k += 1
  EndWhile
  lastDay = today
  Debug.Notification("You feel somewhat bad.")
  If DaysMin != Never
    daysLeft -= past
    If daysLeft <= 0
      End()
    EndIf
  EndIf
EndEvent

; IncrementDailyDiseaseEffects.
Function Daily()
  Int damage = Utility.RandomInt(MinDamage, MaxDamage)
  TMPassives passives = None
  Quest q = Quest.GetQuest("TMProgression")
  If q
    passives = q as TMPassives
  EndIf
  Int s = 0
  While s < 8
    Int by = Factors[s] * damage
    If by > 0 && passives
      Int a = Attribute(s)
      passives.ModAttribute(a, -by)
      drained[a] = drained[a] + by
    EndIf
    s += 1
  EndWhile
  If Factors[8] > 0
    host.DamageActorValue("Health", (Factors[8] * damage) as Float)
  EndIf
  If Factors[9] > 0
    host.DamageActorValue("Stamina", (Factors[9] * damage) as Float)
  EndIf
  If Factors[10] > 0
    host.DamageActorValue("Magicka", (Factors[10] * damage) as Float)
  EndIf
EndFunction

; EndDisease: its course run (or caught too early); the effect ends with the spell.
Function End()
  If ended
    Return
  EndIf
  ended = True
  UnregisterForUpdateGameTime()
  If host && Disease
    host.RemoveSpell(Disease)
  EndIf
EndFunction

; Ended or cured: the attributes it took are given back (its stat changes go with the effect).
Event OnEffectFinish(Actor akTarget, Actor akCaster)
  ended = True
  UnregisterForUpdateGameTime()
  If !drained
    Return
  EndIf
  Quest q = Quest.GetQuest("TMProgression")
  TMPassives passives = None
  If q
    passives = q as TMPassives
  EndIf
  Int a = 0
  While a < 8
    If drained[a] > 0 && passives
      passives.ModAttribute(a, drained[a])
    EndIf
    drained[a] = 0
    a += 1
  EndWhile
EndEvent
