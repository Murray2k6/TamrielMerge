ScriptName TMRgState extends Quest
{What Redguard's converted scripts (TMRgSoup) share, on the always-running quest RGScripts: the lists that name
Redguard's flags (globals, by SOUP386.DEF index), items (by ITEM.INI index), sound effects (MAIN.SFX, by index) and
script lines (spoken by RGLine index), Cyrus's log, the spells that stand in for Redguard's fireballs and dragon
breath, the potion mixing state (GetPotion/SetPotion), the player's selected item and how many scripts hold the
player's controls.}

FormList Property Flags Auto
FormList Property Items Auto
FormList Property Sounds Auto
FormList Property Lines Auto
Quest Property LogQuest Auto
Spell Property Fireball Auto
Spell Property Breath Auto
Spell Property Zap Auto
GlobalVariable[] Property MenuShow Auto
{Which buttons of a script's menu show (RGMenuShow0-9), as Redguard's menuAddItem added them.}

Int[] potions
Int Property SelectedItem = 1 Auto Hidden
Int lockouts = 0
Float Property DialogDue = 0.0 Auto Hidden

Event OnInit()
  potions = new Int[32]
EndEvent

Int Function GetPotion(Int i)
  If !potions
    potions = new Int[32]
  EndIf
  If i < 0 || i >= potions.Length
    Return 0
  EndIf
  Return potions[i]
EndFunction

Function SetPotion(Int i, Int v)
  If !potions
    potions = new Int[32]
  EndIf
  If i >= 0 && i < potions.Length
    potions[i] = v
  EndIf
EndFunction

; Redguard's lockoutPlayer(1/0): scripts nest it, so controls come back when the last one lets go.
Function Lockout(Bool on)
  If on
    lockouts += 1
    If lockouts == 1
      Game.DisablePlayerControls(True, True, False, False, True, True, True)
    EndIf
  ElseIf lockouts > 0
    lockouts -= 1
    If lockouts == 0
      Game.EnablePlayerControls()
    EndIf
  EndIf
EndFunction

Function ClearMenu()
  If !MenuShow
    Return
  EndIf
  Int i = 0
  While i < MenuShow.Length
    MenuShow[i].SetValue(0)
    i += 1
  EndWhile
EndFunction

Function ShowMenuItem(Int i)
  If MenuShow && i >= 0 && i < MenuShow.Length
    MenuShow[i].SetValue(1)
  EndIf
EndFunction

Int Function Flag(Int i)
  GlobalVariable g = Flags.GetAt(i) as GlobalVariable
  If g
    Return g.GetValueInt()
  EndIf
  Return 0
EndFunction

Function SetFlag(Int i, Int v)
  GlobalVariable g = Flags.GetAt(i) as GlobalVariable
  If g
    g.SetValueInt(v)
  EndIf
EndFunction

Form Function Item(Int i)
  If i < 0 || i >= Items.GetSize()
    Return None
  EndIf
  Return Items.GetAt(i)
EndFunction
