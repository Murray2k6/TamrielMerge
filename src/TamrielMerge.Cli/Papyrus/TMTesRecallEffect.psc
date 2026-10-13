ScriptName TMTesRecallEffect extends ActiveMagicEffect
{Morrowind's Mark and Recall (TesMagic): Mark moves the anchor marker to where the caster stands; Recall takes the
caster back to it. Only one mark at a time, as in Morrowind.}

ObjectReference Property Anchor Auto
GlobalVariable Property AnchorSet Auto
Bool Property IsMark = False Auto

Event OnEffectStart(Actor akTarget, Actor akCaster)
  If IsMark
    Anchor.MoveTo(akCaster)
    AnchorSet.SetValue(1)
    Debug.Notification("You have set a mark.")
  ElseIf AnchorSet.GetValue() > 0
    akCaster.MoveTo(Anchor)
  Else
    Debug.Notification("You have not set a mark.")
  EndIf
EndEvent
