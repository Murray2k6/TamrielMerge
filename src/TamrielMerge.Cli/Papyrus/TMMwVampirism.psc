ScriptName TMMwVampirism extends ActiveMagicEffect
{Morrowind's Vampirism magic effect (MGEF 133), on the ability "vampire sun damage" that Morrowind's scripts
(Vampire_Aundae_PC, Vampire_Berne_PC, Vampire_Quarra_PC, started by VampireCheck three days after a clan's Porphyric
Hemophilia) give the player, and Vampire_Cure_PC takes away. In Morrowind the effect itself does two things (OpenMW
filter.cpp Function_PcVampire, npcanimation.cpp getNpcType/getVampireHead): the dialogue's PCVampire holds while its
magnitude is above 0, and the actor wears the vampire head of its race. Here the dialogue tests the converted ability
(HasSpell, MwDialogueBuilder), and the look is Skyrim's vampire form of the player's race, swapped in by
TMMwAfflictions.CheckVampire (the player alias on MWAfflictions, which also swaps back once the effect is gone, as
Skyrim's own vampirism swaps the player's race). This script only asks for that check at once, so the change doesn't
wait for the alias's next look. Morrowind's vampire people get the vampire form when they are built (their head is a
vampire body part: MwActors), so nothing is done for them here.}

Event OnEffectStart(Actor akTarget, Actor akCaster)
  Ask(akTarget)
EndEvent

Event OnEffectFinish(Actor akTarget, Actor akCaster)
  Ask(akTarget)
EndEvent

Function Ask(Actor akTarget)
  If !akTarget || akTarget != Game.GetPlayer()
    Return
  EndIf
  Quest q = Quest.GetQuest("MWAfflictions")
  If q
    TMMwAfflictions forms = q.GetAlias(0) as TMMwAfflictions
    If forms
      forms.CheckVampire()
    EndIf
  EndIf
EndFunction
