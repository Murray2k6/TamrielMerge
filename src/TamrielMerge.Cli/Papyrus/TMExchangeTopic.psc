ScriptName TMExchangeTopic extends TopicInfo
{"I'd like to exchange coin." to a money changer, or "I'd like to do some banking." to a Daggerfall banker: opens the
changer's service in the Tamriel menu (TMMoneyChanger).}

TMMoneyChanger Property Changer Auto

Function Fragment_End(ObjectReference akSpeakerRef)
  Changer.Open(akSpeakerRef as Actor)
EndFunction
