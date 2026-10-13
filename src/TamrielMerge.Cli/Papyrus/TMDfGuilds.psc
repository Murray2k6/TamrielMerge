ScriptName TMDfGuilds extends Quest
{Daggerfall's guilds (rules from Daggerfall Unity's Guild classes, MIT; messages from TEXT.RSC): the Mages and Fighters
Guilds, the Thieves Guild and Dark Brotherhood, the eight temples and the ten knightly orders.
- Joining: reputation with the guild 0 or more, and in the guild's skills at least one at 22 and another at 4.
- Rank (0-9): the highest whose requirements are met: reputation 10 x rank, one guild skill at the high mark
  (22 23 31 39 47 55 63 71 79 87) and another at the low mark (4 5 9 13 17 21 25 29 33 37). It is checked once a
  day and changes at most every 28 days: promotion, demotion, or expulsion below reputation 0.
- The Thieves Guild invites after ten pickpockets or a shop broken into; the Dark Brotherhood after three murders.
- Services: training in the guild's skills (members), healing and cures at temples.
Skyrim skills stand for Daggerfall's (lists made by the converter). Texts use SKSE's StringUtil for the player's
name and title.}

Actor Property PlayerRef Auto
Faction[] Property Members Auto
{The player's membership faction per guild; its rank is the guild rank.}
GlobalVariable[] Property Reputation Auto
Int[] Property SkillFirst Auto
Int[] Property SkillCount Auto
String[] Property Skills Auto
{Actor value names, guild by guild.}
Bool[] Property Joinable Auto
Int[] Property TitleKind Auto
String[] Property Titles Auto
{Rank titles, kind by kind (10 each).}
String[] Property GuildNames Auto
String[] Property EligibleText Auto
String[] Property LowSkillText Auto
String[] Property BadRepText Auto
String[] Property WelcomeText Auto
String[] Property PromotionText Auto
{Per guild: ten promotion messages joined by "|" (rank 0 to 9).}
String Property DemotionText Auto
String Property ExpulsionText Auto
Int Property ThievesIndex = -1 Auto
Int Property BrotherhoodIndex = -1 Auto
Int Property ArkayIndex = -1 Auto
GlobalVariable Property BreakIns Auto
Spell Property TempleCure Auto
GlobalVariable Property GameMonth Auto
GlobalVariable Property GameDay Auto

Float[] lastChange

Event OnInit()
  lastChange = new Float[32]
  RegisterForSingleUpdateGameTime(24.0)
EndEvent

Event OnUpdateGameTime()
  CheckInvitations()
  CheckRanks()
  RegisterForSingleUpdateGameTime(24.0)
EndEvent

Int Function HighMark(Int rank)
  If rank <= 0
    Return 22
  ElseIf rank == 1
    Return 23
  EndIf
  Return 31 + (rank - 2) * 8
EndFunction

Int Function LowMark(Int rank)
  If rank <= 0
    Return 4
  ElseIf rank == 1
    Return 5
  EndIf
  Return 9 + (rank - 2) * 4
EndFunction

Bool Function SkillsMeet(Int g, Int rank)
  Int high = 0
  Int low = 0
  Int i = SkillFirst[g]
  Int last = SkillFirst[g] + SkillCount[g]
  While i < last
    Float v = PlayerRef.GetActorValue(Skills[i])
    If v >= HighMark(rank)
      high += 1
    ElseIf v >= LowMark(rank)
      low += 1
    EndIf
    i += 1
  EndWhile
  Return high >= 1 && high + low >= 2
EndFunction

Int Function RankFor(Int g)
  Float rep = Reputation[g].GetValue()
  Int rank = 9
  While rank > 0
    If rep >= rank * 10 && SkillsMeet(g, rank)
      Return rank
    EndIf
    rank -= 1
  EndWhile
  Return 0
EndFunction

Bool Function IsMember(Int g)
  Return PlayerRef.IsInFaction(Members[g])
EndFunction

String Function Title(Int g, Int rank)
  Return Titles[TitleKind[g] * 10 + rank]
EndFunction

; Daggerfall's macros said at the time: the player's name and title.
String Function Fill(String s, Int g, Int rank)
  s = Replace(s, "%lev", Title(g, rank))
  String name = PlayerRef.GetBaseObject().GetName()
  s = Replace(s, "%pcn", name)
  s = Replace(s, "%pcf", name)
  Return s
EndFunction

String Function Replace(String s, String find, String with)
  Int at = StringUtil.Find(s, find)
  While at >= 0
    s = StringUtil.Substring(s, 0, at) + with + StringUtil.Substring(s, at + StringUtil.GetLength(find))
    at = StringUtil.Find(s, find, at + StringUtil.GetLength(with))
  EndWhile
  Return s
EndFunction

String Function Promotion(Int g, Int rank)
  String[] parts = StringUtil.Split(PromotionText[g], "|")
  If rank < parts.Length && parts[rank] != ""
    Return parts[rank]
  EndIf
  Return "You have been promoted to the rank of %lev."
EndFunction

Function Enroll(Int g, String text)
  PlayerRef.AddToFaction(Members[g])
  PlayerRef.SetFactionRank(Members[g], 0)
  lastChange[g] = Utility.GetCurrentGameTime()
  Debug.MessageBox(Fill(text, g, 0))
EndFunction

; "I'd like to join." from a member of guild g.
Function Join(Int g)
  If IsMember(g)
    Debug.MessageBox("You are already a " + Title(g, PlayerRef.GetFactionRank(Members[g])) + " of " + GuildNames[g] + ".")
    Return
  EndIf
  If !Joinable[g]
    Debug.MessageBox(GuildNames[g] + " does not take members who walk in off the street.")
    Return
  EndIf
  If Reputation[g].GetValue() < 0
    Debug.MessageBox(Fill(BadRepText[g], g, 0))
    Return
  EndIf
  If !SkillsMeet(g, 0)
    Debug.MessageBox(Fill(LowSkillText[g], g, 0))
    Return
  EndIf
  Enroll(g, EligibleText[g] + "\n\n" + WelcomeText[g])
EndFunction

Function CheckRanks()
  Float now = Utility.GetCurrentGameTime()
  Int g = 0
  While g < Members.Length
    If IsMember(g) && now - lastChange[g] >= 28.0
      Int rank = PlayerRef.GetFactionRank(Members[g])
      If Reputation[g].GetValue() < 0
        PlayerRef.RemoveFromFaction(Members[g])
        Debug.MessageBox(Fill(ExpulsionText, g, 0))
      Else
        Int newRank = RankFor(g)
        If newRank > rank
          PlayerRef.SetFactionRank(Members[g], newRank)
          lastChange[g] = now
          Debug.MessageBox(Fill(Promotion(g, newRank), g, newRank))
        ElseIf newRank < rank
          PlayerRef.SetFactionRank(Members[g], newRank)
          lastChange[g] = now
          Debug.MessageBox(Fill(DemotionText, g, newRank))
        EndIf
      EndIf
    EndIf
    g += 1
  EndWhile
EndFunction

Function CheckInvitations()
  If ThievesIndex >= 0 && !IsMember(ThievesIndex) && Reputation[ThievesIndex].GetValue() >= 0
    If Game.QueryStat("Pockets Picked") >= 10 || (BreakIns && BreakIns.GetValue() >= 1)
      Enroll(ThievesIndex, WelcomeText[ThievesIndex])
    EndIf
  EndIf
  If BrotherhoodIndex >= 0 && !IsMember(BrotherhoodIndex) && Reputation[BrotherhoodIndex].GetValue() >= 0
    If Game.QueryStat("People Killed") >= 3
      Enroll(BrotherhoodIndex, WelcomeText[BrotherhoodIndex])
    EndIf
  EndIf
EndFunction

; Training: Skyrim's training menu with the speaker (a trainer in one of the guild's skills, up to 50).
Function Train(Int g, Actor trainer)
  If !IsMember(g)
    Debug.MessageBox("Training is only for members of " + GuildNames[g] + ".")
    Return
  EndIf
  Game.ShowTrainingMenu(trainer)
EndFunction

; Temple healing: health restored and diseases cured; Arkay's members pay 10% less per rank (Daggerfall's Arkay perk).
Function Heal(Int g)
  Float missing = PlayerRef.GetBaseActorValue("Health") - PlayerRef.GetActorValue("Health")
  Int price = 25 + (missing as Int)
  If g == ArkayIndex && IsMember(g)
    price = price * (10 - PlayerRef.GetFactionRank(Members[g])) / 10
  EndIf
  ; Daggerfall's healing holidays: South Wind's Prayer, First Planting, Second Planting.
  If GameMonth && GameDay
    Int month = GameMonth.GetValueInt()
    Int day = GameDay.GetValueInt()
    If month == 0 && day == 15 || month == 2 && day == 7 || month == 4 && day == 7
      price = 0
    EndIf
  EndIf
  Form gold = Game.GetForm(0xF)
  If PlayerRef.GetItemCount(gold) < price
    Debug.MessageBox("Healing costs " + price + " gold. You do not have enough.")
    Return
  EndIf
  PlayerRef.RemoveItem(gold, price)
  PlayerRef.RestoreActorValue("Health", missing + 1.0)
  If TempleCure
    TempleCure.Cast(PlayerRef, PlayerRef)
  EndIf
  Debug.MessageBox("You are healed, for " + price + " gold.")
EndFunction
