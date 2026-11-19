ScriptName TMWearMw extends TMWear
{Morrowind's item condition (TMWear; settings from Morrowind.esm's GMSTs). Sources: OpenMW 0.48 mwclass/npc.cpp
Npc::onHit (armour; master has moved it), mwmechanics/combat.cpp, mwmechanics/repair.cpp, mwgui/merchantrepair.cpp.

Armour, each hit on the player: a slot from Morrowind's table of 20 (cuirass 6, shield, helmet, greaves, boots and each
pauldron 2, each gauntlet 1); a hit on the shield's slot with no shield goes to the cuirass or the left pauldron, even
odds. Then x = damage / (damage + armour rating), the hit is multiplied by max(fCombatArmorMinMult, x), and the piece
loses what that took off, as a whole number, at least 1. A creature's unarmed blow doesn't wear armour (OpenMW's default,
"Unarmed creature attacks don't affect armor condition"). At 0 the piece comes off and can't be put on again
(sInventoryMessage1). The armour rating (getArmorRating) weighs each slot: cuirass 0.3, shield, helmet, greaves, boots and
pauldrons 0.1, gauntlets 0.05, a piece being its rating x its armour skill / iBaseArmorSkill x its condition's fraction, an
empty slot fUnarmoredBase1 x Unarmored x fUnarmoredBase2 x Unarmored.

A hit blocked with a shield wears the shield by the hit's whole number of damage and nothing else (blockMeleeAttack).

Weapons (reduceWeaponCondition): max(1, fWeaponDamageMult x damage), whole; a miss has damage 0, so 1. Here every swing
costs the 1 and a hit adds the rest. The damage is the weapon's Morrowind maximum (Skyrim's Damage is 0.6 of it + 2: the
attack's wind-up has no Skyrim counterpart) x its condition x (fDamageStrengthBase + Strength x fDamageStrengthMult x
0.1), as adjustWeaponDamage has it. At 0 it comes off.

The size of a blow at the player isn't given by Skyrim's OnHit: it is the attacker's weapon's Morrowind maximum by the same
reckoning, or the attacker's unarmed damage.

Repair (Repair::repair): the tool loses a use at every attempt. x = (0.1 Strength + 0.1 Luck + Armorer) x the fatigue term;
a roll of 0-99 at or under x succeeds, adding max(1, int(fRepairAmountMult x the tool's quality x the roll)) up to the
maximum, with an Armorer skill use (Skyrim's Smithing; the use value from Armorer's SKDT); else sRepairFailed. A spent tool
goes, with sNotifyMessage51, and the next of the stack starts full.

Service (MerchantRepair): r = max(1, int(maximum / max(1, value))), x = max(1, int(fRepairMult x int((maximum - condition)
/ r))), the price the barter offer on x (getBarterOffer, as TMMwBarterPrices, without haggling: the window has none). Any
item, enchanted or not, to full. The gold goes to the merchant's pool.}

Quest Property MwState Auto
{MWScriptState (TMMwState), for the stats; without it Skyrim's skills and 50 for an attribute.}
FormList Property MediumArmor Auto
{MWMediumArmor.}
Float Property WeaponDamageMult = 0.1 Auto
Float Property CombatArmorMinMult = 0.25 Auto
Float Property RepairAmountMult = 3.0 Auto
Float Property RepairMult = 1.0 Auto
Float Property FatigueBase = 1.25 Auto
Float Property FatigueMult = 0.5 Auto
Float Property DamageStrengthBase = 0.5 Auto
Float Property DamageStrengthMult = 0.1 Auto
Float Property UnarmoredBase1 = 0.1 Auto
Float Property UnarmoredBase2 = 0.065 Auto
Float Property BaseArmorSkill = 30.0 Auto
Float Property ArmorerUse = 1.0 Auto
{Armorer's SKDT use value for a repair: the Smithing experience a success gives.}
String Property RepairSuccess Auto
String Property RepairFailed Auto
String Property UsedUp Auto
{sNotifyMessage51, with %s for the tool.}

Bool ratingDirty = True
Float rating = 0.0

; A stat of TMMwState (0 Strength, 7 Luck, 8 + Morrowind's skill number); asSkyrimAv "" for an attribute.
Float Function Stat(Actor akWho, Int aiStat, String asSkyrimAv)
  TMMwState st = MwState as TMMwState
  If st
    Return st.GetStat(akWho, aiStat, -1)
  ElseIf asSkyrimAv == ""
    Return 50.0
  EndIf
  Return akWho.GetActorValue(asSkyrimAv)
EndFunction

Float Function FatigueTerm(Actor akWho)
  Float normalised = akWho.GetActorValuePercentage("Stamina")
  If normalised < 0.0
    normalised = 0.0
  ElseIf normalised > 1.0
    normalised = 1.0
  EndIf
  Return FatigueBase - FatigueMult * (1.0 - normalised)
EndFunction

; A Skyrim Damage back to Morrowind's maximum (MwItems.WeaponItem: maximum x 0.6 + 2).
Float Function MorrowindDamage(Weapon akWeapon)
  Float d = (akWeapon.GetBaseDamage() - 2.0) / 0.6
  If d < 1.0
    Return 1.0
  EndIf
  Return d
EndFunction

Function ConditionChanged()
  ratingDirty = True
EndFunction

; ---- wear ----

Function Swung(Weapon akWeapon)
  WearDown(akWeapon, 1.0)
EndFunction

Function WeaponHit(Weapon akWeapon)
  Float top = MaxOf(akWeapon)
  If top <= 0.0
    Return
  EndIf
  Float damage = MorrowindDamage(akWeapon) * ConditionOf(akWeapon, top) / top
  damage *= DamageStrengthBase + Stat(PlayerRef, 0, "") * DamageStrengthMult * 0.1
  Int loss = (WeaponDamageMult * damage) as Int
  ; The swing has paid its 1.
  If loss > 1
    WearDown(akWeapon, (loss - 1) as Float)
  EndIf
EndFunction

Function ArmorStruck(Actor akAttacker, Weapon akWeapon, Bool abBlocked)
  Float damage = 0.0
  If akWeapon
    damage = MorrowindDamage(akWeapon)
  ElseIf ActorNpc && akAttacker.HasKeyword(ActorNpc)
    damage = akAttacker.GetActorValue("UnarmedDamage")
  EndIf
  If damage <= 0.0
    Return
  EndIf
  If abBlocked
    Form shield = WornAt(10)
    If shield as Armor
      WearDown(shield, (damage as Int) as Float)
    EndIf
    Return
  EndIf
  Int slot = HitSlot(Utility.RandomInt(0, 19))
  Form piece = WornAt(slot)
  If slot == 10 && !(piece as Armor)
    If Utility.RandomInt(0, 1) == 0
      piece = WornAt(3)
    Else
      piece = WornAt(6)
    EndIf
  EndIf
  If !(piece as Armor) || MaxOf(piece) <= 0.0
    Return
  EndIf
  Float mitigation = damage / (damage + ArmorRating())
  If mitigation < CombatArmorMinMult
    mitigation = CombatArmorMinMult
  EndIf
  Int absorbed = (damage - damage * mitigation) as Int
  If absorbed < 1
    absorbed = 1
  EndIf
  WearDown(piece, absorbed as Float)
EndFunction

; Npc::onHit's table of 20.
Int Function HitSlot(Int aiRoll)
  If aiRoll <= 5
    Return 3
  ElseIf aiRoll <= 7
    Return 10
  ElseIf aiRoll <= 9
    Return 2
  ElseIf aiRoll <= 11
    Return 4
  ElseIf aiRoll <= 13
    Return 5
  ElseIf aiRoll <= 15
    Return 6
  ElseIf aiRoll <= 17
    Return 7
  ElseIf aiRoll == 18
    Return 8
  EndIf
  Return 9
EndFunction

; getArmorRating, kept until what is worn or its condition changes.
Float Function ArmorRating()
  If !ratingDirty
    Return rating
  EndIf
  ratingDirty = False
  Float unarmoredSkill = Stat(PlayerRef, 25, "LightArmor")
  Float bare = UnarmoredBase1 * unarmoredSkill * UnarmoredBase2 * unarmoredSkill
  Float total = 0.0
  Int s = 2
  While s < SlotCount
    Float slotRating = bare
    Armor piece = WornAt(s) as Armor
    If piece
      ; The converters' Skyrim rating is Morrowind's x 1.5.
      slotRating = piece.GetArmorRating() / 1.5 * ArmorSkill(piece) / BaseArmorSkill
      Float top = MaxOf(piece)
      If top > 0.0
        slotRating *= ConditionOf(piece, top) / top
      EndIf
    EndIf
    If s == 3
      total += slotRating * 0.3
    ElseIf s == 8 || s == 9
      total += slotRating * 0.05
    Else
      total += slotRating * 0.1
    EndIf
    s += 1
  EndWhile
  rating = total
  Return rating
EndFunction

; The piece's armour skill: Light Armor (stat 29), Medium Armor (10) by the converter's list, Heavy Armor (11).
Float Function ArmorSkill(Armor akPiece)
  If MediumArmor && MediumArmor.HasForm(akPiece)
    Return Stat(PlayerRef, 10, "HeavyArmor")
  ElseIf akPiece.IsLightArmor()
    Return Stat(PlayerRef, 29, "LightArmor")
  EndIf
  Return Stat(PlayerRef, 11, "HeavyArmor")
EndFunction

Function Broke(Form akItem)
  PlayerRef.UnequipItem(akItem, False, True)
  Debug.Notification(BrokenEquip)
EndFunction

; ---- repair ----

String Function Attempt(Form akTool, Float afQuality, Int aiUses, Form akItem)
  Int uses = ToolUses(akTool, aiUses) - 1
  String said = ""
  Float chance = (0.1 * Stat(PlayerRef, 0, "") + 0.1 * Stat(PlayerRef, 7, "") + Stat(PlayerRef, 9, "Smithing")) * FatigueTerm(PlayerRef)
  Int roll = Utility.RandomInt(0, 99)
  If (roll as Float) <= chance
    Int amount = (RepairAmountMult * afQuality * roll) as Int
    If amount < 1
      amount = 1
    EndIf
    Float top = MaxOf(akItem)
    Float cond = ConditionOf(akItem, top) + amount
    If cond > top
      cond = top
    EndIf
    SetCondition(akItem, cond, top)
    Game.AdvanceSkill("Smithing", ArmorerUse)
    PlayRepair(True)
    said = RepairSuccess
  Else
    PlayRepair(False)
    said = RepairFailed
  EndIf
  If uses <= 0
    ClearTool(akTool)
    PlayerRef.RemoveItem(akTool, 1, True)
    said += "\n" + Named(UsedUp, akTool.GetName())
  Else
    SetToolUses(akTool, uses)
  EndIf
  Return said
EndFunction

String Function Named(String asText, String asName)
  Int at = StringUtil.Find(asText, "%s")
  If at < 0
    Return asText
  EndIf
  Return StringUtil.Substring(asText, 0, at) + asName + StringUtil.Substring(asText, at + 2)
EndFunction

Float Function Capped(Float afValue, Float afCap)
  If afValue > afCap
    Return afCap
  EndIf
  Return afValue
EndFunction

Int Function Price(Form akItem, Float afCondition, Float afMax, Actor akService)
  Int value = akItem.GetGoldValue()
  If value < 1
    value = 1
  EndIf
  Int r = (afMax / value) as Int
  If r < 1
    r = 1
  EndIf
  Int basePrice = ((afMax - afCondition) / r) as Int
  basePrice = (RepairMult * basePrice) as Int
  If basePrice < 1
    basePrice = 1
  EndIf
  Return BarterOffer(basePrice, akService)
EndFunction

; MechanicsManager::getBarterOffer, buying (the terms as TMMwBarterPrices sets them).
Int Function BarterOffer(Int aiBase, Actor akService)
  Actor p = PlayerRef
  Int baseMercantile = -1
  Int baseLuck = -1
  Int basePersonality = -1
  TMMwRepairer own = akService as TMMwRepairer
  If own
    baseMercantile = own.Mercantile
    baseLuck = own.Luck
    basePersonality = own.Personality
  EndIf
  Float npcMercantile = 30.0
  Float npcLuck = 50.0
  Float npcPersonality = 50.0
  Float pcMercantile = 30.0 + p.GetLevel()
  Float pcLuck = 50.0
  Float pcPersonality = 50.0
  TMMwState st = MwState as TMMwState
  If st
    pcMercantile = st.GetStat(p, 32, -1)
    pcLuck = st.GetStat(p, 7, -1)
    pcPersonality = st.GetStat(p, 6, -1)
    npcMercantile = st.GetStat(akService, 32, baseMercantile)
    npcLuck = st.GetStat(akService, 7, baseLuck)
    npcPersonality = st.GetStat(akService, 6, basePersonality)
  EndIf
  TMMwActorAI ai = akService as TMMwActorAI
  If ai
    ai.UpdateDisposition()
  EndIf
  Float disposition = TMMwScript.GetDisposition(akService)
  Float pcTerm = (disposition - 50.0 + Capped(pcMercantile, 100.0) + Capped(0.1 * pcLuck, 10.0) + Capped(0.2 * pcPersonality, 10.0)) * FatigueTerm(p)
  Float npcTerm = (Capped(npcMercantile, 100.0) + Capped(0.1 * npcLuck, 10.0) + Capped(0.2 * npcPersonality, 10.0)) * FatigueTerm(akService)
  Int offer = (aiBase * 0.01 * (100.0 - 0.5 * (pcTerm - npcTerm))) as Int
  If offer < 1
    Return 1
  EndIf
  Return offer
EndFunction

; The gold joins the merchant's pool (setGoldPool), its chest here; one who doesn't barter keeps it on its person.
Function PaidTo(Actor akPerson, Int aiPrice)
  TMMwVendor vendor = akPerson as TMMwVendor
  If vendor && vendor.Chest
    vendor.Chest.AddItem(Gold, aiPrice, True)
  Else
    akPerson.AddItem(Gold, aiPrice, True)
  EndIf
EndFunction
