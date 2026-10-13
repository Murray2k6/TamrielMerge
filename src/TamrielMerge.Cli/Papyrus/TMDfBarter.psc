ScriptName TMDfBarter extends TopicInfo Hidden
{"Let me see your wares.": a Daggerfall shopkeeper's barter menu.}

Function Fragment_End(ObjectReference akSpeakerRef)
  Actor keeper = akSpeakerRef as Actor
  If keeper
    keeper.ShowBarterMenu()
  EndIf
EndFunction
