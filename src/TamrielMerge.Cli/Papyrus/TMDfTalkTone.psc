ScriptName TMDfTalkTone extends TopicInfo Hidden
{A tone topic's response (Daggerfall's polite, normal or blunt talk: TalkTone): the tone is kept for the rest of the
talk, and the reactions rolled again for it when the next question is asked.}

Int Property Tone = 1 Auto
{0 polite (Etiquette), 1 normal, 2 blunt (Streetwise).}

Function Fragment_End(ObjectReference akSpeakerRef)
  TMDfQuestMachine m = Quest.GetQuest("DFQuestMachine") as TMDfQuestMachine
  If m
    m.SetTalkTone(Tone)
  EndIf
EndFunction
