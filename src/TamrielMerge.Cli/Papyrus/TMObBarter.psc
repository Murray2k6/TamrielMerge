ScriptName TMObBarter extends TopicInfo Hidden
{"Barter": an Oblivion merchant's barter menu, its gold set back to its barter gold first (TMObVendor.Barter).}

Function Fragment_End(ObjectReference akSpeakerRef)
  TMObVendor vendor = akSpeakerRef as TMObVendor
  If vendor
    vendor.Barter()
  Else
    Actor a = akSpeakerRef as Actor
    If a
      a.ShowBarterMenu()
    EndIf
  EndIf
EndFunction
