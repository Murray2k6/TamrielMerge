ScriptName TMTrainerTopic extends TopicInfo
{"I'd like training in <skill>." asked of a Morrowind or Oblivion trainer (Skyrim.GameTrainers): for one of Skyrim's
skills, the trainer takes the class teaching it up to their level and Skyrim's own training menu opens (its five
sessions a level and its prices); for one of the progression's skills (Spear, Mercantile...), which that menu can't
train, TMSkills trains it at the game's price.}

Class Property TrainerClass Auto
String Property SkillId Auto
String Property SkillName Auto
Int Property MaxLevel Auto
Int Property CostPerLevel Auto
Message Property Confirm Auto

Function Fragment_End(ObjectReference akSpeakerRef)
  Actor trainer = akSpeakerRef as Actor
  If !trainer
    Return
  EndIf
  If TrainerClass
    ; SKSE's class change isn't saved: set each time, before the menu reads it.
    trainer.GetActorBase().SetClass(TrainerClass)
    Game.ShowTrainingMenu(trainer)
    Return
  EndIf
  TMSkills skills = Quest.GetQuest("TMProgression") as TMSkills
  If !skills
    Debug.MessageBox("Training in " + SkillName + " needs TamrielMerge Progression.esp.")
    Return
  EndIf
  skills.Train(SkillId, SkillName, MaxLevel, CostPerLevel, trainer, Confirm)
EndFunction
