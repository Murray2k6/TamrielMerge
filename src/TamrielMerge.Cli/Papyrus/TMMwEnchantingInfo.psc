ScriptName TMMwEnchantingInfo extends TopicInfo Hidden
{"Enchanting" (sEnchanting): an enchanter's service (TMMwEnchanter.OpenEnchanting).}

Function Fragment_End(ObjectReference akSpeakerRef)
  TMMwEnchanter enchanter = akSpeakerRef as TMMwEnchanter
  If enchanter
    enchanter.OpenEnchanting()
  EndIf
EndFunction
