ScriptName TMMwState extends Quest
{What Morrowind keeps for its people that Skyrim's actors don't have, for the scripts TamrielMerge translates from
Morrowind (on the quest MWScriptState): the eight attributes (Strength, Intelligence, Willpower, Agility, Speed,
Endurance, Personality, Luck), Morrowind's 27 skills, and some effects' magnitudes (user, 2026-10-23: "Keep original
stats"). A stat is its base - the Morrowind record's (the translated script passes it) - plus what the scripts changed.
The player's are where they live: a skill Skyrim has as its own (Block, Destruction, Sneak...) Skyrim's, one the
progression adds (Mysticism, Athletics, Spear...) or keeps hidden (Short and Long Blade, Axe, Blunt Weapon) the
progression's (TMSkills on TMProgression), and a script's change goes there; the attributes the progression's (its
Mundus stones' levelling, TMPassives) or, without it, reckoned from Morrowind's race values, the standing stone's and
growth by how the player plays. Changing one changes what follows from it in Morrowind: Strength the carry weight, fatigue (stamina)
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

Int Property StatCount = 49 AutoReadOnly
{Stats kept: 0-7 attributes, 8-34 Morrowind's skills (8 + its index), 35-48 effects.}
String[] Property PlayerSkillAv Auto
{Each Morrowind skill's Skyrim actor value where Skyrim has it as its own ("" otherwise).}
String[] Property PlayerSkillId Auto
{Each Morrowind skill's progression skill (TMSkills id) where the progression keeps it ("" otherwise).}

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
Float[] d8
Float[] d9
Float[] d10
Float[] d11
Float[] d12
Float[] d13
Float[] d14
Float[] d15

; 32 slots of 49 stats: 1,568 changes in sixteen arrays of 100 (Papyrus makes none longer than 128).
Function Setup()
  If !actors
    actors = new Actor[32]
  EndIf
  If !d15
    d0 = new Float[100]
    d1 = new Float[100]
    d2 = new Float[100]
    d3 = new Float[100]
    d4 = new Float[100]
    d5 = new Float[100]
    d6 = new Float[100]
    d7 = new Float[100]
    d8 = new Float[100]
    d9 = new Float[100]
    d10 = new Float[100]
    d11 = new Float[100]
    d12 = new Float[100]
    d13 = new Float[100]
    d14 = new Float[100]
    d15 = new Float[100]
  EndIf
EndFunction

Float[] Function Page(Int aiPart)
  If aiPart == 0
    Return d0
  ElseIf aiPart == 1
    Return d1
  ElseIf aiPart == 2
    Return d2
  ElseIf aiPart == 3
    Return d3
  ElseIf aiPart == 4
    Return d4
  ElseIf aiPart == 5
    Return d5
  ElseIf aiPart == 6
    Return d6
  ElseIf aiPart == 7
    Return d7
  ElseIf aiPart == 8
    Return d8
  ElseIf aiPart == 9
    Return d9
  ElseIf aiPart == 10
    Return d10
  ElseIf aiPart == 11
    Return d11
  ElseIf aiPart == 12
    Return d12
  ElseIf aiPart == 13
    Return d13
  ElseIf aiPart == 14
    Return d14
  EndIf
  Return d15
EndFunction

Float Function Delta(Int aiSlot, Int aiStat)
  Int flat = aiSlot * StatCount + aiStat
  Return Page(flat / 100)[flat % 100]
EndFunction

; Papyrus arrays are shared by reference: the page's element is set in place.
Function SetDelta(Int aiSlot, Int aiStat, Float afValue)
  Int flat = aiSlot * StatCount + aiStat
  Float[] page = Page(flat / 100)
  page[flat % 100] = afValue
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
  ; The player's skills are kept where they live: the change goes there.
  If aiStat >= 8 && aiStat < 35 && a == Game.GetPlayer()
    SetPlayerSkill(a, aiStat - 8, afValue)
    If PlayerStats && aiStat < PlayerStats.Length && PlayerStats[aiStat]
      PlayerStats[aiStat].SetValue(afValue)
    EndIf
    Return
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

; GetHealthGetRatio and the like for a kept stat: its current value over its base.
Float Function GetRatio(Actor a, Int aiStat, Int aiBase)
  Float base = BaseOf(a, aiStat, aiBase)
  If base <= 0.0
    Return 1.0
  EndIf
  Return GetStat(a, aiStat, aiBase) / base
EndFunction

; ---- the player's skills, where they live ----

TMSkills Function ProgressionSkills()
  Quest q = Quest.GetQuest("TMProgression")
  If q
    Return (q as TMSkills)
  EndIf
  Return None
EndFunction

Float Function PlayerSkill(Actor p, Int aiSkill)
  If PlayerSkillAv && aiSkill < PlayerSkillAv.Length && PlayerSkillAv[aiSkill] != ""
    Return p.GetActorValue(PlayerSkillAv[aiSkill])
  EndIf
  If PlayerSkillId && aiSkill < PlayerSkillId.Length && PlayerSkillId[aiSkill] != ""
    TMSkills skills = ProgressionSkills()
    If skills
      Int i = skills.Index(PlayerSkillId[aiSkill])
      If i >= 0
        Return skills.EffectiveLevel(i)
      EndIf
    EndIf
  EndIf
  ; Without the progression: as a middling character of the player's level.
  Float skill = 30.0 + p.GetLevel()
  If skill > 100.0
    skill = 100.0
  EndIf
  Return skill
EndFunction

Function SetPlayerSkill(Actor p, Int aiSkill, Float afValue)
  If PlayerSkillAv && aiSkill < PlayerSkillAv.Length && PlayerSkillAv[aiSkill] != ""
    ; Morrowind's Set<Skill> sets the base, with what fortifies it on top.
    Float fortified = p.GetActorValue(PlayerSkillAv[aiSkill]) - p.GetBaseActorValue(PlayerSkillAv[aiSkill])
    p.SetActorValue(PlayerSkillAv[aiSkill], afValue - fortified)
    Return
  EndIf
  If PlayerSkillId && aiSkill < PlayerSkillId.Length && PlayerSkillId[aiSkill] != ""
    TMSkills skills = ProgressionSkills()
    If skills
      Int i = skills.Index(PlayerSkillId[aiSkill])
      If i >= 0
        skills.SetEffectiveLevel(i, afValue as Int)
      EndIf
    EndIf
  EndIf
EndFunction

; ---- what follows from the attributes ----

; Morrowind's fatigue is Strength + Willpower + Agility + Endurance, its magicka Intelligence (times the race's and
; sign's multiplier), its encumbrance 5 x Strength, its health (Strength + Endurance) / 2 at the start and a tenth of
; Endurance each level, its walking speed 100 + Speed.
Function Follow(Actor a, Int aiStat, Float afWas, Float afNow)
  Float d = afNow - afWas
  If d == 0.0
    Return
  EndIf
  ; A skill of someone else's: its Skyrim counterpart moves with it, so the change shows in how they fight and cast.
  If aiStat >= 8 && aiStat < 35
    If PlayerSkillAv && aiStat - 8 < PlayerSkillAv.Length && PlayerSkillAv[aiStat - 8] != ""
      a.ModActorValue(PlayerSkillAv[aiStat - 8], d)
    ElseIf aiStat == 15
      a.ModActorValue("TwoHanded", d)   ; Spear: Skyrim's spears are two-handed
    ElseIf aiStat == 12 || aiStat == 13 || aiStat == 14 || aiStat == 30
      ; Blunt Weapon, Long Blade, Axe, Short Blade: Skyrim's One- and Two-handed.
      a.ModActorValue("OneHanded", d)
      a.ModActorValue("TwoHanded", d)
    EndIf
    Return
  EndIf
  If aiStat > 5
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
  If aiStat >= 8 && aiStat < 35 && a == Game.GetPlayer()
    Return PlayerSkill(a, aiStat - 8)
  ElseIf aiBase >= 0
    Return aiBase
  ElseIf aiStat >= 35
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
  ; The progression's attributes (its Mundus stones' levelling), when it is there.
  If aiStat < 8
    Quest q = Quest.GetQuest("TMProgression")
    TMPassives passives = None
    If q
      passives = (q as TMPassives)
    EndIf
    If passives
      Int v = passives.AttributeValue(aiStat)
      If v > 0
        Return v
      EndIf
    EndIf
  EndIf
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

; ---- message boxes: shown without the script waiting, the button read once (OpenMW's window manager) ----

Int lastButton = -1

; MessageBox with buttons: Morrowind's script goes on while it shows; the box is shown on a thread of this quest's
; (a mod event), and the button chosen kept for GetButtonPressed, whichever script asks.
Function ShowMessage(Message akMessage, Float a1 = 0.0, Float a2 = 0.0, Float a3 = 0.0, Float a4 = 0.0, Float a5 = 0.0, Float a6 = 0.0, Float a7 = 0.0, Float a8 = 0.0, Float a9 = 0.0)
  If !akMessage
    Return
  EndIf
  lastButton = -1
  RegisterForModEvent("TMMwShowMessage", "OnTMMwShowMessage")
  Int handle = ModEvent.Create("TMMwShowMessage")
  If !handle
    lastButton = akMessage.Show(a1, a2, a3, a4, a5, a6, a7, a8, a9)
    Return
  EndIf
  ModEvent.PushForm(handle, akMessage)
  ModEvent.PushFloat(handle, a1)
  ModEvent.PushFloat(handle, a2)
  ModEvent.PushFloat(handle, a3)
  ModEvent.PushFloat(handle, a4)
  ModEvent.PushFloat(handle, a5)
  ModEvent.PushFloat(handle, a6)
  ModEvent.PushFloat(handle, a7)
  ModEvent.PushFloat(handle, a8)
  ModEvent.PushFloat(handle, a9)
  ModEvent.Send(handle)
EndFunction

Event OnTMMwShowMessage(Form akMessage, Float a1, Float a2, Float a3, Float a4, Float a5, Float a6, Float a7, Float a8, Float a9)
  Message m = akMessage as Message
  If m
    lastButton = m.Show(a1, a2, a3, a4, a5, a6, a7, a8, a9)
  EndIf
EndEvent

; GetButtonPressed: the last button chosen, once (-1 until one is).
Int Function TakeButton()
  Int b = lastButton
  lastButton = -1
  Return b
EndFunction

; ---- factions: rank requirements and reactions (OpenMW filter.cpp, npcstats.cpp, dialoguemanagerimp.cpp) ----

Faction[] Property MwFactions Auto
{Morrowind's factions, in the order of FactionRanks and FactionReactions.}

; The actor's Morrowind faction (Morrowind gives a person one): the one of its factions that is Morrowind's.
Faction Function MwFactionOf(Actor a)
  If !a || !MwFactions
    Return None
  EndIf
  Faction[] all = a.GetFactions(-128, 127)
  Int i = 0
  While all && i < all.Length
    If MwFactions.Find(all[i]) >= 0
      Return all[i]
    EndIf
    i += 1
  EndWhile
  Return None
EndFunction
String Property FactionRanks Auto
{Per faction, 59 numbers: its two attributes; per rank the two attribute levels, the primary and favoured skill levels and
the reputation it needs; its seven skills (Morrowind's skill numbers, -1 none). Factions ";" apart.}
String Property FactionReactions Auto
{"i,j,v;": faction i's reaction to faction j in its record.}
GlobalVariable[] Property FactionRep Auto
{The player's reputation with each faction (Get/Mod/SetPCFacRep).}
GlobalVariable[] Property RankReq Auto
GlobalVariable[] Property ReactLow Auto
GlobalVariable[] Property ReactHigh Auto

Int[] rankData
Int[] reactionBase
Int[] reactionOverride
Bool[] overridden

; The numbers of a text of numbers apart by "," and ";", into an array of n (SKSE's, which may be longer than 128).
Int[] Function ParseInts(String asText, Int n)
  Int[] values = Utility.CreateIntArray(n)
  Int count = 0
  Int value = 0
  Bool negative = False
  Bool inNumber = False
  Int len = StringUtil.GetLength(asText)
  Int i = 0
  While i < len && count < n
    String ch = StringUtil.GetNthChar(asText, i)
    If ch == "-"
      negative = True
    ElseIf ch == "," || ch == ";"
      If inNumber
        If negative
          value = -value
        EndIf
        values[count] = value
        count += 1
      EndIf
      value = 0
      negative = False
      inNumber = False
    Else
      value = value * 10 + StringUtil.AsOrd(ch) - 48
      inNumber = True
    EndIf
    i += 1
  EndWhile
  Return values
EndFunction

Function SetupFactions()
  If !MwFactions || rankData
    Return
  EndIf
  Int n = MwFactions.Length
  rankData = ParseInts(FactionRanks, n * 59)
  reactionBase = Utility.CreateIntArray(n * n)
  reactionOverride = Utility.CreateIntArray(n * n)
  overridden = Utility.CreateBoolArray(n * n)
  ; The record's reactions, three numbers each.
  Int count = 1
  Int len = StringUtil.GetLength(FactionReactions)
  Int k = 0
  While k < len
    If StringUtil.GetNthChar(FactionReactions, k) == ";"
      count += 1
    EndIf
    k += 1
  EndWhile
  Int[] r = ParseInts(FactionReactions, count * 3)
  k = 0
  While k + 2 < r.Length
    If r[k] >= 0 && r[k] < n && r[k + 1] >= 0 && r[k + 1] < n
      reactionBase[r[k] * n + r[k + 1]] = r[k + 2]
    EndIf
    k += 3
  EndWhile
EndFunction

Int Function FactionIndex(Faction akFaction)
  If !MwFactions
    Return -1
  EndIf
  Return MwFactions.Find(akFaction)
EndFunction

; getFactionReaction: a script's change (which replaces the record's), else the record's, else 0.
Int Function Reaction(Faction akA, Faction akB)
  SetupFactions()
  Int a = FactionIndex(akA)
  Int b = FactionIndex(akB)
  If a < 0 || b < 0
    Return 0
  EndIf
  Int at = a * MwFactions.Length + b
  If overridden[at]
    Return reactionOverride[at]
  EndIf
  Return reactionBase[at]
EndFunction

; setFactionReaction / modFactionReaction (no clamping, as Morrowind's), and Skyrim's relation from it for its AI.
Function SetReaction(Faction akA, Faction akB, Int aiValue)
  SetupFactions()
  Int a = FactionIndex(akA)
  Int b = FactionIndex(akB)
  If a < 0 || b < 0
    Return
  EndIf
  Int at = a * MwFactions.Length + b
  reactionOverride[at] = aiValue
  overridden[at] = True
  TMMwScript.SetReaction(akA, akB, aiValue)
EndFunction

; The player's Morrowind skill's base (Morrowind compares bases for a rank).
Float Function PlayerSkillBase(Actor p, Int aiSkill)
  If PlayerSkillAv && aiSkill < PlayerSkillAv.Length && PlayerSkillAv[aiSkill] != ""
    Return p.GetBaseActorValue(PlayerSkillAv[aiSkill])
  EndIf
  Return PlayerSkill(p, aiSkill)
EndFunction

; hasSkillsForRank and the faction's attributes (bit 1), its reputation (bit 2), for the player's next rank in faction f.
Int Function RankRequirement(Actor p, Int f)
  Faction fac = MwFactions[f]
  If !fac || !p.IsInFaction(fac)
    Return 0
  EndIf
  Int rank = p.GetFactionRank(fac)
  If rank >= 9
    Return 0
  EndIf
  Int next = rank + 1
  If next < 0
    next = 0
  EndIf
  Int at = f * 59
  Int rankAt = at + 2 + next * 5
  Bool skillsMet = True
  ; The faction's skills, best first: the best at least the primary level, the next two at least the favoured one.
  Float[] best = new Float[7]
  Int n = 0
  Int k = 0
  While k < 7
    Int skill = rankData[at + 52 + k]
    If skill >= 0 && skill < 27
      best[n] = PlayerSkillBase(p, skill)
      n += 1
    EndIf
    k += 1
  EndWhile
  If n > 0
    Float first = TopOf(best, n, 0)
    If first < rankData[rankAt + 2]
      skillsMet = False
    ElseIf n >= 2 && TopOf(best, n, 1) < rankData[rankAt + 3]
      skillsMet = False
    ElseIf n >= 3 && TopOf(best, n, 2) < rankData[rankAt + 3]
      skillsMet = False
    EndIf
  EndIf
  Int a1 = rankData[at]
  Int a2 = rankData[at + 1]
  If skillsMet && a1 >= 0 && a1 < 8 && GetStat(p, a1, -1) < rankData[rankAt]
    skillsMet = False
  EndIf
  If skillsMet && a2 >= 0 && a2 < 8 && GetStat(p, a2, -1) < rankData[rankAt + 1]
    skillsMet = False
  EndIf
  Int result = 0
  If skillsMet
    result += 1
  EndIf
  If FactionRep && f < FactionRep.Length && FactionRep[f] && FactionRep[f].GetValue() >= rankData[rankAt + 4]
    result += 2
  EndIf
  Return result
EndFunction

; The k-th highest (0 the highest) of the first n values.
Float Function TopOf(Float[] values, Int n, Int k)
  Float[] sorted = new Float[7]
  Int i = 0
  While i < n
    sorted[i] = values[i]
    i += 1
  EndWhile
  Int pass = 0
  While pass <= k
    Int pick = pass
    Int j = pass + 1
    While j < n
      If sorted[j] > sorted[pick]
        pick = j
      EndIf
      j += 1
    EndWhile
    Float t = sorted[pass]
    sorted[pass] = sorted[pick]
    sorted[pick] = t
    pass += 1
  EndWhile
  Return sorted[k]
EndFunction

; Each faction's globals for the dialogue: RankRequirement, and FacReactionLowest / Highest (its reactions to the
; player's factions, from 0).
Function UpdateFactions(Actor p)
  If !MwFactions
    Return
  EndIf
  SetupFactions()
  Int n = MwFactions.Length
  ; The player's factions, once.
  Int[] mine = Utility.CreateIntArray(n)
  Int count = 0
  Int g0 = 0
  While g0 < n
    If MwFactions[g0] && p.IsInFaction(MwFactions[g0])
      mine[count] = g0
      count += 1
    EndIf
    g0 += 1
  EndWhile
  Int f = 0
  While f < n
    If RankReq && f < RankReq.Length && RankReq[f]
      RankReq[f].SetValue(RankRequirement(p, f))
    EndIf
    Int low = 0
    Int high = 0
    Int m = 0
    While m < count
      Int r = Reaction(MwFactions[f], MwFactions[mine[m]])
      If r < low
        low = r
      EndIf
      If r > high
        high = r
      EndIf
      m += 1
    EndWhile
    If ReactLow && f < ReactLow.Length && ReactLow[f]
      ReactLow[f].SetValue(low)
    EndIf
    If ReactHigh && f < ReactHigh.Length && ReactHigh[f]
      ReactHigh[f].SetValue(high)
    EndIf
    f += 1
  EndWhile
EndFunction

; ---- the player's values for the dialogue ----

GlobalVariable[] Property PlayerStats Auto
{Globals holding the player's attributes, Athletics, Acrobatics and Unarmored (by the stats' numbers), which the
converted dialogue's filters on them read (Skyrim's conditions can't ask a script).}

Event OnInit()
  RegisterForSingleUpdate(5.0)
  RegisterForMenu("Dialogue Menu")
EndEvent

; Every two seconds, and as a conversation opens, so the dialogue's filters see the player's stats as they are.
Event OnUpdate()
  If TMMwScript.InMorrowind()
    UpdatePlayerStats()
  EndIf
  RegisterForMenu("Dialogue Menu")
  RegisterForSingleUpdate(2.0)
EndEvent

Event OnMenuOpen(String asMenuName)
  UpdatePlayerStats()
EndEvent

Function UpdatePlayerStats()
  If !PlayerStats
    Return
  EndIf
  Actor p = Game.GetPlayer()
  UpdateFactions(p)
  Int k = 0
  While k < PlayerStats.Length && k < 35
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
