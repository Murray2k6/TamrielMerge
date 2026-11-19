ScriptName TMObVampirism extends ActiveMagicEffect
{Oblivion's Vampirism effect (VAMP) on the player: the effect of the Vampirism25/50/75/100 abilities (Oblivion.esm
03DB3A, 02C2A2, 02C2A4, 02C2A5), its magnitude the stage (25, 50, 75, 100). Oblivion's engine made of it the vampire
face and the "vampirism" actor value VampireScript (the Vampire quest, translated as OB_VampireScript) reads to swap
the stages; everything else of the stages is in the abilities' other effects (Sun Damage: TMObSunDamage, Weakness to
Fire, the resistances) and the Att/Skills abilities and powers VampireScript adds. Here: the stage is told to TMObState
(GetAV Vampirism, the vampire look, the Vampire keyword GetVampire and the dialogue test), and the player gets the
Feed choice on sleepers (OBVampireFeed, Skyrim's own VampireFeedBeds perk with TMObVampireFeed; VampireScript's
HasVampireFed sees the feeding). When the effect ends TMObState waits a moment: the next stage's ability (VampireScript
removes one and adds the next) or the cure (nothing left: the look and the perk go).}

Perk Property FeedPerk Auto
{OBVampireFeed: the "Feed" choice on a sleeping person.}
MagicEffect Property Effect Auto
{This effect's own record (OBEffectVampirism), to see whether another stage's ability still carries it.}

Actor host

Event OnEffectStart(Actor akTarget, Actor akCaster)
  host = akTarget
  If host != Game.GetPlayer()
    Return
  EndIf
  TMObState st = Quest.GetQuest("TMObState") as TMObState
  If st
    st.SetVampirism(host, GetMagnitude())
  EndIf
  If FeedPerk && !host.HasPerk(FeedPerk)
    host.AddPerk(FeedPerk)
  EndIf
EndEvent

Event OnEffectFinish(Actor akTarget, Actor akCaster)
  If !host || host != Game.GetPlayer()
    Return
  EndIf
  TMObState st = Quest.GetQuest("TMObState") as TMObState
  If st
    st.VampirismEnded(host, Effect, FeedPerk)
  EndIf
EndEvent
