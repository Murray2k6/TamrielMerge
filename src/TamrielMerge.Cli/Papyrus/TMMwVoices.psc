ScriptName TMMwVoices extends Quest
{Morrowind's voice topics (MwBarks: MWVoiceHello ...), which Skyrim's engine never says by itself: TMMwActorAI and TMMwLaw
say them when OpenMW does (actors.cpp, aicombat.cpp, npc.cpp, mechanicsmanagerimp.cpp), through Speak.}

Topic Property HelloTopic Auto
Topic Property IdleTopic Auto
Topic Property IntruderTopic Auto
Topic Property ThiefTopic Auto
Topic Property AlarmTopic Auto
Topic Property AttackTopic Auto
Topic Property HitTopic Auto
Topic Property FleeTopic Auto

; DialogueManager::say: nothing if the actor is already saying something (a voice or script line, TMMwSayLine), a person
; under water, or knocked down; the topic's line its conditions allow otherwise. True if it was asked to speak.
Bool Function Speak(Actor akActor, Topic akTopic)
  If !akActor || !akTopic || akActor.IsDead() || akActor.IsUnconscious() || akActor.IsBleedingOut()
    Return False
  EndIf
  TMMwActorAI mw = akActor as TMMwActorAI
  If mw
    If mw.Saying && Utility.GetCurrentRealTime() - mw.SaidAt < 60.0
      Return False
    EndIf
    If mw.IsPerson && akActor.IsSwimming()
      Return False
    EndIf
  EndIf
  If akActor.IsInDialogueWithPlayer()
    Return False
  EndIf
  akActor.Say(akTopic)
  Return True
EndFunction
