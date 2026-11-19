ScriptName TMDfVampirism extends ActiveMagicEffect
{Daggerfall's vampirism on the player (Daggerfall Unity VampirismEffect, MIT):
- +20 to every attribute but Intelligence (the Anthotis get that too); +30 to Jumping (Acrobatics), Running (Athletics),
  Stealth (Sneak), Critical Strike, Climbing and Hand-to-Hand, for as long as it lasts.
- Immune to disease (Skyrim's resistance at 100 on the curse itself; Daggerfall's diseases shed here) and to paralysis
  (Daggerfall's Paralysis dispelled; the spiders' and scorpions' hits don't cast it on a vampire).
- The clan's spells (SPELLS.STD): the ones every clan has and the clan's own, given on turning, taken on the cure.
- The sun: 12 points every four magic rounds outdoors by day (6:00 to 18:00). A holy place: 12 points every four rounds
  inside a temple.
- Feeding: any weapon hit on one of Daggerfall's actors (TMDfSenses.OnHit sets DFVampireFedDay); sated for a day, after
  which the vampire can't rest (TMDfSensesControl, TEXT.RSC 36).
- Becoming one cures everything else (Daggerfall's diseases).}

Spell Property Curse Auto
{This vampirism's own spell.}
Spell Property Paralysis Auto
{Daggerfall's Paralysis (SPELLS.STD 66).}
FormList Property Diseases Auto
{Daggerfall's 17 diseases (cured on becoming one, shed while one).}
GlobalVariable Property VampireClan Auto
{DFVampireClan: the clan's faction (150 Vraseth .. 158 Selenu).}
GlobalVariable Property FedDay Auto
{DFVampireFedDay: the game day of the last feeding.}
Spell[] Property ClanSpells Auto
{The clans' spells, with ClanOf: 0 every clan, else the clan's faction.}
Int[] Property ClanOf Auto
Quest Property Machine Auto
{TMDfQuestMachine: the building the player is in (a temple).}
Int Property AnthotisClan = 157 AutoReadOnly
Int Property TempleType = 14 AutoReadOnly
Float Property Round = 5.0 AutoReadOnly          ; a magic round, in real seconds
Int Property DamageEvery = 4 AutoReadOnly         ; rounds
Float Property Damage = 12.0 AutoReadOnly
Int Property StatMod = 20 AutoReadOnly
Int Property SkillMod = 30 AutoReadOnly

Actor player
Bool applied = False
Bool spellsGiven = False
Int rounds = 0
Cell lastCell
Bool inTemple = False

Event OnEffectStart(Actor akTarget, Actor akCaster)
  player = akTarget
  If player != Game.GetPlayer()
    Return
  EndIf
  ; Our transformation is complete: everything else cured.
  ShedDiseases()
  Advantages(1)
  Spells(True)
  RegisterForSingleUpdate(Round)
EndEvent

Int Function Clan()
  If VampireClan
    Return VampireClan.GetValueInt()
  EndIf
  Return 0
EndFunction

; ApplyVampireAdvantages (sign 1) and their end (-1).
Function Advantages(Int sign)
  If (sign > 0) == applied
    Return
  EndIf
  applied = sign > 0
  Quest q = Quest.GetQuest("TMProgression")
  If q
    TMPassives passives = q as TMPassives
    If passives
      Int a = 0
      While a < 8
        If a != 1 || Clan() == AnthotisClan
          passives.ModAttribute(a, sign * StatMod)
        EndIf
        a += 1
      EndWhile
    EndIf
    TMSkills skills = q as TMSkills
    If skills
      skills.ModTempBonus("Acrobatics", sign * SkillMod)
      skills.ModTempBonus("Athletics", sign * SkillMod)
      skills.ModTempBonus("CriticalStrike", sign * SkillMod)
      skills.ModTempBonus("Climbing", sign * SkillMod)
      skills.ModTempBonus("HandToHand", sign * SkillMod)
    EndIf
  EndIf
  player.ModActorValue("Sneak", (sign * SkillMod) as Float)
EndFunction

; AssignVampireSpells: the clan's spells given, or taken back.
Function Spells(Bool give)
  If give == spellsGiven || !ClanSpells || !ClanOf
    Return
  EndIf
  spellsGiven = give
  Int clan = Clan()
  Int i = 0
  While i < ClanSpells.Length && i < ClanOf.Length
    Spell s = ClanSpells[i]
    If s && (ClanOf[i] == 0 || ClanOf[i] == clan)
      If give
        player.AddSpell(s, False)
      Else
        player.RemoveSpell(s)
      EndIf
    EndIf
    i += 1
  EndWhile
EndFunction

Function ShedDiseases()
  If !Diseases
    Return
  EndIf
  Int d = 0
  While d < Diseases.GetSize()
    Spell s = Diseases.GetAt(d) as Spell
    If s && player.HasSpell(s)
      player.RemoveSpell(s)
    EndIf
    d += 1
  EndWhile
EndFunction

Bool Function IsDay()
  GlobalVariable hour = Game.GetFormFromFile(0x38, "Skyrim.esm") as GlobalVariable
  If !hour
    Return True
  EndIf
  Float h = hour.GetValue()
  Return h >= 6.0 && h < 18.0
EndFunction

; The building the player is in, looked up when the cell changes (TMDfQuestMachine.SiteOfCell).
Function PlaceCheck()
  Cell c = player.GetParentCell()
  If c == lastCell
    Return
  EndIf
  lastCell = c
  inTemple = False
  If !player.IsInInterior()
    Return
  EndIf
  TMDfQuestMachine m = Machine as TMDfQuestMachine
  If m
    Int site = m.SiteOfCell(c)
    inTemple = site >= 0 && m.SiteKind(site) == 0 && m.SiteBuildingType(site) == TempleType
  EndIf
EndFunction

; MagicRound and ConstantEffect.
Event OnUpdate()
  If !player
    Return
  EndIf
  If player.IsDead()
    RegisterForSingleUpdate(Round)
    Return
  EndIf
  ; Immunities: Daggerfall's diseases shed, its paralysis dispelled.
  ShedDiseases()
  If Paralysis && player.HasMagicEffect(Paralysis.GetNthEffectMagicEffect(0))
    player.DispelSpell(Paralysis)
  EndIf
  rounds += 1
  If rounds % DamageEvery == 0
    PlaceCheck()
    ; DamageSunlight: outdoors by day.
    If !player.IsInInterior() && IsDay()
      player.DamageActorValue("Health", Damage)
    EndIf
    ; DamageHolyPlace: inside a temple.
    If inTemple
      player.DamageActorValue("Health", Damage)
    EndIf
  EndIf
  RegisterForSingleUpdate(Round)
EndEvent

; Cured (TMDfQuestMachine.CurePlayer): the advantages and the spells taken back, the clan forgotten.
Event OnEffectFinish(Actor akTarget, Actor akCaster)
  UnregisterForUpdate()
  If player
    Advantages(-1)
    Spells(False)
  EndIf
  If VampireClan
    VampireClan.SetValueInt(0)
  EndIf
EndEvent
