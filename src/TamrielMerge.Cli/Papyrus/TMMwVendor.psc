ScriptName TMMwVendor extends Actor
{A Morrowind vendor (an NPC with a barter service in AIDT; MwVendors): its merchant chest holds what Morrowind barters
(its own inventory, the containers and items it owns in its cell, a spell merchant's tomes) and its barter gold (NPDT's
gold, OpenMW MWClass::Npc::getBaseGold). Restocking is Morrowind's: when a barter opens and fBarterGoldResetDelay hours
(24) have passed since the last restock, the gold goes back to the record's (OpenMW mwgui/tradewindow.cpp
TradeWindow::restock) and every restocking item - a negative count in the record's inventory - is refilled to its count
(mwworld/containerstore.cpp ContainerStore::restock); what the player sold stays. Barter (TMMwBarter) restocks, sets
Morrowind's prices (TMMwBarterPrices) and opens Skyrim's barter menu.}

ObjectReference Property Chest Auto
{The merchant chest (in the holding cell MWVendorChests).}
Form Property Gold Auto
{Skyrim's gold item.}
Quest Property BarterQuest Auto
{MWBarter (TMMwBarterPrices).}
Int Property BarterGold = 0 Auto
{The record's gold: the gold pool a restock sets.}
Form[] Property RestockItems Auto
Int[] Property RestockCounts Auto
{Restocking items and the count each is refilled to.}
Float Property ResetDelay = 24.0 Auto
{fBarterGoldResetDelay, hours.}
Int Property Mercantile = 0 Auto
Int Property Luck = 0 Auto
Int Property Personality = 0 Auto
{The record's Mercantile skill, Luck and Personality (what TMMwState's scripts changed is added by the prices).}

Float lastRestock = -1000000000.0
Bool stocked = False

; The Barter topic: restock as Morrowind would, Morrowind's prices for this trade, Skyrim's menu.
Function Barter()
  Restock()
  TMMwBarterPrices prices = BarterQuest as TMMwBarterPrices
  If prices
    prices.Apply(Self, Mercantile, Luck, Personality)
  EndIf
  ShowBarterMenu()
EndFunction

; TradeWindow::restock: once ResetDelay hours have passed since the last restock. The first barter finds the chest as
; the record stocked it, so it only starts the clock.
Function Restock()
  Float now = Utility.GetCurrentGameTime() * 24.0
  If stocked && now - lastRestock < ResetDelay
    Return
  EndIf
  lastRestock = now
  If !stocked
    stocked = True
    Return
  EndIf
  If !Chest
    Return
  EndIf
  ; The gold pool back to the record's gold (setGoldPool(getBaseGold)), whatever it had grown or shrunk to.
  Int have = Chest.GetItemCount(Gold)
  If have > 0
    Chest.RemoveItem(Gold, have, True)
  EndIf
  If BarterGold > 0
    Chest.AddItem(Gold, BarterGold, True)
  EndIf
  ; Each restocking item up to its count (ContainerStore::restock adds what is missing, never takes away).
  Int i = 0
  While RestockItems && i < RestockItems.Length
    Form item = RestockItems[i]
    If item && RestockCounts && i < RestockCounts.Length
      Int want = RestockCounts[i]
      Int got = Chest.GetItemCount(item)
      If got < want
        Chest.AddItem(item, want - got, True)
      EndIf
    EndIf
    i += 1
  EndWhile
EndFunction
