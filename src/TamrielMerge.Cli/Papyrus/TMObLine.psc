ScriptName TMObLine extends TMMwInfo Hidden
{An Oblivion dialogue response as it is said (TamrielMerge): the topics its Add Topics list gives the player, known
once it is said (Learns: their OBTopicKnown globals, which the topics' responses ask for), and its speaker talking
from its first word to its last (TMObState.LineStarted / LineEnded: IsTalking, and Say's length). A response with a
result script has that script (OB_R_...), which extends this one. A conversation's line (ConvTopic) tells
TMObConversations it is said and done, with what comes next.}

TMObState Property ObState Auto
Float[] Property Lengths Auto
{How long the response lasts in each of TMObState.VoiceTypes' voices (Oblivion's recordings of it, all its lines);
0 where Oblivion recorded none.}
Int Property ConvTopic = 0 Auto
{A conversation line's topic (its place in OBConvTopics, 1 on; 0 not a conversation's).}
Int Property ConvNext = 0 Auto
{Who says the next line: 0 the listener, 1 the speaker again, 2 either (Oblivion's Next Speaker).}
Bool Property ConvGoodbye = False Auto
{The conversation ends with it.}
Topic[] Property ConvLinks Auto
{Oblivion's Link To: the topics the conversation may go on with, in order.}
Quest Property Conversations Auto
{OBDialogue, which runs Oblivion's conversations (TMObConversations).}

Function Fragment_Begin(ObjectReference akSpeakerRef)
  Parent.Fragment_Begin(akSpeakerRef)
  If ObState
    ObState.LineStarted(akSpeakerRef as Actor, Self)
  EndIf
  If ConvTopic > 0 && Conversations
    (Conversations as TMObConversations).Spoken(akSpeakerRef as Actor, Self)
  EndIf
EndFunction

Function Fragment_End(ObjectReference akSpeakerRef)
  Parent.Fragment_End(akSpeakerRef)
  If ObState
    ObState.LineEnded(akSpeakerRef as Actor, Self)
  EndIf
  If ConvTopic > 0 && Conversations
    (Conversations as TMObConversations).Said(akSpeakerRef as Actor, Self)
  EndIf
EndFunction
