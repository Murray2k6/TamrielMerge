ScriptName TMMwRechargeInfo extends TopicInfo Hidden
{"Recharge Enchanted Item" (sRechargeEnchantment): an enchanter's service (TMMwEnchanter.OfferRecharge).}

Function Fragment_End(ObjectReference akSpeakerRef)
  TMMwEnchanter enchanter = akSpeakerRef as TMMwEnchanter
  If enchanter
    enchanter.OfferRecharge()
  EndIf
EndFunction
