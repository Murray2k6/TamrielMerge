ScriptName TMDfRecallEffect extends ActiveMagicEffect
{Daggerfall's Recall: the first cast sets an anchor where the caster stands, the next cast returns them to it.}

ObjectReference Property Anchor Auto
GlobalVariable Property AnchorSet Auto

Event OnEffectStart(Actor akTarget, Actor akCaster)
  If AnchorSet.GetValue() == 0
    Anchor.MoveTo(akCaster)
    AnchorSet.SetValue(1)
    Debug.Notification("Recall anchor set.")
  Else
    AnchorSet.SetValue(0)
    akCaster.MoveTo(Anchor)
  EndIf
EndEvent
