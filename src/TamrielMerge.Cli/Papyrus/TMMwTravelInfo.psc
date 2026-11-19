ScriptName TMMwTravelInfo extends TopicInfo Hidden
{The Travel topic's response (Morrowind's travel services): as it ends, the speaker's destinations are offered with
their prices (TMMwTravel.Offer).}

TMMwTravel Property Travel Auto

Function Fragment_End(ObjectReference akSpeakerRef)
  If Travel
    Travel.Offer(akSpeakerRef as Actor)
  EndIf
EndFunction
