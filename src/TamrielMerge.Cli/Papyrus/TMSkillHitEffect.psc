ScriptName TMSkillHitEffect extends ActiveMagicEffect
{The combat hit spell (TMSkillHitPerk's Apply Combat Hit Spell) on whoever the player strikes: tells TMSkills, for the
skills that rise with hits (Hand-to-Hand, Spear, Backstabbing, Critical Strike).}

TMSkills Property Skills Auto

Event OnEffectStart(Actor akTarget, Actor akCaster)
  If Skills && akCaster == Game.GetPlayer()
    Skills.PlayerHit(akTarget)
  EndIf
EndEvent
