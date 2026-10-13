ScriptName TMPickpockets extends Quest
{Pickpockets (docs/progression.md, "Pickpockets"): the thieves of each converted game (Daggerfall's underworld,
Morrowind's and Oblivion's thieves and beggars, Skyrim's Thieves Guild) now and then lift a little coin or a small item
(a potion, an ingredient, a lockpick, a soul gem) from the player, who is not told. Every Interval seconds there is a
TryChance% chance that someone within reach tries: a member of a pickpocket faction who is not hostile, in combat, a
follower or a child, shares no faction with the player (no thief robs a fellow guild member) and hasn't tried in the
last game day. They succeed with 30 + their Pickpocket / 2 - the player's Sneak / 2 percent (5 to 70); a failed try
is noticed half the time. What they take goes into their own pockets, where it can be stolen or taken back. The
TMPickpocketsOn global (1) turns them off at 0.}

Actor Property PlayerRef Auto
GlobalVariable Property Enabled Auto
String[] Property FactionPlugins Auto
Int[] Property FactionIds Auto
Float Property Interval = 30.0 Auto
Int Property TryChance = 25 Auto
Float Property Reach = 250.0 Auto

Faction[] thieves
Actor[] recent
Float[] recentAt
Int recentNext = 0

; After each load (TMProgression.Setup): find the pickpocket factions of the games installed, and start the rounds.
Function Setup()
  thieves = new Faction[16]
  Int n = 0
  Int i = 0
  While i < FactionIds.Length && n < 16
    Faction f = Game.GetFormFromFile(FactionIds[i], FactionPlugins[i]) as Faction
    If f
      thieves[n] = f
      n += 1
    EndIf
    i += 1
  EndWhile
  If !recent
    recent = new Actor[16]
    recentAt = new Float[16]
  EndIf
  RegisterForSingleUpdate(Interval)
EndFunction

Event OnUpdate()
  If Enabled && Enabled.GetValue() > 0 && thieves && !Utility.IsInMenuMode() && !PlayerRef.IsInCombat() && Utility.RandomInt(1, 100) <= TryChance
    Actor thief = FindThief()
    If thief
      Attempt(thief)
    EndIf
  EndIf
  RegisterForSingleUpdate(Interval)
EndEvent

Actor Function FindThief()
  Int tries = 0
  While tries < 4
    Actor a = Game.FindRandomActorFromRef(PlayerRef, Reach)
    If a && a != PlayerRef && IsThief(a) && !TriedLately(a)
      Return a
    EndIf
    tries += 1
  EndWhile
  Return None
EndFunction

Bool Function IsThief(Actor a)
  If a.IsDead() || a.IsInCombat() || a.IsPlayerTeammate() || a.IsChild() || a.IsHostileToActor(PlayerRef) || a.IsBleedingOut()
    Return False
  EndIf
  Bool member = False
  Int i = 0
  While i < thieves.Length && !member
    If thieves[i] && a.IsInFaction(thieves[i])
      member = True
    EndIf
    i += 1
  EndWhile
  If !member
    Return False
  EndIf
  ; Fellow members are spared.
  Faction[] own = a.GetFactions(0, 127)
  i = 0
  While own && i < own.Length
    If own[i] && thieves.Find(own[i]) < 0 && PlayerRef.IsInFaction(own[i])
      Return False
    EndIf
    i += 1
  EndWhile
  Return True
EndFunction

Bool Function TriedLately(Actor a)
  Int i = recent.Find(a)
  Return i >= 0 && Utility.GetCurrentGameTime() - recentAt[i] < 1.0
EndFunction

Function Remember(Actor a)
  Int i = recent.Find(a)
  If i < 0
    i = recentNext
    recentNext = (recentNext + 1) % recent.Length
  EndIf
  recent[i] = a
  recentAt[i] = Utility.GetCurrentGameTime()
EndFunction

Function Attempt(Actor thief)
  Remember(thief)
  Int chance = 30 + (thief.GetActorValue("Pickpocket") as Int) / 2 - (PlayerRef.GetActorValue("Sneak") as Int) / 2
  If chance < 5
    chance = 5
  ElseIf chance > 70
    chance = 70
  EndIf
  If Utility.RandomInt(1, 100) > chance
    If Utility.RandomInt(1, 2) == 1
      Debug.Notification("You catch " + thief.GetDisplayName() + " with a hand in your pocket.")
    EndIf
    Return
  EndIf
  If Utility.RandomInt(1, 100) <= 65 && StealCoin(thief)
    Return
  EndIf
  If !StealItem(thief)
    StealCoin(thief)
  EndIf
EndFunction

Bool Function StealCoin(Actor thief)
  MiscObject gold = Game.GetFormFromFile(0x00000F, "Skyrim.esm") as MiscObject
  Int carried = PlayerRef.GetItemCount(gold)
  If carried <= 0
    Return False
  EndIf
  Int take = Utility.RandomInt(3, 10 + (thief.GetActorValue("Pickpocket") as Int))
  If take > carried / 4 + 1
    take = carried / 4 + 1
  EndIf
  If take > carried
    take = carried
  EndIf
  PlayerRef.RemoveItem(gold, take, True, thief)
  Debug.Trace("[TamrielMerge] " + thief + " picked " + take + " coin from the player")
  Return True
EndFunction

Bool Function StealItem(Actor thief)
  Int count = PlayerRef.GetNumItems()
  If count <= 0
    Return False
  EndIf
  Int start = Utility.RandomInt(0, count - 1)
  Int i = 0
  While i < count
    Form f = PlayerRef.GetNthForm((start + i) % count)
    If Pocketable(f)
      PlayerRef.RemoveItem(f, 1, True, thief)
      Debug.Trace("[TamrielMerge] " + thief + " picked " + f + " from the player")
      Return True
    EndIf
    i += 1
  EndWhile
  Return False
EndFunction

; Small things a hand can take unnoticed, and never what the player has equipped.
Bool Function Pocketable(Form f)
  If !f || PlayerRef.IsEquipped(f)
    Return False
  EndIf
  If f as Potion || f as Ingredient || f as SoulGem
    Return True
  EndIf
  Return f.GetFormID() == 0x00000A   ; Skyrim's lockpick
EndFunction
