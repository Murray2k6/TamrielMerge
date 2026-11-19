ScriptName TMWearOb extends TMWear
{Oblivion's item condition (TMWear). The settings are Oblivion.esm's, and where it leaves one out the engine's default
read from Oblivion.exe. The rules below were read from Oblivion.exe's code (addresses of the Steam build) unless a
source is named; UESP gives no hammer, price or armour-piece rule.

Weapons: each strike loses fDamageToWeaponPercentage x the weapon's base damage (0x547240; UESP Oblivion:Weapons), as a
fraction. Its damage is multiplied by fDamageWeaponConditionBase + fDamageWeaponConditionMult x the condition's fraction
(UESP: (health% + 1) / 2). At 0 it comes off and can't be equipped (UESP Oblivion:Health).

Armour: a roll of 0-99 walks the chances iArmorDamageHelmChance 10, Cuirass 25, Greaves 15, Gauntlets 10, Boots 10, Shield
30 to a slot; an empty one rolls again, up to 7 times (0x5e5a40). The piece loses damage x the armour's fraction x
fDamageToArmorPercentage (0x547260). Oblivion.esm's 9.0 and UESP's measured 4.5 x the damage absorbed (Oblivion:Armor)
differ by two; what halves the damage before that code wasn't traced, so half the setting is used on the strike's damage,
which matches the measurement. The fraction is the worn rating (each piece's rating x (0.35 + 0.0065 x its skill) x its
condition, UESP Oblivion:Armor) in percent, at most fMaxArmorRating. Rating scales with the condition (the band
multiplier). At 0 armour stays on and protects nothing (UESP Oblivion:Health; a talk page says a broken shield can't be
used, unconfirmed).

Mastery (ObMasteryPerks, the held perks): Heavy or Light Armor Apprentice halves that armour's loss; Block Expert means a
blocked hit wears nothing. A blocked hit otherwise wears the shield by the armour rule, or a blocking weapon by its own
rule (not read from the code: from the Block Expert rule alone).

Hammers (not in combat: iAllowRepairDuringCombat 0, sCanNotEquipRepairHammer): a use adds int(Armorer x fRepairArmorerMult
+ fRepairArmorerBase + Strength x fRepairStrengthMult) (0x5482f0; UESP's "Armorer + 3" is that at Strength 30). The sum
stops at the maximum, or for an Expert at 125% (0x5d26c5). Magic items need a Journeyman (sNoRepairMagic). After a use the
hammer breaks at a chance of fRepairSkillBreakBase + fRepairSkillBreakMult x the skill with Luck (skill +
fActorLuckSkillMult x Luck + iActorLuckSkillBase, 0 to 100), x fRepairBreakApprenticeMult from Apprentice, never for a
Master (0x548330). Each use is 1.5 experience (UESP Oblivion:Armorer), Skyrim's Smithing here.

Service (0x5483c0, 0x5d2966): int((maximum - condition) / maximum x value x fRepairCostMult), at least 1; neither
Mercantile nor disposition enters that function. Any item to full; "Repair all" too.}

Quest Property Mastery Auto
{TMObMastery, for the held ranks.}
Float Property DamageToWeaponPercentage = 0.06 Auto
Float Property DamageToArmorPercentage = 9.0 Auto
Float Property MaxArmorRating = 85.0 Auto
Float Property DamageWeaponMult = 0.5 Auto
Float Property DamageStrengthBase = 0.75 Auto
Float Property DamageStrengthMult = 0.5 Auto
Float Property DamageSkillBase = 0.2 Auto
Float Property DamageSkillMult = 1.5 Auto
Float Property RepairArmorerBase = 0.0 Auto
Float Property RepairArmorerMult = 1.0 Auto
Float Property RepairStrengthMult = 0.1 Auto
Float Property RepairCostMult = 0.9 Auto
Float Property RepairSkillBreakBase = 30.0 Auto
Float Property RepairSkillBreakMult = -0.2 Auto
Float Property RepairBreakApprenticeMult = 0.5 Auto
Float Property ActorLuckSkillMult = 0.4 Auto
Float Property ActorLuckSkillBase = -20.0 Auto
Float Property RepairExperience = 1.5 Auto
Int[] Property PieceChance Auto
{Helm, Cuirass, Greaves, Gauntlets, Boots, Shield.}
String Property CanNotInCombat Auto
String Property NoRepairMagic Auto
String Property NoHammer Auto

Int Property ArmorerSkill = 0 AutoReadOnly
Int Property BlockSkill = 3 AutoReadOnly
Int Property HeavySkill = 6 AutoReadOnly
Int Property LightSkill = 15 AutoReadOnly
Int Property ArmorerStat = 8 AutoReadOnly
Int Property LuckStat = 7 AutoReadOnly

Bool ratingDirty = True
Float rating = 0.0
Bool skippedMagic = False

TMObState Function ObState()
  Return Quest.GetQuest("TMObState") as TMObState
EndFunction

Int Function HeldOf(Int aiSkill)
  TMObMastery m = Mastery as TMObMastery
  If m
    Return m.HeldRank(aiSkill)
  EndIf
  Return 0
EndFunction

; A stat of TMObState (0 Strength, 7 Luck, 8 + Oblivion's skill number); asSkyrimAv "" for an attribute.
Float Function Stat(Actor akWho, Int aiStat, String asSkyrimAv)
  TMObState st = ObState()
  If st
    Return st.GetStat(akWho, aiStat)
  ElseIf asSkyrimAv == ""
    Return 50.0
  EndIf
  Return akWho.GetActorValue(asSkyrimAv)
EndFunction

Function ConditionChanged()
  ratingDirty = True
EndFunction

; ---- wear ----

Function WeaponHit(Weapon akWeapon)
  WearDown(akWeapon, DamageToWeaponPercentage * akWeapon.GetBaseDamage())
EndFunction

; The skill of a weapon: Blade (stat 10), Blunt (12), Marksman (24), Hand to Hand (13). GetWeaponType: 1 sword, 2 dagger,
; 5 greatsword, 7 bow, 9 crossbow.
Int Function WeaponStat(Weapon akWeapon)
  If !akWeapon
    Return 13
  EndIf
  Int kind = akWeapon.GetWeaponType()
  If kind == 1 || kind == 2 || kind == 5
    Return 10
  ElseIf kind == 7 || kind == 9
    Return 24
  EndIf
  Return 12
EndFunction

; The strike's damage (UESP Oblivion:Weapons) by the attacker's Strength and skill: Skyrim's OnHit doesn't give it.
Float Function StrikeDamage(Actor akAttacker, Weapon akWeapon)
  Float base = akAttacker.GetActorValue("UnarmedDamage")
  If akWeapon
    base = akWeapon.GetBaseDamage()
  EndIf
  Float strength = Stat(akAttacker, 0, "")
  Float skill = Stat(akAttacker, WeaponStat(akWeapon), "OneHanded")
  Return base * DamageWeaponMult * (DamageStrengthBase + strength * DamageStrengthMult * 0.01) * (DamageSkillBase + skill * DamageSkillMult * 0.01)
EndFunction

Function ArmorStruck(Actor akAttacker, Weapon akWeapon, Bool abBlocked)
  Float damage = StrikeDamage(akAttacker, akWeapon)
  If damage <= 0.0
    Return
  EndIf
  If abBlocked
    If HeldOf(BlockSkill) >= 3
      Return
    EndIf
    Armor shield = WornAt(10) as Armor
    If shield
      WearPiece(shield, damage)
    ElseIf akWeapon
      Weapon own = PlayerRef.GetEquippedWeapon(False)
      If own
        WearDown(own, DamageToWeaponPercentage * akWeapon.GetBaseDamage())
      EndIf
    EndIf
    Return
  EndIf
  Int tries = 0
  While tries < 7
    Armor piece = WornAt(RolledSlot(Utility.RandomInt(0, 99))) as Armor
    If piece
      WearPiece(piece, damage)
      Return
    EndIf
    tries += 1
  EndWhile
EndFunction

Int Function RolledSlot(Int aiRoll)
  Int top = PieceChance[0]
  If aiRoll < top
    Return 2
  EndIf
  top += PieceChance[1]
  If aiRoll < top
    Return 3
  EndIf
  top += PieceChance[2]
  If aiRoll < top
    Return 4
  EndIf
  top += PieceChance[3]
  If aiRoll < top
    Return 9
  EndIf
  top += PieceChance[4]
  If aiRoll < top
    Return 5
  EndIf
  Return 10
EndFunction

Function WearPiece(Armor akPiece, Float afDamage)
  Float fraction = ArmorRating() / 100.0
  Float loss = afDamage * fraction * DamageToArmorPercentage * 0.5
  ; Apprentice in the armour's skill: it wears half as fast (UESP Oblivion:Heavy Armor, Light Armor).
  Int skill = HeavySkill
  If akPiece.IsLightArmor()
    skill = LightSkill
  EndIf
  If HeldOf(skill) >= 1
    loss *= 0.5
  EndIf
  WearDown(akPiece, loss)
EndFunction

; The worn armour's rating in percent, kept until what is worn or its condition changes.
Float Function ArmorRating()
  If !ratingDirty
    Return rating
  EndIf
  ratingDirty = False
  Float total = 0.0
  Int s = 2
  While s < SlotCount
    Armor piece = WornAt(s) as Armor
    If piece
      Float skill = 0.0
      If piece.IsLightArmor()
        skill = Stat(PlayerRef, 23, "LightArmor")
      Else
        skill = Stat(PlayerRef, 14, "HeavyArmor")
      EndIf
      ; The converter's Skyrim rating is Oblivion's x 1.5.
      Float pieceRating = piece.GetArmorRating() / 1.5 * (0.35 + 0.0065 * skill)
      Float top = MaxOf(piece)
      If top > 0.0
        pieceRating *= ConditionOf(piece, top) / top
      EndIf
      total += pieceRating
    EndIf
    s += 1
  EndWhile
  If total > MaxArmorRating
    total = MaxArmorRating
  EndIf
  rating = total
  Return rating
EndFunction

Bool Function BrokenCantBeUsed(Form akItem)
  Return akItem as Weapon
EndFunction

Function Broke(Form akItem)
  If akItem as Weapon
    PlayerRef.UnequipItem(akItem, False, True)
  EndIf
  Debug.Notification(akItem.GetName() + " has broken.")
EndFunction

; ---- repair ----

Bool Function CanUseTool()
  If PlayerRef.IsInCombat()
    Debug.Notification(CanNotInCombat)
    Return False
  EndIf
  skippedMagic = False
  Return True
EndFunction

; The record's enchantment, or one added in play (a sigil stone: WornObject, while worn).
Bool Function Enchanted(Form akItem)
  Weapon w = akItem as Weapon
  If w
    If w.GetEnchantment()
      Return True
    EndIf
    Return PlayerRef.IsEquipped(w) && WornObject.GetEnchantment(PlayerRef, 1, 0)
  EndIf
  Armor piece = akItem as Armor
  If piece
    If piece.GetEnchantment()
      Return True
    EndIf
    Return PlayerRef.IsEquipped(piece) && WornObject.GetEnchantment(PlayerRef, 0, piece.GetSlotMask())
  EndIf
  Return False
EndFunction

Bool Function CanMend(Form akItem, Float afCondition, Float afMax, Actor akService)
  If akService
    Return afCondition < afMax
  EndIf
  Int held = HeldOf(ArmorerSkill)
  Float top = afMax
  If held >= 3
    top = afMax * 1.25
  EndIf
  If afCondition >= top
    Return False
  EndIf
  If held < 2 && Enchanted(akItem)
    skippedMagic = True
    Return False
  EndIf
  Return True
EndFunction

String Function ToolNothingText()
  If skippedMagic
    Return NoRepairMagic
  EndIf
  Return NothingToRepair
EndFunction

Bool Function HammerBreaks(Int aiHeld)
  If aiHeld >= 4
    Return False
  EndIf
  Float skill = Stat(PlayerRef, ArmorerStat, "Smithing") + Stat(PlayerRef, LuckStat, "") * ActorLuckSkillMult + ActorLuckSkillBase
  If skill < 0.0
    skill = 0.0
  ElseIf skill > 100.0
    skill = 100.0
  EndIf
  Float chance = RepairSkillBreakBase + RepairSkillBreakMult * skill
  If aiHeld >= 1
    chance *= RepairBreakApprenticeMult
  EndIf
  Return chance > Utility.RandomInt(0, 99)
EndFunction

String Function Attempt(Form akTool, Float afQuality, Int aiUses, Form akItem)
  Int held = HeldOf(ArmorerSkill)
  Float top = MaxOf(akItem)
  Float cond = ConditionOf(akItem, top)
  Float armorer = Stat(PlayerRef, ArmorerStat, "Smithing")
  Int amount = (armorer * RepairArmorerMult + RepairArmorerBase + Stat(PlayerRef, 0, "") * RepairStrengthMult) as Int
  Float sum = cond + amount
  If sum > top
    If held >= 3
      If sum > top * 1.25
        sum = top * 1.25
      EndIf
    Else
      sum = top
    EndIf
  EndIf
  SetCondition(akItem, sum, top)
  Game.AdvanceSkill("Smithing", RepairExperience)
  String said = akItem.GetName() + ": " + (sum as Int) + " / " + (top as Int)
  If HammerBreaks(held)
    PlayRepair(False)
    PlayerRef.RemoveItem(akTool, 1, True)
    If PlayerRef.GetItemCount(akTool) <= 0
      said += "\n" + NoHammer
    EndIf
  Else
    PlayRepair(True)
  EndIf
  Return said
EndFunction

Int Function Price(Form akItem, Float afCondition, Float afMax, Actor akService)
  Int cost = ((afMax - afCondition) / afMax * akItem.GetGoldValue() * RepairCostMult) as Int
  If cost < 1
    Return 1
  EndIf
  Return cost
EndFunction

Bool Function HasRepairAll()
  Return True
EndFunction

Function PaidTo(Actor akPerson, Int aiPrice)
  akPerson.AddItem(Gold, aiPrice, True)
EndFunction
