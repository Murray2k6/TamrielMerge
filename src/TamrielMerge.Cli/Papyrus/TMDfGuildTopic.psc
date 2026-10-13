ScriptName TMDfGuildTopic extends TopicInfo
{A Daggerfall guild service asked of a guild member (TMDfGuilds): 0 join, 1 training, 2 temple healing.}

TMDfGuilds Property Guilds Auto
Int Property Guild = 0 Auto
Int Property Service = 0 Auto

Function Fragment_End(ObjectReference akSpeakerRef)
  Actor speaker = akSpeakerRef as Actor
  If Service == 0
    Guilds.Join(Guild)
  ElseIf Service == 1
    Guilds.Train(Guild, speaker)
  ElseIf Service == 2
    Guilds.Heal(Guild)
  EndIf
EndFunction
