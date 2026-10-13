ScriptName TMDfHolidays extends Quest
{Daggerfall's holidays (UESP Daggerfall:Holidays) while the player is in the Iliac Bay: each morning of a holiday says
so, and Merchants' Festival (10 Sun's Height) halves every shop's prices (Skyrim's barter settings, through SKSE).
The other holidays' effects live where they apply: free temple healing (TMDfGuilds), free drinks and Heart's Day
rooms (TMDfTavern), shops shut on Sun's Rest (the barter line's conditions).}

Location Property IliacBay Auto
GlobalVariable Property GameMonth Auto
GlobalVariable Property GameDay Auto

Int lastDay = -1
Float barterMax
Float barterMin
Bool halved = False

Event OnInit()
  RegisterForSingleUpdateGameTime(1.0)
EndEvent

String Function Holiday(Int month, Int day)
  If month == 0 && day == 1
    Return "the New Life Festival: ale is free at the taverns"
  ElseIf month == 0 && day == 15
    Return "South Wind's Prayer: the temples heal for free"
  ElseIf month == 1 && day == 16
    Return "Heart's Day: the inns give rooms for free"
  ElseIf month == 2 && day == 7
    Return "First Planting: the temples cure for free"
  ElseIf month == 3 && day == 28
    Return "Jester's Day"
  ElseIf month == 4 && day == 7
    Return "Second Planting: the temples cure for free"
  ElseIf month == 5 && day == 16
    Return "the Mid Year Celebration"
  ElseIf month == 6 && day == 10
    Return "the Merchants' Festival: everything in the shops is half price"
  ElseIf month == 6 && day == 20
    Return "Sun's Rest: the shops are closed"
  ElseIf month == 7 && day == 27
    Return "Harvest's End: drinks are free at the taverns"
  ElseIf month == 8 && day == 3
    Return "Tales and Tallows"
  ElseIf month == 9 && day == 13
    Return "the Witches Festival"
  ElseIf month == 9 && day == 30
    Return "the Emperor's Day"
  ElseIf month == 10 && day == 20
    Return "the Warriors Festival"
  ElseIf month == 11 && day == 15
    Return "North Wind's Prayer"
  ElseIf month == 11 && day == 30
    Return "the Old Life Festival"
  EndIf
  Return ""
EndFunction

Event OnUpdateGameTime()
  Int month = GameMonth.GetValueInt()
  Int day = GameDay.GetValueInt()
  Bool here = Game.GetPlayer().IsInLocation(IliacBay)
  Bool merchants = here && month == 6 && day == 10
  If SKSE.GetVersion() > 0
    If merchants && !halved
      barterMax = Game.GetGameSettingFloat("fBarterMax")
      barterMin = Game.GetGameSettingFloat("fBarterMin")
      Game.SetGameSettingFloat("fBarterMax", barterMax * 0.5)
      Game.SetGameSettingFloat("fBarterMin", barterMin * 0.5)
      halved = True
    ElseIf !merchants && halved
      Game.SetGameSettingFloat("fBarterMax", barterMax)
      Game.SetGameSettingFloat("fBarterMin", barterMin)
      halved = False
    EndIf
  EndIf
  If here && day != lastDay
    String name = Holiday(month, day)
    If name != ""
      Debug.Notification("Today is " + name + ".")
    EndIf
  EndIf
  lastDay = day
  RegisterForSingleUpdateGameTime(1.0)
EndEvent
