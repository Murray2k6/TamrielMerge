ScriptName TMObPersuade extends TMObLine Hidden
{Oblivion's persuasion (the Admire/Boast/Joke/Coerce wheel and bribes) as a topic (TamrielMerge, ObDialogue.Persuasion):
each response of the Persuasion topics is one of these. Oblivion's rule (UESP Oblivion:Speechcraft; the game's own
settings): every NPC loves one of the four actions, likes one, dislikes one and hates one, fixed for it; a loved or
liked action raises its disposition, a disliked or hated one lowers it, by the wedge the player picked; a hated
action costs fPersuasionReactionHate (-150%) and a disliked one fPersuasionReactionDislike (-50%) of what a loved one
gives (an Expert of Speechcraft, 75, pays -100% for a hated one); the wheel can only raise disposition to a maximum
set by Speechcraft (UESP: 79 at skill 50, 188 at its top: 29 + Speechcraft); a round that changed disposition trains
Speechcraft (2.4 experience per four actions). Skyrim has no wheel: each action is asked once per persuasion at one
"medium" wedge (Wedge, 0.5 of a full one), and the amount of a full loved wedge is fPersuasionMod (0.3) of the
player's Speechcraft, so a skill of 50 gives +8 loved, +4 liked, -4 disliked, -11 hated. Bribes give what the gold
buys (Disposition, taken by TMMwInfo with the Gold). GetPersuasionNumber and GetTotalPersuasionNumber read the last
change and the persuasion's total (Last, Total), as Oblivion's exit lines do.}

Int Property PersuadeAction = -1 Auto
{What this response answers: 0 Admire, 1 Boast, 2 Joke, 3 Coerce; 4 a bribe; -1 entering the persuasion (resets it);
-2 leaving it.}
Int Property Reaction = 0 Auto
{The speaker's fixed reaction to the action: 0 loved, 1 liked, 2 disliked, 3 hated.}
Float Property Wedge = 0.5 Auto
{The wedge picked, as a fraction of a full one.}
Float Property PersuasionMod = 0.3 Auto
{fPersuasionMod from Oblivion.esm.}
Float Property ReactionDislike = -50.0 Auto
{fPersuasionReactionDislike from Oblivion.esm (percent of a loved action's change).}
Float Property ReactionHate = -150.0 Auto
{fPersuasionReactionHate from Oblivion.esm (percent of a loved action's change).}
Int Property CapBase = 29 Auto
{The disposition the wheel can reach is CapBase + Speechcraft, at most 100.}
GlobalVariable Property Last Auto
{OBPersuasionLast: the last action's change (GetPersuasionNumber).}
GlobalVariable Property Total Auto
{OBPersuasionTotal: this persuasion's total change (GetTotalPersuasionNumber).}
GlobalVariable Property Asked Auto
{OBPersuaded<action>: 1 once this action was used in this persuasion (the topic hides it).}
GlobalVariable[] Property AskedAll Auto
{All four, reset on entering.}

Function Fragment_End(ObjectReference akSpeakerRef)
  ; Bribes: the parent takes the gold and gives the disposition (TMMwInfo's Gold and Disposition).
  Parent.Fragment_End(akSpeakerRef)
  Actor speaker = akSpeakerRef as Actor
  If PersuadeAction == -1
    Int i = 0
    While AskedAll && i < AskedAll.Length
      If AskedAll[i]
        AskedAll[i].SetValue(0.0)
      EndIf
      i += 1
    EndWhile
    If Total
      Total.SetValue(0.0)
    EndIf
    If Last
      Last.SetValue(0.0)
    EndIf
    Return
  EndIf
  If PersuadeAction == 4
    If Last
      Last.SetValue(Disposition as Float)
    EndIf
    If Total
      Total.SetValue(Total.GetValue() + Disposition)
    EndIf
    Return
  EndIf
  If PersuadeAction < 0 || !speaker
    Return
  EndIf
  Float skill = Game.GetPlayer().GetActorValue("Speechcraft")
  ; A full loved wedge: fPersuasionMod of the player's Speechcraft; this wedge's share of it.
  Float full = PersuasionMod * skill * Wedge
  Float percent = 100.0
  If Reaction == 1
    percent = 50.0
  ElseIf Reaction == 2
    percent = ReactionDislike
  ElseIf Reaction == 3
    percent = ReactionHate
    If skill >= 75.0
      ; Expert of Speechcraft: a hated action costs -100% instead of -150% (UESP Oblivion:Speechcraft).
      percent = -100.0
    EndIf
  EndIf
  Int change = Math.Floor(full * percent / 100.0 + 0.5)
  If change == 0
    If percent > 0.0
      change = 1
    Else
      change = -1
    EndIf
  EndIf
  If change > 0
    ; The wheel raises disposition only up to what Speechcraft allows.
    Int cap = CapBase + (skill as Int)
    If cap > 100
      cap = 100
    EndIf
    Int now = TMMwScript.GetDisposition(speaker)
    If now + change > cap
      change = cap - now
      If change < 0
        change = 0
      EndIf
    EndIf
  EndIf
  If change != 0
    TMMwScript.ModDisposition(speaker, change as Float)
    ; 2.4 Speechcraft experience per round of four actions that changed disposition: 0.6 per action.
    Game.AdvanceSkill("Speechcraft", 0.6)
  EndIf
  If Last
    Last.SetValue(change as Float)
  EndIf
  If Total
    Total.SetValue(Total.GetValue() + change)
  EndIf
  If Asked
    Asked.SetValue(1.0)
  EndIf
EndFunction
