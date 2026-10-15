ScriptName TMMwState extends Quest
{What Morrowind keeps for its people that Skyrim's actors don't have, for the scripts TamrielMerge translates from
Morrowind (on the quest MWScriptState): the eight attributes (Strength, Intelligence, Willpower, Agility, Speed,
Endurance, Personality, Luck), the skills Athletics, Acrobatics and Unarmored, and some effects' magnitudes.
A stat is its base - the Morrowind record's (the translated script passes it), or for the player Morrowind's race
values, the standing stone's (Skyrim's are Morrowind's birthsigns) and growth by how the player plays - plus what the
scripts changed. Changing one changes what follows from it in Morrowind: Strength the carry weight, fatigue (stamina)
and health; Intelligence the magicka; Willpower and Agility the fatigue; Speed the running speed; Endurance the
fatigue and health.}

Race[] Property Races Auto
{Skyrim's races (and their vampire forms) of Morrowind's races.}
Int[] Property RaceRows Auto
{Each race's row in RaceMale and RaceFemale.}
Int[] Property RaceMale Auto
{Morrowind's race attributes, eight per row (Strength first).}
Int[] Property RaceFemale Auto
Spell[] Property Signs Auto
{The standing stones' abilities (Skyrim's standing stones are Morrowind's birthsigns).}
Int[] Property SignStats Auto
{What each birthsign adds to the eight attributes (eight per sign).}

Int Property StatCount = 25 AutoReadOnly
{Stats kept: 0-7 attributes, 8 Athletics, 9 Acrobatics, 10 Unarmored, 11-24 effects.}

Actor[] actors
Int nextSlot
; What scripts changed: per slot (an actor) a run of 25, four slots to an array.
Float[] d0
Float[] d1
Float[] d2
Float[] d3
Float[] d4
Float[] d5
Float[] d6
Float[] d7

Function Setup()
  If !actors
    actors = new Actor[32]
    d0 = new Float[100]
    d1 = new Float[100]
    d2 = new Float[100]
    d3 = new Float[100]
    d4 = new Float[100]
    d5 = new Float[100]
    d6 = new Float[100]
    d7 = new Float[100]
  EndIf
EndFunction

Float Function Delta(Int aiSlot, Int aiStat)
  Int flat = aiSlot * 25 + aiStat
  Int i = flat % 100
  Int part = flat / 100
  If part == 0
    Return d0[i]
  ElseIf part == 1
    Return d1[i]
  ElseIf part == 2
    Return d2[i]
  ElseIf part == 3
    Return d3[i]
  ElseIf part == 4
    Return d4[i]
  ElseIf part == 5
    Return d5[i]
  ElseIf part == 6
    Return d6[i]
  EndIf
  Return d7[i]
EndFunction

Function SetDelta(Int aiSlot, Int aiStat, Float afValue)
  Int flat = aiSlot * 25 + aiStat
  Int i = flat % 100
  Int part = flat / 100
  If part == 0
    d0[i] = afValue
  ElseIf part == 1
    d1[i] = afValue
  ElseIf part == 2
    d2[i] = afValue
  ElseIf part == 3
    d3[i] = afValue
  ElseIf part == 4
    d4[i] = afValue
  ElseIf part == 5
    d5[i] = afValue
  ElseIf part == 6
    d6[i] = afValue
  Else
    d7[i] = afValue
  EndIf
EndFunction

; The actor's slot of changes (made when asked: a free one, a dead actor's, else the oldest).
Int Function SlotOf(Actor a, Bool abMake)
  Setup()
  Int i = actors.Find(a)
  If i >= 0 || !abMake
    Return i
  EndIf
  i = actors.Find(None)
  If i < 0
    i = 0
    While i < actors.Length && !actors[i].IsDead()
      i += 1
    EndWhile
    If i >= actors.Length
      i = nextSlot
      nextSlot = (nextSlot + 1) % actors.Length
    EndIf
  EndIf
  actors[i] = a
  Int k = 0
  While k < StatCount
    SetDelta(i, k, 0.0)
    k += 1
  EndWhile
  Return i
EndFunction

; ---- the scripts' functions ----

; GetStrength and the rest: the base (aiBase from the record, -1 to reckon it) plus what scripts changed.
Float Function GetStat(Actor a, Int aiStat, Int aiBase)
  If !a || aiStat < 0 || aiStat >= StatCount
    Return 0.0
  EndIf
  Float v = BaseOf(a, aiStat, aiBase)
  Int slot = SlotOf(a, False)
  If slot >= 0
    v += Delta(slot, aiStat)
  EndIf
  If v < 0.0
    v = 0.0
  EndIf
  Return v
EndFunction

Function SetStat(Actor a, Int aiStat, Int aiBase, Float afValue)
  If !a || aiStat < 0 || aiStat >= StatCount
    Return
  EndIf
  If afValue < 0.0
    afValue = 0.0
  EndIf
  Float was = GetStat(a, aiStat, aiBase)
  Int slot = SlotOf(a, True)
  SetDelta(slot, aiStat, afValue - BaseOf(a, aiStat, aiBase))
  Follow(a, aiStat, was, afValue)
  If a == Game.GetPlayer() && PlayerStats && aiStat < PlayerStats.Length && PlayerStats[aiStat]
    PlayerStats[aiStat].SetValue(afValue)
  EndIf
EndFunction

Function ModStat(Actor a, Int aiStat, Int aiBase, Float afBy)
  SetStat(a, aiStat, aiBase, GetStat(a, aiStat, aiBase) + afBy)
EndFunction

; ---- what follows from the attributes ----

; Morrowind's fatigue is Strength + Willpower + Agility + Endurance, its magicka Intelligence (times the race's and
; sign's multiplier), its encumbrance 5 x Strength, its health (Strength + Endurance) / 2 at the start and a tenth of
; Endurance each level, its walking speed 100 + Speed.
Function Follow(Actor a, Int aiStat, Float afWas, Float afNow)
  Float d = afNow - afWas
  If d == 0.0 || aiStat > 5
    Return
  EndIf
  If aiStat == 0
    a.ModActorValue("CarryWeight", 5.0 * d)
    a.ModActorValue("Stamina", d)
    Health(a, d / 2.0)
  ElseIf aiStat == 1
    a.ModActorValue("Magicka", d)
  ElseIf aiStat == 2 || aiStat == 3
    a.ModActorValue("Stamina", d)
  ElseIf aiStat == 4
    If afWas > -99.0 && afNow > -99.0
      Float speed = a.GetActorValue("SpeedMult")
      a.ModActorValue("SpeedMult", speed * (100.0 + afNow) / (100.0 + afWas) - speed)
      ; Skyrim applies a new speed when the carried weight changes.
      a.ModActorValue("CarryWeight", 0.1)
      a.ModActorValue("CarryWeight", -0.1)
    EndIf
  ElseIf aiStat == 5
    a.ModActorValue("Stamina", d)
    Health(a, d / 2.0 + (MwLevel(a) - 1.0) * d / 10.0)
  EndIf
EndFunction

; Health changed, never below one point (a weakening script doesn't kill).
Function Health(Actor a, Float afBy)
  If afBy < 0.0
    Float spare = a.GetActorValue("Health") - 1.0
    If spare < 0.0
      spare = 0.0
    EndIf
    If -afBy > spare
      afBy = -spare
    EndIf
  EndIf
  a.ModActorValue("Health", afBy)
EndFunction

; Morrowind's level: the converter made people's levels half as much again (the player's is their own).
Float Function MwLevel(Actor a)
  If a == Game.GetPlayer()
    Return a.GetLevel()
  EndIf
  Float level = a.GetLevel() / 1.5
  If level < 1.0
    level = 1.0
  EndIf
  Return level
EndFunction

; ---- bases ----

Float Function BaseOf(Actor a, Int aiStat, Int aiBase)
  If aiBase >= 0
    Return aiBase
  ElseIf aiStat > 10
    Return 0.0
  ElseIf a == Game.GetPlayer()
    Return PlayerBase(a, aiStat)
  EndIf
  Return Estimate(a, aiStat)
EndFunction

; Morrowind's race value of an attribute (by the actor's race and sex), or -1 for a race Morrowind hasn't.
Int Function RaceBase(Actor a, Int aiStat)
  If aiStat > 7 || !Races || !RaceRows
    Return -1
  EndIf
  Int i = Races.Find(a.GetRace())
  If i < 0
    Return -1
  EndIf
  Int row = RaceRows[i] * 8 + aiStat
  ActorBase b = a.GetLeveledActorBase()
  If b && b.GetSex() == 1
    Return RaceFemale[row]
  EndIf
  Return RaceMale[row]
EndFunction

; Someone the converter has no record for (made by a script, another game's): their race's values and about a point
; a level, as Morrowind's auto-calculated people grow; skills as a middling person's of that level.
Float Function Estimate(Actor a, Int aiStat)
  Float level = MwLevel(a)
  Float v
  If aiStat < 8
    Int r = RaceBase(a, aiStat)
    If r < 0
      r = 40
    EndIf
    v = r + level - 1.0
  Else
    v = 10.0 + level * 1.5
  EndIf
  If v > 100.0
    v = 100.0
  EndIf
  Return v
EndFunction

; The player: Morrowind's race values, +10 to the two attributes they use most (Morrowind's class), growth by how
; they play (the Skyrim skills nearest to those each attribute governs), and their standing stone (birthsign).
; Reckoned every few seconds at most (it reads some forty of the player's values).
Float Function PlayerBase(Actor p, Int aiStat)
  Float now = Utility.GetCurrentRealTime()
  If !playerBase || now - playerAt > 5.0 || now < playerAt
    RefreshPlayer(p)
  EndIf
  Return playerBase[aiStat]
EndFunction

Float[] playerBase
Float playerAt

Function RefreshPlayer(Actor p)
  Float[] growth = new Float[8]
  Float[] values = new Float[11]
  Int k = 0
  While k < 8
    growth[k] = Growth(p, k)
    k += 1
  EndWhile
  ; The birthsign: the standing stone whose ability the player has.
  Int sign = -1
  Int i = 0
  While sign < 0 && Signs && i < Signs.Length
    If Signs[i] && p.HasSpell(Signs[i])
      sign = i
    EndIf
    i += 1
  EndWhile
  k = 0
  While k < 8
    Float v = RaceBase(p, k)
    If v < 0.0
      v = 40.0
    EndIf
    v += growth[k]
    ; The class's favoured attributes: the two that grew most (Luck is never one).
    If k < 7
      Int higher = 0
      Int j = 0
      While j < 7
        If j != k && growth[j] > growth[k]
          higher += 1
        EndIf
        j += 1
      EndWhile
      If higher < 2
        v += 10.0
      EndIf
    EndIf
    If v > 100.0
      v = 100.0
    EndIf
    If sign >= 0 && SignStats && sign * 8 + k < SignStats.Length
      v += SignStats[sign * 8 + k]
    EndIf
    values[k] = v
    k += 1
  EndWhile
  ; Athletics, Acrobatics, Unarmored: as a middling character of the player's level.
  Float skill = 30.0 + p.GetLevel()
  If skill > 100.0
    skill = 100.0
  EndIf
  values[8] = skill
  values[9] = skill
  values[10] = skill
  playerBase = values
  playerAt = Utility.GetCurrentRealTime()
EndFunction

; ---- the player's values for the dialogue ----

GlobalVariable[] Property PlayerStats Auto
{Globals holding the player's attributes, Athletics, Acrobatics and Unarmored (by the stats' numbers), which the
converted dialogue's filters on them read (Skyrim's conditions can't ask a script).}

Event OnInit()
  RegisterForSingleUpdate(5.0)
EndEvent

Event OnUpdate()
  If TMMwScript.InMorrowind()
    UpdatePlayerStats()
  EndIf
  RegisterForSingleUpdate(10.0)
EndEvent

Function UpdatePlayerStats()
  If !PlayerStats
    Return
  EndIf
  Actor p = Game.GetPlayer()
  Int k = 0
  While k < PlayerStats.Length && k < 11
    If PlayerStats[k]
      PlayerStats[k].SetValue(GetStat(p, k, -1))
    EndIf
    k += 1
  EndWhile
EndFunction

; Morrowind raises an attribute by the skills it governs; Skyrim's nearest skills stand in for them.
Float Function Growth(Actor p, Int aiStat)
  If aiStat == 0
    Return Skills(p, "TwoHanded", "OneHanded", "Smithing", "")
  ElseIf aiStat == 1
    Return Skills(p, "Alchemy", "Conjuration", "Enchanting", "Lockpicking")
  ElseIf aiStat == 2
    Return Skills(p, "Alteration", "Destruction", "Restoration", "")
  ElseIf aiStat == 3
    Return Skills(p, "Block", "LightArmor", "Marksman", "Sneak")
  ElseIf aiStat == 4
    Return Skills(p, "OneHanded", "Pickpocket", "", "")
  ElseIf aiStat == 5
    Return Skills(p, "HeavyArmor", "TwoHanded", "", "")
  ElseIf aiStat == 6
    Return Skills(p, "Illusion", "Speechcraft", "", "")
  EndIf
  ; Luck rises only as the player chooses at a level: a point every few levels.
  Return (p.GetLevel() - 1) / 3.0
EndFunction

Float Function Skills(Actor p, String a1, String a2, String a3, String a4)
  Float sum = p.GetBaseActorValue(a1) + p.GetBaseActorValue(a2) - 30.0
  Float n = 2.0
  If a3 != ""
    sum += p.GetBaseActorValue(a3) - 15.0
    n += 1.0
  EndIf
  If a4 != ""
    sum += p.GetBaseActorValue(a4) - 15.0
    n += 1.0
  EndIf
  Float g = sum / n * 0.6
  If g < 0.0
    g = 0.0
  EndIf
  Return g
EndFunction
