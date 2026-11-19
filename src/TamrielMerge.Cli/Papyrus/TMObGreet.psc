Scriptname TMObGreet extends Package Hidden
{Oblivion's Find and Ambush packages aimed at the player: the actor walks up and starts talking. The converter makes
them Skyrim's own ForceGreet package (ObScriptRecords.AimGreeting), which waits, walks up and opens the conversation
with the actor's greeting by itself. This only marks the moment: Oblivion's GREETING lines are held to "in a
conversation" (they are not remarks made in passing), and the OBGreetNow faction says this actor's conversation is
being opened (the actor's own mark: one global for everybody, until 2026-11-15, was set by any actor's greeting
anywhere and cleared by the first of them to end). The package itself waits while its actor is still saying a line
(IsTalking).

Until 2026-11-15 this script opened the conversation itself, by activating the actor for the player at the end of a
walk: the greeting then came in about half the runs (the Emperor's, in the prisoner's cell: seen in game some fifteen
times; his last line's timing, the greeting's conditions and the packages' interrupt flags were each tried and were
not why).}

Faction Property GreetNow Auto
{OBGreetNow: the actor is in it while its greeting package runs.}
Quest Property ObState Auto
{TMObState (kept for plugins built with it: not used).}

; The mark goes on as the walk begins: the package says no hellos in passing on the way (its interrupt flags), so the
; greeting is said only when its ForceGreet opens the conversation. (Put on only within a few steps of the player, as
; tried in run r85, it came too late: the game opened the conversation before a script looking every tenth of a second
; had seen the actor arrive.)
Function Fragment_Begin(Actor akActor)
  If GreetNow && akActor
    akActor.AddToFaction(GreetNow)
  EndIf
  Debug.Trace("[TamrielMerge] " + akActor + " is to walk up and speak to the player")
  If Report
    Tell(akActor)
  EndIf
EndFunction

Bool Property Report = False Auto
{For the log: the state the actor's greeting is chosen in.}

Function Tell(Actor akActor)
  Quest charGen = Quest.GetQuest("OBQCharactergen")
  Actor player = Game.GetPlayer()
  String line = "[TamrielMerge] greeting state of " + akActor + ": base " + akActor.GetActorBase() + " (leveled " + akActor.GetLeveledActorBase() + "), voice " + akActor.GetVoiceType()
  line += ", in OBGreetNow " + akActor.IsInFaction(GreetNow) + ", in dialogue " + akActor.IsInDialogueWithPlayer()
  If charGen
    line += ", OBQCharactergen stage " + charGen.GetStage() + " running " + charGen.IsRunning()
  Else
    line += ", no OBQCharactergen"
  EndIf
  line += ", " + akActor.GetDistance(player) + " from the player, package " + akActor.GetCurrentPackage() + ", dialogue menu " + UI.IsMenuOpen("Dialogue Menu")
  Debug.Trace(line)
EndFunction

Function Fragment_End(Actor akActor)
  Debug.Trace("[TamrielMerge] " + akActor + " has walked up to the player (the greeting package is over)")
  Release(akActor)
EndFunction

; Another package took over.
Function Fragment_Change(Actor akActor)
  Debug.Trace("[TamrielMerge] " + akActor + " has another package after walking up to the player")
  Release(akActor)
EndFunction

; The mark off once the conversation has begun (from then the greetings' "in a conversation" holds by itself), or after
; twenty seconds without one. Not as the package ends: the game opens the conversation, and chooses the greeting, a few
; seconds after that (run r74, 2026-11-15: the Emperor's package over at 4:12:28, the menu up at 4:12:31, and with the
; mark already off his greeting was passed over, leaving a menu with nothing of his to ask. The run before had worked
; only because the mark was one global then and nine far-off actors' packages were holding it on).
Function Release(Actor akActor)
  If !GreetNow || !akActor
    Return
  EndIf
  If Report
    Tell(akActor)
  EndIf
  Float waited = 0.0
  While waited < 20.0 && !akActor.IsInDialogueWithPlayer()
    Utility.Wait(0.5)
    waited += 0.5
  EndWhile
  If Report
    Tell(akActor)
  EndIf
  akActor.RemoveFromFaction(GreetNow)
  Debug.Trace("[TamrielMerge] " + akActor + " greeting mark off after " + waited + " s, in dialogue " + akActor.IsInDialogueWithPlayer())
EndFunction
