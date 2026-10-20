Scriptname TMObTrigger extends ObjectReference
{An Oblivion trigger shape (a phantom in the model of a flame, a pressure plate, a tripwire, a trigger zone...) as a
Skyrim trigger box: what enters it is told to the reference whose model it is, whose own script handles it in its
OnTrigger event, as vanilla's DLC2ApocryphaBookPortalActivator hands its activation to the book (Controller.OnActivate).}

ObjectReference Property Owner Auto

Event OnTrigger(ObjectReference akActionRef)
	If Owner
		Owner.OnTrigger(akActionRef)
	EndIf
EndEvent

Event OnTriggerEnter(ObjectReference akActionRef)
	If Owner
		Owner.OnTriggerEnter(akActionRef)
	EndIf
EndEvent

Event OnTriggerLeave(ObjectReference akActionRef)
	If Owner
		Owner.OnTriggerLeave(akActionRef)
	EndIf
EndEvent
