ScriptName TMMwBarterTrades extends ReferenceAlias
{The player, on MWBarter: each trade with a Morrowind vendor (gold entering or leaving the player's inventory while its
barter is open) is an accepted offer, raising the vendor's disposition by iBarterSuccessDisposition
(TMMwBarterPrices.Traded).}

Form Property Gold Auto
GlobalVariable Property Active Auto

Event OnInit()
  Watch()
EndEvent

Event OnPlayerLoadGame()
  Watch()
EndEvent

Function Watch()
  RemoveAllInventoryEventFilters()
  If Gold
    AddInventoryEventFilter(Gold)
  EndIf
EndFunction

Event OnItemAdded(Form akBaseItem, Int aiItemCount, ObjectReference akItemReference, ObjectReference akSourceContainer)
  Trade()
EndEvent

Event OnItemRemoved(Form akBaseItem, Int aiItemCount, ObjectReference akItemReference, ObjectReference akDestContainer)
  Trade()
EndEvent

Function Trade()
  If Active && Active.GetValue() == 1.0 && UI.IsMenuOpen("BarterMenu")
    (GetOwningQuest() as TMMwBarterPrices).Traded()
  EndIf
EndFunction
