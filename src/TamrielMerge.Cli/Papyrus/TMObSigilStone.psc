ScriptName TMObSigilStone extends ActiveMagicEffect
{Using one of Oblivion's sigil stones (SGST: 150, five strengths of thirty pairs of effects). Oblivion's rule (UESP
Oblivion:Sigil Stones): select the stone in the inventory, then "any unenchanted, non-quest item in your inventory
(weapon, armor, clothing, ring, etc.)"; a weapon takes the stone's offensive (touch) effect on strike, apparel its
defensive (self) effect constantly; "once you are done, the Sigil Stone will vanish". The stone is a potion here, so
it can be used from the inventory (Skyrim's misc items can't be); using it runs this effect: the stone is given back,
a message box offers the worn pieces (the two hands' weapons, head, body, hands, feet, shield, amulet, ring: a button
shows only for a slot worn with an unenchanted, non-quest item), and the chosen one is enchanted in place with the
stone's enchantment through SKSE's WornObject.CreateEnchantment (the one way to enchant an item the player already
has), a weapon with the stone's charge (its uses times the strike's cost); then the stone is gone. Cancel keeps it.}

Potion Property Stone Auto
{This stone's item.}
Enchantment Property WeaponEnchantment Auto
{The stone's offensive effect, on strike (OBSigil...Weapon).}
Enchantment Property ApparelEnchantment Auto
{The stone's defensive effect, constant (OBSigil...Apparel).}
Float Property WeaponCharge Auto
{A weapon's charge: the stone's uses times the strike's cost (Oblivion's cost formula).}
Message Property Menu Auto
{TMObSigilStoneMenu: Right hand, Left hand, Head, Body, Hands, Feet, Shield, Amulet, Ring, Cancel.}
GlobalVariable[] Property SlotShown Auto
{TMOB_SigilSlot0..8: 1 shows the button (the message's conditions).}

Int Property Choices = 9 AutoReadOnly

Event OnEffectStart(Actor akTarget, Actor akCaster)
  Actor player = akTarget
  If Stone
    player.AddItem(Stone, 1, True)
  EndIf
  If SKSE.GetVersion() <= 0
    Debug.Notification("A sigil stone needs SKSE to enchant an item.")
    Dispel()
    Return
  EndIf
  Int i = 0
  While i < Choices
    Form worn = WornAt(player, i)
    If SlotShown && i < SlotShown.Length && SlotShown[i]
      If worn && Fits(player, worn, i)
        SlotShown[i].SetValueInt(1)
      Else
        SlotShown[i].SetValueInt(0)
      EndIf
    EndIf
    i += 1
  EndWhile
  Int choice = -1
  If Menu
    choice = Menu.Show()
  EndIf
  If choice < 0 || choice >= Choices
    Dispel()
    Return
  EndIf
  Form chosen = WornAt(player, choice)
  Enchantment source = ApparelEnchantment
  If choice < 2
    source = WeaponEnchantment
  EndIf
  If !chosen || !source || !Fits(player, chosen, choice)
    Debug.Notification("Nothing worn there takes the sigil stone.")
    Dispel()
    Return
  EndIf
  If Enchant(player, choice, source)
    If Stone
      player.RemoveItem(Stone, 1, True)
    EndIf
    Debug.Notification(chosen.GetName() + " is enchanted by the sigil stone.")
  EndIf
  Dispel()
EndEvent

; The worn item of a choice: 0 the right hand's weapon, 1 the left hand's, then the armour slots.
Form Function WornAt(Actor akPlayer, Int aiChoice)
  If aiChoice == 0
    Return akPlayer.GetEquippedObject(1) as Weapon
  ElseIf aiChoice == 1
    Return akPlayer.GetEquippedObject(0) as Weapon
  EndIf
  Return akPlayer.GetWornForm(SlotMask(aiChoice))
EndFunction

; Skyrim's biped slot masks: head 30 (1), body 32 (4), hands 33 (8), feet 37 (128), shield 39 (512), amulet 35 (32), ring 36 (64).
Int Function SlotMask(Int aiChoice)
  If aiChoice == 2
    Return 1
  ElseIf aiChoice == 3
    Return 4
  ElseIf aiChoice == 4
    Return 8
  ElseIf aiChoice == 5
    Return 128
  ElseIf aiChoice == 6
    Return 512
  ElseIf aiChoice == 7
    Return 32
  ElseIf aiChoice == 8
    Return 64
  EndIf
  Return 0
EndFunction

; The hand slot SKSE's WornObject takes: 1 the right hand, 0 the left (and 0 with an armour slot mask).
Int Function HandSlot(Int aiChoice)
  If aiChoice == 0
    Return 1
  EndIf
  Return 0
EndFunction

; Unenchanted (neither its record's enchantment nor a player-made one) and not a quest item.
Bool Function Fits(Actor akPlayer, Form akWorn, Int aiChoice)
  TMObState st = Quest.GetQuest("TMObState") as TMObState
  If st && st.IsQuestItem(akWorn)
    Return False
  EndIf
  Weapon w = akWorn as Weapon
  Armor a = akWorn as Armor
  If w
    If w.GetEnchantment()
      Return False
    EndIf
  ElseIf a
    If a.GetEnchantment()
      Return False
    EndIf
  Else
    Return False
  EndIf
  Int mask = 0
  If aiChoice >= 2
    mask = SlotMask(aiChoice)
  EndIf
  Return WornObject.GetEnchantment(akPlayer, HandSlot(aiChoice), mask) == None
EndFunction

; The stone's enchantment, effect by effect, as a new enchantment on the worn item.
Bool Function Enchant(Actor akPlayer, Int aiChoice, Enchantment akSource)
  Int n = akSource.GetNumEffects()
  If n <= 0
    Return False
  EndIf
  MagicEffect[] effects
  If n == 1
    effects = new MagicEffect[1]
  ElseIf n == 2
    effects = new MagicEffect[2]
  ElseIf n == 3
    effects = new MagicEffect[3]
  Else
    effects = new MagicEffect[4]
    n = 4
  EndIf
  Float[] magnitudes = Utility.CreateFloatArray(n)
  Int[] areas = Utility.CreateIntArray(n)
  Int[] durations = Utility.CreateIntArray(n)
  Int i = 0
  While i < n
    effects[i] = akSource.GetNthEffectMagicEffect(i)
    magnitudes[i] = akSource.GetNthEffectMagnitude(i)
    areas[i] = akSource.GetNthEffectArea(i)
    durations[i] = akSource.GetNthEffectDuration(i)
    i += 1
  EndWhile
  Float charge = 0.0
  Int mask = 0
  If aiChoice < 2
    charge = WeaponCharge
  Else
    mask = SlotMask(aiChoice)
  EndIf
  WornObject.CreateEnchantment(akPlayer, HandSlot(aiChoice), mask, charge, effects, magnitudes, areas, durations)
  Return True
EndFunction
