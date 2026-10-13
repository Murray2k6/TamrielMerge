ScriptName TMDfWhereIs extends TopicInfo Hidden
{The "Where is..." response: works out the directions to each kind of building before the player picks one.}

TMDfTalk Property Talk Auto

Function Fragment_Begin(ObjectReference akSpeakerRef)
  Talk.UpdateDirections()
EndFunction
