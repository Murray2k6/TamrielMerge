ScriptName TMTravelClimate extends Quest
{Daggerfall's map for travel (TamrielMerge Travel.esp): a quest that never runs, holding the map in squares of Step
map pixels (Daggerfall's 1000 x 500 as 250 x 125; a quest's script data holds 64 KB at most). Rows, north to south, a
character per square: "O" the sea, else Daggerfall Unity's terrain index (0 desert, 1 mountain, 2 rainforest, 3 swamp,
4 subtropical, 5 the woods). Lands, the same squares: which landmass each is ("." the sea, a character per landmass
with a town on it, "?" one without), so a town across the sea is known to need a ship.}

String[] Property Rows Auto
String[] Property Lands Auto
Int Property Step = 4 Auto

String Function Cell(String[] rows, Int px, Int py)
  If px < 0 || py < 0
    Return ""
  EndIf
  Int r = py / Step
  If r >= rows.Length
    Return ""
  EndIf
  String row = rows[r]
  Int c = px / Step
  If c >= StringUtil.GetLength(row)
    Return ""
  EndIf
  Return StringUtil.GetNthChar(row, c)
EndFunction

; The terrain of map pixel (px, py); off the map is the sea.
String Function At(Int px, Int py)
  String c = Cell(Rows, px, py)
  If c == ""
    Return "O"
  EndIf
  Return c
EndFunction

; The landmass of map pixel (px, py): "." for the sea or off the map.
String Function LandAt(Int px, Int py)
  String c = Cell(Lands, px, py)
  If c == ""
    Return "."
  EndIf
  Return c
EndFunction
