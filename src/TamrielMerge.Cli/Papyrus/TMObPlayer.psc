ScriptName TMObPlayer extends ReferenceAlias
{The player, for what Oblivion's engine noticed about the player and the converted Oblivion scripts ask (TamrielMerge):
who the player has talked to (GetTalkedToPC), theft and pickpocketing (GetCrimeKnown, GetPCFactionSteal, OnAlarm),
trespassing, and stolen goods sold (GetAmountSoldStolen).}

TMObState Property ObState Auto
Actor Property PlayerRef Auto

Event OnInit()
  ObState.GameLoaded()
  Register()
  RegisterForSingleUpdate(5.0)
EndEvent

Event OnPlayerLoadGame()
  ObState.GameLoaded()
  Register()
  RegisterForSingleUpdate(5.0)
EndEvent

; The menus Oblivion's scripts are in MenuMode for (ObPapyrus.MenuNames), conversations among them: the play clock
; stops in them (TMObState.PlayTime).
Function Register()
  RegisterForSleep()
  String[] menus = new String[21]
  menus[0] = "Dialogue Menu"
  menus[1] = "InventoryMenu"
  menus[2] = "ContainerMenu"
  menus[3] = "BarterMenu"
  menus[4] = "GiftMenu"
  menus[5] = "MagicMenu"
  menus[6] = "MapMenu"
  menus[7] = "StatsMenu"
  menus[8] = "Journal Menu"
  menus[9] = "Sleep/Wait Menu"
  menus[10] = "Book Menu"
  menus[11] = "Lockpicking Menu"
  menus[12] = "Crafting Menu"
  menus[13] = "TweenMenu"
  menus[14] = "Training Menu"
  menus[15] = "MessageBoxMenu"
  menus[16] = "LevelUp Menu"
  menus[17] = "FavoritesMenu"
  menus[18] = "RaceSex Menu"
  menus[19] = "Console"
  menus[20] = "Loading Menu"
  Int i = menus.Length
  While i > 0
    i -= 1
    RegisterForMenu(menus[i])
  EndWhile
EndFunction

Event OnMenuClose(String asMenuName)
  ObState.MenuClosed()
EndEvent

; Every few seconds: whether the player is in an Oblivion jail (IsPlayerInJail), and the bounty in step with Skyrim's.
Event OnUpdate()
  ObState.MirrorPlayer()
  ObState.CheckJail()
  RegisterForSingleUpdate(5.0)
EndEvent

Event OnSleepStart(Float afSleepStartTime, Float afDesiredSleepEndTime)
  ObState.PCSleeping = True
EndEvent

Event OnSleepStop(Bool abInterrupted)
  ObState.PCSleeping = False
EndEvent

; Whoever is in a conversation with the player has talked to the player.
Event OnMenuOpen(String asMenuName)
  ObState.MenuOpened()
  If asMenuName != "Dialogue Menu"
    Return
  EndIf
  ObState.MirrorPlayer()
  Cell here = PlayerRef.GetParentCell()
  If !here
    Return
  EndIf
  Int i = here.GetNumRefs(62)
  While i > 0
    i -= 1
    Actor other = here.GetNthRef(i, 62) as Actor
    If other && other != PlayerRef && other.IsInDialogueWithPlayer() && !ObState.TalkedToPC.HasForm(other)
      ObState.TalkedToPC.AddForm(other)
    EndIf
  EndWhile
EndEvent

; Taking what someone else owns is theft (from a living person's pockets, pickpocketing), as in Oblivion.
Event OnItemAdded(Form akBaseItem, Int aiItemCount, ObjectReference akItemReference, ObjectReference akSourceContainer)
  If UI.IsMenuOpen("BarterMenu") || UI.IsMenuOpen("GiftMenu")
    Return
  EndIf
  Form owner = None
  Faction factionOwner = None
  Actor fromActor = akSourceContainer as Actor
  If fromActor
    If fromActor.IsDead() || fromActor.IsPlayerTeammate()
      Return
    EndIf
    owner = fromActor
  ElseIf akItemReference
    owner = akItemReference.GetActorOwner()
    factionOwner = akItemReference.GetFactionOwner()
  ElseIf akSourceContainer
    owner = akSourceContainer.GetActorOwner()
    factionOwner = akSourceContainer.GetFactionOwner()
  EndIf
  ; Taken from the world in an owned cell (a house, a shop): the cell's owner's.
  If !owner && !factionOwner && !akSourceContainer && PlayerRef.GetParentCell()
    owner = PlayerRef.GetParentCell().GetActorOwner()
    factionOwner = PlayerRef.GetParentCell().GetFactionOwner()
  EndIf
  If !owner && !factionOwner
    Return
  EndIf
  If owner == PlayerRef.GetActorBase() || (factionOwner && PlayerRef.IsInFaction(factionOwner))
    Return
  EndIf
  If !StolenItems().HasForm(akBaseItem)
    StolenItems().AddForm(akBaseItem)
  EndIf
  If fromActor
    ObState.Crime(1, PlayerRef, fromActor)
  ElseIf owner
    ObState.Crime(0, PlayerRef, owner)
  Else
    ObState.Crime(0, PlayerRef, factionOwner)
  EndIf
EndEvent

; Stolen goods sold (to any merchant) count towards GetAmountSoldStolen. A quest item the player drops, stores, sells
; or gives away comes back (Oblivion keeps them in the player's inventory); a script's RemoveItem is let be.
Event OnItemRemoved(Form akBaseItem, Int aiItemCount, ObjectReference akItemReference, ObjectReference akDestContainer)
  If ObState.IsQuestItem(akBaseItem) && PlayerGaveAway(akDestContainer)
    If akItemReference
      PlayerRef.AddItem(akItemReference, aiItemCount, True)
    ElseIf akDestContainer
      akDestContainer.RemoveItem(akBaseItem, aiItemCount, True, PlayerRef)
    EndIf
    Debug.Notification(Game.GetGameSettingString("sDropQuestItemWarning"))
    Return
  EndIf
  If akDestContainer && UI.IsMenuOpen("BarterMenu") && StolenItems().HasForm(akBaseItem)
    ObState.PCAmountSoldStolen.Mod(akBaseItem.GetGoldValue() * aiItemCount)
  EndIf
EndEvent

; The player's own doing: dropped from the inventory, or put in a container, sold or given in its menu.
Bool Function PlayerGaveAway(ObjectReference akDestContainer)
  If !akDestContainer
    Return UI.IsMenuOpen("InventoryMenu") || UI.IsMenuOpen("FavoritesMenu")
  EndIf
  Return UI.IsMenuOpen("ContainerMenu") || UI.IsMenuOpen("BarterMenu") || UI.IsMenuOpen("GiftMenu")
EndFunction

Event OnLocationChange(Location akOldLoc, Location akNewLoc)
  Utility.Wait(2.0)
  If PlayerRef.IsTrespassing()
    Cell here = PlayerRef.GetParentCell()
    Form owner = None
    If here
      owner = here.GetActorOwner()
      If !owner
        owner = here.GetFactionOwner()
      EndIf
    EndIf
    ObState.Crime(2, PlayerRef, owner)
  EndIf
EndEvent

FormList Function StolenItems()
  Return ObState.StolenItems
EndFunction
