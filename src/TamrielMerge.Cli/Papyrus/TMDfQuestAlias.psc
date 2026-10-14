ScriptName TMDfQuestAlias extends ReferenceAlias
{A Daggerfall quest's person (Kind 1: clicking them, "clicked npc"), item (Kind 0: picking it up, "clicked item";
reading or using it, "item used") or named individual (Kind 2, Index the faction: "when individual is available")
for TMDfQuest.}

Int Property Kind = 1 Auto
Int Property Index = 0 Auto

Event OnActivate(ObjectReference akActionRef)
  If Kind == 1 && akActionRef == Game.GetPlayer()
    (GetOwningQuest() as TMDfQuest).PersonClicked(Index)
  ElseIf Kind == 2 && akActionRef == Game.GetPlayer()
    (GetOwningQuest() as TMDfQuest).IndividualClicked(Index)
  EndIf
EndEvent

Event OnContainerChanged(ObjectReference akNewContainer, ObjectReference akOldContainer)
  If Kind == 0
    (GetOwningQuest() as TMDfQuest).ItemMoved(Index, akNewContainer, akOldContainer)
  EndIf
EndEvent

Event OnRead()
  If Kind == 0
    (GetOwningQuest() as TMDfQuest).ItemUsedByPlayer(Index)
  EndIf
EndEvent

Event OnEquipped(Actor akActor)
  If Kind == 0 && akActor == Game.GetPlayer()
    (GetOwningQuest() as TMDfQuest).ItemUsedByPlayer(Index)
  EndIf
EndEvent
