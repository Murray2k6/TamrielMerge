ScriptName TMDfQuestMachine extends Quest
{Daggerfall's quest machine (DFQuestMachine in Daggerfall.esm; rules from Daggerfall Unity's QuestMachine,
QuestListsManager and Place/Person resources, MIT). It keeps:
- the catalogue of converted quests (each a quest record running TMDfQuest) and offers them: "Do you have any work for
  me?" to a guild's people gives the guild's quests by membership and rank, to commoners, merchants and nobles their
  quests by level; a quest is set up first, then its offer (message 1000) is accepted (1002) or refused (1001);
- the quest sites: every converted town's buildings and every dungeon, registered at start by the TMDfQuestSites quests
  of Daggerfall.esm and the town and dungeon plugins; a site id is source * 100000 + 0..29999 a building,
  30000.. a dungeon, 60000.. a town's streets;
- the named individuals (King Gothryd, Lord Castellian ...) where they stand;
- Daggerfall's 64 global quest variables (DFQuestGlobal<n>), the player's legal standing per region, and the main quest:
  _BRISIEN (and the tutorial) start on the first arrival in the Iliac Bay; an ending (S0000100-S0000104, S0000106)
  finishing
  tells the story order (TMStoryOrder.StoryCompleted) that Daggerfall's story is over.}

TMDfReputation Property Reputation Auto
TMDfGuilds Property Guilds Auto
Actor Property PlayerRef Auto
ObjectReference Property Holding Auto
{A marker in a closed cell where quest items, name tags and people wait.}
Activator Property NameTag Auto
Int Property AfterwordReady = 0 Auto Hidden
{1 while a townsperson has an ended quest's rumour to tell (the Afterword alias names it).}
Int Property QuestorWordReady = 0 Auto Hidden
{1 while the player talks to the questor of an ended quest (the QuestorWord alias holds their answer).}
Int Property AfterwordsKept = 16 AutoReadOnly
GlobalVariable Property TalkTone Auto
{DFTalkTone: 0 polite, 1 normal, 2 blunt (the tone topics set it).}
GlobalVariable[] Property TalkReacts Auto
{DFTalkReact0-4: how the tone lands with each social group (0 refused, 1 plain, 2 warm), rolled when a question is asked
(RollTalk); the answers' conditions read them.}
Int[] Property EtiquetteMods Auto
Int[] Property StreetwiseMods Auto
Quest Property Senses Auto
{TMDfSensesQuest (TMDfSensesControl): Daggerfall's own skills, Etiquette (0) and Streetwise (1), their values and tallies.}
FormList Property Sources Auto
FormList Property RunningQuests Auto
FormList Property DoneOnce Auto
FormList Property FactionForms Auto
Int[] Property FactionFormIds Auto
GlobalVariable[] Property QuestGlobals Auto
String[] Property QuestNames Auto
Quest[] Property Quests Auto
Int[] Property QuestLetter Auto
{The quest name's first character (its giver), as a character code.}
Int[] Property QuestMembership Auto
{'A' joining, 'B' members, 'C' non-members (character codes).}
Int[] Property QuestMinReq Auto
Bool[] Property QuestOneTime Auto
Int[] Property GuildFaction Auto
{Per TMDfGuilds guild: the faction its quests' reputation goes to.}
String[] Property GuildLetters Auto
{Per TMDfGuilds guild: the quest-name letters its people give.}
ActorBase[] Property PersonBases Auto
{Quest people as the town people are: ((race * 2 + female) * Dresses + dress) * FacesPerDress + face; races Breton,
Redguard, Nord, Dark Elf, High Elf, Wood Elf, Khajiit, Argonian, Orc; dresses commoner, merchant, scholar, noble,
barkeep, smith.}
String[] Property PersonNames Auto
{Daggerfall names, (race * 2 + female) * NamesPerRace + i.}
Int[] Property RaceOfDf Auto
{FACTION.TXT race number -> race index (-1 none).}
Int[] Property DressByBuilding Auto
Int[] Property DressBySGroup Auto
Int Property Dresses = 6 Auto
Int Property FacesPerDress = 2 Auto
Int Property NamesPerRace = 12 Auto
Bool[] Property RegionRedguard Auto
{Per region: its people are Redguards (else Bretons).}
String[] Property MaleNames Auto
String[] Property FemaleNames Auto
FormList[] Property GroupItems Auto
{Per Daggerfall item group: Skyrim items to pick from.}
Spell[] Property DfSpells Auto
Int[] Property DfSpellIds Auto
FormList Property Diseases Auto
{Daggerfall's 17 diseases in its order (DfDiseases).}
Quest Property VampireQuest Auto
Spell Property WerewolfPower Auto
Quest Property WerewolfCure Auto
{Skyrim's Companions housekeeping quest, whose CurePlayer ends lycanthropy.}
String[] Property RulerTitles Auto
{By region: its province's ruler's title (FACTION.TXT ruler: King, Queen, Duke ... Lady, else Lord), for %rt.}
String[] Property RulerNames Auto
{By region: the ruler's name (the province's individual faction; "" none), for %rn.}
FormList Property Lycanthropy Auto
{Daggerfall's lycanthropy (DfDiseases.Lycanthropy): werewolf infection, wereboar infection, werewolf, wereboar.}
FormList Property Vampirism Auto
{Daggerfall's vampirism (DfDiseases.Vampirism): the infection, then the curse.}
Location Property IliacBay Auto
String[] Property RegionNames Auto
String[] Property BuildingLabels Auto
String[] Property Gods Auto
String[] Property Oaths Auto
Sound Property QuestSound Auto
Sound[] Property DfSounds Auto
Int[] Property DfSoundIds Auto
{Daggerfall's sounds the quests play, by DAGGER.SND record id.}
Int Property MinutesPerPixel = 90 Auto
{Cautious travel time per map pixel, for clocks that count the way there.}
Int Property CommonersFaction = 0 Auto
Faction Property MerchantsGroup Auto
Faction Property NobilityGroup Auto
{The social group factions conversations put townsfolk in (TownTalk).}

; ---- The Daedra Princes a witches' coven summons (Daggerfall Unity DaggerfallQuestPopupWindow.daedraData, MIT) ----
Int[] Property DaedraFaction Auto
{Per prince: Hircine, Clavicus Vile, Mehrunes Dagon, Molag Bal, Sanguine, Peryite, Malacath, Hermaeus Mora, Sheogorath,
Boethiah, Namira, Meridia, Vaernima, Nocturnal, Mephala, Azura.}
String[] Property DaedraQuest Auto
Int[] Property DaedraDay Auto
{Daggerfall's day of the year (month * 30 + day) the prince can be summoned.}
Int[] Property DaedraWeather Auto
{The weather that adds 30 to the chance: 0 any, 1 rain, 2 a thunderstorm.}
String[] Property DaedraVideo Auto
Message[] Property SummonNotToday Auto
Message[] Property SummonAsk Auto
{Per prince (TEXT.RSC 481, the cost added).}
Message[] Property SummonBefore Auto
Message[] Property SummonFailed Auto
Message Property SummonNoGold Auto
Form[] Property SummonFoes Auto
{Daedra Lord, Daedra Seducer, Daedroth, Fire Daedra, Frost Daedra.}
FormList Property StormWeathers Auto
{Daggerfall's thunderstorms (rain counts as rain).}
Int Property GlenmorilWitches = 419 Auto
Int Property SheogorathIndex = 8 Auto

Int Property CurrentTown = -1 Auto Hidden
Int summonDay = -1
Int summonIndex = -1
Bool[] summoned
Bool Property MainStarted = False Auto Hidden
Int[] legal

String[] afterNames
String[] afterRumours
String[] afterWords
Actor[] afterQuestors
Int afterCount = 0
ObjectReference afterTag
ObjectReference questorTag

Event OnInit()
  legal = new Int[64]
  RegisterForMenu("Dialogue Menu")
  RegisterForSingleUpdate(5.0)
EndEvent

; ---- What is said after a quest (Daggerfall Unity TombstoneQuest: AddOrReplaceQuestProgressRumor and
; AddQuestorPostQuestMessage; TalkManager.GetNewsOrRumors and GetNPCQuestGreeting) ----

; An ended quest's rumour (message 1006 or 1007) and its questor's answer (1008 or 1009), replacing the same quest's
; earlier ones; the oldest of 16 forgotten.
Function Afterword(String name, String rumour, String word, Actor questor)
  If !afterNames
    afterNames = Utility.CreateStringArray(AfterwordsKept)
    afterRumours = Utility.CreateStringArray(AfterwordsKept)
    afterWords = Utility.CreateStringArray(AfterwordsKept)
    afterQuestors = new Actor[16]
  EndIf
  Int at = -1
  Int k = 0
  While k < afterCount && at < 0
    If afterNames[k] == name
      at = k
    EndIf
    k += 1
  EndWhile
  If at < 0
    If afterCount < AfterwordsKept
      at = afterCount
      afterCount += 1
    Else
      k = 1
      While k < AfterwordsKept
        afterNames[k - 1] = afterNames[k]
        afterRumours[k - 1] = afterRumours[k]
        afterWords[k - 1] = afterWords[k]
        afterQuestors[k - 1] = afterQuestors[k]
        k += 1
      EndWhile
      at = AfterwordsKept - 1
    EndIf
  EndIf
  afterNames[at] = name
  afterRumours[at] = rumour
  afterWords[at] = word
  afterQuestors[at] = questor
EndFunction

; Talking to someone: a rumour of an ended quest picked at random for "Have you heard any rumors?", and the questor's
; answer when it is them.
; ---- The talk's tone (TalkTone, Daggerfall Unity GetReactionToPlayer_0_1_2) ----

Int[] talkReaction      ; by tone: the reaction worked out this talk (0 none yet), offset by 1000
Bool[] talkTallied      ; by tone: the skill tallied this talk
Int talkRoll = -1       ; the roll to beat this talk (0-20)

Function SetTalkTone(Int tone)
  If TalkTone
    TalkTone.SetValueInt(tone)
  EndIf
EndFunction

; The reactions of the five social groups to the player's tone for a question (questionMod 5 "where is" a building, 0
; work): Personality / 5 + the question's modifier + the tone's modifier for the group + 5 for a skill roll made, -10
; failed, against this talk's roll of 0 to 20; the skill (Etiquette polite, Streetwise blunt) tallied once a talk.
Function RollTalk(Int questionMod)
  If !TalkReacts
    Return
  EndIf
  If !talkReaction
    talkReaction = new Int[3]
    talkTallied = new Bool[3]
  EndIf
  Int tone = 1
  If TalkTone
    tone = TalkTone.GetValueInt()
  EndIf
  If tone < 0 || tone > 2
    tone = 1
  EndIf
  If talkRoll < 0
    talkRoll = Utility.RandomInt(0, 20)
  EndIf
  If talkReaction[tone] == 0
    Int personality = 50
    Quest q = Quest.GetQuest("TMProgression")
    If q && (q as TMPassives)
      personality = (q as TMPassives).AttributeValue(6)
    EndIf
    Int toneMod = 0
    If tone != 1
      Int skill = 0
      Int index = 0   ; Etiquette
      If tone == 2
        index = 1     ; Streetwise
      EndIf
      TMDfSensesControl control = Senses as TMDfSensesControl
      If control
        skill = control.SkillValue(index)
        If !talkTallied[tone]
          control.Tally(index, 1)
          talkTallied[tone] = True
        EndIf
      EndIf
      If Utility.RandomInt(1, 100) > skill
        toneMod = -10
      Else
        toneMod = 5
      EndIf
    EndIf
    talkReaction[tone] = 1000 + personality / 5 + questionMod + toneMod
  EndIf
  Int base = talkReaction[tone] - 1000
  Int g = 0
  While g < TalkReacts.Length && g < 5
    Int reaction = base
    If tone == 0 && EtiquetteMods && g < EtiquetteMods.Length
      reaction += EtiquetteMods[g]
    ElseIf tone == 2 && StreetwiseMods && g < StreetwiseMods.Length
      reaction += StreetwiseMods[g]
    EndIf
    Int r = 2
    If reaction < talkRoll
      r = 0
    ElseIf reaction < talkRoll + 20
      r = 1
    EndIf
    If TalkReacts[g]
      TalkReacts[g].SetValueInt(r)
    EndIf
    g += 1
  EndWhile
EndFunction

Event OnMenuOpen(String asMenuName)
  If asMenuName != "Dialogue Menu"
    Return
  EndIf
  ; A new talk: the tone's reactions rolled afresh when a question is asked (TalkToNpc resets them).
  talkReaction = new Int[3]
  talkTallied = new Bool[3]
  talkRoll = -1
  AfterwordReady = 0
  QuestorWordReady = 0
  If afterCount == 0
    Return
  EndIf
  Int count = 0
  Int pick = -1
  Int k = 0
  While k < afterCount
    If afterRumours[k] != ""
      count += 1
      If Utility.RandomInt(1, count) == 1
        pick = k
      EndIf
    EndIf
    k += 1
  EndWhile
  If pick >= 0 && Tag(0)
    afterTag.SetDisplayName(afterRumours[pick], True)
    AfterwordReady = 1
  EndIf
  Actor target = PlayerRef.GetDialogueTarget()
  If target
    k = afterCount
    While k > 0
      k -= 1
      If afterQuestors[k] == target && afterWords[k] != "" && Tag(1)
        questorTag.SetDisplayName(afterWords[k], True)
        QuestorWordReady = 1
        k = 0
      EndIf
    EndWhile
  EndIf
EndEvent

; The name tag the Afterword (0; alias 1) or QuestorWord (1; alias 2) alias shows, made at the holding cell the first time.
Bool Function Tag(Int which)
  If !NameTag || !Holding
    Return False
  EndIf
  If which == 0
    If !afterTag
      afterTag = Holding.PlaceAtMe(NameTag, 1, True, False)
      (GetAlias(1) as ReferenceAlias).ForceRefTo(afterTag)
    EndIf
    Return afterTag
  EndIf
  If !questorTag
    questorTag = Holding.PlaceAtMe(NameTag, 1, True, False)
    (GetAlias(2) as ReferenceAlias).ForceRefTo(questorTag)
  EndIf
  Return questorTag
EndFunction

Event OnUpdate()
  If !legal
    legal = new Int[64]
  EndIf
  If !MainStarted && IliacBay && PlayerRef.IsInLocation(IliacBay)
    MainStarted = True
    ; The tutorial is Privateer's Hold's: it opens with "You wake and look around the room" and teaches in that
    ; dungeon. A first arrival anywhere else (travel from another land comes in overland) has no room to wake in:
    ; arriving in Daggerfall city showed that text in the street (seen in game, 2026-11-10).
    Cell arrivedIn = PlayerRef.GetParentCell()
    If arrivedIn && arrivedIn.IsInterior()
      StartByName("_TUTOR__", None, 0, False)
    EndIf
    StartByName("_BRISIEN", None, 0, False)
  EndIf
  RacialQuests()
  RegisterForSingleUpdate(10.0)
EndEvent

; ---- lycanthropes' and vampires' quests (Daggerfall Unity PlayerEntity.Update: every 38 days StartQuest(False), every
; 84 days StartQuest(True), by the day count; LycanthropyEffect / VampirismEffect.StartQuest) ----

Int lastRacialDay = -1
Bool vampireQuestStarted = False

Function RacialQuests()
  Int today = Math.Floor(Utility.GetCurrentGameTime())
  If lastRacialDay < 0 || today < lastRacialDay
    lastRacialDay = today
    Return
  EndIf
  While lastRacialDay < today
    lastRacialDay += 1
    If lastRacialDay % 38 == 0
      RacialQuest(False)
    EndIf
    If lastRacialDay % 84 == 0
      RacialQuest(True)
    EndIf
    ; Every 112 days reputations drift a point toward 0 (Daggerfall Unity PlayerEntity.Update: NormalizeReputations at
    ; 161,280 minutes): every region's legal standing and every faction's regard.
    If lastRacialDay % 112 == 0
      NormalizeReputations()
    EndIf
  EndWhile
EndFunction

; NormalizeReputations (Daggerfall Unity): each region's legal reputation one step toward 0, then each faction's one.
Function NormalizeReputations()
  If legal
    Int r = 0
    While r < legal.Length
      If legal[r] > 0
        legal[r] = legal[r] - 1
      ElseIf legal[r] < 0
        legal[r] = legal[r] + 1
      EndIf
      r += 1
    EndWhile
  EndIf
  If Reputation
    Reputation.Normalize()
  EndIf
EndFunction

Function RacialQuest(Bool cure)
  Bool lycanthrope = Lycanthropy && (PlayerRef.HasSpell(Lycanthropy.GetAt(2)) || PlayerRef.HasSpell(Lycanthropy.GetAt(3)))
  Bool vampire = (Vampirism && Vampirism.GetSize() > 1 && PlayerRef.HasSpell(Vampirism.GetAt(1) as Spell)) || (VampireQuest && (VampireQuest as PlayerVampireQuestScript).VampireStatus > 0)
  If lycanthrope
    ; A cure, 1-100 under 30, unless one is running already.
    If cure && Utility.RandomInt(1, 100) < 30 && !QuestRunning("$CUREWER")
      StartByName("$CUREWER", None, 0, False)
    EndIf
  ElseIf vampire
    If cure
      If Utility.RandomInt(10, 100) < 30
        StartByName("$CUREVAM", None, 0, False)
      EndIf
    ElseIf vampireQuestStarted
      ; A clan's quest, 1-100 under 50 (the vampires' offer pool).
      If Utility.RandomInt(1, 100) < 50
        OfferClanQuest()
      EndIf
    ElseIf Utility.RandomInt(1, 100) < 50
      StartByName("P0A01L00", None, 0, False)
      vampireQuestStarted = True
    EndIf
  EndIf
EndFunction

Bool Function QuestRunning(String name)
  Int i = QuestNames.Find(name)
  Return i >= 0 && i < Quests.Length && Quests[i] && Quests[i].IsRunning()
EndFunction

; A vampire clan's quest (QuestListsManager.GetGuildQuest(Vampires, Member, ...)): one of the P0 quests at random that
; isn't running or done once.
Function OfferClanQuest()
  Int count = 0
  Int pick = -1
  Int i = 0
  While i < QuestNames.Length
    String n = QuestNames[i]
    If StringUtil.Find(n, "P0") == 0 && n != "P0A01L00" && !QuestRunning(n) && !(i < Quests.Length && DoneOnce.HasForm(Quests[i]))
      count += 1
      If Utility.RandomInt(1, count) == 1
        pick = i
      EndIf
    EndIf
    i += 1
  EndWhile
  If pick >= 0
    StartByName(QuestNames[pick], None, 0, False)
  EndIf
EndFunction

; ---- Registration ----

Function Register(TMDfQuestSites s)
  lastRegister = Utility.GetCurrentRealTime()
  If !s || Sources.Find(s) >= 0
    Return
  EndIf
  Sources.AddForm(s)
EndFunction

; Whether the player is in Daggerfall's lands (TMStoryOrder.WorldOf, on Skyrim's MQ101): its quests run only there.
Bool Function InDaggerfall()
  TMStoryOrder order = Game.GetFormFromFile(0x3372B, "Skyrim.esm") as TMStoryOrder
  If !order
    Return True
  EndIf
  Return order.WorldOf(Game.GetPlayer()) == order.StoryDaggerfall
EndFunction

; The sites register one at a time after the game starts (95 lists, some seconds: in the game on 2026-11-09 the opening's
; quests began 2 s before the last had, found "no site for place 0" and never ran). A quest waits for them: until none
; has registered for 3 s, a minute at most. (Real time starts over with each run of the game: a time before the last
; registration means they registered in an earlier run.)
Float lastRegister = 0.0
Function WaitForSites()
  Int tries = 0
  While tries < 120
    Float since = Utility.GetCurrentRealTime() - lastRegister
    If Sources.GetSize() > 0 && (lastRegister == 0.0 || since < 0.0 || since >= 3.0)
      Return
    EndIf
    Utility.WaitMenuMode(0.5)
    tries += 1
  EndWhile
EndFunction

TMDfQuestSites Function Source(Int site)
  If site < 0
    Return None
  EndIf
  Return Sources.GetAt(site / 100000) as TMDfQuestSites
EndFunction

; ---- Daedra summoning ----

; Daggerfall's day of the year (Daggerfall Unity DayOfYear: twelve months of 30 days).
Int Function DayOfYear()
  GlobalVariable month = Game.GetFormFromFile(0x36, "Skyrim.esm") as GlobalVariable
  GlobalVariable day = Game.GetFormFromFile(0x37, "Skyrim.esm") as GlobalVariable
  Return (month.GetValueInt() % 12) * 30 + day.GetValueInt()
EndFunction

; 0 fair, 1 rain (or snow), 2 a thunderstorm.
Int Function WeatherNow()
  Weather w = Weather.GetCurrentWeather()
  If !w || PlayerRef.IsInInterior()
    Return 0
  ElseIf StormWeathers && StormWeathers.HasForm(w)
    Return 2
  ElseIf w.GetClassification() >= 2
    Return 1
  EndIf
  Return 0
EndFunction

; A witch's "Summon": Daggerfall Unity's DaggerfallQuestPopupWindow. The Glenmoril Witches always call Hircine, any other
; coven a prince picked at random for the day (never Hircine); anyone else only the prince whose day it is. The cost is
; 200000 - 1000 x the summoner's regard; Sheogorath comes instead 5% of the time (15% in a storm); the prince answers
; with 30 + the player's regard with them (+30 on their weather, or always for the princes without one) percent. A prince
; met before only speaks; the first time they offer their quest, and refusing it (or the summoning failing at a coven)
; brings Daedra.
Function SummonDaedra(Actor witch)
  If !witch || !DaedraFaction
    Return
  EndIf
  If !summoned
    summoned = new Bool[16]
  EndIf
  Int fid = DfFactionOf(witch)
  Bool coven = Reputation.KindOf(fid) == 8
  Int today = DayOfYear()
  Int prince = -1
  If fid == GlenmorilWitches
    prince = 0
  ElseIf coven
    If summonDay != today
      summonDay = today
      summonIndex = Utility.RandomInt(1, 15)
    EndIf
    prince = summonIndex
  Else
    prince = DaedraDay.Find(today)
  EndIf
  If prince < 0
    SummonNotToday[Utility.RandomInt(0, SummonNotToday.Length - 1)].Show()
    Return
  EndIf
  Int cost = 200000 - (Reputation.GetRep(fid) as Int) * 1000
  If SummonAsk[prince].Show(cost) != 0
    Return
  EndIf
  If PlayerRef.GetGoldAmount() < cost
    SummonNoGold.Show(cost)
    Return
  EndIf
  PlayerRef.RemoveItem(Game.GetForm(0xF), cost)
  Int wx = WeatherNow()
  Int sheogorath = 5
  If wx == 2
    sheogorath = 15
  EndIf
  If Utility.RandomInt(1, 100) <= sheogorath
    prince = SheogorathIndex
  EndIf
  Int bonus = 0
  If DaedraWeather[prince] == 0 || (DaedraWeather[prince] == 1 && wx >= 1) || (DaedraWeather[prince] == 2 && wx == 2)
    bonus = 30
  EndIf
  Int chance = 30 + (Reputation.GetRep(DaedraFaction[prince]) as Int) + bonus
  If Utility.RandomInt(1, 100) > chance
    SummonFailed[prince].Show()
    If coven
      SpawnDaedra(Utility.RandomInt(1, 3))
    EndIf
    Return
  EndIf
  Game.PlayBink(DaedraVideo[prince], True)
  If summoned[prince]
    SummonBefore[prince].Show()
    Return
  EndIf
  summoned[prince] = True
  Int i = QuestNames.Find(DaedraQuest[prince])
  If i >= 0 && StartQuest(i, witch, fid, True) && !Quests[i].IsRunning()
    ; Refused: the prince's servants answer.
    SpawnDaedra(Utility.RandomInt(3, 5))
  EndIf
EndFunction

Function SpawnDaedra(Int count)
  If !SummonFoes || SummonFoes.Length == 0
    Return
  EndIf
  Int k = 0
  While k < count
    Form f = SummonFoes[Utility.RandomInt(0, SummonFoes.Length - 1)]
    If f
      Float a = Utility.RandomFloat(0.0, 360.0)
      Float r = Utility.RandomFloat(300.0, 1200.0)
      Actor d = PlayerRef.PlaceAtMe(f, 1, False, False) as Actor
      If d
        d.MoveTo(PlayerRef, r * Math.Sin(a), r * Math.Cos(a), 0.0)
        d.StartCombat(PlayerRef)
      EndIf
    EndIf
    k += 1
  EndWhile
EndFunction

; ---- Globals and law ----

Bool Function GetGlobal(Int n)
  If n < 0 || n >= QuestGlobals.Length
    Return False
  EndIf
  Return QuestGlobals[n].GetValue() != 0.0
EndFunction

Function SetGlobal(Int n, Bool value)
  If n >= 0 && n < QuestGlobals.Length
    If value
      QuestGlobals[n].SetValue(1.0)
    Else
      QuestGlobals[n].SetValue(0.0)
    EndIf
  EndIf
EndFunction

Function ChangeLegal(Int amount)
  If !legal
    legal = new Int[64]
  EndIf
  Int r = SiteRegion(CurrentTown)
  If r < 0
    Return
  EndIf
  legal[r] = legal[r] + amount
  If legal[r] > 100
    legal[r] = 100
  ElseIf legal[r] < -100
    legal[r] = -100
  EndIf
EndFunction

; The player's legal standing in a region (-100..100; TMDfLaw reads it for guards and the court).
Int Function LegalOf(Int region)
  If !legal || region < 0 || region >= legal.Length
    Return 0
  EndIf
  Return legal[region]
EndFunction

Function SetLegalOf(Int region, Int value)
  If !legal
    legal = new Int[64]
  EndIf
  If region >= 0 && region < legal.Length
    If value > 100
      value = 100
    ElseIf value < -100
      value = -100
    EndIf
    legal[region] = value
  EndIf
EndFunction

; The Skyrim location of a town site (None for anything else).
Location Function SiteLocation(Int site)
  If SiteKind(site) != 2
    Return None
  EndIf
  Return Source(site).TownLocation[SiteLocal(site)]
EndFunction

; ---- Offering ----

; "Do you have any work for me?"
Function OfferFrom(Actor speaker)
  If !speaker
    Return
  EndIf
  Int fid = DfFactionOf(speaker)
  Int guild = GuildOf(fid)
  Int tries = 0
  While tries < 6
    Int pick = PickQuest(fid, guild, speaker)
    If pick < 0
      Debug.MessageBox("I have no work for you at the moment.")
      Return
    EndIf
    Int questFaction = fid
    If guild >= 0
      questFaction = GuildFaction[guild]
    EndIf
    If StartQuest(pick, speaker, questFaction, True)
      Return
    EndIf
    tries += 1
  EndWhile
  Debug.MessageBox("I have no work for you at the moment.")
EndFunction

; The speaker's Daggerfall fid (the Skyrim factions conversations put them in).
Int Function DfFactionOf(Actor a)
  Faction[] fs = a.GetFactions(-128, 127)
  Int i = 0
  While i < fs.Length
    Int k = FactionForms.Find(fs[i])
    If k >= 0
      Return FactionFormIds[k]
    EndIf
    i += 1
  EndWhile
  Return 0
EndFunction

Int Function GuildOf(Int fid)
  If fid <= 0 || !Guilds
    Return -1
  EndIf
  Return GuildFaction.Find(fid)
EndFunction

; A quest for this giver (Daggerfall Unity QuestListsManager): guilds by membership and rank (or reputation for
; ranks of 10 and more), townsfolk by the player's level.
Int Function PickQuest(Int fid, Int guild, Actor speaker)
  String letters = ""
  Bool member = False
  Int rank = 0
  Float rep = Reputation.GetRep(fid)
  If guild >= 0
    letters = GuildLetters[guild]
    member = Guilds.IsMember(guild)
    If member
      rank = PlayerRef.GetFactionRank(Guilds.Members[guild])
    EndIf
    rep = Reputation.GetRep(GuildFaction[guild])
  Else
    Int kind = Reputation.KindOf(fid)
    If kind == 8
      letters = "Q"
    ElseIf kind == 6
      letters = "P"
    ElseIf NobilityGroup && speaker.IsInFaction(NobilityGroup)
      letters = "R"
    ElseIf MerchantsGroup && speaker.IsInFaction(MerchantsGroup)
      letters = "K"
    Else
      letters = "A"
    EndIf
  EndIf
  Int level = PlayerRef.GetLevel()
  Int found = -1
  Int count = 0
  Int i = 0
  While i < Quests.Length
    String c = StringUtil.AsChar(QuestLetter[i])
    If StringUtil.Find(letters, c) >= 0 && !Quests[i].IsRunning() && !(QuestOneTime[i] && DoneOnce.Find(Quests[i]) >= 0)
      Bool ok = False
      Int req = QuestMinReq[i]
      If guild >= 0
        Bool wantMember = QuestMembership[i] == 66   ; 'B'
        If wantMember == member
          ok = (!member && req == 0) || (req < 10 && req <= rank) || (req >= 10 && req <= rep)
        EndIf
      Else
        ok = (req < 10 && req <= level) || rep >= req
      EndIf
      If ok
        count += 1
        If Utility.RandomInt(1, count) == 1
          found = i
        EndIf
      EndIf
    EndIf
    i += 1
  EndWhile
  Return found
EndFunction


; Sets quest i up and, offered, asks; True when it was handled (taken, refused or started).
Bool Function StartQuest(Int i, Actor questor, Int fid, Bool offer)
  Quest q = Quests[i]
  TMDfQuest t = q as TMDfQuest
  If !t || q.IsRunning()
    Return False
  EndIf
  WaitForSites()
  If q.IsRunning()
    Return False
  EndIf
  If !q.Start()
    Return False
  EndIf
  If !t.Setup(questor, fid, CurrentTown)
    t.Abort()
    Return False
  EndIf
  If offer
    If t.Say(1000) != 0
      t.Say(1001)
      t.Abort()
      Return True
    EndIf
    t.Say(1002)
  EndIf
  RunningQuests.AddForm(q)
  t.Begin()
  Return True
EndFunction

Bool Function StartByName(String name, Actor questor, Int fid, Bool offer)
  Int i = QuestNames.Find(name)
  If i < 0
    Return False
  EndIf
  Return StartQuest(i, questor, fid, offer)
EndFunction

; "start quest N": the main quest's S000000N.
Function StartStoryQuest(Int n)
  String digits = n
  While StringUtil.GetLength(digits) < 7
    digits = "0" + digits
  EndWhile
  StartByName("S" + digits, None, 0, False)
EndFunction

Function QuestEnded(TMDfQuest q, Bool success)
  RunningQuests.RemoveAddedForm(q)
  Int i = Quests.Find(q)
  If i >= 0 && QuestOneTime[i]
    DoneOnce.AddForm(q)
  EndIf
  ; Daggerfall's endings (S0000008 starts the one for whoever gets the Totem: S0000100-S0000104, S0000106; S0000107 is a
  ; reputation change S0000005 makes on the way): the story is over.
  If StringUtil.Find(q.DfName, "S00001") == 0 && q.DfName != "S0000107"
    TMStoryOrder story = Game.GetFormFromFile(0x03372B, "Skyrim.esm") as TMStoryOrder
    If story
      story.StoryCompleted(0)
    EndIf
  EndIf
EndFunction

Bool Function IndividualBusy(Int fid, TMDfQuest asker)
  Int i = RunningQuests.GetSize()
  While i > 0
    i -= 1
    TMDfQuest t = RunningQuests.GetAt(i) as TMDfQuest
    If t && t != asker && t.UsesIndividual(fid)
      Return True
    EndIf
  EndWhile
  Return False
EndFunction

; ---- Events for the running quests ----

Function LocationChanged(Location here)
  Int town = TownOf(here)
  If town >= 0
    CurrentTown = town
  EndIf
  PassAll()
EndFunction

Function PassAll()
  Int i = RunningQuests.GetSize()
  While i > 0
    i -= 1
    TMDfQuest t = RunningQuests.GetAt(i) as TMDfQuest
    If t
      t.Pass()
    EndIf
  EndWhile
EndFunction

Function SpellCast(Spell s)
  Int k = DfSpells.Find(s)
  If k < 0
    Return
  EndIf
  Int i = RunningQuests.GetSize()
  While i > 0
    i -= 1
    TMDfQuest t = RunningQuests.GetAt(i) as TMDfQuest
    If t
      t.SpellCastByPlayer(DfSpellIds[k])
    EndIf
  EndWhile
EndFunction


; ---- Individuals ----

Bool Function IsIndividual(Int fid)
  Return Reputation.KindOf(fid) == 4
EndFunction

Actor Function FindIndividual(Int fid)
  Int s = 0
  While s < Sources.GetSize()
    TMDfQuestSites src = Sources.GetAt(s) as TMDfQuestSites
    Int k = src.IFaction.Find(fid)
    If k >= 0 && src.IRef[k]
      Return src.IRef[k]
    EndIf
    s += 1
  EndWhile
  Return None
EndFunction

Int Function IndividualFaction(ObjectReference who)
  Int s = 0
  While s < Sources.GetSize()
    TMDfQuestSites src = Sources.GetAt(s) as TMDfQuestSites
    Int k = src.IRef.Find(who as Actor)
    If k >= 0
      Return src.IFaction[k]
    EndIf
    s += 1
  EndWhile
  Return 0
EndFunction

; ---- Sites ----

Int Function TownOf(Location here)
  If !here
    Return -1
  EndIf
  Int s = 0
  While s < Sources.GetSize()
    TMDfQuestSites src = Sources.GetAt(s) as TMDfQuestSites
    Int k = src.TownLocation.Find(here)
    If k >= 0
      Return s * 100000 + 60000 + k
    EndIf
    s += 1
  EndWhile
  Return -1
EndFunction

Int Function SiteKind(Int site)
  If site < 0
    Return -1
  EndIf
  Return (site % 100000) / 30000
EndFunction

Int Function SiteLocal(Int site)
  Return (site % 100000) % 30000
EndFunction

; The site of the town a building or town site is in (-1 for dungeons).
Int Function SiteTown(Int site)
  Int kind = SiteKind(site)
  If kind == 2
    Return site
  ElseIf kind == 0
    Return (site / 100000) * 100000 + 60000 + Source(site).BTown[SiteLocal(site)]
  EndIf
  Return -1
EndFunction

; A building site's type (DFLocation.BuildingTypes: 14 the temple, 15 the tavern ...), -1 for other sites.
Int Function SiteBuildingType(Int site)
  If SiteKind(site) != 0
    Return -1
  EndIf
  TMDfQuestSites src = Source(site)
  If !src
    Return -1
  EndIf
  Return src.BType[SiteLocal(site)]
EndFunction

Int Function SiteRegion(Int site)
  Int kind = SiteKind(site)
  If kind < 0
    Return -1
  EndIf
  TMDfQuestSites src = Source(site)
  If kind == 1
    Return src.DRegion[SiteLocal(site)]
  EndIf
  Return src.TownRegion[SiteLocal(SiteTown(site))]
EndFunction

; A place for a quest (Daggerfall Unity Place): scope 0 a fixed site (p1 Daggerfall location id, p2 0 its streets,
; 1 its dungeon), 1 in this town, 2 elsewhere in the region; p1 0 a building of type p2 (-1 any, p3 1 any house), 1 a
; dungeon of type p2 (-1 any), 2 a town's streets. Sites the quest already uses are avoided.
Int Function FindSite(Int scope, Int p1, Int p2, Int p3, Int town, Int[] used, Int upTo)
  If scope == 0
    Return FixedSite(p1, p2)
  EndIf
  Int region = SiteRegion(town)
  If region < 0
    region = SiteRegion(CurrentTown)
  EndIf
  If p1 == 1
    ; A dungeon of the type in the region, else any dungeon of the region (Daggerfall Unity SelectRemoteDungeonSite's
    ; classic fallback); never another region's (the quest is dropped instead, as there).
    Int d = DungeonSite(region, p2, used, upTo)
    If d < 0
      d = DungeonSite(region, -1, used, upTo)
    EndIf
    Return d
  ElseIf p1 == 2
    Return TownSite(region, town)
  EndIf
  Int b = -1
  If scope == 1 && town >= 0
    b = BuildingSite(region, town, True, p2, p3, used, upTo)
  EndIf
  If b < 0
    b = BuildingSite(region, town, False, p2, p3, used, upTo)
  EndIf
  If b < 0 && p2 >= 17 && p2 <= 22
    b = BuildingSite(region, town, scope == 1, -1, 1, used, upTo)
  EndIf
  If b < 0
    b = BuildingSite(region, -1, False, p2, p3, used, upTo)
  EndIf
  Return b
EndFunction

Bool Function Used(Int site, Int[] used, Int upTo)
  Int i = 0
  While i < upTo && i < used.Length
    If used[i] == site
      Return True
    EndIf
    i += 1
  EndWhile
  Return False
EndFunction

Bool Function TypeFits(Int type, Int p2, Int p3)
  If p2 >= 0
    Return type == p2
  ElseIf p3 == 1
    Return type >= 17 && type <= 22
  ElseIf p3 == 2
    Return type == 0 || type == 2 || (type >= 5 && type <= 9) || type == 12 || type == 13
  EndIf
  Return True
EndFunction

; A random building: in town (here True) or in another town of the region (here False; region -1: anywhere).
Int Function BuildingSite(Int region, Int town, Bool here, Int p2, Int p3, Int[] used, Int upTo)
  Int found = -1
  Int count = 0
  Int s = 0
  While s < Sources.GetSize()
    TMDfQuestSites src = Sources.GetAt(s) as TMDfQuestSites
    If region < 0 || src.Regions.Find(region) >= 0
      Int t = 0
      While t < src.TownName.Length
        Int townSite = s * 100000 + 60000 + t
        If (region < 0 || src.TownRegion[t] == region) && (here == (townSite == town))
          Int b = src.TownFirst[t]
          Int last = b + src.TownCount[t]
          While b < last
            Int site = s * 100000 + b
            If TypeFits(src.BType[b], p2, p3) && !Used(site, used, upTo)
              count += 1
              If Utility.RandomInt(1, count) == 1
                found = site
              EndIf
            EndIf
            b += 1
          EndWhile
        EndIf
        t += 1
      EndWhile
    EndIf
    s += 1
  EndWhile
  Return found
EndFunction

Int Function DungeonSite(Int region, Int type, Int[] used, Int upTo)
  Int found = -1
  Int count = 0
  Int s = 0
  While s < Sources.GetSize()
    TMDfQuestSites src = Sources.GetAt(s) as TMDfQuestSites
    If region < 0 || src.Regions.Find(region) >= 0
      Int d = 0
      While d < src.DName.Length
        Int site = s * 100000 + 30000 + d
        If (region < 0 || src.DRegion[d] == region) && (type < 0 || src.DType[d] == type) && !Used(site, used, upTo)
          count += 1
          If Utility.RandomInt(1, count) == 1
            found = site
          EndIf
        EndIf
        d += 1
      EndWhile
    EndIf
    s += 1
  EndWhile
  Return found
EndFunction

Int Function TownSite(Int region, Int notTown)
  Int found = -1
  Int count = 0
  Int s = 0
  While s < Sources.GetSize()
    TMDfQuestSites src = Sources.GetAt(s) as TMDfQuestSites
    Int t = 0
    While t < src.TownName.Length
      Int site = s * 100000 + 60000 + t
      If (region < 0 || src.TownRegion[t] == region) && site != notTown
        count += 1
        If Utility.RandomInt(1, count) == 1
          found = site
        EndIf
      EndIf
      t += 1
    EndWhile
    s += 1
  EndWhile
  Return found
EndFunction

; A fixed place: the town (or coven) built somewhere is preferred over a place only marked on the map.
Int Function FixedSite(Int locId, Int dungeon)
  Int mapOnly = -1
  Int s = 0
  While s < Sources.GetSize()
    TMDfQuestSites src = Sources.GetAt(s) as TMDfQuestSites
    If dungeon == 1
      Int d = src.DLocId.Find(locId)
      If d >= 0
        Return s * 100000 + 30000 + d
      EndIf
    Else
      Int t = src.TownLocId.Find(locId)
      If t >= 0
        If src.TownLocation[t]
          Return s * 100000 + 60000 + t
        ElseIf mapOnly < 0
          mapOnly = s * 100000 + 60000 + t
        EndIf
      EndIf
    EndIf
    s += 1
  EndWhile
  Return mapOnly
EndFunction

; A site's references: 0 the door in, 1 where one arrives inside, 2 a quest spawn marker, 3 a quest item marker, 4 the
; map marker.
ObjectReference Function SiteRef(Int site, Int which)
  Int kind = SiteKind(site)
  If kind < 0
    Return None
  EndIf
  TMDfQuestSites src = Source(site)
  Int k = SiteLocal(site)
  ObjectReference r = None
  If kind == 0
    If which == 0
      r = src.BDoor[k]
    ElseIf which == 1
      r = src.BArrival[k]
    ElseIf which == 2
      r = src.BSpawn[k]
    ElseIf which == 3
      r = src.BItem[k]
    Else
      Return src.TownMap[src.BTown[k]]
    EndIf
    If !r
      r = src.BArrival[k]
    EndIf
  ElseIf kind == 1
    If which == 0
      r = src.DDoor[k]
    ElseIf which == 1
      r = src.DStart[k]
    ElseIf which == 2
      r = src.DSpawn[k]
    ElseIf which == 3
      r = src.DItem[k]
    Else
      Return src.DMap[k]
    EndIf
    If !r
      r = src.DStart[k]
    EndIf
  Else
    Return src.TownMap[k]
  EndIf
  Return r
EndFunction

Bool Function PlayerAt(Int site)
  Return RefAt(PlayerRef, site)
EndFunction

Bool Function RefAt(ObjectReference r, Int site)
  Int kind = SiteKind(site)
  If kind < 0 || !r
    Return False
  EndIf
  If kind == 2
    If r == PlayerRef && !PlayerRef.IsInInterior()
      Return TownOf(PlayerRef.GetCurrentLocation()) == site
    EndIf
    Return False
  EndIf
  ObjectReference inside = SiteRef(site, 1)
  If !inside || r.GetParentCell() != inside.GetParentCell()
    Return False
  EndIf
  ; Buildings of one layout share an interior: it is this one when the player came in from this town.
  Return kind == 1 || SiteTown(site) == CurrentTown
EndFunction

; The site (building or dungeon) a reference is inside, or -1 (outdoors, or a place no site is).
Int Function SiteOfRef(ObjectReference r)
  If !r || !r.IsInInterior()
    Return -1
  EndIf
  Return SiteOfCell(r.GetParentCell())
EndFunction

; The building or dungeon whose interior is this cell: the current town's buildings first, then those of the towns
; and the dungeons of its region (each site's arrival or start marker stands in its interior).
Int Function SiteOfCell(Cell c)
  If !c
    Return -1
  EndIf
  Int townSite = SiteTown(CurrentTown)
  Int region = SiteRegion(CurrentTown)
  Int s = 0
  While s < Sources.GetSize()
    TMDfQuestSites src = Sources.GetAt(s) as TMDfQuestSites
    If region < 0 || src.Regions.Find(region) >= 0
      Int t = 0
      While t < src.TownName.Length
        If (region < 0 || src.TownRegion[t] == region) && (townSite < 0 || s * 100000 + 60000 + t == townSite || region >= 0)
          Int b = src.TownFirst[t]
          Int last = b + src.TownCount[t]
          While b < last
            If (src.BArrival[b] && src.BArrival[b].GetParentCell() == c) || (src.BSpawn[b] && src.BSpawn[b].GetParentCell() == c)
              Return s * 100000 + b
            EndIf
            b += 1
          EndWhile
        EndIf
        t += 1
      EndWhile
      Int d = 0
      While d < src.DName.Length
        If (region < 0 || src.DRegion[d] == region) && src.DStart[d] && src.DStart[d].GetParentCell() == c
          Return s * 100000 + 30000 + d
        EndIf
        d += 1
      EndWhile
    EndIf
    s += 1
  EndWhile
  Return -1
EndFunction

; Text for a site as Daggerfall's macros name it: 1 the building (or dungeon), 2 and 3 the town (or dungeon), 4 the region.
String Function SiteText(Int site, Int mode)
  Int kind = SiteKind(site)
  If kind < 0
    Return ""
  EndIf
  TMDfQuestSites src = Source(site)
  Int k = SiteLocal(site)
  Int region = SiteRegion(site)
  If mode == 4
    Return RegionNames[region]
  EndIf
  If kind == 1
    Return src.DName[k]
  ElseIf kind == 2
    Return src.TownName[k]
  EndIf
  If mode == 1
    ; Daggerfall's own name for the building (Daggerfall Unity BuildingNames), else what kind it is.
    If src.BName && k < src.BName.Length && src.BName[k] != ""
      Return src.BName[k]
    EndIf
    Return BuildingLabels[src.BType[k]]
  EndIf
  Return src.TownName[src.BTown[k]]
EndFunction

Function RevealSite(Int site)
  ObjectReference m = SiteRef(site, 4)
  If m
    m.AddToMap(True)
  EndIf
EndFunction

; Travel minutes from a town to a site (map pixels apart, the longer way across).
Int Function TravelMinutes(Int fromTown, Int site)
  If fromTown < 0 || site < 0
    Return 0
  EndIf
  Int ax = PixelX(fromTown)
  Int ay = PixelY(fromTown)
  Int dx = Math.Abs(PixelX(site) - ax) as Int
  Int dy = Math.Abs(PixelY(site) - ay) as Int
  If dy > dx
    dx = dy
  EndIf
  Return dx * MinutesPerPixel
EndFunction

; A quest clock's travel to a site (Daggerfall Unity Clock.GetTravelTimeInSeconds): from where the player is
; (TravelTimeCalculator.GetPlayerTravelPosition: the map pixel outdoors, else the town the player is in, else the
; quest's), by cart, cautiously, camping out, over Daggerfall's terrain (the Iliac Bay's travel world, Travel.esp); one
; way, at least a day. Without the travel world the pixels at MinutesPerPixel.
Int Function ClockTravelMinutes(Int site, Int questTown)
  If site < 0
    Return 1440
  EndIf
  Int minutes = -1
  TMTravelWorld world = Quest.GetQuest("TMTravelWorldIliacBay") as TMTravelWorld
  If world
    Int px
    Int py
    If !PlayerRef.IsInInterior() && world.Has(PlayerRef.GetWorldSpace())
      px = world.PixelX(PlayerRef.GetPositionX())
      py = world.PixelY(PlayerRef.GetPositionY())
    ElseIf CurrentTown >= 0
      px = PixelX(CurrentTown)
      py = PixelY(CurrentTown)
    ElseIf questTown >= 0
      px = PixelX(questTown)
      py = PixelY(questTown)
    Else
      px = PixelX(site)
      py = PixelY(site)
    EndIf
    minutes = world.PixelMinutes(px, py, PixelX(site), PixelY(site), True, False, False, 192)
  Else
    Int from = CurrentTown
    If from < 0
      from = questTown
    EndIf
    minutes = TravelMinutes(from, site)
  EndIf
  If minutes < 1440
    minutes = 1440
  EndIf
  Return minutes
EndFunction

Int Function PixelX(Int site)
  TMDfQuestSites src = Source(site)
  If SiteKind(site) == 1
    Return src.DX[SiteLocal(site)]
  EndIf
  Return src.TownX[SiteLocal(SiteTown(site))]
EndFunction

Int Function PixelY(Int site)
  TMDfQuestSites src = Source(site)
  If SiteKind(site) == 1
    Return src.DY[SiteLocal(site)]
  EndIf
  Return src.TownY[SiteLocal(SiteTown(site))]
EndFunction

; ---- Things the quests make ----

ObjectReference Function NewTag()
  Return Holding.PlaceAtMe(NameTag, 1, True, False)
EndFunction

; Someone for a quest, at a site's quest marker: a named individual keeps the fid's name, anyone else gets a
; Daggerfall name.
ObjectReference Function MakePerson(Int site, Int fid, Int kind)
  ObjectReference spot = SiteRef(site, 2)
  If !spot
    spot = Holding
  EndIf
  Int sex = Utility.RandomInt(0, 1)
  Int ri = PersonRace(fid, site)
  ; Dressed for their trade (a building type's people), else for their faction's social group.
  Int dress = -1
  If kind >= 0 && kind < DressByBuilding.Length
    dress = DressByBuilding[kind]
  EndIf
  If dress < 0
    Int g = Reputation.SGroupOf(fid)
    If g < 0 || g >= DressBySGroup.Length
      g = 0
    EndIf
    dress = DressBySGroup[g]
  EndIf
  ActorBase base = PersonBases[((ri * 2 + sex) * Dresses + dress) * FacesPerDress + Utility.RandomInt(0, FacesPerDress - 1)]
  ObjectReference a = spot.PlaceAtMe(base, 1, True, False)
  String name
  If IsIndividual(fid)
    name = Reputation.NameOf(fid)
  Else
    name = PersonNames[(ri * 2 + sex) * NamesPerRace + Utility.RandomInt(0, NamesPerRace - 1)]
  EndIf
  a.SetDisplayName(name, True)
  Return a
EndFunction

; A quest person's race (as Daggerfall Unity's StaticNPC: the faction's race, else the region's Breton or Redguard).
Int Function PersonRace(Int fid, Int site)
  Int r = Reputation.RaceOf(fid)
  If r >= 0 && r < RaceOfDf.Length && RaceOfDf[r] >= 0
    Return RaceOfDf[r]
  EndIf
  Int region = SiteRegion(site)
  If region < 0
    region = SiteRegion(CurrentTown)
  EndIf
  If region >= 0 && region < RegionRedguard.Length && RegionRedguard[region]
    Return 1
  EndIf
  Return 0
EndFunction

Function PlaceStranger(Int site)
  MakePerson(site, 0, -1)
EndFunction

; "transfer pc inside _place_ marker N" (Daggerfall Unity TeleportPc): the dungeon's N-th quest spawn marker, the first
; (the site's spawn marker) when there is no such marker or none is named.
Function TransferPlayer(Int site, Int marker = -1)
  ObjectReference spot = None
  If marker >= 0 && SiteKind(site) == 1
    spot = SpawnMarker(site, marker)
  EndIf
  If !spot
    spot = SiteRef(site, 2)
  EndIf
  If spot
    PlayerRef.MoveTo(spot)
    OpenHeldDoors(site)
  EndIf
EndFunction

; A dungeon's N-th quest spawn marker (DSpawnAll / DSpawnDungeon), None without one.
ObjectReference Function SpawnMarker(Int site, Int n)
  TMDfQuestSites src = Source(site)
  If !src || !src.DSpawnAll || !src.DSpawnDungeon
    Return None
  EndIf
  Int local = SiteLocal(site)
  Int seen = 0
  Int k = 0
  While k < src.DSpawnAll.Length && k < src.DSpawnDungeon.Length
    If src.DSpawnDungeon[k] == local
      If seen == n
        Return src.DSpawnAll[k]
      EndIf
      seen += 1
    EndIf
    k += 1
  EndWhile
  Return None
EndFunction

; A transfer into a dungeon opens its magically held doors (Castle Daggerfall's foyer: Daggerfall Unity's special case,
; so the player isn't shut in behind doors no key or pick opens).
Function OpenHeldDoors(Int site)
  If SiteKind(site) != 1
    Return
  EndIf
  TMDfQuestSites src = Source(site)
  If !src || !src.DHeld || !src.DHeldDungeon
    Return
  EndIf
  Int local = SiteLocal(site)
  Int k = 0
  While k < src.DHeld.Length && k < src.DHeldDungeon.Length
    If src.DHeldDungeon[k] == local && src.DHeld[k]
      src.DHeld[k].Lock(False)
      TMDfDoor.SetDoor(src.DHeld[k], True)
    EndIf
    k += 1
  EndWhile
EndFunction

Form Function RandomItemOf(Int group, Bool reward)
  If group < 0 || group >= GroupItems.Length || !GroupItems[group] || GroupItems[group].GetSize() == 0
    Return None
  EndIf
  Return GroupItems[group].GetAt(Utility.RandomInt(0, GroupItems[group].GetSize() - 1))
EndFunction

; Daggerfall Unity's quest reward: 150-200 gold per power (guild rank + 1, else level / 2 + 1, at most 10).
; A quest's gold reward with no range given (Daggerfall Unity Item.CreateGold): 150-200 x (level / 2 + 1, or the guild
; rank + 1 for a guild's quest, 10 at most) x (the region's price level / 2 + 500) / 1000 x (the faction's power + 50) /
; 100 (50 for no guild); the Fighters Guild (faction 41) pays (10 + rank) / 10 of it (its AlterReward; no other guild alters it).
; Regional prices aren't tracked here, so the price level is Daggerfall's middle, 1000.
Int Function RewardGold(Int fid)
  Int playerMod = PlayerRef.GetLevel() / 2 + 1
  Int factionMod = 50
  Int g = GuildOf(fid)
  Bool member = g >= 0 && Guilds.IsMember(g)
  If member
    playerMod = PlayerRef.GetFactionRank(Guilds.Members[g]) + 1
    Int power = Reputation.PowerOf(fid)
    If power > 0
      factionMod = power
    EndIf
  EndIf
  If playerMod > 10
    playerMod = 10
  ElseIf playerMod < 1
    playerMod = 1
  EndIf
  Int regionPriceMod = 500
  Int amount = Utility.RandomInt(150 * playerMod, 200 * playerMod) * (regionPriceMod + 500) / 1000 * (factionMod + 50) / 100
  If member && g < GuildFaction.Length && GuildFaction[g] == 41
    amount = amount * (10 + PlayerRef.GetFactionRank(Guilds.Members[g])) / 10
  EndIf
  If amount < 1
    amount = 1
  EndIf
  Return amount
EndFunction

Int Function FactionOfType(Int type, Int town)
  Return Reputation.OfType(type, SiteRegion(town))
EndFunction

Int Function PeopleFaction(Int town)
  Int f = Reputation.OfType(15, SiteRegion(town))
  If f < 0
    Return CommonersFaction
  EndIf
  Return f
EndFunction

String Function FactionName(Int fid)
  Return Reputation.NameOf(fid)
EndFunction

; "add face" / "drop face" (a person's or a foe's): Daggerfall Unity's EscortingFaces, the faces of those travelling with
; the player on the HUD, nothing more (the quest itself hides and places the person: no one walks along). Skyrim's HUD
; has no faces: their names are kept (Faces) and each joining or leaving is shown as a notice.
String[] faceNames
Quest[] faceQuests

Function Face(Quest owner, String name, Bool joins)
  If !faceNames
    faceNames = new String[16]
    faceQuests = new Quest[16]
  EndIf
  Int k = FaceOf(owner, name)
  If joins
    If k < 0
      k = faceNames.Find("")
      If k < 0
        k = 0
      EndIf
      faceNames[k] = name
      faceQuests[k] = owner
      If name != ""
        Debug.Notification(name + " travels with you.")
      EndIf
    EndIf
  ElseIf k >= 0
    faceNames[k] = ""
    faceQuests[k] = None
    If name != ""
      Debug.Notification(name + " leaves you.")
    EndIf
  EndIf
EndFunction

Int Function FaceOf(Quest owner, String name)
  If !faceNames
    Return -1
  EndIf
  Int k = 0
  While k < faceNames.Length
    If faceQuests[k] == owner && faceNames[k] == name && name != ""
      Return k
    EndIf
    k += 1
  EndWhile
  Return -1
EndFunction

; A quest's faces go with it.
Function DropFaces(Quest owner)
  If !faceNames
    Return
  EndIf
  Int k = 0
  While k < faceNames.Length
    If faceQuests[k] == owner
      faceNames[k] = ""
      faceQuests[k] = None
    EndIf
    k += 1
  EndWhile
EndFunction

; "cure <disease>" (what 0: that one of Daggerfall's diseases only, Daggerfall Unity CureDisease), "cure vampirism" (1),
; "cure lycanthropy" (2: Skyrim's own cure, CompanionsHousekeepingScript.CurePlayer, else the beast form taken).
Function CurePlayer(Int what, Int disease = -1)
  If what == 0
    Spell s = DiseaseSpell(disease)
    If s
      PlayerRef.RemoveSpell(s)
    EndIf
  ElseIf what == 1
    ; Daggerfall's vampirism and its infection (DfDiseases.Vampirism), then Skyrim's own should the player have that.
    If Vampirism
      Int v = 0
      While v < Vampirism.GetSize()
        Spell vs = Vampirism.GetAt(v) as Spell
        If vs && PlayerRef.HasSpell(vs)
          PlayerRef.RemoveSpell(vs)
        EndIf
        v += 1
      EndWhile
    EndIf
    If VampireQuest && (VampireQuest as PlayerVampireQuestScript).VampireStatus > 0
      (VampireQuest as PlayerVampireQuestScript).VampireCure(PlayerRef)
    EndIf
  ElseIf what == 2
    If WerewolfCure
      (WerewolfCure as CompanionsHousekeepingScript).CurePlayer()
    ElseIf WerewolfPower
      PlayerRef.RemoveSpell(WerewolfPower)
    EndIf
    ; Daggerfall's lycanthropy and its infections (DfDiseases.Lycanthropy: werewolf and wereboar infection, then curse).
    If Lycanthropy
      Int k = 0
      While k < Lycanthropy.GetSize()
        Spell s = Lycanthropy.GetAt(k) as Spell
        If s && PlayerRef.HasSpell(s)
          PlayerRef.RemoveSpell(s)
        EndIf
        k += 1
      EndWhile
    EndIf
    ; CureLycanthropy: healed back to full.
    PlayerRef.RestoreActorValue("Health", 100000.0)
  EndIf
EndFunction

; "make pc ill with <disease>": Daggerfall's disease of that number (DfDiseases), resisted by nothing (Daggerfall Unity
; AssignBundle with BypassSavingThrows); caught at level 1 it doesn't take (TMDfDisease).
Function MakePlayerIll(Int disease)
  Spell s = DiseaseSpell(disease)
  If s && !PlayerRef.HasSpell(s)
    PlayerRef.AddSpell(s, False)
  EndIf
EndFunction

Spell Function DiseaseSpell(Int disease)
  If !Diseases || disease < 0 || disease >= Diseases.GetSize()
    Return None
  EndIf
  Return Diseases.GetAt(disease) as Spell
EndFunction

; Daggerfall's quest videos (ANIMnnnn.VID) as the videos step converts them.
Function PlayVideo(Int n)
  Game.PlayBink("TamrielMerge_DfAnim" + n + ".bik", True)
EndFunction

; "play sound N": Daggerfall's own sound with record id N in DAGGER.SND (the converter's DfSounds), else a stand-in.
Function PlayDfSound(Int n)
  Int k = -1
  If DfSoundIds
    k = DfSoundIds.Find(n)
  EndIf
  If k >= 0 && DfSounds[k]
    DfSounds[k].Play(PlayerRef)
  ElseIf QuestSound
    QuestSound.Play(PlayerRef)
  EndIf
EndFunction

Function CastDfSpell(Int dfSpell, ObjectReference f)
  Int k = DfSpellIds.Find(dfSpell)
  If k >= 0 && f
    DfSpells[k].Cast(f, f)
  EndIf
EndFunction

; Daggerfall's fixed text macros.
String Function MacroText(Int code, TMDfQuest q)
  If code == 1
    Return PlayerRef.GetRace().GetName()
  ElseIf code == 2
    Int r = SiteRegion(CurrentTown)
    If r >= 0
      Return RegionNames[r]
    EndIf
    Return "the Iliac Bay"
  ElseIf code == 3
    Return Oaths[Utility.RandomInt(0, Oaths.Length - 1)]
  ElseIf code == 4
    Return Gods[Utility.RandomInt(0, Gods.Length - 1)]
  ElseIf code == 5
    Return FactionName(FactionOfType(6, CurrentTown))
  ElseIf code == 7
    Int g = GuildOf(q.FactionId)
    If g >= 0 && Guilds.IsMember(g)
      Return Guilds.Title(g, PlayerRef.GetFactionRank(Guilds.Members[g]))
    EndIf
    Return PlayerRef.GetActorBase().GetName()
  ElseIf code == 8
    Return Direction(q, q.LastPlaceSite)
  ElseIf code == 9
    If CurrentTown >= 0
      Return SiteText(CurrentTown, 2)
    EndIf
    Return "town"
  ElseIf code == 10
    ; %rt: the title of the region's ruler (MacroHelper.RegentTitle / GetRulerTitle: FACTION.TXT's ruler of its province).
    Int rr = SiteRegion(CurrentTown)
    If RulerTitles && rr >= 0 && rr < RulerTitles.Length && RulerTitles[rr] != ""
      Return RulerTitles[rr]
    EndIf
    Return "Lord"
  ElseIf code == 11
    ; %rn: the ruler's name (the province's individual faction), else a name at random (RegentName).
    Int rn = SiteRegion(CurrentTown)
    If RulerNames && rn >= 0 && rn < RulerNames.Length && RulerNames[rn] != ""
      Return RulerNames[rn]
    EndIf
    Return MacroText(14, q)
  ElseIf code == 12
    Return FactionName(q.FactionId)
  ElseIf code == 13
    ; %qdt: the date of the quest's log entry, else of its start (Quest.GetCurrentLogMessageTime).
    Return DateOf(q.LogDay, q.LogMonth)
  ElseIf code == 14
    If Utility.RandomInt(0, 1) == 1
      Return FemaleNames[Utility.RandomInt(0, FemaleNames.Length - 1)]
    EndIf
    Return MaleNames[Utility.RandomInt(0, MaleNames.Length - 1)]
  EndIf
  Return ""
EndFunction

; %di: the way to the place the text last named (Quest.LastPlaceReferenced; place 0 before any), in Daggerfall Unity's
; eight words (TalkManager.DirectionVector2DirectionHintString); "this place" there already (GetBuildingCompassDirection);
; "...never mind..." when it can't be worked out (resolvingError: no place, or the player in an interior of no site).
String Function Direction(TMDfQuest q, Int site)
  If site < 0
    site = q.PlaceSiteOf(0)
  EndIf
  ObjectReference target = SiteRef(site, 0)
  If !target
    Return "...never mind..."
  EndIf
  If PlayerRef.IsInInterior() && target.GetParentCell() == PlayerRef.GetParentCell()
    Return "this place"
  EndIf
  ObjectReference from = PlayerRef
  If PlayerRef.IsInInterior()
    ; Inside: from the building the player is in (its site's own marker outside).
    from = SiteRef(SiteOfCell(PlayerRef.GetParentCell()), 0)
  EndIf
  If !from || from.IsInInterior() || target.IsInInterior() || target.GetWorldSpace() != from.GetWorldSpace()
    Return "...never mind..."
  EndIf
  Float dx = target.GetPositionX() - from.GetPositionX()
  Float dy = target.GetPositionY() - from.GetPositionY()
  If dx == 0.0 && dy == 0.0
    Return "this place"
  EndIf
  ; Degrees anticlockwise from east, as Daggerfall Unity measures it.
  Float angle = Math.acos(dx / Math.sqrt(dx * dx + dy * dy))
  If dy < 0.0
    angle = 360.0 - angle
  EndIf
  If angle < 22.5 || angle >= 337.5
    Return "east"
  ElseIf angle < 67.5
    Return "northeast"
  ElseIf angle < 112.5
    Return "north"
  ElseIf angle < 157.5
    Return "northwest"
  ElseIf angle < 202.5
    Return "west"
  ElseIf angle < 247.5
    Return "southwest"
  ElseIf angle < 292.5
    Return "south"
  EndIf
  Return "southeast"
EndFunction

; Today's date, as the date of a log entry is kept (TMDfQuest.LogDay / LogMonth).
String Function DateText()
  GlobalVariable month = Game.GetFormFromFile(0x36, "Skyrim.esm") as GlobalVariable
  GlobalVariable day = Game.GetFormFromFile(0x37, "Skyrim.esm") as GlobalVariable
  Return DateOf(day.GetValueInt(), month.GetValueInt())
EndFunction

String Function DateOf(Int day, Int month)
  String[] months = new String[12]
  months[0] = "Morning Star"
  months[1] = "Sun's Dawn"
  months[2] = "First Seed"
  months[3] = "Rain's Hand"
  months[4] = "Second Seed"
  months[5] = "Midyear"
  months[6] = "Sun's Height"
  months[7] = "Last Seed"
  months[8] = "Hearthfire"
  months[9] = "Frostfall"
  months[10] = "Sun's Dusk"
  months[11] = "Evening Star"
  Return day + " " + months[month % 12]
EndFunction
