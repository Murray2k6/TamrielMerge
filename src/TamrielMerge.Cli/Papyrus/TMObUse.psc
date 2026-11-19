Scriptname TMObUse extends Package Hidden
{Oblivion's "use item at" package aimed at a thing that is used by activating it (a switch, a lever, a door, a chest):
the actor goes to it and activates it, as Oblivion's does (Captain Renault and the switch of the secret wall in the
prisoner's cell). The converter makes it a walk to the thing (ObScriptRecords.UsesReference); its beginning and its
end are here.}

ObjectReference Property Target Auto
{What is used.}
Quest Property ObState Auto
{TMObState: told when the thing has been used, for the actor's script to hear its package done.}

Float lastAt

; Activated as by the actor (the thing's own script sees who), once the actor is at it; not again within 5 seconds
; (the package runs again while its conditions hold).
Function Use(Actor akActor, Float afWithin)
  If !akActor || !Target || akActor.IsDead() || akActor.IsInCombat()
    Return
  EndIf
  If akActor.GetDistance(Target) > afWithin
    Return
  EndIf
  Float now = Utility.GetCurrentRealTime()
  If lastAt > 0.0 && now >= lastAt && now - lastAt < 5.0
    Return
  EndIf
  lastAt = now
  Debug.Trace("[TamrielMerge] " + akActor + " uses " + Target)
  Target.Activate(akActor)
  ; Oblivion's package is done with that, and the actor's script is told (OnPackageDone: Captain Renault's sets the
  ; next stage of the Emperor's escape when her use of the wall's switch is done).
  TMObState told = ObState as TMObState
  If !told
    told = Quest.GetQuest("TMObState") as TMObState
  EndIf
  If told
    told.PackageDone(akActor, Self as Package)
  EndIf
EndFunction

; Already at it as the package starts, there is no walk and the game never ends it.
Function Fragment_Begin(Actor akActor)
  Use(akActor, 300.0)
EndFunction

; The walk over.
Function Fragment_End(Actor akActor)
  Use(akActor, 400.0)
EndFunction
