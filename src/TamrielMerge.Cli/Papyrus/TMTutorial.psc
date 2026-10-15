ScriptName TMTutorial extends Quest
{TamrielMerge's guide for a new character (user, 2026-10-15: "i need a tutorial popup on where stuff is after character
creation"; "we can start in any world"; "its based on what games the user owns"). On MQ101 beside the story order: the
first time the race menu closes in a game (whichever world it began in, Helgen's own race menu too), pages of help
show one after another, the games' pages only for the games installed (TMStoryOrder.IsStoryAvailable), and the guide
is handed over as a book to read again. Needs SKSE (the race menu's event), as the Tamriel menu does.}

String[] Property Pages Auto
{The general pages; "%KEY%" becomes the Tamriel Menu control's key, "%GAMES%" the games installed.}
String[] Property GamePages Auto
{One page per story (0 Daggerfall, 1 Redguard, 2 Morrowind, 3 Oblivion, 4 Skyrim), shown when it is installed.}
String[] Property GameNames Auto
String[] Property KeyNames Auto
{DirectX scan codes' names (index = code).}
Book Property Guide Auto
String Property MenuControl = "Tamriel Menu" AutoReadOnly

Bool shown = False

Event OnInit()
  If SKSE.GetVersion() > 0
    RegisterForMenu("RaceSex Menu")
  EndIf
EndEvent

Event OnMenuClose(String menuName)
  If menuName == "RaceSex Menu" && !shown
    shown = True
    UnregisterForMenu("RaceSex Menu")
    RegisterForSingleUpdate(1.5)
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
  ; Message boxes queue: each page shows when the one before is closed.
  i = 0
  While Pages && i < Pages.Length
    Debug.MessageBox(Fill(Pages[i], keyName, games))
    i += 1
  EndWhile
  i = 0
  While GamePages && i < GamePages.Length
    If GamePages[i] != "" && (!story || story.IsStoryAvailable(i))
      Debug.MessageBox(GamePages[i])
    EndIf
    i += 1
  EndWhile
  If Guide && Game.GetPlayer().GetItemCount(Guide) == 0
    Game.GetPlayer().AddItem(Guide, 1, true)
    Debug.Notification("TamrielMerge Guide added to your books.")
  EndIf
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
