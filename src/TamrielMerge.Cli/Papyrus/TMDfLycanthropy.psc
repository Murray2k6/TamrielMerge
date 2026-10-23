ScriptName TMDfLycanthropy extends ActiveMagicEffect
{Daggerfall's lycanthropy on the player (Daggerfall Unity LycanthropyEffect, MIT), on Skyrim's own beast form:
- Skyrim's werewolf: the beast form power (once a day, as Daggerfall's Morph Self), its immunity to disease, the
  Companions' beast blood (Skyrim's cure works). A wereboar changes into Skyrim's werewolf: Skyrim has no boar form.
- +40 Strength, Agility, Endurance and Speed; +30 to Swimming, Running (Athletics), Stealth (Sneak), Critical Strike,
  Climbing, Hand-to-Hand and Jumping (Acrobatics), for as long as it lasts. Every change heals fully.
- A full moon (Masser or Secunda by Daggerfall's calendar: (day of the year + year x 360 + 3, or - 1) mod 32 = 0) forces
  the change ("You dream, once again, of the moon..."), unless the Ring of Hircine is worn.
- An innocent must be killed once a month (Skyrim's Murders count); after a month without, "You feel the need to hunt
  the innocent..." every two minutes and the maximum health falls 24 a day, to 4 at least, until one is.
- Becoming one cures everything else (Daggerfall's diseases).}

Int Property Kind = 0 Auto
{0 werewolf, 1 wereboar.}
Spell Property Curse Auto
{This lycanthropy's own spell: ended when Skyrim's own cure (the Companions', Glenmoril's) takes the beast form.}
Spell Property BeastForm Auto
Spell Property Immunity Auto
Quest Property Housekeeping Auto
{Skyrim's CompanionsHousekeepingScript quest: the beast blood flag.}
Race Property BeastRace Auto
{WerewolfBeastRace: the player changed.}
Armor Property HircinesRing Auto
FormList Property Diseases Auto
{Daggerfall's 17 diseases (cured on becoming one).}
GlobalVariable Property GameYear Auto
GlobalVariable Property GameMonth Auto
GlobalVariable Property GameDay Auto

Float Property Round = 5.0 AutoReadOnly          ; a magic round, in real seconds
Float Property MonthMinutes = 43200.0 AutoReadOnly ; Daggerfall's 30-day month
Float Property NotifySeconds = 120.0 AutoReadOnly
Int Property StatMod = 40 AutoReadOnly
Int Property SkillMod = 30 AutoReadOnly

Actor player
Float lastKilled          ; game days
Int murders
Float healthTaken = 0.0
Float notifyAt = 0.0
Bool applied = False

Event OnEffectStart(Actor akTarget, Actor akCaster)
  player = akTarget
  If player != Game.GetPlayer()
    Return
  EndIf
  ; Skyrim's beast blood.
  If BeastForm
    player.AddSpell(BeastForm, False)
  EndIf
  If Immunity
    player.AddSpell(Immunity, False)
  EndIf
  CompanionsHousekeepingScript companions = Housekeeping as CompanionsHousekeepingScript
  If companions
    companions.PlayerHasBeastBlood = True
  EndIf
  player.SendLycanthropyStateChanged(True)
  ; Our transformation is complete: everything else cured.
  If Diseases
    Int d = 0
    While d < Diseases.GetSize()
      Spell s = Diseases.GetAt(d) as Spell
      If s && player.HasSpell(s)
        player.RemoveSpell(s)
      EndIf
      d += 1
    EndWhile
  EndIf
  Advantages(1)
  ; Sated on first start.
  lastKilled = Utility.GetCurrentGameTime()
  murders = Game.QueryStat("Murders")
  RegisterForSingleUpdate(Round)
EndEvent

; ApplyLycanthropeAdvantages (sign 1) and their end (-1).
Function Advantages(Int sign)
  If (sign > 0) == applied
    Return
  EndIf
  applied = sign > 0
  Quest q = Quest.GetQuest("TMProgression")
  If q
    TMPassives passives = q as TMPassives
    If passives
      passives.ModAttribute(0, sign * StatMod)   ; Strength
      passives.ModAttribute(3, sign * StatMod)   ; Agility
      passives.ModAttribute(5, sign * StatMod)   ; Endurance
      passives.ModAttribute(4, sign * StatMod)   ; Speed
    EndIf
    TMSkills skills = q as TMSkills
    If skills
      skills.ModTempBonus("Swimming", sign * SkillMod)
      skills.ModTempBonus("Athletics", sign * SkillMod)
      skills.ModTempBonus("CriticalStrike", sign * SkillMod)
      skills.ModTempBonus("Climbing", sign * SkillMod)
      skills.ModTempBonus("HandToHand", sign * SkillMod)
      skills.ModTempBonus("Acrobatics", sign * SkillMod)
    EndIf
  EndIf
  player.ModActorValue("Sneak", (sign * SkillMod) as Float)
EndFunction

Bool Function Transformed()
  Return BeastRace && player.GetRace() == BeastRace
EndFunction

; Daggerfall's lunar phases (DaggerfallDateTime.GetLunarPhase): full when the ratio is 0.
Bool Function FullMoon()
  If !GameYear || !GameMonth || !GameDay
    Return False
  EndIf
  Int dayOfYear = GameMonth.GetValueInt() * 30 + GameDay.GetValueInt()
  Int base = dayOfYear + GameYear.GetValueInt() * 360
  Return (base + 3) % 32 == 0 || (base - 1) % 32 == 0
EndFunction

; MagicRound and ConstantEffect.
Event OnUpdate()
  If !player || player.IsDead()
    Return
  EndIf
  ; Cured Skyrim's way: the beast form gone.
  If BeastForm && !player.HasSpell(BeastForm) && !Transformed()
    If Curse
      player.RemoveSpell(Curse)
    EndIf
    Return
  EndIf
  Bool ring = HircinesRing && player.IsEquipped(HircinesRing)
  ; ForceTransformDuringFullMoon.
  If FullMoon() && !ring && !Transformed() && BeastForm
    Debug.Notification("You dream, once again, of the moon...")
    BeastForm.Cast(player, player)
  EndIf
  ; An innocent killed: sated (KilledInnocent: Skyrim counts killing one who isn't hostile as a murder).
  Int now = Game.QueryStat("Murders")
  If now > murders
    murders = now
    lastKilled = Utility.GetCurrentGameTime()
    Restore()
  EndIf
  ; The need to kill: after a month without, unless the ring is worn.
  Float minutes = (Utility.GetCurrentGameTime() - lastKilled) * 1440.0
  If !ring && minutes > MonthMinutes
    Float real = Utility.GetCurrentRealTime()
    If real >= notifyAt || real < notifyAt - NotifySeconds
      Debug.Notification("You feel the need to hunt the innocent...")
      notifyAt = real + NotifySeconds
    EndIf
    ; The maximum health falls 24 a day past the month, to 4 at least.
    Float reduction = Math.Floor((minutes - MonthMinutes) * 24.0 / 1440.0 + 0.5)
    Float base = player.GetBaseActorValue("Health")
    If reduction > base - 4.0
      reduction = base - 4.0
    EndIf
    If reduction > healthTaken
      player.ModActorValue("Health", healthTaken - reduction)
      healthTaken = reduction
    EndIf
  Else
    Restore()
  EndIf
  RegisterForSingleUpdate(Round)
EndEvent

Function Restore()
  If healthTaken > 0.0
    player.ModActorValue("Health", healthTaken)
    healthTaken = 0.0
  EndIf
EndFunction

; Every change heals fully (MorphSelf).
Event OnRaceSwitchComplete()
  If player
    player.RestoreActorValue("Health", 100000.0)
  EndIf
EndEvent

; Cured (TMDfQuestMachine.CurePlayer): the advantages and the lost health back.
Event OnEffectFinish(Actor akTarget, Actor akCaster)
  UnregisterForUpdate()
  If player
    Restore()
    Advantages(-1)
  EndIf
EndEvent
