ScriptName TMProgression extends Quest
{TamrielMerge's progression (docs/progression.md): the Tamriel menu (Interface\TamrielMerge\tamriel.swf, opened with
SKSE's UI.OpenCustomMenu from the "Tamriel" power or the "Tamriel Menu" control, U unless rebound in Skyrim's
Settings > Controls; our Interface\Controls\PC\controlmap.txt adds it) and the data behind it. Factions: every converted
game's joinable factions, found in their plugins at run time (so a game that isn't installed is just left out); the
menu lists each game's, marking the ones the player belongs to with their rank and title. Coin: each converted game's
money (kept by TMStoryOrder) and how much the player has of it. The menu asks for its data
when it has loaded (mod event TM_MenuReady) and says when it closes (TM_MenuClose).}

Actor Property PlayerRef Auto
Spell Property TamrielPower Auto
String Property MenuControl = "Tamriel Menu" AutoReadOnly
Int Property MenuKey = 22 Auto
{U: used only when the control map lacks "Tamriel Menu" (another mod's controlmap.txt won).}
String[] Property FactionPlugins Auto
Int[] Property FactionIds Auto
String[] Property FactionNames Auto
String[] Property FactionGames Auto
String[] Property FactionRanks Auto
{Rank titles, "|" between them.}

String Property MenuName = "CustomMenu" AutoReadOnly
String Property MenuFile = "TamrielMerge/tamriel" AutoReadOnly

Event OnInit()
  Setup()
EndEvent

Function Setup()
  If TamrielPower && !PlayerRef.HasSpell(TamrielPower)
    PlayerRef.AddSpell(TamrielPower, False)
  EndIf
  If SKSE.GetVersion() > 0
    UnregisterForAllKeys()
    UnregisterForAllControls()
    Int mapped = Input.GetMappedKey(MenuControl)
    If mapped >= 0 && mapped != 255
      RegisterForControl(MenuControl)
    Else
      RegisterForKey(MenuKey)
    EndIf
    RegisterForModEvent("TM_MenuReady", "OnTamrielReady")
    RegisterForModEvent("TM_MenuClose", "OnTamrielClose")
  EndIf
  ; The gold item's name and weight for the coin carried don't survive loading: set them again.
  TMStoryOrder story = StoryOrder()
  If story
    story.NameMoney()
    story.ApplyTravelRules()
  EndIf
  TMPickpockets pickpockets = (Self as Quest) as TMPickpockets
  If pickpockets
    pickpockets.Setup()
  EndIf
  TMNightDanger night = (Self as Quest) as TMNightDanger
  If night
    night.Setup()
  EndIf
EndFunction

; The story order (TamrielMerge Start.esp's script on Skyrim's MQ101), which keeps each time's coin; None without it.
TMStoryOrder Function StoryOrder()
  Return Game.GetFormFromFile(0x03372B, "Skyrim.esm") as TMStoryOrder
EndFunction

; Each converted game's coin, in story order, and how much the player has of it (TMStoryOrder.SendMoneyToMenu).
Function SendMoney()
  TMStoryOrder story = StoryOrder()
  If story
    story.SendMoneyToMenu()
    Return
  EndIf
  UI.InvokeString(MenuName, "_root.ClearMoney", "")
  UI.InvokeString(MenuName, "_root.AddMoney", "Skyrim|gold|" + PlayerRef.GetGoldAmount() + "|1|4")
  UI.InvokeString(MenuName, "_root.ShowMoney", "")
EndFunction

Event OnControlDown(String control)
  If control == MenuControl && !Utility.IsInMenuMode()
    OpenMenu()
  EndIf
EndEvent

Event OnKeyDown(Int keyCode)
  If keyCode == MenuKey && !Utility.IsInMenuMode()
    OpenMenu()
  EndIf
EndEvent

Function OpenMenu()
  If SKSE.GetVersion() <= 0
    Debug.MessageBox("The Tamriel menu needs SKSE.")
    Return
  EndIf
  UI.OpenCustomMenu(MenuFile)
EndFunction

; The menu has loaded: send it each game's coin, then the factions, game by game.
Event OnTamrielReady(String eventName, String strArg, Float numArg, Form sender)
  SendMoney()
  UI.InvokeString(MenuName, "_root.ClearFactions", "")
  Int i = 0
  While i < FactionIds.Length
    Faction f = Game.GetFormFromFile(FactionIds[i], FactionPlugins[i]) as Faction
    If f
      String line = FactionGames[i] + "|" + FactionNames[i]
      If PlayerRef.IsInFaction(f)
        Int rank = PlayerRef.GetFactionRank(f)
        String[] titles = StringUtil.Split(FactionRanks[i], "|")
        String title = ""
        If rank >= 0 && rank < titles.Length
          title = titles[rank]
        EndIf
        line += "|" + (rank + 1) + "|" + titles.Length + "|" + title
      Else
        line += "|0|" + StringUtil.Split(FactionRanks[i], "|").Length + "|"
      EndIf
      UI.InvokeString(MenuName, "_root.AddFaction", line)
    EndIf
    i += 1
  EndWhile
  UI.InvokeString(MenuName, "_root.ShowFactions", "")
EndEvent

Event OnTamrielClose(String eventName, String strArg, Float numArg, Form sender)
EndEvent
