ScriptName TMDfQuestTopic extends TopicInfo
{Daggerfall quest dialogue: Service 0 "Do you have any work for me?" (TMDfQuestMachine.OfferFrom), 1 an answer about a
quest resource ("Tell me about"): a place's marks it on the map, and every answer shows the topics of the resources it
names (Reveals: Daggerfall Unity's answers reveal dialog links), 2 the summoning of a Daedra Prince by a witches' coven,
the Mages Guild or a temple (TMDfQuestMachine.SummonDaedra).}

TMDfQuestMachine Property Machine Auto
Int Property Service = 0 Auto
TMDfQuest Property DfQuest Auto
Int Property Place = -1 Auto
Int[] Property Reveals Auto
{The "Tell me about" keys the answer names (place n, person 20 + n, item 40 + n).}

; The tone's reactions for "any work" (its modifier 0: TalkTone); a refused answer (reaction 0) has no offer after it.
Function Fragment_Begin(ObjectReference akSpeakerRef)
  If Service == 0
    Machine.RollTalk(0)
  EndIf
EndFunction

Function Fragment_End(ObjectReference akSpeakerRef)
  If Service == 0
    Machine.OfferFrom(akSpeakerRef as Actor)
  ElseIf Service == 1 && DfQuest
    If Place >= 0
      Machine.RevealSite(DfQuest.PlaceSiteOf(Place))
    EndIf
    If Reveals
      Int i = 0
      While i < Reveals.Length
        DfQuest.ShowAbout(Reveals[i], True)
        i += 1
      EndWhile
    EndIf
  ElseIf Service == 2
    Machine.SummonDaedra(akSpeakerRef as Actor)
  EndIf
EndFunction
