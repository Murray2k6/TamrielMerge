ScriptName TMMwSecurity extends Quest
{Morrowind's Security skill (rules as OpenMW's MWMechanics::Security, values from Morrowind.esm's settings). Picks
and probes are tools held in the hand; an attempt's chance is
  (0.2 Agility + 0.1 Luck + Security) x quality x fatigue term - lock level        (picking, fPickLockMult -1)
  (0.2 Agility + 0.1 Luck + Security) x quality x fatigue term                     (disarming, fTrapCostMult 0)
with fatigue term 1.25 - 0.5 x (1 - stamina fraction); nothing at or below 0 can be tried. Every attempt wears the
tool by one use; a worn-out tool breaks. Skyrim's Lockpicking stands for Security. Skyrim has no attributes, so
Agility and Luck are a typical Morrowind character's: 40, Agility rising by one a level.}

FormList Property Picks Auto
Float[] Property PickQuality Auto
Int[] Property PickUses Auto
FormList Property Probes Auto
Float[] Property ProbeQuality Auto
Int[] Property ProbeUses Auto

Int[] pickLeft
Int[] probeLeft

Event OnInit()
  pickLeft = new Int[128]
  probeLeft = new Int[128]
EndEvent

Float Function Skill(Actor player)
  Float agility = 40.0 + player.GetLevel()
  If agility > 100.0
    agility = 100.0
  EndIf
  Float fatigue = 1.25 - 0.5 * (1.0 - player.GetActorValuePercentage("Stamina"))
  Return (0.2 * agility + 0.1 * 40.0 + player.GetActorValue("Lockpicking")) * fatigue
EndFunction

; One attempt at a lock with the pick in hand; True when it opened.
Bool Function Pick(Weapon tool, Int lockLevel)
  Actor player = Game.GetPlayer()
  Int i = Picks.Find(tool)
  If i < 0
    Return False
  EndIf
  Float x = Skill(player) * PickQuality[i] - lockLevel
  If x <= 0.0
    Debug.Notification("Lock too complex.")
    Return False
  EndIf
  Wear(player, tool, i, PickUses, pickLeft)
  If Utility.RandomInt(0, 99) <= x
    Debug.Notification("Lock pick success!")
    Game.AdvanceSkill("Lockpicking", 2.0)
    Return True
  EndIf
  Debug.Notification("Lock pick failed.")
  Return False
EndFunction

; One attempt at a trap with the probe in hand; True when it was disarmed.
Bool Function Probe(Weapon tool)
  Actor player = Game.GetPlayer()
  Int i = Probes.Find(tool)
  If i < 0
    Return False
  EndIf
  Float x = Skill(player) * ProbeQuality[i]
  If x <= 0.0
    Debug.Notification("Trap too complex, your chance to disarm it is zero")
    Return False
  EndIf
  Wear(player, tool, i, ProbeUses, probeLeft)
  If Utility.RandomInt(0, 99) <= x
    Debug.Notification("Trap disarmed!")
    Game.AdvanceSkill("Lockpicking", 2.0)
    Return True
  EndIf
  Debug.Notification("Disarm trap failed.")
  Return False
EndFunction

Function Wear(Actor player, Weapon tool, Int i, Int[] maxUses, Int[] left)
  If i >= left.Length
    Return
  EndIf
  If left[i] <= 0
    left[i] = maxUses[i]
  EndIf
  left[i] = left[i] - 1
  If left[i] <= 0
    player.RemoveItem(tool, 1, True)
    If player.GetItemCount(tool) > 0
      player.EquipItem(tool, False, True)
    EndIf
  EndIf
EndFunction
