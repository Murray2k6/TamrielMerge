ScriptName TMNextStoryEffect extends ActiveMagicEffect
{Begin the Next Story power: hands off to the story order on MQ101.}

TMStoryOrder Property StoryOrder Auto

Event OnEffectStart(Actor akTarget, Actor akCaster)
  StoryOrder.BeginNextStory()
EndEvent
