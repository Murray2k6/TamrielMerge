ScriptName TMTesInterventionEffect extends ActiveMagicEffect
{Morrowind's Divine and Almsivi Intervention (TesMagic): takes the caster to the nearest Imperial Cult shrine or
Tribunal Temple (Morrowind's DivineMarker / TempleMarker spots, kept as markers). Outside, nearest to the caster.
Inside, nearest to where the interior is in the world: each converted interior holds a WorldSpot marker linked to
the outside door that leads to it (found from the doors when converting); the cell's references are searched with
SKSE. Without one, the first marker.}

FormList Property Markers Auto
Static Property WorldSpot Auto

Event OnEffectStart(Actor akTarget, Actor akCaster)
  Int count = Markers.GetSize()
  If count == 0
    Debug.Notification("Nothing answers your call here.")
    Return
  EndIf
  ObjectReference from = akCaster
  If akCaster.IsInInterior()
    from = OutsideSpot(akCaster.GetParentCell())
  EndIf
  ObjectReference best = Markers.GetAt(0) as ObjectReference
  If from
    Float bestDistance = -1.0
    Float x = from.GetPositionX()
    Float y = from.GetPositionY()
    Int i = 0
    While i < count
      ObjectReference marker = Markers.GetAt(i) as ObjectReference
      If marker
        Float dx = marker.GetPositionX() - x
        Float dy = marker.GetPositionY() - y
        Float d = dx * dx + dy * dy
        If bestDistance < 0.0 || d < bestDistance
          best = marker
          bestDistance = d
        EndIf
      EndIf
      i += 1
    EndWhile
  EndIf
  If best
    akCaster.MoveTo(best)
  EndIf
EndEvent

; The outside door an interior leads to, through its WorldSpot marker (SKSE Cell.GetNthRef; statics are form type 34).
ObjectReference Function OutsideSpot(Cell here)
  If !here || !WorldSpot || SKSE.GetVersion() <= 0
    Return None
  EndIf
  Int n = here.GetNumRefs(34)
  Int i = 0
  While i < n
    ObjectReference r = here.GetNthRef(i, 34)
    If r && r.GetBaseObject() == WorldSpot
      Return r.GetLinkedRef()
    EndIf
    i += 1
  EndWhile
  Return None
EndFunction
