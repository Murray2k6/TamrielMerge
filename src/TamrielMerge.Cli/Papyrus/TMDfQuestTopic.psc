ScriptName TMDfQuestTopic extends TopicInfo
{Daggerfall quest dialogue: Service 0 "Do you have any work for me?" (TMDfQuestMachine.OfferFrom), 1 an answer about a
quest place, which marks it on the map.}

TMDfQuestMachine Property Machine Auto
Int Property Service = 0 Auto
TMDfQuest Property DfQuest Auto
Int Property Place = -1 Auto

Function Fragment_End(ObjectReference akSpeakerRef)
  If Service == 0
    Machine.OfferFrom(akSpeakerRef as Actor)
  ElseIf Service == 1 && DfQuest && Place >= 0
    Machine.RevealSite(DfQuest.PlaceSiteOf(Place))
  EndIf
EndFunction
