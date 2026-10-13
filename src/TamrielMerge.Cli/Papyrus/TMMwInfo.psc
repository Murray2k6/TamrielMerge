ScriptName TMMwInfo extends TopicInfo Hidden
{What a converted (Morrowind or Oblivion) dialogue response's result script did, for every response that does
something when said: quest stages set (journal updates), quests started or stopped, globals set, items given to or
taken from the player, joining a faction or rising in it, attacking, and the speaker's disposition changed
(ModDisposition, SetDisposition, persuasion) - Variable09 holds disposition minus 50, kept within 0-100.}

Quest[] Property StageQuests Auto
Int[] Property Stages Auto
Quest[] Property StartQuests Auto
Quest[] Property StopQuests Auto
GlobalVariable[] Property Globals Auto
Float[] Property GlobalValues Auto
Form[] Property Items Auto
Int[] Property ItemCounts Auto
Faction Property JoinFaction Auto
Faction Property RankFaction Auto
Bool Property Attack = False Auto
Int Property Disposition = 0 Auto
Bool Property SetsDisposition = False Auto
Int Property Gold = 0 Auto
GlobalVariable Property Roll Auto

Function Fragment_End(ObjectReference akSpeakerRef)
  Actor player = Game.GetPlayer()
  Actor speaker = akSpeakerRef as Actor
  Int i = 0
  While StageQuests && i < StageQuests.Length
    StageQuests[i].SetStage(Stages[i])
    i += 1
  EndWhile
  i = 0
  While StartQuests && i < StartQuests.Length
    StartQuests[i].Start()
    i += 1
  EndWhile
  i = 0
  While StopQuests && i < StopQuests.Length
    StopQuests[i].Stop()
    i += 1
  EndWhile
  i = 0
  While Globals && i < Globals.Length
    Globals[i].SetValue(GlobalValues[i])
    i += 1
  EndWhile
  i = 0
  While Items && i < Items.Length
    If ItemCounts[i] >= 0
      player.AddItem(Items[i], ItemCounts[i])
    Else
      player.RemoveItem(Items[i], -ItemCounts[i], False, akSpeakerRef)
    EndIf
    i += 1
  EndWhile
  If Gold > 0
    player.RemoveItem(Game.GetFormFromFile(0x0F, "Skyrim.esm"), Gold, False, akSpeakerRef)
  EndIf
  If speaker && (Disposition != 0 || SetsDisposition)
    Float now = speaker.GetActorValue("Variable09")
    Float target = now + Disposition
    If SetsDisposition
      target = Disposition - 50
    EndIf
    If target < -50.0
      target = -50.0
    ElseIf target > 50.0
      target = 50.0
    EndIf
    speaker.ModActorValue("Variable09", target - now)
  EndIf
  If JoinFaction
    player.AddToFaction(JoinFaction)
  EndIf
  If RankFaction
    player.SetFactionRank(RankFaction, player.GetFactionRank(RankFaction) + 1)
  EndIf
  If Attack && speaker
    speaker.StartCombat(player)
  EndIf
  ; Persuasion: the next attempt's roll, drawn now so every response of that attempt sees the same one.
  If Roll
    Roll.SetValue(Utility.RandomInt(0, 99))
  EndIf
EndFunction
