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
ActorBase[] Property MaleBases Auto
ActorBase[] Property FemaleBases Auto
String[] Property MaleNames Auto
String[] Property FemaleNames Auto
FormList[] Property GroupItems Auto
{Per Daggerfall item group: Skyrim items to pick from.}
Spell[] Property DfSpells Auto
Int[] Property DfSpellIds Auto
Spell Property CureDisease Auto
FormList Property Diseases Auto
Quest Property VampireQuest Auto
Spell Property WerewolfPower Auto
Location Property IliacBay Auto
String[] Property RegionNames Auto
String[] Property BuildingLabels Auto
String[] Property Gods Auto
String[] Property Oaths Auto
Sound Property QuestSound Auto
Int Property MinutesPerPixel = 90 Auto
{Cautious travel time per map pixel, for clocks that count the way there.}
Int Property CommonersFaction = 0 Auto
Faction Property MerchantsGroup Auto
Faction Property NobilityGroup Auto
{The social group factions conversations put townsfolk in (TownTalk).}

Int Property CurrentTown = -1 Auto Hidden
Bool Property MainStarted = False Auto Hidden
Int[] legal

Event OnInit()
  legal = new Int[64]
  RegisterForSingleUpdate(5.0)
EndEvent

Event OnUpdate()
  If !legal
    legal = new Int[64]
  EndIf
  If !MainStarted && IliacBay && PlayerRef.IsInLocation(IliacBay)
    MainStarted = True
    StartByName("_TUTOR__", None, 0, False)
    StartByName("_BRISIEN", None, 0, False)
  EndIf
  RegisterForSingleUpdate(10.0)
EndEvent

; ---- Registration ----

Function Register(TMDfQuestSites s)
  If !s || Sources.Find(s) >= 0
    Return
  EndIf
  Sources.AddForm(s)
EndFunction

TMDfQuestSites Function Source(Int site)
  If site < 0
    Return None
  EndIf
  Return Sources.GetAt(site / 100000) as TMDfQuestSites
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
    Int d = DungeonSite(region, p2, used, upTo)
    If d < 0
      d = DungeonSite(region, -1, used, upTo)
    EndIf
    If d < 0
      d = DungeonSite(-1, -1, used, upTo)
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

Int Function FixedSite(Int locId, Int dungeon)
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
        Return s * 100000 + 60000 + t
      EndIf
    EndIf
    s += 1
  EndWhile
  Return -1
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

Int Function SiteOfRef(ObjectReference r)
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
  Bool female = Utility.RandomInt(0, 1) == 1
  ActorBase base
  If female
    base = FemaleBases[Utility.RandomInt(0, FemaleBases.Length - 1)]
  Else
    base = MaleBases[Utility.RandomInt(0, MaleBases.Length - 1)]
  EndIf
  ObjectReference a = spot.PlaceAtMe(base, 1, True, False)
  String name
  If IsIndividual(fid)
    name = Reputation.NameOf(fid)
  ElseIf female
    name = FemaleNames[Utility.RandomInt(0, FemaleNames.Length - 1)]
  Else
    name = MaleNames[Utility.RandomInt(0, MaleNames.Length - 1)]
  EndIf
  a.SetDisplayName(name, True)
  Return a
EndFunction

Function PlaceStranger(Int site)
  MakePerson(site, 0, -1)
EndFunction

Function TransferPlayer(Int site)
  ObjectReference spot = SiteRef(site, 2)
  If spot
    PlayerRef.MoveTo(spot)
  EndIf
EndFunction

Form Function RandomItemOf(Int group, Bool reward)
  If group < 0 || group >= GroupItems.Length || !GroupItems[group] || GroupItems[group].GetSize() == 0
    Return None
  EndIf
  Return GroupItems[group].GetAt(Utility.RandomInt(0, GroupItems[group].GetSize() - 1))
EndFunction

; Daggerfall Unity's quest reward: 150-200 gold per power (guild rank + 1, else level / 2 + 1, at most 10).
Int Function RewardGold(Int fid)
  Int power = PlayerRef.GetLevel() / 2 + 1
  Int g = GuildOf(fid)
  If g >= 0 && Guilds.IsMember(g)
    power = PlayerRef.GetFactionRank(Guilds.Members[g]) + 1
  EndIf
  If power > 10
    power = 10
  ElseIf power < 1
    power = 1
  EndIf
  Return Utility.RandomInt(150 * power, 200 * power)
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

Function Escort(String name, Bool joins)
  If joins
    Debug.Notification(name + " travels with you.")
  Else
    Debug.Notification(name + " leaves you.")
  EndIf
EndFunction

Function CurePlayer(Int what)
  If what == 0 && CureDisease
    CureDisease.Cast(PlayerRef, PlayerRef)
  ElseIf what == 1 && VampireQuest
    (VampireQuest as PlayerVampireQuestScript).VampireCure(PlayerRef)
  ElseIf what == 2 && WerewolfPower
    PlayerRef.RemoveSpell(WerewolfPower)
  EndIf
EndFunction

Function MakePlayerIll(Int disease)
  If Diseases && Diseases.GetSize() > 0
    Spell s = Diseases.GetAt(disease % Diseases.GetSize()) as Spell
    If s
      PlayerRef.AddSpell(s, False)
    EndIf
  EndIf
EndFunction

; Daggerfall's quest videos (ANIMnnnn.VID) as the videos step converts them.
Function PlayVideo(Int n)
  Game.PlayBink("TamrielMerge_DfAnim" + n + ".bik", True)
EndFunction

Function PlayDfSound(Int n)
  If QuestSound
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
    Return Direction(q)
  ElseIf code == 9
    If CurrentTown >= 0
      Return SiteText(CurrentTown, 2)
    EndIf
    Return "town"
  ElseIf code == 10
    Return "Lord"
  ElseIf code == 11
    Return "the ruler of " + MacroText(2, q)
  ElseIf code == 12
    Return FactionName(q.FactionId)
  ElseIf code == 13
    Return DateText()
  ElseIf code == 14
    If Utility.RandomInt(0, 1) == 1
      Return FemaleNames[Utility.RandomInt(0, FemaleNames.Length - 1)]
    EndIf
    Return MaleNames[Utility.RandomInt(0, MaleNames.Length - 1)]
  EndIf
  Return ""
EndFunction

String Function Direction(TMDfQuest q)
  ObjectReference target = SiteRef(q.PlaceSiteOf(0), 0)
  If !target || PlayerRef.IsInInterior() || target.GetWorldSpace() != PlayerRef.GetWorldSpace()
    Return "that way"
  EndIf
  Float dx = target.GetPositionX() - PlayerRef.GetPositionX()
  Float dy = target.GetPositionY() - PlayerRef.GetPositionY()
  String[] names = new String[8]
  names[0] = "north"
  names[1] = "north-east"
  names[2] = "east"
  names[3] = "south-east"
  names[4] = "south"
  names[5] = "south-west"
  names[6] = "west"
  names[7] = "north-west"
  Float angle = Math.atan(dx / (dy + 0.001))
  If dy < 0.0
    angle += 180.0
  ElseIf dx < 0.0
    angle += 360.0
  EndIf
  Return names[(((angle + 22.5) / 45.0) as Int) % 8]
EndFunction

String Function DateText()
  GlobalVariable month = Game.GetFormFromFile(0x36, "Skyrim.esm") as GlobalVariable
  GlobalVariable day = Game.GetFormFromFile(0x37, "Skyrim.esm") as GlobalVariable
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
  Return day.GetValueInt() + " " + months[month.GetValueInt() % 12]
EndFunction
