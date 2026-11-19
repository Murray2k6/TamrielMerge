ScriptName TMObVendor extends Actor
{An Oblivion merchant (ObVendors.cs). Oblivion's barter gold is what a merchant can pay for one item, and it never runs
out; Skyrim's merchants pay from a purse that empties. The chest's gold is set back to the barter gold each time the
barter opens, so the merchant can always pay up to it again, and never holds more.}

ObjectReference Property Chest Auto
MiscObject Property Gold Auto
Int Property BarterGold Auto

Function Barter()
  If Chest && Gold
    Int have = Chest.GetItemCount(Gold)
    If have < BarterGold
      Chest.AddItem(Gold, BarterGold - have, True)
    ElseIf have > BarterGold
      Chest.RemoveItem(Gold, have - BarterGold, True)
    EndIf
  EndIf
  ShowBarterMenu()
EndFunction
