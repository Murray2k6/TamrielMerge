ScriptName TMDfTavernTopic extends TopicInfo
{"I'd like a room." (rents a room) or "I'll have a drink." (Drink) to a tavern keeper (TMDfTavern).}

TMDfTavern Property Tavern Auto
Bool Property Drink = False Auto

Function Fragment_End(ObjectReference akSpeakerRef)
  If Drink
    Tavern.Drink()
  Else
    Tavern.Rent(akSpeakerRef as Actor)
  EndIf
EndFunction
