Scriptname TMMwRegionWeather extends Quest
{Morrowind's ModRegion: a region's weather chances changed by a script (the main quest clearing Red Mountain's blight,
Mournhold's weather machine). Skyrim's regions keep the chances their records have, so a region a script has changed is
run from here: its weather chosen by the new chances and held while the player is in it.

OpenMW mwworld/weather.cpp (read 2026-11-11): RegionWeather::setChances replaces the chances and picks a new weather
only when the current one's chance is now 0; chooseNewWeather rolls 1-100 against the running sum in the weathers'
order, the first weather when the chances don't reach the roll; a change in the player's region begins a transition
(not a jump); every "Hours Between Weather Changes" (Morrowind.ini) all regions pick again.}

String[] Property RegionNames Auto
{Morrowind's regions (REGN IDs); a region's place here is its letter's place in Alphabet.}
String[] Property Rows Auto
{The exterior cells' regions: row r is Morrowind cell row MinY + r, its character c the cell MinX + c: the region's
letter, or "." for none.}
Int Property MinX Auto
Int Property MinY Auto
String Property Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789" AutoReadOnly
FormList Property Weathers Auto
{Morrowind's weathers in its numbering (0 clear .. 9 blizzard).}
WorldSpace Property World Auto
FormList[] Property SkyCells Auto
{For each region, the interiors that show its sky (Mournhold's districts); an empty list for most.}
Float Property HoursBetween = 20.0 Auto
Float Property CellUnits = 8192.0 Auto

Int[] slotRegion
Int[] slotChances
Int[] slotWeather
Int slots = 0
Float nextChange = 0.0
Int held = -1

Function Setup()
  If !slotRegion
    slotRegion = new Int[12]
    slotChances = new Int[120]
    slotWeather = new Int[12]
  EndIf
EndFunction

Int Function RegionIndex(String asRegion)
  Int i = 0
  While RegionNames && i < RegionNames.Length
    If RegionNames[i] == asRegion
      Return i
    EndIf
    i += 1
  EndWhile
  Return -1
EndFunction

Int Function SlotOf(Int aiRegion)
  If aiRegion < 0 || !slotRegion
    Return -1
  EndIf
  Int i = 0
  While i < slots
    If slotRegion[i] == aiRegion
      Return i
    EndIf
    i += 1
  EndWhile
  Return -1
EndFunction

; The region the player is in: by the cell's place outdoors, by the cell itself in an interior with a sky.
Int Function RegionHere()
  Actor player = Game.GetPlayer()
  Cell c = player.GetParentCell()
  If !c
    Return -1
  EndIf
  If c.IsInterior()
    Int i = 0
    While SkyCells && i < SkyCells.Length
      If SkyCells[i] && SkyCells[i].GetSize() > 0 && SkyCells[i].HasForm(c)
        Return i
      EndIf
      i += 1
    EndWhile
    Return -1
  EndIf
  If !World || player.GetWorldSpace() != World || !Rows
    Return -1
  EndIf
  Int row = Math.Floor(player.GetPositionY() / CellUnits) - MinY
  Int col = Math.Floor(player.GetPositionX() / CellUnits) - MinX
  If row < 0 || row >= Rows.Length || col < 0 || col >= StringUtil.GetLength(Rows[row])
    Return -1
  EndIf
  String letter = StringUtil.GetNthChar(Rows[row], col)
  If letter == "" || letter == "."
    Return -1
  EndIf
  Return StringUtil.Find(Alphabet, letter)
EndFunction

Function Choose(Int aiSlot)
  Int roll = Utility.RandomInt(1, 100)
  Int sum = 0
  Int i = 0
  While i < 10
    sum += slotChances[aiSlot * 10 + i]
    If roll <= sum
      slotWeather[aiSlot] = i
      Return
    EndIf
    i += 1
  EndWhile
  slotWeather[aiSlot] = 0
EndFunction

Function Hold(Int aiSlot)
  Weather w = None
  If Weathers
    w = Weathers.GetAt(slotWeather[aiSlot]) as Weather
  EndIf
  If !w
    Return
  EndIf
  If held != aiSlot || (Weather.GetCurrentWeather() != w && Weather.GetOutgoingWeather() != w)
    w.SetActive(True, False)
  EndIf
  held = aiSlot
EndFunction

Function ModRegion(String asRegion, Int c0, Int c1, Int c2, Int c3, Int c4, Int c5, Int c6, Int c7, Int c8, Int c9)
  Int region = RegionIndex(asRegion)
  If region < 0
    Return
  EndIf
  Setup()
  Int slot = SlotOf(region)
  If slot < 0
    If slots >= 12
      Return
    EndIf
    slot = slots
    slots += 1
    slotRegion[slot] = region
    slotWeather[slot] = -1
  EndIf
  Int at = slot * 10
  slotChances[at] = c0
  slotChances[at + 1] = c1
  slotChances[at + 2] = c2
  slotChances[at + 3] = c3
  slotChances[at + 4] = c4
  slotChances[at + 5] = c5
  slotChances[at + 6] = c6
  slotChances[at + 7] = c7
  slotChances[at + 8] = c8
  slotChances[at + 9] = c9
  If slotWeather[slot] < 0
    ; The first change: the weather this region has now stands if the new chances still allow it.
    Int now = -1
    If Weathers && RegionHere() == region
      now = Weathers.Find(Weather.GetCurrentWeather())
    EndIf
    If now >= 0 && now < 10 && slotChances[at + now] > 0
      slotWeather[slot] = now
    Else
      Choose(slot)
    EndIf
  ElseIf slotChances[at + slotWeather[slot]] == 0
    Choose(slot)
  EndIf
  If nextChange == 0.0
    nextChange = Utility.GetCurrentGameTime() * 24.0 + HoursBetween
  EndIf
  If RegionHere() == region
    Hold(slot)
  EndIf
  RegisterForSingleUpdate(5.0)
EndFunction

Event OnUpdate()
  If slots == 0
    Return
  EndIf
  Float hours = Utility.GetCurrentGameTime() * 24.0
  If hours >= nextChange
    Int i = 0
    While i < slots
      Choose(i)
      i += 1
    EndWhile
    nextChange += HoursBetween
    If nextChange <= hours
      nextChange = hours + HoursBetween
    EndIf
  EndIf
  Int slot = SlotOf(RegionHere())
  If slot >= 0
    Hold(slot)
  ElseIf held >= 0
    Weather.ReleaseOverride()
    held = -1
  EndIf
  RegisterForSingleUpdate(5.0)
EndEvent
