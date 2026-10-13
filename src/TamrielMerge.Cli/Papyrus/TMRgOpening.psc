Scriptname TMRgOpening extends ObjectReference
{Redguard's opening (START.RGM), on the marker where Redguard's story begins (user, 2026-10-09: "the start of redguard
cyrus fights etc that needs fixed as well"): two of the League's pirates have boarded Brennan's boat; once both are
dead or in the sea, Brennan comes out of his cabin and he and Cyrus talk in the game's own recordings (Brennan through
his dialogue, Cyrus as a sound with his line shown); Brennan points out Vandar's boat, which sails off; "Let us move on"
and on to Stros M'Kai's docks. Plays once.}

Actor[] Property Boarders Auto
Actor Property Brennan Auto
Actor[] Property VandarCrew Auto
ObjectReference Property VandarShip Auto
Topic[] Property BrennanLines Auto
Float[] Property BrennanWaits Auto
Sound[] Property CyrusLines Auto
String[] Property CyrusTexts Auto
Float[] Property CyrusWaits Auto
Float Property SailAway = 6000.0 Auto
ObjectReference Property Docks Auto

Bool started = False
Bool done = False

Event OnCellAttach()
  If !started
    started = True
    RegisterForSingleUpdate(1.0)
  EndIf
EndEvent

Event OnLoad()
  If !started
    started = True
    RegisterForSingleUpdate(1.0)
  EndIf
EndEvent

Event OnUpdate()
  If done
    Return
  EndIf
  If !Gone(Boarders[0]) || !Gone(Boarders[1])
    RegisterForSingleUpdate(1.0)
    Return
  EndIf
  done = True
  Utility.Wait(2.0)
  Brennan.Enable()
  Utility.Wait(1.5)
  ; CYRUS! / CAPTAIN, GOOD TO SEE YOU BACK IN COMMAND. / AH... PLEASE CALL ME BRENNAN... / THAT SO, HM?
  BrennanSays(0)
  CyrusSays(0)
  BrennanSays(1)
  CyrusSays(1)
  ; LOOK! OVER THERE!
  BrennanSays(2)
  If VandarShip
    VandarShip.Enable()
  EndIf
  Int i = 0
  While i < VandarCrew.Length
    VandarCrew[i].Enable()
    i += 1
  EndWhile
  Utility.Wait(6.0)
  If VandarShip
    i = 0
    While i < VandarCrew.Length
      VandarCrew[i].Disable(True)
      i += 1
    EndWhile
    VandarShip.TranslateTo(VandarShip.X, VandarShip.Y - SailAway, VandarShip.Z, VandarShip.GetAngleX(), VandarShip.GetAngleY(), VandarShip.GetAngleZ(), 400.0)
  EndIf
  Utility.Wait(4.0)
  ; LET US MOVE ON, BEFORE MORE SHOW UP.
  BrennanSays(3)
  Utility.Wait(1.5)
  Game.GetPlayer().MoveTo(Docks)
  Utility.Wait(2.0)
  Game.RequestAutoSave()
EndEvent

Bool Function Gone(Actor a)
  Return a == None || a.IsDead() || a.IsSwimming()
EndFunction

Function BrennanSays(Int n)
  If n < BrennanLines.Length && BrennanLines[n]
    Brennan.Say(BrennanLines[n])
    Utility.Wait(BrennanWaits[n])
  EndIf
EndFunction

Function CyrusSays(Int n)
  If n < CyrusLines.Length && CyrusLines[n]
    Debug.Notification(CyrusTexts[n])
    CyrusLines[n].Play(Game.GetPlayer())
    Utility.Wait(CyrusWaits[n])
  EndIf
EndFunction
