Scriptname TMStrings Hidden
{Texts kept in pieces (Skyrim.ScriptStrings): Skyrim stores every script variable in the save game and loading one
crashes on a script string of 4,096 characters or more (2026-11-05: Morrowind's faction table, 4,207, and the Iliac
Bay's largest region's town names, 4,347). Lists of names are kept as pieces of "|"-separated names, each group's
first piece in a list of its own; a group is read back as one array, never as one long string.}

; Group g's names: its pieces (from akFirst[g] up to the next group's first piece, or the end) split at "|", as one
; array (SKSE's, which may be longer than 128). Without akFirst (a plugin made before the pieces) piece g is group g.
String[] Function Group(String[] akPieces, Int[] akFirst, Int g) Global
  If !akPieces
    Return Utility.CreateStringArray(0)
  EndIf
  Int first = g
  Int last = g + 1
  If akFirst && akFirst.Length > 0
    If g < 0 || g >= akFirst.Length
      Return Utility.CreateStringArray(0)
    EndIf
    first = akFirst[g]
    last = akPieces.Length
    If g + 1 < akFirst.Length
      last = akFirst[g + 1]
    EndIf
  EndIf
  If first < 0 || first >= akPieces.Length || last <= first
    Return Utility.CreateStringArray(0)
  EndIf
  If last - first == 1
    Return StringUtil.Split(akPieces[first], "|")
  EndIf
  Int total = 0
  Int p = first
  While p < last
    total += StringUtil.Split(akPieces[p], "|").Length
    p += 1
  EndWhile
  String[] names = Utility.CreateStringArray(total)
  Int n = 0
  p = first
  While p < last
    String[] part = StringUtil.Split(akPieces[p], "|")
    Int i = 0
    While i < part.Length && n < total
      names[n] = part[i]
      n += 1
      i += 1
    EndWhile
    p += 1
  EndWhile
  Return names
EndFunction
