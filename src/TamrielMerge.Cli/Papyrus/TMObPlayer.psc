ScriptName TMObPlayer extends ReferenceAlias
{The player, for what Oblivion's engine noticed about the player and the converted Oblivion scripts ask (TamrielMerge):
who the player has talked to (GetTalkedToPC), theft and pickpocketing (GetCrimeKnown, GetPCFactionSteal, OnAlarm),
trespassing, and stolen goods sold (GetAmountSoldStolen).}

TMObState Property ObState Auto
Actor Property PlayerRef Auto

Event OnInit()
  RegisterForMenu("Dialogue Menu")
  RegisterForSleep()
  RegisterForSingleUpdate(5.0)
EndEvent

Event OnPlayerLoadGame()
  RegisterForMenu("Dialogue Menu")
  RegisterForSleep()
  RegisterForSingleUpdate(5.0)
EndEvent

; Every few seconds: whether the player is in an Oblivion jail (IsPlayerInJail), and the bounty in step with Skyrim's.
Event OnUpdate()
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

; Stolen goods sold (to any merchant) count towards GetAmountSoldStolen.
Event OnItemRemoved(Form akBaseItem, Int aiItemCount, ObjectReference akItemReference, ObjectReference akDestContainer)
  If akDestContainer && UI.IsMenuOpen("BarterMenu") && StolenItems().HasForm(akBaseItem)
    ObState.PCAmountSoldStolen.Mod(akBaseItem.GetGoldValue() * aiItemCount)
  EndIf
EndEvent

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
