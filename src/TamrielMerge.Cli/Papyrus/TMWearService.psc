ScriptName TMWearService extends TopicInfo Hidden
{"Repair": the person's repair service, the game's own rules and prices (TMWear.Service).}

TMWear Property Wear Auto

Function Fragment_End(ObjectReference akSpeakerRef)
  If Wear
    Wear.Service(akSpeakerRef as Actor)
  EndIf
EndFunction
