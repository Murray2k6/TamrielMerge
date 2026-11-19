ScriptName TMMwBarter extends TopicInfo Hidden
{"Barter" (sBarter): a Morrowind vendor's barter menu - restocked, priced as Morrowind prices it (TMMwVendor.Barter).}

Function Fragment_End(ObjectReference akSpeakerRef)
  TMMwVendor vendor = akSpeakerRef as TMMwVendor
  If vendor
    vendor.Barter()
  Else
    Actor a = akSpeakerRef as Actor
    If a
      a.ShowBarterMenu()
    EndIf
  EndIf
EndFunction
