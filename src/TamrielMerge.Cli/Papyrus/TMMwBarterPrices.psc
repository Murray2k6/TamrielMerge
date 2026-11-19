ScriptName TMMwBarterPrices extends Quest
{Morrowind's barter prices in Skyrim's barter menu (the quest MWBarter; MwVendors). Skyrim prices by the Speech skill;
Morrowind by OpenMW's MechanicsManager::getBarterOffer (apps/openmw/mwmechanics/mechanicsmanagerimp.cpp):

  a = min(player Mercantile, 100)      b = min(0.1 x player Luck, 10)       c = min(0.2 x player Personality, 10)
  d = min(merchant Mercantile, 100)    e = min(0.1 x merchant Luck, 10)     f = min(0.2 x merchant Personality, 10)
  pcTerm  = (disposition - 50 + a + b + c) x player fatigue term
  npcTerm = (d + e + f) x merchant fatigue term
  buying:  price = base x 0.01 x (100 - 0.5 x (pcTerm - npcTerm))
  selling: price = base x 0.01 x (50 - 0.5 x (npcTerm - pcTerm))        (each at least 1)

where the fatigue term is fFatigueBase - fFatigueMult x (1 - fatigue / max fatigue) (CreatureStats::getFatigueTerm) and
the disposition is the merchant's derived one (TMMwActorAI). Morrowind then lets the player haggle: an offer d% off the
merchant's (or d% more when selling) is accepted when a roll of 1-100 is within
  x = fBargainOfferMulti x d + fBargainOfferBase + |pcTerm' - npcTerm'|
(mwmechanics/trading.cpp Trading::haggle; pcTerm' has fDispositionMod x (disposition - 50) for the disposition), a
failure costing iBarterFailDisposition. Skyrim's menu has no offer and counter-offer, so the price here is the best
accepted offer - the largest d with x >= 100, which never fails: d = (fBargainOfferBase + |pcTerm' - npcTerm'| - 100)
/ -fBargainOfferMulti, 0 when that is negative - and each trade raises disposition by iBarterSuccessDisposition, as
every accepted offer does (TMMwBarterTrades). The prices go through the hidden perk MWBarterPerk (Mod Buy Prices and
Mod Sell Prices set to the buying and selling terms while MWBarterActive is 1); the player's Morrowind stats come from
TMMwState (the progression's Mercantile skill; Luck and Personality as the stones keep them).}

Perk Property BarterPerk Auto
GlobalVariable Property Active Auto
{1 while a Morrowind vendor's barter is open: the perk's entries apply.}
Quest Property MwState Auto
{MWScriptState (TMMwState), for the stats; without it the player is a middling character.}
Float Property BargainOfferBase = 50.0 Auto
Float Property BargainOfferMulti = -4.0 Auto
Float Property DispositionMod = 1.0 Auto
Float Property FatigueBase = 1.25 Auto
Float Property FatigueMult = 0.5 Auto
Int Property SuccessDisposition = 1 Auto
{Morrowind's settings (GMSTs).}

Actor vendor

Event OnInit()
  Setup()
EndEvent

Function Setup()
  Actor p = Game.GetPlayer()
  If BarterPerk && !p.HasPerk(BarterPerk)
    p.AddPerk(BarterPerk)
  EndIf
  RegisterForMenu("BarterMenu")
  RegisterForModEvent("TMMwGameLoaded", "OnTMMwGameLoaded")
  Clear()
EndFunction

; After a load (TMMwPlayerState sends it): SKSE's changes to the perk aren't saved, and no barter is open.
Event OnTMMwGameLoaded()
  Setup()
EndEvent

Event OnMenuClose(String asMenuName)
  If asMenuName == "BarterMenu"
    Clear()
  EndIf
EndEvent

; Skyrim's own prices again (the entries are conditioned on Active, and set back to 1 besides).
Function Clear()
  vendor = None
  If Active
    Active.SetValue(0.0)
  EndIf
  If BarterPerk
    BarterPerk.SetNthEntryValue(0, 0, 1.0)
    BarterPerk.SetNthEntryValue(1, 0, 1.0)
  EndIf
EndFunction

; CreatureStats::getFatigueTerm: fatigue is Skyrim's stamina.
Float Function FatigueTerm(Actor a)
  Float normalised = a.GetActorValuePercentage("Stamina")
  If normalised < 0.0
    normalised = 0.0
  ElseIf normalised > 1.0
    normalised = 1.0
  EndIf
  Return FatigueBase - FatigueMult * (1.0 - normalised)
EndFunction

Float Function Capped(Float afValue, Float afCap)
  If afValue > afCap
    Return afCap
  EndIf
  Return afValue
EndFunction

; The vendor's prices for the barter about to open (TMMwVendor.Barter): the record's Mercantile, Luck and Personality
; are passed, TMMwState adding what its scripts changed.
Function Apply(Actor akVendor, Int aiMercantile, Int aiLuck, Int aiPersonality)
  Actor p = Game.GetPlayer()
  vendor = akVendor
  TMMwState st = None
  If MwState
    st = MwState as TMMwState
  EndIf
  Float pcMercantile = 30.0 + p.GetLevel()
  Float pcLuck = 50.0
  Float pcPersonality = 50.0
  Float npcMercantile = aiMercantile
  Float npcLuck = aiLuck
  Float npcPersonality = aiPersonality
  If st
    pcMercantile = st.GetStat(p, 32, -1)
    pcLuck = st.GetStat(p, 7, -1)
    pcPersonality = st.GetStat(p, 6, -1)
    npcMercantile = st.GetStat(akVendor, 32, aiMercantile)
    npcLuck = st.GetStat(akVendor, 7, aiLuck)
    npcPersonality = st.GetStat(akVendor, 6, aiPersonality)
  EndIf
  ; The derived disposition as it stands now (Variable09 + 50, Charm included).
  TMMwActorAI ai = akVendor as TMMwActorAI
  If ai
    ai.UpdateDisposition()
  EndIf
  Float disposition = TMMwScript.GetDisposition(akVendor)
  Float a = Capped(pcMercantile, 100.0)
  Float b = Capped(0.1 * pcLuck, 10.0)
  Float c = Capped(0.2 * pcPersonality, 10.0)
  Float d = Capped(npcMercantile, 100.0)
  Float e = Capped(0.1 * npcLuck, 10.0)
  Float f = Capped(0.2 * npcPersonality, 10.0)
  Float pcFatigue = FatigueTerm(p)
  Float npcFatigue = FatigueTerm(akVendor)
  Float pcTerm = (disposition - 50.0 + a + b + c) * pcFatigue
  Float npcTerm = (d + e + f) * npcFatigue
  Float buyTerm = 0.01 * (100.0 - 0.5 * (pcTerm - npcTerm))
  Float sellTerm = 0.01 * (50.0 - 0.5 * (npcTerm - pcTerm))
  ; The best offer Trading::haggle always accepts.
  Float pcHaggle = (DispositionMod * (disposition - 50.0) - 50.0 + a + b + c) * pcFatigue
  Float diff = Math.Abs(pcHaggle - npcTerm)
  Float dMax = 0.0
  If BargainOfferMulti < 0.0
    dMax = (BargainOfferBase + diff - 100.0) / (0.0 - BargainOfferMulti)
  EndIf
  If dMax < 0.0
    dMax = 0.0
  ElseIf dMax > 99.0
    dMax = 99.0
  EndIf
  Float buy = buyTerm * (1.0 - dMax / 100.0)
  Float sell = sellTerm / (1.0 - dMax / 100.0)
  If buy < 0.01
    buy = 0.01
  EndIf
  If sell < 0.0
    sell = 0.0
  EndIf
  If BarterPerk
    BarterPerk.SetNthEntryValue(0, 0, buy)
    BarterPerk.SetNthEntryValue(1, 0, sell)
  EndIf
  If Active
    Active.SetValue(1.0)
  EndIf
EndFunction

; A trade done (gold changed hands in the menu): an accepted offer's iBarterSuccessDisposition, permanent
; (DialogueManager::applyBarterDispositionChange).
Function Traded()
  If !vendor
    Return
  EndIf
  TMMwActorAI ai = vendor as TMMwActorAI
  If ai
    ai.SetBaseDisposition(ai.GetBaseDisposition() + SuccessDisposition)
  EndIf
EndFunction
