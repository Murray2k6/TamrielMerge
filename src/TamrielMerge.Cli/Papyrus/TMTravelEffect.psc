ScriptName TMTravelEffect extends ActiveMagicEffect
{Travel Through Time power: hands off to the story order on MQ101.}

TMStoryOrder Property StoryOrder Auto

Event OnEffectStart(Actor akTarget, Actor akCaster)
  StoryOrder.TravelBetweenWorlds()
EndEvent
