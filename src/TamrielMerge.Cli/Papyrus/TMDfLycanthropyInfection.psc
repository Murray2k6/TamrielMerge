ScriptName TMDfLycanthropyInfection extends ActiveMagicEffect
{Daggerfall's lycanthropy, caught (Daggerfall Unity LycanthropyInfection / WerewolfInfection / WereboarInfection, MIT): a
disease with no effects of its own (Skyrim's cures end it while it lasts). Not caught by one who is already a lycanthrope
or a vampire, or has the other kind's infection. The day after, the dream (ANIM0002.VID); more than three days after,
with the dream seen, the player is a lycanthrope (Curse: TMDfLycanthropy) and the infection ends.}

Int Property Kind = 0 Auto
{0 werewolf, 1 wereboar.}
Spell Property Infection Auto
{This infection's spell.}
Spell Property Other Auto
{The other kind's infection.}
Spell Property Curse Auto
{The lycanthropy it becomes (TMDfLycanthropy).}
Spell Property OtherCurse Auto
Spell Property BeastForm Auto
{Skyrim's beast form (WerewolfChange): a lycanthrope already.}
Quest Property VampireQuest Auto
{Skyrim's PlayerVampireQuest: a vampire already.}
Spell Property VampireCurse Auto
{Daggerfall's vampirism (TMDfVampirism): a vampire already.}

Actor host
Int startingDay
Bool dreamed = False
Bool deployed = False

Int Function Today()
  Return Math.Floor(Utility.GetCurrentGameTime())
EndFunction

Event OnEffectStart(Actor akTarget, Actor akCaster)
  host = akTarget
  Bool vampire = (VampireQuest && (VampireQuest as PlayerVampireQuestScript).VampireStatus > 0) || (VampireCurse && host.HasSpell(VampireCurse))
  Bool lycanthrope = (BeastForm && host.HasSpell(BeastForm)) || (Curse && host.HasSpell(Curse)) || (OtherCurse && host.HasSpell(OtherCurse))
  If host != Game.GetPlayer() || vampire || lycanthrope || (Other && host.HasSpell(Other))
    End()
    Return
  EndIf
  startingDay = Today()
  RegisterForUpdateGameTime(1.0)
EndEvent

; ProgressDisease, each game hour.
Event OnUpdateGameTime()
  If deployed || !host
    Return
  EndIf
  Int daysPast = Today() - startingDay
  If daysPast > 0 && !dreamed
    dreamed = True
    Game.PlayBink("TamrielMerge_DfAnim2.bik", True)
  ElseIf daysPast > 3 && dreamed
    deployed = True
    If Curse
      host.AddSpell(Curse, False)
    EndIf
    End()
  EndIf
EndEvent

Function End()
  UnregisterForUpdateGameTime()
  If host && Infection
    host.RemoveSpell(Infection)
  EndIf
EndFunction

Event OnEffectFinish(Actor akTarget, Actor akCaster)
  UnregisterForUpdateGameTime()
EndEvent
