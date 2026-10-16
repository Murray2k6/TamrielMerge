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

String Property MenuName = "CustomMenu" AutoReadOnly
Int Property Groups = 8 AutoReadOnly

Int[] taken
Int start = -1
Int spent = 0

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
  EndIf
  SendState(msg)
EndEvent

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
