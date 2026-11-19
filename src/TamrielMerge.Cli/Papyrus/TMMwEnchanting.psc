ScriptName TMMwEnchanting extends Quest
{Morrowind's enchanters at Skyrim's table (the quest MWEnchantingService; MwEnchanters).

Enchanting. OpenMW mwgui/enchantingdialog.cpp + mwmechanics/enchanting.cpp: with an enchanter the player pays and nothing
is rolled (Enchanting::create rolls only when self-enchanting, and the dialog hides the chance), so the service never
fails. The price is Enchanting::getEnchantPrice: getBarterOffer(enchanter, the final effect's cost x fEnchantmentValueMult)
x the count, and the gold joins the enchanter's trading gold (payForEnchantment). Skyrim's table does the work with the
player's skill, so while it is open a hidden Fortify Enchanting stands for the enchanter's skill: Skyrim's enchanting
strength is 1 + (fEnchantingSkillFactor - 1) x skill / 100 (the formula from memory of UESP; the setting from Skyrim.esm),
and the boost is the enchanter's over the player's, none when the player is better. A script can't read which
enchantment Skyrim made, nor what it would cost, so the cost is by the soul gem spent (MwEnchanters.EnchantCosts): each
item made ("Magic Items Made") is charged for a gem gone from the inventory, the gold taken as it is made. Morrowind
refuses a price the player can't pay before it enchants; here the menu can only be refused at the start, for the
cheapest gem carried, so a dearer gem can take all the gold there is and leave the rest of the fee unpaid.

Recharge. Morrowind has no recharging service: the player does it with a filled gem (OpenMW mwmechanics/recharge.cpp
rechargeItem), and here the enchanter does it in the player's place, so its stats roll:
  chance = (Enchant + 0.2 x Intelligence (1-20) + 0.1 x Luck (1 when under 1 or over 10)) x fatigue term
  a roll of 0-99 under that restores soul x roll / chance, to the item's maximum; the gem goes either way.
Morrowind has no price for this: the charge is what the gem's soul is worth (fSoulGemMult x the soul restored), through
getBarterOffer. Skyrim's charges are reachable only on what is held: WornObject reads them and, as SKSE's own note says,
the Right/LeftItemCharge actor values change them. A soul is Morrowind's for Morrowind's enchantments (charges from
ENDT) and Skyrim's for Skyrim's or the player's (their charges are Skyrim's). Azura's Star is one form full or empty, so
it can't be offered here.}

Actor Property PlayerRef Auto
Form Property Gold Auto
Furniture Property Table Auto
{Skyrim's enchanting table, placed at the player.}
Form Property AzuraStar Auto
Spell Property Boost Auto
{Fortify Enchanting for the enchanter's skill (its magnitude set here).}
Quest Property MwState Auto
{MWScriptState (TMMwState), for what scripts changed.}
Quest Property Travel Auto
{MWTravel (TMMwTravel.BarterOffer: getBarterOffer, buying).}
Message Property HandMenu Auto
{Right, Left, Cancel.}
Message Property GemMenu Auto
{Petty, Lesser, Common, Greater, Grand, Cancel.}
Float[] Property Costs Auto
{What an enchantment costs by the size of its gem, petty first.}
Float[] Property Souls Auto
{A gem's soul in Morrowind by size, petty first (value x fSoulGemMult).}
Float[] Property SkyrimSouls Auto
{A gem's charge in Skyrim by size, petty first.}
Float Property ValueMult = 1000.0 Auto
Float Property SoulGemMult = 3.0 Auto
Float Property FatigueBase = 1.25 Auto
Float Property FatigueMult = 0.5 Auto
String Property NoGem Auto
String Property NoGold Auto
String Property Failed Auto

Bool busy = False
Actor seller
Int madeSoFar
Int[] gemsBefore

; ---- both services ----

; The price of a thing the enchanter sells, as Morrowind's getBarterOffer gives it.
Int Function Offer(Actor akSeller, Int aiBase)
  TMMwTravel t = Travel as TMMwTravel
  If t
    Return t.BarterOffer(akSeller, aiBase)
  EndIf
  Return aiBase
EndFunction

Float Function FatigueTerm(Actor a)
  Float normalised = a.GetActorValuePercentage("Stamina")
  If normalised < 0.0
    normalised = 0.0
  ElseIf normalised > 1.0
    normalised = 1.0
  EndIf
  Return FatigueBase - FatigueMult * (1.0 - normalised)
EndFunction

; The player's filled gems of each size (element 1 petty .. 5 grand; a black gem with a grand soul is grand).
Int[] Function Gems()
  Int[] counts = new Int[6]
  Int i = PlayerRef.GetNumItems()
  While i > 0
    i -= 1
    SoulGem gem = PlayerRef.GetNthForm(i) as SoulGem
    If gem
      Int size = gem.GetSoulSize()
      If size >= 1 && size <= 5
        counts[size] = counts[size] + PlayerRef.GetItemCount(gem)
      EndIf
    EndIf
  EndWhile
  Return counts
EndFunction

; ---- enchanting ----

; Enchanting::getEnchantPrice for a gem of a size.
Int Function Fee(Int aiSize)
  Int price = Offer(seller, (Costs[aiSize - 1] * ValueMult) as Int)
  If price < 1
    price = 1
  EndIf
  Return price
EndFunction

; The fee in gold, to the enchanter's trading gold when it barters (payForEnchantment).
Function Pay(Int aiPrice)
  Int have = PlayerRef.GetItemCount(Gold)
  If have < aiPrice
    aiPrice = have
    Debug.Notification(NoGold)
  EndIf
  If aiPrice <= 0
    Return
  EndIf
  PlayerRef.RemoveItem(Gold, aiPrice, False)
  TMMwVendor vendor = seller as TMMwVendor
  If vendor && vendor.Chest
    vendor.Chest.AddItem(Gold, aiPrice, True)
  EndIf
EndFunction

; Each item made since the last look pays for a gem gone; one made with no gem gone took Azura's Star, a grand soul.
Function Settle()
  Int now = Game.QueryStat("Magic Items Made")
  If now <= madeSoFar
    Return
  EndIf
  Int made = now - madeSoFar
  madeSoFar = now
  Int[] gems = Gems()
  Int size = 5
  While size >= 1
    Int gone = gemsBefore[size] - gems[size]
    While gone > 0 && made > 0
      Pay(Fee(size))
      gone -= 1
      made -= 1
    EndWhile
    size -= 1
  EndWhile
  While made > 0
    Pay(Fee(5))
    made -= 1
  EndWhile
  gemsBefore = gems
EndFunction

; The table at the player, the enchanter's skill on it, the fee taken as each item is made.
Function OpenTable(Actor akEnchanter, Int aiEnchant)
  If busy || !Table || SKSE.GetVersion() <= 0
    Return
  EndIf
  busy = True
  seller = akEnchanter
  gemsBefore = Gems()
  Int cheapest = 0
  Int size = 5
  While size >= 1
    If gemsBefore[size] > 0
      cheapest = size
    EndIf
    size -= 1
  EndWhile
  If cheapest == 0 && PlayerRef.GetItemCount(AzuraStar) > 0
    cheapest = 5
  EndIf
  If cheapest == 0
    Debug.Notification(NoGem)
    seller = None
    busy = False
    Return
  EndIf
  If PlayerRef.GetItemCount(Gold) < Fee(cheapest)
    Debug.Notification(NoGold)
    seller = None
    busy = False
    Return
  EndIf
  Float own = PlayerRef.GetActorValue("Enchanting")
  Float theirs = aiEnchant
  TMMwState st = MwState as TMMwState
  If st
    ; Stats: 8 + skill, Enchant being skill 9.
    own = st.GetStat(PlayerRef, 17, -1)
    theirs = st.GetStat(akEnchanter, 17, aiEnchant)
  EndIf
  Float factor = Game.GetGameSettingFloat("fEnchantingSkillFactor") - 1.0
  Float percent = 100.0 * ((1.0 + factor * theirs / 100.0) / (1.0 + factor * own / 100.0) - 1.0)
  If Boost && percent > 0.0
    Boost.SetNthEffectMagnitude(0, percent)
    PlayerRef.AddSpell(Boost, False)
  EndIf
  madeSoFar = Game.QueryStat("Magic Items Made")
  ObjectReference placed = PlayerRef.PlaceAtMe(Table)
  placed.Activate(PlayerRef)
  ; WaitMenuMode counts while the menu is up, so the fee follows each item.
  Utility.WaitMenuMode(1.0)
  While UI.IsMenuOpen("Crafting Menu") || PlayerRef.GetSitState() != 0
    Settle()
    Utility.WaitMenuMode(0.5)
  EndWhile
  Settle()
  If Boost
    PlayerRef.RemoveSpell(Boost)
  EndIf
  placed.Disable()
  placed.Delete()
  seller = None
  busy = False
EndFunction

; ---- recharging ----

; The hand to recharge (0 left, 1 right, as WornObject), asked when both hold an enchanted item; -1 for none.
Int Function ChooseHand()
  Bool right = WornObject.GetItemMaxCharge(PlayerRef, 1, 0) > 0.0
  Bool left = WornObject.GetItemMaxCharge(PlayerRef, 0, 0) > 0.0
  If right && left
    Int button = HandMenu.Show()
    If button == 0
      Return 1
    ElseIf button == 1
      Return 0
    EndIf
    Return -1
  ElseIf right
    Return 1
  ElseIf left
    Return 0
  EndIf
  Debug.Notification("You hold nothing that takes a charge.")
  Return -1
EndFunction

; A filled gem of a size the player carries, or None.
SoulGem Function GemOf(Int aiSize)
  Int i = PlayerRef.GetNumItems()
  While i > 0
    i -= 1
    SoulGem gem = PlayerRef.GetNthForm(i) as SoulGem
    If gem && gem.GetSoulSize() == aiSize
      Return gem
    EndIf
  EndWhile
  Return None
EndFunction

; Whether an enchantment is one of this plugin's (Morrowind's charges), by the load order byte it shares with this quest.
Bool Function IsOurs(Form akForm)
  Return akForm && Math.RightShift(akForm.GetFormID(), 24) == Math.RightShift(GetFormID(), 24)
EndFunction

; RightItemCharge, LeftItemCharge: SKSE has no SetItemCharge for what is worn. The value moved by ModActorValue, or where
; that leaves it, by RestoreActorValue.
Function AddCharge(Int aiHand, Float afAmount)
  String value = "RightItemCharge"
  If aiHand == 0
    value = "LeftItemCharge"
  EndIf
  Float was = WornObject.GetItemCharge(PlayerRef, aiHand, 0)
  PlayerRef.ModActorValue(value, afAmount)
  If WornObject.GetItemCharge(PlayerRef, aiHand, 0) <= was
    PlayerRef.RestoreActorValue(value, afAmount)
  EndIf
EndFunction

; The enchanter recharges what the player holds with the player's gem (rechargeItem, the enchanter's stats in the player's).
Function Recharge(Actor akEnchanter, Int aiEnchant, Int aiIntelligence, Int aiLuck)
  If busy || SKSE.GetVersion() <= 0
    Return
  EndIf
  busy = True
  Int hand = ChooseHand()
  If hand < 0
    busy = False
    Return
  EndIf
  Float maxCharge = WornObject.GetItemMaxCharge(PlayerRef, hand, 0)
  Float charge = WornObject.GetItemCharge(PlayerRef, hand, 0)
  If charge >= maxCharge
    Debug.Notification("That is fully charged.")
    busy = False
    Return
  EndIf
  Int size = GemMenu.Show() + 1
  If size < 1 || size > 5
    busy = False
    Return
  EndIf
  SoulGem gem = GemOf(size)
  If !gem
    Debug.Notification("You have no soul gem of that size with a soul.")
    busy = False
    Return
  EndIf
  ; The most the fee can be: the whole soul.
  If PlayerRef.GetItemCount(Gold) < Offer(akEnchanter, (Souls[size - 1] * SoulGemMult) as Int)
    Debug.Notification(NoGold)
    busy = False
    Return
  EndIf
  Float skill = aiEnchant
  Float intelligence = aiIntelligence
  Float luck = aiLuck
  TMMwState st = MwState as TMMwState
  If st
    skill = st.GetStat(akEnchanter, 17, aiEnchant)
    intelligence = st.GetStat(akEnchanter, 1, aiIntelligence)
    luck = st.GetStat(akEnchanter, 7, aiLuck)
  EndIf
  Float luckTerm = 0.1 * luck
  If luckTerm < 1.0 || luckTerm > 10.0
    luckTerm = 1.0
  EndIf
  Float intelligenceTerm = 0.2 * intelligence
  If intelligenceTerm > 20.0
    intelligenceTerm = 20.0
  ElseIf intelligenceTerm < 1.0
    intelligenceTerm = 1.0
  EndIf
  Float chance = (skill + intelligenceTerm + luckTerm) * FatigueTerm(akEnchanter)
  Int roll = Utility.RandomInt(0, 99)
  If roll < chance
    Float share = roll / chance
    Float soul = SkyrimSouls[size - 1]
    If IsOurs(WornObject.GetEnchantment(PlayerRef, hand, 0))
      soul = Souls[size - 1]
    EndIf
    Float restored = soul * share
    If charge + restored > maxCharge
      restored = maxCharge - charge
    EndIf
    AddCharge(hand, restored)
    seller = akEnchanter
    Int price = Offer(akEnchanter, (Souls[size - 1] * share * SoulGemMult) as Int)
    If price < 1
      price = 1
    EndIf
    Pay(price)
    seller = None
  Else
    Debug.Notification(Failed)
  EndIf
  PlayerRef.RemoveItem(gem, 1, False)
  busy = False
EndFunction
