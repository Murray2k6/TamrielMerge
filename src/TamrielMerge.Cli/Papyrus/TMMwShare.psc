ScriptName TMMwShare extends TopicInfo Hidden
{Morrowind's Companion Share: the companion's inventory opened to the player (to take and give freely), offered in the
dialogue of whoever's companion local is 1. A hired companion's script counts what changes hands (minimumProfit).}

Function Fragment_End(ObjectReference akSpeakerRef)
  Actor a = akSpeakerRef as Actor
  If a
    a.OpenInventory(True)
  EndIf
EndFunction
