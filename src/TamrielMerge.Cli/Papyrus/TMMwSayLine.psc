ScriptName TMMwSayLine extends TopicInfo Hidden
{A Morrowind script's Say line (MWScriptSay's topics): the speaker is saying it from its first word to its last
(TMMwActorAI.Saying), which SayDone reads, as Morrowind asks the actor whether it has finished speaking.}

Function Fragment_Begin(ObjectReference akSpeakerRef)
  TMMwActorAI mw = akSpeakerRef as TMMwActorAI
  If mw
    mw.Saying = True
    mw.SaidAt = Utility.GetCurrentRealTime()
  EndIf
EndFunction

Function Fragment_End(ObjectReference akSpeakerRef)
  TMMwActorAI mw = akSpeakerRef as TMMwActorAI
  If mw
    mw.Saying = False
  EndIf
EndFunction
