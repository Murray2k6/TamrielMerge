ScriptName TMSwingDoor extends ObjectReference
{A swinging door converted from Morrowind or Oblivion. Those games open a door by turning it about its hinge
(the model's origin); this does the same, swinging it OpenAngle degrees open or back shut.

An activator's prompt can't change, so each door has a twin (Partner) standing at the open angle with a "Close"
prompt, initially disabled: once the door has swung open the twin takes its place, and activating the twin brings
the door back and swings it shut. A door without a twin (saves from before twins) just toggles.}

Float Property OpenAngle = 90.0 Auto
{Degrees to swing open; Bethesda headings turn clockwise.}
Float Property Speed = 150.0 Auto
{Swing speed, degrees per second.}
Sound Property OpenSound Auto
Sound Property CloseSound Auto
ObjectReference Property Partner Auto
{On a door: its open twin. On a twin: its door.}
Bool Property IsOpenTwin = False Auto

Bool isOpen = False
Bool opening = False
Bool busy = False
Bool hasClosedAngle = False
Float closedAngle

Bool Property Scripted = False Auto Hidden
{A Morrowind script's OnActivate decides when the door opens (TMMwScript.TakeActivation; it calls OpenBy).}

Event OnActivate(ObjectReference akActionRef)
  If !Scripted || IsOpenTwin
    OpenBy(akActionRef)
  EndIf
EndEvent

; Opens (or shuts) the door for akActionRef, as activating it does.
Function OpenBy(ObjectReference akActionRef)
  If IsOpenTwin
    TMSwingDoor swingDoor = Partner as TMSwingDoor
    If swingDoor
      swingDoor.SwingShut(Self)
    EndIf
  ElseIf !busy
    ; A Morrowind or Oblivion lock on the door (TMMwLock, TMObLock) has its say before it opens.
    TMMwLock mwLock = (Self as ObjectReference) as TMMwLock
    If mwLock && !isOpen && !mwLock.LetIn(akActionRef)
      Return
    EndIf
    TMObLock obLock = (Self as ObjectReference) as TMObLock
    If obLock && !isOpen && !obLock.LetIn(akActionRef)
      Return
    EndIf
    If !hasClosedAngle
      closedAngle = GetAngleZ()
      hasClosedAngle = True
    EndIf
    If isOpen
      ; No twin (or it was never swapped in): toggle shut.
      Swing(closedAngle, CloseSound, False)
    Else
      Swing(closedAngle + OpenAngle, OpenSound, True)
    EndIf
  EndIf
EndFunction

Function Swing(Float target, Sound effect, Bool toOpen)
  busy = True
  opening = toOpen
  isOpen = toOpen
  If effect
    effect.Play(Self)
  EndIf
  TranslateTo(GetPositionX(), GetPositionY(), GetPositionZ(), GetAngleX(), GetAngleY(), target, 1.0, Speed)
EndFunction

; Called by the twin: come back at the open angle and swing shut.
Function SwingShut(ObjectReference twin)
  If busy
    Return
  EndIf
  Enable()
  twin.DisableNoWait()
  Swing(closedAngle, CloseSound, False)
EndFunction

Event OnTranslationComplete()
  busy = False
  If opening && Partner
    Partner.EnableNoWait()
    DisableNoWait()
  EndIf
EndEvent

Event OnTranslationFailed()
  busy = False
EndEvent
