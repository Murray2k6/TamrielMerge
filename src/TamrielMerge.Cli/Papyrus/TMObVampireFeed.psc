ScriptName TMObVampireFeed extends Perk Hidden
{The "Feed" choice on a sleeping person for an Oblivion vampire: OBVampireFeed is Skyrim's own VampireFeedBeds perk
(Skyrim.esm 0CF02C: an Add Activate Choice on a sleeping, uneaten adult NPC) carrying this fragment in place of
Skyrim's, so the feeding is Oblivion's (TMObState.VampireFeed) and Skyrim's PlayerVampireQuest stays out of it.
TMObVampirism gives the perk with the first stage and takes it with the cure.}

Function Fragment_0(ObjectReference akTargetRef, Actor akActor)
  TMObState st = Quest.GetQuest("TMObState") as TMObState
  If st
    st.VampireFeed(akActor, akTargetRef as Actor)
  EndIf
EndFunction
