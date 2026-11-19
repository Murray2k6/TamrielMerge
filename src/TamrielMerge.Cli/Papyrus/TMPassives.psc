ScriptName TMPassives extends Quest
{The passive web (docs/progression.md "Passive web"; PassiveWeb builds it), in place of the standing stones: small nodes
add to stats, notables and keystones are perks. The web starts at the player's birthsign: the standing stone they hold
(Skyrim's, which Morrowind's signs also give) or Oblivion's sign (TMObState); with neither (Daggerfall has no signs)
any sign may be the start. Taking the start is free; every other node costs a point (one per character level) and must
touch a node already taken; some notables and keystones need a rank in a converted game's faction (TMProgression's
faction registry, FactionGroup naming each faction's group). What is taken is kept here (an array, a node each); the
stats are one hidden ability whose magnitudes are the sums (SKSE SetNthEffectMagnitude, set again after loading) and
the notables' and keystones' perks are given or taken away. The Tamriel menu's Passives tab asks with mod event TM_Web
("state", "take:<node>", "reset") and gets WebState(...).}

Actor Property PlayerRef Auto
Int[] Property NodeKind Auto
{0 small, 1 notable, 2 keystone, 3 a sign's start.}
Int[] Property NodeStat Auto
Float[] Property NodeAmount Auto
Perk[] Property NodePerks Auto
Int[] Property NodeGate Auto
{The faction group a node needs (-1 none) and the rank in it (1 the first).}
Int[] Property NodeGateRank Auto
Int[] Property LinkA Auto
Int[] Property LinkB Auto
Int[] Property SignStart Auto
{Each of the 13 signs' start node: Warrior, Lady, Steed, Lord, Mage, Apprentice, Atronach, Ritual, Thief, Lover,
Shadow, Tower, Serpent.}
Spell[] Property SignStones Auto
Int[] Property FactionGroup Auto
Int[] Property ObSigns Auto
{Oblivion's signs in TMObState's order, as the 13.}
Int Property ObStateId Auto
Spell Property StatsAbility Auto

; ---- the games' levelling on the Mundus stones (GameStones; docs/progression.md) ----
String[] Property GameNames Auto
{The converted games with levelling, in order; the rest of these are by game.}
Int[] Property GameRule Auto
{0: three attributes picked at each level, each raised by its multiplier (Morrowind, Oblivion); 1: points to share out
(Daggerfall).}
Int[] Property GameLevelUp Auto
{11 a game: the multiplier for 0..10 (or more) rises of the skills an attribute governs.}
Int[] Property GameLuckGain Auto
Int[] Property GamePoolMin Auto
Int[] Property GamePoolMax Auto
Int[] Property GameNeutral Auto
{The game's average attribute (40, Daggerfall 50): only the difference from it does anything, so an average character
is Skyrim's own.}
Int[] Property GameHealthDf Auto
{1: Daggerfall's health each level (the class's roll); 0: by Endurance.}
Int[] Property GameFavoredBonus Auto
Int[] Property GameGoverning Auto
{For each game, the attribute governing each counted skill (Skyrim's 18, then the progression's).}
Int[] Property GameRaceAttributes Auto
{160 a game: 10 races (GameStones.Races order) x male, female x 8 attributes (0: the game has no table).}
Int Property DefaultGame = -1 Auto
{The game whose rule a character without a class follows (-1: none converted).}
Race[] Property RaceForms Auto
Int[] Property RaceFormIndex Auto
String[] Property SkyrimSkillAvs Auto
Int Property ProgressionSkillCount Auto
Perk[] Property ClassPerks Auto
Int[] Property ClassGame Auto
Int[] Property ClassFavored Auto
{2 a class: its favoured attributes (-1 none).}
Int[] Property ClassAttributes Auto
{8 a class: a Daggerfall class's own attributes (-1: the race's instead).}
Int[] Property ClassHitPoints Auto
FormList[] Property ClassForbidden Auto
{Each class's forbidden keywords (Daggerfall's forbidden weapons, armour and materials).}
String[] Property ClassForbiddenWords Auto
{"|"-separated: materials known by name (Daggerfall's mithril, adamantium, silver), "shield:light|heavy|any".}
Int[] Property ClassSunDamage Auto
Int[] Property ClassHolyDamage Auto
Float[] Property ClassSkillRate Auto
{ProgressionSkillCount a class: how fast each progression skill rises (TMSkills.SetClass).}
Int[] Property ClassSkillBonus Auto
String[] Property ClassNames Auto
Spell Property AttributeAbility Auto
{For each attribute effect an up and a down effect, then health up and down.}
Int[] Property AttributeEffectOf Auto
Float[] Property AttributeEffectPerPoint Auto
Int[] Property SignListSign Auto
{The owned games' versions of each sign: its web sign, and the plugin and FormID of the form list of its spells.}
String[] Property SignListPlugins Auto
Int[] Property SignListIds Auto
Keyword Property ArmorShield Auto
Keyword Property ArmorLight Auto
Keyword Property ArmorHeavy Auto
Keyword Property LocTypeTemple Auto
Int[] Property RaceListRace Auto
{The owned games' versions of each race (GameStones.Races index): the plugin and FormID of the form list of its spells.}
String[] Property RaceListPlugins Auto
Int[] Property RaceListIds Auto
Spell[] Property RaceSkillAbility Auto
{10 a game: each race's skill bonuses in that game (Skyrim's skills), given for the game whose rules apply.}
Int[] Property RaceSkillBonus Auto
{ProgressionSkillCount for each game and race: its bonuses to the progression's skills.}
Perk[] Property DfRacePerks Auto
{Daggerfall's racial abilities, a perk for each race (none for those without).}
Int[] Property DfRaceDivisor Auto
{Its weapon damage +level / this (0: none), the perk's entry DfRaceEntry.}
Int[] Property DfRaceEntry Auto

String Property MenuName = "CustomMenu" AutoReadOnly
Int Property Groups = 8 AutoReadOnly

Int[] taken
Int start = -1
Int spent = 0

Int playerClass = -1
; Each attribute's gains from the levels so far.
Int[] gains
Int lastLevel = 0
; Levels whose attribute gains are still to be chosen, and the next one's multipliers (or points).
Int pending = 0
Int[] pendingMult
Int pendingPool = 0
; The counted skills at the last level (their rises since are this level's).
Int[] snapshot
Float levelHealth = 0.0
Int signGiven = -1
Int raceGiven = -1
Bool wantCharacter = False

; At the start and after each load (TMProgression.Setup): the stats set again, the menu's event registered.
Function Setup()
  If !NodeKind || SKSE.GetVersion() <= 0
    Return
  EndIf
  If !taken
    taken = Utility.CreateIntArray(NodeKind.Length)
  ElseIf taken.Length != NodeKind.Length
    ; A new build changed the web: everything is given back.
    Reset()
  EndIf
  Apply()
  RegisterForModEvent("TM_Web", "OnWebMenu")
  ; The games' levelling: a class kept only while the build still has it, the attributes, the level checked.
  If !gains || gains.Length != 8
    gains = new Int[8]
  EndIf
  If !pendingMult || pendingMult.Length != 8
    pendingMult = new Int[8]
    ResetMultipliers()
  EndIf
  If !ClassPerks || playerClass >= ClassPerks.Length
    playerClass = -1
  EndIf
  ApplyClass()
  If !snapshot || snapshot.Length != SkyrimSkillAvs.Length + ProgressionSkillCount
    snapshot = Skills()
  EndIf
  If lastLevel <= 0
    lastLevel = PlayerRef.GetLevel()
  EndIf
  CheckLevel()
  ApplyAttributes()
  ; Skyrim's own level-up happens in its skills screen: the games' gains follow when the player leaves it.
  RegisterForMenu("StatsMenu")
  RegisterForMenu("LevelUp Menu")
  RegisterForMenu("RaceSex Menu")
  ApplyRace()
EndFunction

Int Function FreePoints()
  Return PlayerRef.GetLevel() - spent
EndFunction

; The sign the player was born under (0-12), or -1.
Int Function PlayerSign()
  Int i = 0
  While i < SignStones.Length
    If SignStones[i] && PlayerRef.HasSpell(SignStones[i])
      Return i
    EndIf
    i += 1
  EndWhile
  If ObStateId > 0 && Game.GetModByName("Oblivion.esm") != 255
    TMObState ob = Game.GetFormFromFile(ObStateId, "Oblivion.esm") as TMObState
    If ob && ob.ChosenBirthsign >= 0 && ob.ChosenBirthsign < ObSigns.Length
      Return ObSigns[ob.ChosenBirthsign]
    EndIf
  EndIf
  Return -1
EndFunction

; The player's best rank (1 the first) in each faction group, 0 none.
Int[] Function GroupRanks()
  Int[] ranks = new Int[8]
  TMProgression progression = (Self as Quest) as TMProgression
  If !progression || !progression.FactionIds
    Return ranks
  EndIf
  Int i = 0
  While i < FactionGroup.Length && i < progression.FactionIds.Length
    Int g = FactionGroup[i]
    If g >= 0 && g < Groups
      Faction f = Game.GetFormFromFile(progression.FactionIds[i], progression.FactionPlugins[i]) as Faction
      If f && PlayerRef.IsInFaction(f)
        Int rank = PlayerRef.GetFactionRank(f) + 1
        If rank > ranks[g]
          ranks[g] = rank
        EndIf
      EndIf
    EndIf
    i += 1
  EndWhile
  Return ranks
EndFunction

Bool Function Touches(Int n)
  Int i = 0
  While i < LinkA.Length
    If (LinkA[i] == n && taken[LinkB[i]] == 1) || (LinkB[i] == n && taken[LinkA[i]] == 1)
      Return True
    EndIf
    i += 1
  EndWhile
  Return False
EndFunction

String Function Take(Int n)
  If n < 0 || n >= NodeKind.Length
    Return ""
  EndIf
  If taken[n] == 1
    Return "You have that already."
  EndIf
  If start < 0
    Int sign = PlayerSign()
    If NodeKind[n] != 3
      Return "Begin at your birthsign."
    EndIf
    If sign >= 0 && SignStart[sign] != n
      Return "You were born under another sign: begin at it."
    EndIf
    taken[n] = 1
    start = n
    Apply()
    ; The owned games' versions of the sign come with it.
    GiveSigns(SignStart.Find(n), True)
    Return "Your web begins."
  EndIf
  If !Touches(n)
    Return "Take a node next to one you have."
  EndIf
  If FreePoints() < 1
    Return "No points left: each level brings one."
  EndIf
  If NodeGate[n] >= 0 && GroupRanks()[NodeGate[n]] < NodeGateRank[n]
    Return "That needs a higher rank in its faction."
  EndIf
  taken[n] = 1
  spent += 1
  Apply()
  Return ""
EndFunction

; Everything given back (the menu's Reset).
Function Reset()
  GiveSigns(signGiven, False)
  taken = Utility.CreateIntArray(NodeKind.Length)
  start = -1
  spent = 0
  Apply()
EndFunction

; The stats' sums into the ability, and each notable's and keystone's perk given or taken away.
Function Apply()
  If !StatsAbility || !taken
    Return
  EndIf
  Int count = StatsAbility.GetNumEffects()
  Float[] sums = Utility.CreateFloatArray(count)
  Int i = 0
  While i < NodeKind.Length
    If taken[i] == 1 && NodeStat[i] >= 0 && NodeStat[i] < count
      sums[NodeStat[i]] = sums[NodeStat[i]] + NodeAmount[i]
    EndIf
    Perk p = NodePerks[i]
    If p
      If taken[i] == 1 && !PlayerRef.HasPerk(p)
        PlayerRef.AddPerk(p)
      ElseIf taken[i] != 1 && PlayerRef.HasPerk(p)
        PlayerRef.RemovePerk(p)
      EndIf
    EndIf
    i += 1
  EndWhile
  Int k = 0
  While k < count
    StatsAbility.SetNthEffectMagnitude(k, sums[k])
    k += 1
  EndWhile
  PlayerRef.RemoveSpell(StatsAbility)
  PlayerRef.AddSpell(StatsAbility, False)
  ; Speed takes effect when the carry weight changes.
  PlayerRef.ModActorValue("CarryWeight", 0.1)
  PlayerRef.ModActorValue("CarryWeight", -0.1)
EndFunction

Event OnWebMenu(String eventName, String strArg, Float numArg, Form sender)
  String msg = ""
  If StringUtil.Find(strArg, "take:") == 0
    msg = Take(StringUtil.Substring(strArg, 5) as Int)
  ElseIf strArg == "reset"
    Reset()
    msg = "Every point is yours to spend again."
  ElseIf strArg == "char"
    SendCharacter("")
    Return
  ElseIf StringUtil.Find(strArg, "class:") == 0
    SendCharacter(TakeClass(StringUtil.Substring(strArg, 6) as Int))
    Return
  ElseIf StringUtil.Find(strArg, "pick:") == 0
    SendCharacter(Pick(StringUtil.Substring(strArg, 5)))
    Return
  ElseIf StringUtil.Find(strArg, "add:") == 0
    SendCharacter(AddPoint(StringUtil.Substring(strArg, 4) as Int))
    Return
  EndIf
  SendState(msg)
EndEvent

; ---- the games' levelling on the Mundus stones ----

; The game whose rules the character follows: its class's, else the default (-1: no game with levelling).
Int Function RuleGame()
  If playerClass >= 0
    Return ClassGame[playerClass]
  EndIf
  Return DefaultGame
EndFunction

; The player's race in the games' tables (GameStones.Races), or -1.
Int Function RaceIndex()
  If !RaceForms || !RaceFormIndex
    Return -1
  EndIf
  Int i = RaceForms.Find(PlayerRef.GetRace())
  If i < 0 || i >= RaceFormIndex.Length
    Return -1
  EndIf
  Return RaceFormIndex[i]
EndFunction

; Attribute a (0 Strength .. 7 Luck): the Daggerfall class's own or the race's, the class's favoured bonus, the levels'
; gains; at most 100.
; Asked for before Setup has run (other games' scripts read the player's attributes from their first update: Morrowind's
; dispositions did at the start, "Cannot access an element of a None array", 2026-11-05): what is there, gains none yet.
Int Function AttributeValue(Int a)
  Int g = RuleGame()
  If g < 0 || a < 0 || a >= 8 || !GameNeutral || g >= GameNeutral.Length
    Return 0
  EndIf
  Int value = GameNeutral[g]
  If playerClass >= 0 && ClassAttributes && ClassAttributes[playerClass * 8] >= 0
    value = ClassAttributes[playerClass * 8 + a]
  Else
    Int r = RaceIndex()
    If r >= 0
      Int sex = PlayerRef.GetActorBase().GetSex()
      If sex < 0 || sex > 1
        sex = 0
      EndIf
      Int at = g * 160 + r * 16 + sex * 8 + a
      If GameRaceAttributes && at < GameRaceAttributes.Length && GameRaceAttributes[at] > 0
        value = GameRaceAttributes[at]
      EndIf
    EndIf
  EndIf
  If playerClass >= 0 && ClassFavored && GameFavoredBonus && (ClassFavored[playerClass * 2] == a || ClassFavored[playerClass * 2 + 1] == a)
    value += GameFavoredBonus[ClassGame[playerClass]]
  EndIf
  If gains
    value += gains[a]
  EndIf
  If value > 100
    value = 100
  EndIf
  Return value
EndFunction

; A game's script changing the player's attribute (Oblivion's SetAV / ModAV Strength...): by that much, with what it does.
Function ModAttribute(Int a, Int aiBy)
  If a < 0 || a >= 8 || !gains
    Return
  EndIf
  gains[a] = gains[a] + aiBy
  ApplyAttributes()
EndFunction

; The counted skills now: Skyrim's base values, then the progression skills' levels.
Int[] Function Skills()
  Int n = SkyrimSkillAvs.Length + ProgressionSkillCount
  Int[] values = Utility.CreateIntArray(n)
  TMSkills skills = (Self as Quest) as TMSkills
  Int i = 0
  While i < n
    If i < SkyrimSkillAvs.Length
      values[i] = PlayerRef.GetBaseActorValue(SkyrimSkillAvs[i]) as Int
    ElseIf skills
      values[i] = skills.LevelOf(i - SkyrimSkillAvs.Length)
    EndIf
    i += 1
  EndWhile
  Return values
EndFunction

Function ResetMultipliers()
  Int a = 0
  While a < 8
    pendingMult[a] = 1
    a += 1
  EndWhile
EndFunction

; Skyrim's level against the last one seen: each new level's gains (multipliers from the skills raised since, or
; Daggerfall's points) and its health beside Skyrim's own.
Function CheckLevel()
  Int level = PlayerRef.GetLevel()
  Int g = RuleGame()
  If level <= lastLevel || g < 0
    If level < lastLevel
      lastLevel = level
    EndIf
    Return
  EndIf
  Int[] now = Skills()
  Int[] rises = new Int[8]
  Int i = 0
  While i < now.Length && i < snapshot.Length
    Int governs = GameGoverning[g * now.Length + i]
    ; A legendary skill starts again at 15: what it lost isn't a rise.
    If governs >= 0 && governs < 8 && now[i] > snapshot[i]
      rises[governs] = rises[governs] + now[i] - snapshot[i]
    EndIf
    i += 1
  EndWhile
  If pending == 0
    Int a = 0
    While a < 8
      Int r = rises[a]
      If r > 10
        r = 10
      EndIf
      pendingMult[a] = GameLevelUp[g * 11 + r]
      a += 1
    EndWhile
    pendingMult[7] = GameLuckGain[g]
    pendingPool = Utility.RandomInt(GamePoolMin[g], GamePoolMax[g])
  EndIf
  Int ups = level - lastLevel
  Int k = 0
  While k < ups
    levelHealth += HealthGain(g)
    k += 1
  EndWhile
  pending += ups
  lastLevel = level
  snapshot = now
  ApplyAttributes()
  wantCharacter = True
  Debug.Notification("Your attributes can rise: Tamriel menu, Passives, Character.")
EndFunction

; Health a level brings beside Skyrim's +10: Endurance / 10 against the game's average (Morrowind, Oblivion), or
; Daggerfall's roll of half to all the class's hit points against its average, plus Endurance / 10 - 5.
Float Function HealthGain(Int g)
  Int endurance = AttributeValue(5)
  If GameHealthDf[g] == 1
    Int hp = 8
    If playerClass >= 0 && ClassHitPoints[playerClass] > 0
      hp = ClassHitPoints[playerClass]
    EndIf
    Int roll = Utility.RandomInt(hp / 2, hp)
    Return (roll - (hp / 2 + hp) / 2.0) + (endurance / 10 - 5)
  EndIf
  Return (endurance / 10 - GameNeutral[g] / 10) as Float
EndFunction

; Three attributes ("a,b,c") raised by this level's multipliers (Morrowind, Oblivion).
String Function Pick(String list)
  Int g = RuleGame()
  If pending <= 0 || g < 0
    Return "No level is waiting for its attributes."
  EndIf
  If GameRule[g] != 0
    Return "Share out your points instead."
  EndIf
  String[] parts = StringUtil.Split(list, ",")
  If parts.Length != 3 || parts[0] == parts[1] || parts[0] == parts[2] || parts[1] == parts[2]
    Return "Choose three different attributes."
  EndIf
  Int i = 0
  While i < 3
    Int a = parts[i] as Int
    If a >= 0 && a < 8
      Int room = 100 - AttributeValue(a)
      Int add = pendingMult[a]
      If add > room
        add = room
      EndIf
      If add > 0
        gains[a] = gains[a] + add
      EndIf
    EndIf
    i += 1
  EndWhile
  pending -= 1
  ; A further level waiting has no rises of its own left to count.
  ResetMultipliers()
  pendingMult[7] = GameLuckGain[g]
  ApplyAttributes()
  Return ""
EndFunction

; One of Daggerfall's points into attribute a.
String Function AddPoint(Int a)
  Int g = RuleGame()
  If pending <= 0 || g < 0
    Return "No level is waiting for its attributes."
  EndIf
  If GameRule[g] != 1
    Return "Pick three attributes instead."
  EndIf
  If a < 0 || a >= 8 || AttributeValue(a) >= 100
    Return "That attribute can go no higher."
  EndIf
  gains[a] = gains[a] + 1
  pendingPool -= 1
  If pendingPool <= 0
    pending -= 1
    pendingPool = Utility.RandomInt(GamePoolMin[g], GamePoolMax[g])
  EndIf
  ApplyAttributes()
  Return ""
EndFunction

; The attributes' effects (the difference from the game's average, per point) and the levels' health, into the ability.
Function ApplyAttributes()
  If !AttributeAbility || !gains
    Return
  EndIf
  Int g = RuleGame()
  Int n = AttributeEffectOf.Length
  Int k = 0
  While k < n
    Float amount = 0.0
    If g >= 0
      amount = (AttributeValue(AttributeEffectOf[k]) - GameNeutral[g]) * AttributeEffectPerPoint[k]
    EndIf
    If amount >= 0.0
      AttributeAbility.SetNthEffectMagnitude(k * 2, amount)
      AttributeAbility.SetNthEffectMagnitude(k * 2 + 1, 0.0)
    Else
      AttributeAbility.SetNthEffectMagnitude(k * 2, 0.0)
      AttributeAbility.SetNthEffectMagnitude(k * 2 + 1, -amount)
    EndIf
    k += 1
  EndWhile
  If levelHealth >= 0.0
    AttributeAbility.SetNthEffectMagnitude(n * 2, levelHealth)
    AttributeAbility.SetNthEffectMagnitude(n * 2 + 1, 0.0)
  Else
    AttributeAbility.SetNthEffectMagnitude(n * 2, 0.0)
    AttributeAbility.SetNthEffectMagnitude(n * 2 + 1, -levelHealth)
  EndIf
  PlayerRef.RemoveSpell(AttributeAbility)
  If g >= 0
    PlayerRef.AddSpell(AttributeAbility, False)
  EndIf
  PlayerRef.ModActorValue("CarryWeight", 0.1)
  PlayerRef.ModActorValue("CarryWeight", -0.1)
EndFunction

; A class taken (free; -1 none): its perk, its skills' rates and starts for the progression skills, its attributes.
String Function TakeClass(Int c)
  If !ClassPerks || c < -1 || c >= ClassPerks.Length
    Return ""
  EndIf
  playerClass = c
  ; The class (and its game's race skill bonuses: ApplyRace applies the class too).
  ApplyRace()
  ApplyAttributes()
  If c < 0
    Return "You follow no class."
  EndIf
  Return "You are now a " + ClassNames[c] + " (" + GameNames[ClassGame[c]] + ")."
EndFunction

Function ApplyClass()
  If !ClassPerks
    Return
  EndIf
  Int i = 0
  While i < ClassPerks.Length
    If ClassPerks[i]
      If i == playerClass && !PlayerRef.HasPerk(ClassPerks[i])
        PlayerRef.AddPerk(ClassPerks[i])
      ElseIf i != playerClass && PlayerRef.HasPerk(ClassPerks[i])
        PlayerRef.RemovePerk(ClassPerks[i])
      EndIf
    EndIf
    i += 1
  EndWhile
  TMSkills skills = (Self as Quest) as TMSkills
  If skills && ProgressionSkillCount > 0
    Float[] rates = Utility.CreateFloatArray(ProgressionSkillCount, 1.0)
    Int[] bonus = Utility.CreateIntArray(ProgressionSkillCount)
    If playerClass >= 0
      Int k = 0
      While k < ProgressionSkillCount
        rates[k] = ClassSkillRate[playerClass * ProgressionSkillCount + k]
        bonus[k] = ClassSkillBonus[playerClass * ProgressionSkillCount + k]
        k += 1
      EndWhile
    EndIf
    ; The race's bonuses in the game whose rules apply.
    Int g = RuleGame()
    Int r = RaceIndex()
    If g >= 0 && r >= 0 && RaceSkillBonus
      Int kk = 0
      While kk < ProgressionSkillCount
        Int at = (g * 10 + r) * ProgressionSkillCount + kk
        If at < RaceSkillBonus.Length
          bonus[kk] = bonus[kk] + RaceSkillBonus[at]
        EndIf
        kk += 1
      EndWhile
    EndIf
    skills.SetClass(rates, bonus)
  EndIf
EndFunction

; The player's race in the owned games: each game's version of it (its spells: resistances, racial powers), the skill
; bonuses of the game whose rules apply, and Daggerfall's racial abilities (weapon damage growing with the level). Again
; after the race menu, a class change and each level.
Function ApplyRace()
  Int r = RaceIndex()
  Int g = RuleGame()
  If r != raceGiven
    GiveRace(raceGiven, False)
    GiveRace(r, True)
    raceGiven = r
  EndIf
  Int i = 0
  While RaceSkillAbility && i < RaceSkillAbility.Length
    Spell s = RaceSkillAbility[i]
    If s
      If r >= 0 && g >= 0 && i == g * 10 + r
        PlayerRef.AddSpell(s, False)
      Else
        PlayerRef.RemoveSpell(s)
      EndIf
    EndIf
    i += 1
  EndWhile
  i = 0
  While DfRacePerks && i < DfRacePerks.Length
    Perk p = DfRacePerks[i]
    If p
      If i == r
        If DfRaceDivisor[i] > 0
          p.SetNthEntryValue(DfRaceEntry[i], 0, (PlayerRef.GetLevel() / DfRaceDivisor[i]) as Float)
        EndIf
        If !PlayerRef.HasPerk(p)
          PlayerRef.AddPerk(p)
        EndIf
      ElseIf PlayerRef.HasPerk(p)
        PlayerRef.RemovePerk(p)
      EndIf
    EndIf
    i += 1
  EndWhile
  ApplyClass()
EndFunction

Function GiveRace(Int r, Bool give)
  If r < 0 || !RaceListRace
    Return
  EndIf
  Int i = 0
  While i < RaceListRace.Length
    If RaceListRace[i] == r && Game.GetModByName(RaceListPlugins[i]) != 255
      FormList spells = Game.GetFormFromFile(RaceListIds[i], RaceListPlugins[i]) as FormList
      Int k = 0
      While spells && k < spells.GetSize()
        Spell sp = spells.GetAt(k) as Spell
        If sp && give
          PlayerRef.AddSpell(sp, False)
        ElseIf sp
          PlayerRef.RemoveSpell(sp)
        EndIf
        k += 1
      EndWhile
    EndIf
    i += 1
  EndWhile
EndFunction

; The owned games' versions of sign s (their spells) given or taken back.
Function GiveSigns(Int s, Bool give)
  If s < 0 || !SignListSign
    Return
  EndIf
  Int i = 0
  While i < SignListSign.Length
    If SignListSign[i] == s && Game.GetModByName(SignListPlugins[i]) != 255
      FormList spells = Game.GetFormFromFile(SignListIds[i], SignListPlugins[i]) as FormList
      Int k = 0
      While spells && k < spells.GetSize()
        Spell sp = spells.GetAt(k) as Spell
        If sp && give
          PlayerRef.AddSpell(sp, False)
        ElseIf sp
          PlayerRef.RemoveSpell(sp)
        EndIf
        k += 1
      EndWhile
    EndIf
    i += 1
  EndWhile
  If give
    signGiven = s
  Else
    signGiven = -1
  EndIf
EndFunction

; Something the player put on: a Daggerfall class's forbidden weapon, armour, shield or material comes off again.
Function Equipped(Form item)
  If playerClass < 0 || !item || !ClassForbidden
    Return
  EndIf
  String why = ""
  FormList forbidden = ClassForbidden[playerClass]
  Int k = 0
  While forbidden && k < forbidden.GetSize() && why == ""
    Keyword kw = forbidden.GetAt(k) as Keyword
    If kw && item.HasKeyword(kw)
      why = item.GetName()
    EndIf
    k += 1
  EndWhile
  If why == "" && ClassForbiddenWords[playerClass] != ""
    String[] words = StringUtil.Split(ClassForbiddenWords[playerClass], "|")
    Int w = 0
    While w < words.Length && why == ""
      If StringUtil.Find(words[w], "shield:") == 0
        If ArmorShield && item.HasKeyword(ArmorShield)
          String kind = StringUtil.Substring(words[w], 7)
          If kind == "any" || (kind == "light" && item.HasKeyword(ArmorLight)) || (kind == "heavy" && item.HasKeyword(ArmorHeavy))
            why = item.GetName()
          EndIf
        EndIf
      ElseIf StringUtil.Find(item.GetName(), words[w]) >= 0
        why = item.GetName()
      EndIf
      w += 1
    EndWhile
  EndIf
  If why != ""
    PlayerRef.UnequipItem(item, False, True)
    Debug.Notification("As a " + ClassNames[playerClass] + " you may not use " + why + ".")
  EndIf
EndFunction

; A Daggerfall class harmed by sunlight (outside by day) or in holy places (a temple, chapel or shrine): a health a
; second, checked with TMSkills' poll every <seconds>.
Function Hazards(Float seconds)
  If playerClass >= 0 && ClassSunDamage && (ClassSunDamage[playerClass] == 1 || ClassHolyDamage[playerClass] == 1)
    Bool hurt = False
    If ClassSunDamage[playerClass] == 1 && !PlayerRef.IsInInterior()
      Float day = Utility.GetCurrentGameTime()
      Float hour = (day - Math.Floor(day)) * 24.0
      hurt = hour >= 6.0 && hour < 20.0
    EndIf
    If !hurt && ClassHolyDamage[playerClass] == 1
      Location here = PlayerRef.GetCurrentLocation()
      String cellName = PlayerRef.GetParentCell().GetName()
      hurt = (here && LocTypeTemple && here.HasKeyword(LocTypeTemple)) || StringUtil.Find(cellName, "Temple") >= 0 || StringUtil.Find(cellName, "Chapel") >= 0 \
        || StringUtil.Find(cellName, "Shrine") >= 0 || StringUtil.Find(cellName, "Cathedral") >= 0
    EndIf
    If hurt
      PlayerRef.DamageActorValue("Health", seconds)
    EndIf
  EndIf
EndFunction

; Skyrim's level-up is taken in its skills screen: once the player leaves it, the menu opens on the gains.
Event OnMenuClose(String menuName)
  If menuName == "RaceSex Menu"
    ApplyRace()
    Return
  EndIf
  CheckLevel()
  ; Daggerfall's racial damage grows with the level.
  ApplyRace()
  If menuName == "StatsMenu" && pending > 0 && wantCharacter
    TMProgression progression = (Self as Quest) as TMProgression
    If progression
      progression.OpenMenu()
    EndIf
  EndIf
EndEvent

; The menu has loaded (TMProgression.OnTamrielReady): to the Character view when a level's gains wait.
Function MenuReady()
  If wantCharacter && pending > 0
    wantCharacter = False
    UI.InvokeString(MenuName, "_root.ShowCharacter", "")
  EndIf
EndFunction

; "class#game#rule#values#gains#pending#multipliers#points#health#message#race": the class taken (-1 none), the game
; whose rules apply and its kind (0 picks, 1 points), the 8 attributes now and their gains (","), the levels waiting,
; this level's multipliers, Daggerfall's points left, the health the levels gave beside Skyrim's, a message, the race.
Function SendCharacter(String msg)
  If !gains
    Return
  EndIf
  Int g = RuleGame()
  String values = ""
  String gained = ""
  String mults = ""
  Int a = 0
  While a < 8
    If a > 0
      values += ","
      gained += ","
      mults += ","
    EndIf
    values += AttributeValue(a)
    gained += gains[a]
    mults += pendingMult[a]
    a += 1
  EndWhile
  Int kind = 0
  If g >= 0
    kind = GameRule[g]
  EndIf
  UI.InvokeString(MenuName, "_root.CharState", playerClass + "#" + g + "#" + kind + "#" + values + "#" + gained + "#" + pending + "#" + mults + "#" + pendingPool \
    + "#" + Math.Floor(levelHealth) + "#" + msg + "#" + PlayerRef.GetRace().GetName() + "#" + RaceIndex())
EndFunction

; "taken (1/0 a node)#free points#start node#the player's sign#gates open (1/0 a node)#message#level".
Function SendState(String msg)
  If !taken
    Return
  EndIf
  Int[] ranks = GroupRanks()
  String bits = ""
  String gates = ""
  Int i = 0
  While i < NodeKind.Length
    If taken[i] == 1
      bits += "1"
    Else
      bits += "0"
    EndIf
    If NodeGate[i] >= 0 && ranks[NodeGate[i]] < NodeGateRank[i]
      gates += "0"
    Else
      gates += "1"
    EndIf
    i += 1
  EndWhile
  UI.InvokeString(MenuName, "_root.WebState", bits + "#" + FreePoints() + "#" + start + "#" + PlayerSign() + "#" + gates + "#" + msg + "#" + PlayerRef.GetLevel())
EndFunction
