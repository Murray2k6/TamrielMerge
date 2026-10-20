ScriptName TMSkillBook extends ObjectReference
{A Morrowind or Oblivion skill book teaching one of the progression's skills (Spear, Mercantile, Mysticism...): one
level the first time it is read, as the games' skill books give (Skyrim's own skills are taught by Skyrim's book
teaching; Skyrim.GameTrainers.Teach).}

String Property SkillId Auto

Event OnRead()
  TMSkills skills = Quest.GetQuest("TMProgression") as TMSkills
  If skills
    skills.ReadBook(SkillId, GetBaseObject())
  EndIf
EndEvent
