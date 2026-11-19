ScriptName TMTutorial extends Quest
{TamrielMerge's guide for a new character (user, 2026-10-15: "i need a tutorial popup on where stuff is after character
creation"; "we can start in any world"; "its based on what games the user owns"). On MQ101 beside the story order: the
first time the race menu closes, once for the character (whichever world it began in, Helgen's own race menu too,
never again in a later story), one welcome
prompt naming the games installed (TMStoryOrder.IsStoryAvailable) and the Tamriel menu's key, and the guide (every
page, the installed games' pages) handed over as a book to read. Needs SKSE (the race menu's event), as the Tamriel
menu does.}

String[] Property Pages Auto
{The general pages; "%KEY%" becomes the Tamriel Menu control's key, "%GAMES%" the games installed.}
String[] Property GameNames Auto
String[] Property KeyNames Auto
{DirectX scan codes' names (index = code).}
Book Property Guide Auto
GlobalVariable Property Welcomed Auto
{1 once the welcome has shown: a global, not this script's own variable, which Skyrim sets back each time MQ101 starts
(user, 2026-10-25: "the welcome only needs to happen once on any game the user owns but only once").}
String Property MenuControl = "Tamriel Menu" AutoReadOnly

Event OnInit()
  If SKSE.GetVersion() > 0 && Welcomed && Welcomed.GetValue() == 0
    RegisterForMenu("RaceSex Menu")
  EndIf
EndEvent

Event OnMenuClose(String menuName)
  If menuName == "RaceSex Menu"
    UnregisterForMenu("RaceSex Menu")
    If Welcomed && Welcomed.GetValue() == 0
      Welcomed.SetValue(1)
      RegisterForSingleUpdate(1.5)
    EndIf
  EndIf
EndEvent

Event OnUpdate()
  ; After the race menu's own aftermath (the start's kit, a fade): wait out any menu still open.
  If Utility.IsInMenuMode()
    RegisterForSingleUpdate(1.0)
    Return
  EndIf
  ShowGuide()
EndEvent

Function ShowGuide()
  TMStoryOrder story = (Self as Quest) as TMStoryOrder
  String games = ""
  Int i = 0
  While GameNames && i < GameNames.Length
    If !story || story.IsStoryAvailable(i)
      If games != ""
        games += ", "
      EndIf
      games += GameNames[i]
    EndIf
    i += 1
  EndWhile
  String keyName = "U"
  Int code = Input.GetMappedKey(MenuControl)
  If code > 0 && KeyNames && code < KeyNames.Length && KeyNames[code] != ""
    keyName = KeyNames[code]
  EndIf
  ; One prompt, the welcome page (user, 2026-10-25: "the welcome prompt comes up too many times when its suppose to be
  ; once": every page and each game's followed it, up to eight boxes); the rest is in the guide. Once for the player, not
  ; once a game (user, 2026-11-02: "the welcome popup to tamriel merge pops up too many times": the global lives in the
  ; save, and three new games showed it three times): kept outside the saves by TamrielMergeNative's flags.
  If Pages && Pages.Length > 0 && !WelcomedEver()
    Debug.MessageBox(Fill(Pages[0], keyName, games))
    If NativeFlags()
      TMNative.SetFlag("Welcomed", True)
    EndIf
  EndIf
  If Guide && Game.GetPlayer().GetItemCount(Guide) == 0
    Game.GetPlayer().AddItem(Guide, 1, true)
    Debug.Notification("TamrielMerge Guide added to your books.")
  EndIf
EndFunction

; TamrielMergeNative's flags are there (version 2 and later): the welcome is remembered outside the saves.
Bool Function NativeFlags()
  Return SKSE.GetPluginVersion("TamrielMergeNative") >= 2
EndFunction

Bool Function WelcomedEver()
  Return NativeFlags() && TMNative.GetFlag("Welcomed")
EndFunction

; A page with its placeholders filled.
String Function Fill(String page, String keyName, String games)
  page = Replace(page, "%KEY%", keyName)
  Return Replace(page, "%GAMES%", games)
EndFunction

String Function Replace(String text, String find, String with)
  Int at = StringUtil.Find(text, find)
  While at >= 0
    text = StringUtil.Substring(text, 0, at) + with + StringUtil.Substring(text, at + StringUtil.GetLength(find))
    at = StringUtil.Find(text, find, at + StringUtil.GetLength(with))
  EndWhile
  Return text
EndFunction
