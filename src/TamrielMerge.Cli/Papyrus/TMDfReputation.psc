ScriptName TMDfReputation extends Quest
{The player's standing with Daggerfall's factions (FACTION.TXT), kept in the DFReact<id> globals that conversations
(TMDfTalk greetings) and the guilds (TMDfGuilds.Reputation: promotion needs 10 x rank) read. Changes follow Daggerfall
Unity's PersistentFactionData.ChangeReputation (MIT): with propagation, allies gain and enemies lose half; a knightly
order changes itself and the generic order; otherwise the whole tree from the root down changes, the faction itself and
the roots fully and the rest by half, and a god's change reaches the generic temple too. Values stay within -100..100.}

Int[] Property Ids Auto
Int[] Property Parents Auto
Int[] Property Kind Auto
{FACTION.TXT type: 1 god, 4 individual, 6 vampire clan, 8 coven, 9 temple, 15 people ...}
Int[] Property GGroup Auto
Int[] Property Region Auto
Int[] Property Allies Auto
{Three per faction (0 none).}
Int[] Property Enemies Auto
String[] Property Names Auto
Int[] Property Races Auto
{FACTION.TXT race (0 Nord, 1 Khajiit, 2 Redguard, 3 Breton, 4 Argonian, 5 Wood Elf, 6 High Elf, 7 Dark Elf, 17 Orc;
-1 none).}
Int[] Property SGroups Auto
{The social group of the faction's people (its own, else the nearest parent's, as conversations count them).}
GlobalVariable[] Property React Auto
Int Property GenericTemple = 450 Auto
Int Property GenericKnightlyOrder = 844 Auto
Int Property DarkBrotherhoodId = 108 Auto
Int Property KnightlyGuild = 9 Auto
{ggroup of knightly orders.}

Int Function IndexOf(Int id)
  If id <= 0
    Return -1
  EndIf
  Return Ids.Find(id)
EndFunction

Int Function RaceOf(Int id)
  Int k = IndexOf(id)
  If k < 0 || !Races
    Return -1
  EndIf
  Return Races[k]
EndFunction

Int Function SGroupOf(Int id)
  Int k = IndexOf(id)
  If k < 0 || !SGroups
    Return 0
  EndIf
  Return SGroups[k]
EndFunction

Float Function GetRep(Int id)
  Int k = IndexOf(id)
  If k < 0
    Return 0.0
  EndIf
  Return React[k].GetValue()
EndFunction

Function SetRep(Int k, Float value)
  If value > 100.0
    value = 100.0
  ElseIf value < -100.0
    value = -100.0
  EndIf
  React[k].SetValue(value)
EndFunction

Function ChangeRep(Int id, Int amount, Bool propagate)
  Int k = IndexOf(id)
  If k < 0 || amount == 0
    Return
  EndIf
  If !propagate
    SetRep(k, React[k].GetValue() + amount)
    Return
  EndIf
  Int a = 0
  While a < 3
    ChangeRep(Allies[k * 3 + a], amount / 2, False)
    ChangeRep(Enemies[k * 3 + a], -amount / 2, False)
    a += 1
  EndWhile
  If GGroup[k] == KnightlyGuild
    ChangeRep(id, amount, False)
    If id != GenericKnightlyOrder
      ChangeRep(GenericKnightlyOrder, amount, True)
    EndIf
    Return
  EndIf
  ; Up to the root (the Dark Brotherhood counts as one).
  Int root = k
  While Parents[root] > 0 && IndexOf(Parents[root]) >= 0 && Ids[root] != DarkBrotherhoodId
    root = IndexOf(Parents[root])
  EndWhile
  If HasChildren(Ids[root])
    Propagate(root, id, amount)
  Else
    ChangeRep(id, amount, False)
  EndIf
  If Kind[root] == 1 && id != GenericTemple
    ChangeRep(GenericTemple, amount, True)
  EndIf
EndFunction

Bool Function HasChildren(Int id)
  Return Parents.Find(id) >= 0
EndFunction

Function Propagate(Int k, Int from, Int amount)
  Int change = amount / 2
  If Ids[k] == from || Parents[k] == 0
    change = amount
  EndIf
  SetRep(k, React[k].GetValue() + change)
  Int c = Parents.Find(Ids[k])
  While c >= 0
    Propagate(c, from, amount)
    c = Parents.Find(Ids[k], c + 1)
  EndWhile
EndFunction

String Function NameOf(Int id)
  Int k = IndexOf(id)
  If k < 0
    Return ""
  EndIf
  Return Names[k]
EndFunction

Int Function KindOf(Int id)
  Int k = IndexOf(id)
  If k < 0
    Return -1
  EndIf
  Return Kind[k]
EndFunction

; A faction of a type: in the region if there is one, else any.
Int Function OfType(Int type, Int inRegion)
  Int found = -1
  Int count = 0
  Int k = 0
  While k < Ids.Length
    If Kind[k] == type && (inRegion < 0 || Region[k] == inRegion)
      count += 1
      If Utility.RandomInt(1, count) == 1
        found = Ids[k]
      EndIf
    EndIf
    k += 1
  EndWhile
  If found < 0 && inRegion >= 0
    Return OfType(type, -1)
  EndIf
  Return found
EndFunction
