ScriptName TMDfTavern extends Quest
{Daggerfall's tavern rooms (Daggerfall Unity FormulaHelper.CalculateRoomCost): 7 gold a night, and a stay that takes in
Heart's Day (16 Sun's Dawn, day 46 of Daggerfall's 30-day-month year) gets that night free. Renting makes the beds
in the tavern the player's (SKSE finds them in the cell) until the stay is over; they go back to the tavern then.}

Message Property RentChoice Auto
{1 night / 3 nights / a week / never mind.}
Form Property Bed Auto
GlobalVariable Property GameMonth Auto
GlobalVariable Property GameDay Auto
Potion Property Ale Auto

ObjectReference[] rented
Faction[] owners
Int count = 0

Int Function Cost(Int nights)
  Int dayOfYear = GameMonth.GetValueInt() * 30 + GameDay.GetValueInt()
  Int cost = 7 * nights
  If dayOfYear <= 46 && dayOfYear + nights > 46
    cost = 7 * (nights - 1)
  EndIf
  Return cost
EndFunction

; "I'd like a room." to a tavern keeper.
Function Rent(Actor keeper)
  Int choice = RentChoice.Show(Cost(1), Cost(3), Cost(7))
  Int nights = 0
  If choice == 0
    nights = 1
  ElseIf choice == 1
    nights = 3
  ElseIf choice == 2
    nights = 7
  Else
    Return
  EndIf
  Actor player = Game.GetPlayer()
  Form gold = Game.GetForm(0xF)
  Int cost = Cost(nights)
  If player.GetItemCount(gold) < cost
    Debug.MessageBox("A room for that long is " + cost + " gold. You don't have enough.")
    Return
  EndIf
  If cost > 0
    player.RemoveItem(gold, cost)
  EndIf
  GiveBack()
  rented = new ObjectReference[32]
  owners = new Faction[32]
  count = 0
  Cell here = keeper.GetParentCell()
  Int n = here.GetNumRefs(40)
  Int i = 0
  While i < n && count < 32
    ObjectReference r = here.GetNthRef(i, 40)
    ; Skyrim's bed, or Daggerfall's own beds made with its use (DfPropBuilder: copies keeping its name)
    If r && (r.GetBaseObject() == Bed || r.GetBaseObject().GetName() == Bed.GetName())
      rented[count] = r
      owners[count] = r.GetFactionOwner()
      r.SetActorOwner(player.GetActorBase())
      count += 1
    EndIf
    i += 1
  EndWhile
  RegisterForSingleUpdateGameTime(24.0 * nights)
  If cost == 0
    Debug.MessageBox("It's Heart's Day: your room is free. Sleep well.")
  Else
    Debug.MessageBox("You have a room for " + nights + " night(s), for " + cost + " gold.")
  EndIf
EndFunction

; "I'll have a drink.": an ale for 2 gold, free on the New Life Festival (1 Morning Star) and Harvest's End
; (27 Last Seed).
Function Drink()
  Int month = GameMonth.GetValueInt()
  Int day = GameDay.GetValueInt()
  Bool free = month == 0 && day == 1 || month == 7 && day == 27
  Actor player = Game.GetPlayer()
  Form gold = Game.GetForm(0xF)
  If !free
    If player.GetItemCount(gold) < 2
      Debug.Notification("An ale is 2 gold.")
      Return
    EndIf
    player.RemoveItem(gold, 2)
  EndIf
  player.AddItem(Ale, 1)
EndFunction

Event OnUpdateGameTime()
  GiveBack()
EndEvent

Function GiveBack()
  Int i = 0
  While i < count
    If rented[i]
      rented[i].SetFactionOwner(owners[i])
    EndIf
    i += 1
  EndWhile
  count = 0
EndFunction
