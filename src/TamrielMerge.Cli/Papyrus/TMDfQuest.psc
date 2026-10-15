ScriptName TMDfQuest extends Quest
{One of Daggerfall's quests, run from its own QBN program (the converter's DfQuestBuilder turns each QBN into these
properties; the behaviour of every operation follows Daggerfall Unity's quest actions, MIT). One script for all 232
quests: the quest record carries the program, its text (Message records owned by this quest, so <Alias=...> names
fill in), its log (objectives) and aliases for its people, foes, items and the names its text uses.

The program: 9 Ints per operation: code, kind (0 sets its state when something happens, 1 a condition checked every
pass, 2 runs once when its state is set), Not bits, message id, then five operands (references are record indexes or
-1, constants as they are). A pass runs the operations in Daggerfall's order, as its quest engine does.}

TMDfQuestMachine Property Machine Auto
String Property DfName Auto
{Daggerfall's quest name, e.g. M0B00Y15.}
Int[] Property Ops Auto
Int[] Property StateGlobal Auto
{Per state: the global variable it is linked to, or -1.}
Int[] Property ClockState Auto
Int[] Property ClockKind Auto
Int[] Property ClockFlags Auto
Int[] Property ClockMin Auto
Int[] Property ClockMax Auto
Int[] Property ClockLink1 Auto
Int[] Property ClockLink2 Auto
Int[] Property ItemReward Auto
{0 a particular item, 1 a random item of the group, 2/3 gold.}
Int[] Property ItemGroup Auto
Int[] Property ItemIndex Auto
Int[] Property ItemText1 Auto
Int[] Property ItemText2 Auto
Form[] Property ItemBase Auto
Int[] Property GoldMin Auto
Int[] Property GoldMax Auto
Int[] Property PersonKind Auto
{0-20 a building's people (21 the questor), -8/-1 a named individual or a faction's member, -2 a faction type.}
Int[] Property PersonFaction Auto
Int[] Property PersonScope Auto
Int[] Property PlaceScope Auto
Int[] Property PlaceP1 Auto
Int[] Property PlaceP2 Auto
Int[] Property PlaceP3 Auto
Int[] Property FoeType Auto
Int[] Property FoeCount Auto
Form[] Property FoeBase Auto
Message[] Property Msgs Auto
Int[] Property MsgIds Auto
{The message id of each of Msgs (a message's variants are consecutive).}
Int[] Property LogIds Auto
{Objective i shows log message LogIds[i].}
Int[] Property TokenKind Auto
{Names the text uses, each shown by the name tag in alias TagAlias + k: 0 item, 1 person, 2 place, 3 clock, 4 foe,
5 a fixed macro, 6 a person's pronoun.}
Int[] Property TokenIndex Auto
Int[] Property TokenMode Auto
Int Property PersonAlias Auto
Int Property IndividualAlias Auto
Int[] Property IndividualFactions Auto
{Factions of the named individuals "when individual is available" waits for; each is in alias IndividualAlias + k.}
Int Property ItemAlias Auto
Int Property TagAlias Auto
Int Property DialogStage = 100 Auto
{Stage DialogStage + key opens a "Tell me about" topic: key = place, 20 + person, 40 + item.}
Int Property RumorStage = 200 Auto
Int[] Property RumorIds Auto

Bool Property Running = False Auto Hidden
Bool Property Success = False Auto Hidden
Int Property FactionId = 0 Auto Hidden
{The faction whose regard the quest's end changes (Daggerfall: +5 done, -2 failed): the guild or the questor's.}
Actor Property Questor Auto Hidden
Int Property Town = -1 Auto Hidden
{The site town the quest was given in (TMDfQuestMachine).}

Bool[] states
Bool[] doneA
Bool[] doneB
Bool[] primary
ObjectReference[] personRef
Int[] personSite
Bool[] personHidden
Int[] placeSite
ObjectReference[] itemRef
Int[] itemGold
Bool[] itemKeep
Int[] kills
ObjectReference[] foeRefs
Int[] foeKind
Bool[] foeCounted
Bool[] injured
Bool[] clicked
Bool[] itemClicked
Bool[] itemUsed
Bool[] giving
ObjectReference[] holderOf
Int[] foeItem
Float[] clockEnd
Float[] clockDays
Int[] logShown
ObjectReference[] tags
Int[] spawnOp
Float[] spawnNext
Int[] spawnLeft
Int spellCast = -1
Int individualClicked = -1
Bool passing = False
Bool again = False
Int rumors = 0

; ---- Operands ----

Int Function Code(Int i)
  Return Ops[i * 9]
EndFunction

Int Function Arg(Int i, Int k)
  Return Ops[i * 9 + 4 + k]
EndFunction

Bool Function Negated(Int i, Int k)
  Return Math.LogicalAnd(Ops[i * 9 + 2], Math.LeftShift(1, k)) != 0
EndFunction

Int Function MsgOf(Int i)
  Return Ops[i * 9 + 3]
EndFunction

Int Function OpCount()
  Return Ops.Length / 9
EndFunction

; ---- States ----

Bool Function IsSet(Int s)
  If s < 0
    Return True
  EndIf
  If StateGlobal[s] >= 0
    Return Machine.GetGlobal(StateGlobal[s])
  EndIf
  Return states[s]
EndFunction

Function SetState(Int s, Bool value)
  If s < 0
    Return
  EndIf
  Bool was = IsSet(s)
  states[s] = value
  If StateGlobal[s] >= 0
    Machine.SetGlobal(StateGlobal[s], value)
  EndIf
  If was && !value
    Rearm(s)
  EndIf
EndFunction

; A cleared state's actions may run again when it is set again.
Function Rearm(Int s)
  Int i = 0
  Int n = OpCount()
  While i < n
    If Ops[i * 9 + 1] == 2 && Arg(i, 0) == s
      SetDone(i, False)
      Int k = spawnOp.Find(i)
      If k >= 0
        spawnOp[k] = -1
      EndIf
    EndIf
    i += 1
  EndWhile
EndFunction

Bool Function IsDone(Int i)
  If i < 128
    Return doneA[i]
  EndIf
  Return doneB[i - 128]
EndFunction

Function SetDone(Int i, Bool value)
  If i < 128
    doneA[i] = value
  Else
    doneB[i - 128] = value
  EndIf
EndFunction

; ---- Lifetime ----

; Resources are chosen (places, people, items) and the start-up runs; the machine shows the offer first.
Bool Function Setup(Actor giver, Int giverFaction, Int townIndex)
  states = new Bool[64]
  doneA = new Bool[128]
  doneB = new Bool[128]
  primary = new Bool[64]
  personRef = new ObjectReference[16]
  personSite = new Int[16]
  personHidden = new Bool[16]
  placeSite = new Int[8]
  itemRef = new ObjectReference[16]
  itemGold = new Int[16]
  itemKeep = new Bool[16]
  kills = new Int[8]
  foeRefs = new ObjectReference[64]
  foeKind = new Int[64]
  foeCounted = new Bool[64]
  injured = new Bool[8]
  clicked = new Bool[16]
  itemClicked = new Bool[16]
  itemUsed = new Bool[16]
  giving = new Bool[16]
  holderOf = new ObjectReference[16]
  foeItem = new Int[8]
  clockEnd = new Float[12]
  clockDays = new Float[12]
  logShown = new Int[10]
  tags = new ObjectReference[64]
  spawnOp = new Int[8]
  spawnNext = new Float[8]
  spawnLeft = new Int[8]
  Int i = 0
  While i < 16
    personSite[i] = -1
    i += 1
  EndWhile
  i = 0
  While i < 8
    placeSite[i] = -1
    foeItem[i] = -1
    spawnOp[i] = -1
    i += 1
  EndWhile
  i = 0
  While i < 10
    logShown[i] = -1
    i += 1
  EndWhile
  Questor = giver
  FactionId = giverFaction
  Town = townIndex
  Success = False
  rumors = 0
  ; Places first: people live in them and clocks measure the way to them.
  i = 0
  While i < PlaceScope.Length
    placeSite[i] = Machine.FindSite(PlaceScope[i], PlaceP1[i], PlaceP2[i], PlaceP3[i], Town, placeSite, i)
    If placeSite[i] < 0
      Debug.Trace("[TamrielMerge] quest " + DfName + ": no site for place " + i)
      Return False
    EndIf
    i += 1
  EndWhile
  i = 0
  While i < PersonKind.Length
    If !SetupPerson(i)
      Debug.Trace("[TamrielMerge] quest " + DfName + ": no one for person " + i)
      Return False
    EndIf
    i += 1
  EndWhile
  i = 0
  While i < ItemReward.Length
    SetupItem(i)
    i += 1
  EndWhile
  i = 0
  While i < ClockState.Length
    clockDays[i] = ClockDuration(i)
    i += 1
  EndWhile
  i = 0
  While i < IndividualFactions.Length
    Actor who = Machine.FindIndividual(IndividualFactions[i])
    If who
      (GetAlias(IndividualAlias + i) as ReferenceAlias).ForceRefTo(who)
    EndIf
    i += 1
  EndWhile
  i = 0
  While i < TokenKind.Length
    tags[i] = Machine.NewTag()
    (GetAlias(TagAlias + i) as ReferenceAlias).ForceRefTo(tags[i])
    i += 1
  EndWhile
  Return True
EndFunction

; Accepted: the quest runs.
Function Begin()
  Running = True
  SetActive(True)
  Pass()
  RegisterForSingleUpdate(2.0)
EndFunction

; Refused or impossible: everything made for it goes.
Function Abort()
  Running = False
  Cleanup(False)
  Stop()
EndFunction

Event OnUpdate()
  If !Running
    Return
  EndIf
  Pass()
  If Running
    RegisterForSingleUpdate(2.0)
  EndIf
EndEvent

; "end quest": the quest faction's regard changes (Daggerfall Unity Quest.EndQuest: +5 done, -2 not), the log is
; closed and what the quest made is taken away.
Function Finish()
  If !Running
    Return
  EndIf
  Running = False
  If FactionId > 0
    If Success
      Machine.Reputation.ChangeRep(FactionId, 5, True)
    Else
      Machine.Reputation.ChangeRep(FactionId, -2, True)
    EndIf
  EndIf
  Int s = 0
  While s < 10
    If logShown[s] >= 0
      SetObjectiveCompleted(logShown[s], True)
    EndIf
    s += 1
  EndWhile
  Cleanup(True)
  Machine.QuestEnded(self, Success)
  Stop()
EndFunction

Function Cleanup(Bool ended)
  Int i = 0
  While i < 16
    ; People the quest made (not the questor or a town's own individuals) leave with it.
    If personRef[i] && personSite[i] != -2
      personRef[i].Disable()
      personRef[i].Delete()
    EndIf
    If itemRef[i] && !itemKeep[i]
      ObjectReference holder = holderOf[i]
      If holder
        holder.RemoveItem(itemRef[i], 1, True)
      EndIf
      itemRef[i].Disable()
      itemRef[i].Delete()
    EndIf
    i += 1
  EndWhile
  i = 0
  While i < 64
    If foeRefs[i]
      If !(foeRefs[i] as Actor).IsDead()
        foeRefs[i].Disable()
      EndIf
      foeRefs[i].Delete()
      foeRefs[i] = None
    EndIf
    i += 1
  EndWhile
  i = 0
  While i < 64
    If tags[i]
      tags[i].Delete()
      tags[i] = None
    EndIf
    i += 1
  EndWhile
EndFunction

; ---- The pass ----

Function Pass()
  If !Running
    Return
  EndIf
  If passing
    again = True
    Return
  EndIf
  passing = True
  again = True
  While again && Running
    again = False
    Tick()
    RunOps()
    ; Events are seen by one pass.
    Int i = 0
    While i < 16
      clicked[i] = False
      itemClicked[i] = False
      i += 1
    EndWhile
    spellCast = -1
    individualClicked = -1
  EndWhile
  passing = False
EndFunction

Function RunOps()
  Int i = 0
  While i < 64
    primary[i] = False
    i += 1
  EndWhile
  Int n = OpCount()
  i = 0
  While i < n && Running
    Int kind = Ops[i * 9 + 1]
    Int s = Arg(i, 0)
    If kind == 2
      If IsSet(s) && !IsDone(i)
        If RunAction(i)
          SetDone(i, True)
        EndIf
      EndIf
    ElseIf kind == 1
      Bool r = Check(i)
      If s >= 0 && !primary[s]
        primary[s] = True
        If r != IsSet(s)
          SetState(s, r)
          If r && MsgOf(i) >= 0
            Say(MsgOf(i))
          EndIf
        EndIf
      ElseIf r && !IsSet(s)
        SetState(s, True)
        If MsgOf(i) >= 0
          Say(MsgOf(i))
        EndIf
      EndIf
    Else
      If s >= 0 && !IsSet(s) && Check(i)
        SetState(s, True)
        If MsgOf(i) >= 0
          Say(MsgOf(i))
        EndIf
      EndIf
    EndIf
    i += 1
  EndWhile
EndFunction

; Clocks running out and timed spawns.
Function Tick()
  Float now = Utility.GetCurrentGameTime()
  ; Foes: deaths ("killed") and wounds ("injured").
  Int f = 0
  While f < 64
    Actor a = foeRefs[f] as Actor
    If a && !foeCounted[f]
      If a.IsDead()
        foeCounted[f] = True
        kills[foeKind[f]] = kills[foeKind[f]] + 1
        injured[foeKind[f]] = True
      ElseIf a.GetActorValuePercentage("Health") < 1.0
        injured[foeKind[f]] = True
      EndIf
    EndIf
    f += 1
  EndWhile
  Int i = 0
  While i < ClockState.Length
    If clockEnd[i] > 0.0 && now >= clockEnd[i]
      clockEnd[i] = 0.0
      SetState(ClockState[i], True)
    EndIf
    i += 1
  EndWhile
  i = 0
  While i < 8
    Int op = spawnOp[i]
    If op >= 0 && now >= spawnNext[i] && IsSet(Arg(op, 0))
      Int code = Code(op)
      spawnNext[i] = now + Arg(op, 2) / 1440.0
      If code == 0x54
        Machine.PlayDfSound(Arg(op, 1))
        spawnLeft[i] = spawnLeft[i] - 1
      ElseIf Utility.RandomInt(1, 100) <= Arg(op, 3)
        SpawnNearPlayer(Arg(op, 1))
        spawnLeft[i] = spawnLeft[i] - 1
      EndIf
      If spawnLeft[i] == 0
        spawnOp[i] = -1
        SetDone(op, True)
      EndIf
    EndIf
    i += 1
  EndWhile
EndFunction

; Kinds 0 and 1: is it so now?
Bool Function Check(Int i)
  Int code = Code(i)
  If code == 0x1C
    Return clicked[Arg(i, 1)]
  ElseIf code == 0x02
    Return kills[Arg(i, 1)] >= Arg(i, 2)
  ElseIf code == 0x15
    Return injured[Arg(i, 1)]
  ElseIf code == 0x01
    ; Toting the item when the person is clicked: the item is handed over.
    Int item = Arg(i, 1)
    If clicked[Arg(i, 2)] && PlayerHas(item)
      TakeFromPlayer(item)
      Return True
    EndIf
    Return False
  ElseIf code == 0x03
    Return itemClicked[Arg(i, 1)]
  ElseIf code == 0x47
    ; Clicked with at least this much gold: paid, else the other task.
    If clicked[Arg(i, 3)]
      Actor player = Game.GetPlayer()
      Form gold = Game.GetForm(0xF)
      If player.GetItemCount(gold) >= Arg(i, 2)
        player.RemoveItem(gold, Arg(i, 2))
        Return True
      EndIf
      SetState(Arg(i, 1), True)
    EndIf
    Return False
  ElseIf code == 0x4D
    Return Game.GetPlayer().GetLevel() >= Arg(i, 1)
  ElseIf code == 0x34
    Int k = 1
    While k < 5
      Int s = Arg(i, k)
      If s >= 0 && IsSet(s) == Negated(i, k)
        Return False
      EndIf
      k += 1
    EndWhile
    Return True
  ElseIf code == 0x35
    Int k = 1
    While k < 5
      Int s = Arg(i, k)
      If s >= 0 && IsSet(s) != Negated(i, k)
        Return True
      EndIf
      k += 1
    EndWhile
    Return False
  ElseIf code == 0x05
    Return DroppedAt(Arg(i, 1), Arg(i, 2))
  ElseIf code == 0x1F
    ; Minutes after midnight.
    Float minute = (Utility.GetCurrentGameTime() - Math.Floor(Utility.GetCurrentGameTime())) * 1440.0
    Return minute >= Arg(i, 1) && minute <= Arg(i, 2)
  ElseIf code == 0x4E
    Return individualClicked == Arg(i, 1) && !Machine.IndividualBusy(Arg(i, 1), self)
  ElseIf code == 0x4F
    Return Machine.Reputation.GetRep(Arg(i, 1)) >= Arg(i, 2)
  EndIf
  Return False
EndFunction

; Kind 2: does it; True when it is done (until its state is cleared and set again).
Bool Function RunAction(Int i)
  Int code = Code(i)
  Int m = MsgOf(i)
  If code == 0x33
    Say(m)
  ElseIf code == 0x06
    If m >= 0
      Say(m)
    EndIf
    Finish()
  ElseIf code == 0x07
    ; Done first: it may clear its own state, which re-arms it.
    SetDone(i, True)
    Int k = 1
    While k < 5
      SetState(Arg(i, k), False)
      k += 1
    EndWhile
    Return False
  ElseIf code == 0x0C
    Int c = Arg(i, 1)
    If c >= 0
      clockEnd[c] = Utility.GetCurrentGameTime() + clockDays[c]
    EndIf
  ElseIf code == 0x0D
    If Arg(i, 1) >= 0
      clockEnd[Arg(i, 1)] = 0.0
    EndIf
  ElseIf code == 0x17
    ShowLog(Arg(i, 1), Arg(i, 2))
  ElseIf code == 0x18
    Int step = Arg(i, 1)
    If step >= 0 && step < 10 && logShown[step] >= 0
      SetObjectiveDisplayed(logShown[step], False)
      logShown[step] = -1
    EndIf
  ElseIf code == 0x22
    ; One of the states at random.
    Int[] pick = new Int[4]
    Int n = 0
    Int k = 1
    While k < 5
      If Arg(i, k) >= 0
        pick[n] = Arg(i, k)
        n += 1
      EndIf
      k += 1
    EndWhile
    If n > 0
      SetState(pick[Utility.RandomInt(0, n - 1)], True)
    EndIf
  ElseIf code == 0x23
    ; The next state of the cycle (the first, if none is set).
    Int at = -1
    Int k = 1
    While k < 5
      If Arg(i, k) >= 0 && IsSet(Arg(i, k)) && at < 0
        at = k
      EndIf
      k += 1
    EndWhile
    Int next = 1
    If at > 0
      SetState(Arg(i, at), False)
      next = at + 1
      If next > 4 || Arg(i, next) < 0
        next = 1
      EndIf
    EndIf
    SetState(Arg(i, next), True)
  ElseIf code == 0x1D
    ; Yes (button 0) or no.
    If Say(Arg(i, 3)) == 0
      SetState(Arg(i, 1), True)
    Else
      SetState(Arg(i, 2), True)
    EndIf
  ElseIf code == 0x04
    ; Done: the reward (Daggerfall Unity GivePc shows "quest complete", 1004).
    Success = True
    If m >= 0
      Say(m)
    Else
      Say(1004)
    EndIf
    Int k = 1
    While k < 5
      If Arg(i, k) >= 0
        GiveToPlayer(Arg(i, k))
      EndIf
      k += 1
    EndWhile
  ElseIf code == 0x4C
    If m >= 0
      Say(m)
    EndIf
    GiveToPlayer(Arg(i, 1))
  ElseIf code == 0x1A
    If Arg(i, 2) >= 0
      GiveToPerson(Arg(i, 1), Arg(i, 2))
    Else
      GiveToPlayer(Arg(i, 1))
    EndIf
    If m >= 0
      Say(m)
    EndIf
  ElseIf code == 0x24
    TakeFromPlayer(Arg(i, 1))
    If m >= 0
      Say(m)
    EndIf
  ElseIf code == 0x00
    PlaceItemAt(Arg(i, 1), Arg(i, 2))
  ElseIf code == 0x1E
    PlacePersonAt(Arg(i, 1), Arg(i, 2))
  ElseIf code == 0x16
    PlaceFoeAt(Arg(i, 1), Arg(i, 2))
  ElseIf code == 0x09 || code == 0x57 || code == 0x54
    ; Timed: every N minutes, M times (-1 for ever), with a chance.
    Int slot = spawnOp.Find(-1)
    If slot >= 0 && spawnOp.Find(i) < 0
      spawnOp[slot] = i
      spawnNext[slot] = Utility.GetCurrentGameTime() + Arg(i, 2) / 1440.0
      If code == 0x54
        spawnLeft[slot] = -1
      Else
        spawnLeft[slot] = Arg(i, 4)
      EndIf
    EndIf
    Return False
  ElseIf code == 0x13 || code == 0x1B || code == 0x11
    ; reveal, reveal on the map, and a place's location marker (0x11).
    Machine.RevealSite(placeSite[Arg(i, 1)])
  ElseIf code == 0x0A || code == 0x0B
    If Arg(i, 1) >= 0
      SetStage(DialogStage + Arg(i, 1))
    EndIf
    If Arg(i, 2) >= 0
      SetStage(DialogStage + 20 + Arg(i, 2))
    EndIf
    If Arg(i, 3) >= 0
      SetStage(DialogStage + 40 + Arg(i, 3))
    EndIf
  ElseIf code == 0x26
    Int r = RumorIds.Find(Arg(i, 1))
    If r >= 0
      SetStage(RumorStage + r)
    EndIf
  ElseIf code == 0x25
    If Machine.Reputation.GetRep(PersonFactionOf(Arg(i, 2))) < Arg(i, 3)
      Return False
    EndIf
    SetState(Arg(i, 1), True)
  ElseIf code == 0x2B
    If !Machine.PlayerAt(placeSite[Arg(i, 2)])
      Return False
    EndIf
    SetState(Arg(i, 1), True)
  ElseIf code == 0x46
    If !PlayerHas(Arg(i, 2)) || (Arg(i, 3) >= 0 && !PlayerHas(Arg(i, 3)))
      Return False
    EndIf
    SetState(Arg(i, 1), True)
  ElseIf code == 0x39
    Int item = Arg(i, 2)
    If !itemUsed[item]
      Return False
    EndIf
    itemUsed[item] = False
    SetState(Arg(i, 1), True)
    If m >= 0
      Say(m)
    EndIf
    Return False
  ElseIf code == 0x49
    If spellCast != Arg(i, 2)
      Return False
    EndIf
    SetState(Arg(i, 1), True)
  ElseIf code == 0x27
    GiveToFoe(Arg(i, 1), Arg(i, 2))
  ElseIf code == 0x2C
    ObjectReference p = personRef[Arg(i, 1)]
    If p
      p.Disable()
    EndIf
    personHidden[Arg(i, 1)] = True
  ElseIf code == 0x2E
    If personRef[Arg(i, 1)]
      personRef[Arg(i, 1)].Disable()
    EndIf
    personHidden[Arg(i, 1)] = True
  ElseIf code == 0x30
    If personRef[Arg(i, 1)]
      personRef[Arg(i, 1)].Enable()
    EndIf
    personHidden[Arg(i, 1)] = False
  ElseIf code == 0x37
    Machine.Escort(Name(1, Arg(i, 1), 1), True)
  ElseIf code == 0x38
    Machine.Escort(Name(1, Arg(i, 1), 1), False)
  ElseIf code == 0x3D
    Machine.Reputation.ChangeRep(PersonFactionOf(Arg(i, 1)), Arg(i, 2), True)
  ElseIf code == 0x41
    Machine.ChangeLegal(Arg(i, 1))
  ElseIf code == 0x36
    If Arg(i, 1) >= 0
      itemKeep[Arg(i, 1)] = True
    EndIf
  ElseIf code == 0x08
    Machine.StartStoryQuest(Arg(i, 1))
  ElseIf code == 0x31
    Machine.CurePlayer(0)
  ElseIf code == 0x3A
    Machine.CurePlayer(1)
  ElseIf code == 0x3B
    Machine.CurePlayer(2)
  ElseIf code == 0x48
    Machine.MakePlayerIll(Arg(i, 1))
  ElseIf code == 0x32
    Machine.PlayVideo(Arg(i, 1))
  ElseIf code == 0x3C
    Machine.PlayDfSound(Arg(i, 1))
  ElseIf code == 0x3F || code == 0x40 || code == 0x44
    FoesFollow(Arg(i, 1), code)
  ElseIf code == 0x45
    Int k = 0
    While k < 64
      If foeRefs[k] && foeKind[k] == Arg(i, 1)
        Machine.CastDfSpell(Arg(i, 2), foeRefs[k])
      EndIf
      k += 1
    EndWhile
  ElseIf code == 0x51
    If Arg(i, 2) >= 0
      PlacePersonAt(Arg(i, 2), -1)
    ElseIf Arg(i, 1) >= 0
      Machine.PlaceStranger(placeSite[Arg(i, 1)])
    EndIf
  ElseIf code == 0x52
    ; Muted: Daggerfall's talk screen stops offering the person's quest topics; there is nothing to stop here.
  ElseIf code == 0x53
    Machine.TransferPlayer(placeSite[Arg(i, 1)])
  ElseIf code == 0x55 || code == 0x56
    ; The questor role moves: only Daggerfall's talk screen uses it.
    If code == 0x55 && personRef[Arg(i, 1)]
      Questor = personRef[Arg(i, 1)] as Actor
    EndIf
  EndIf
  ; Any other action's message is shown once it has run ("... saying N").
  If m >= 0 && code != 0x33 && code != 0x06 && code != 0x04 && code != 0x4C && code != 0x1A && code != 0x24 && code != 0x1D
    Say(m)
  EndIf
  Return True
EndFunction

; ---- Events from the aliases ----

Function PersonClicked(Int n)
  If Running && n >= 0 && n < 16
    clicked[n] = True
    Pass()
  EndIf
EndFunction

; An item changed hands (TMDfQuestAlias): the player picking it up from the world is "clicked item".
Function ItemMoved(Int item, ObjectReference newHolder, ObjectReference oldHolder)
  holderOf[item] = newHolder
  If newHolder == Game.GetPlayer() && !oldHolder
    ItemTaken(item)
  EndIf
EndFunction

Function ItemTaken(Int item)
  If giving[item]
    giving[item] = False   ; handed over by the quest, not picked up
    Return
  EndIf
  If Running
    itemClicked[item] = True
    If ItemText1[item] > 0
      Say(ItemText1[item])
    EndIf
    Pass()
  EndIf
EndFunction

Function ItemUsedByPlayer(Int item)
  If Running
    itemUsed[item] = True
    If ItemText2[item] > 0
      Say(ItemText2[item])
    EndIf
    Pass()
  EndIf
EndFunction

Function SpellCastByPlayer(Int dfSpell)
  If Running
    spellCast = dfSpell
    Pass()
  EndIf
EndFunction

; Talking to a named individual (TMDfQuestAlias kind 2).
Function IndividualClicked(Int fid)
  If Running
    individualClicked = fid
    Pass()
  EndIf
EndFunction

; ---- Resources ----

Bool Function SetupPerson(Int n)
  Int kind = PersonKind[n]
  If kind == 21
    personRef[n] = Questor
    personSite[n] = -2
  ElseIf kind == -8 || (kind == -1 && Machine.IsIndividual(PersonFaction[n]))
    Actor a = Machine.FindIndividual(PersonFaction[n])
    If a
      personRef[n] = a
      personSite[n] = -2
    EndIf
  Else
    ; Someone of the right kind living in a building of their kind, here or elsewhere in the region.
    Int buildingType = 17 + Utility.RandomInt(0, 5)
    If kind >= 0 && kind <= 20
      buildingType = kind
    EndIf
    Int scope = 2
    If PersonScope[n] == 0
      scope = 1
    EndIf
    Int home = Machine.FindSite(scope, 0, buildingType, 0, Town, placeSite, -1)
    If home < 0
      home = Machine.FindSite(scope, 0, -1, 1, Town, placeSite, -1)
    EndIf
    If home < 0
      Return False
    EndIf
    personSite[n] = home
    personRef[n] = Machine.MakePerson(home, PersonFactionOf(n), kind)
  EndIf
  If personRef[n]
    (GetAlias(PersonAlias + n) as ReferenceAlias).ForceRefTo(personRef[n])
  EndIf
  Return True
EndFunction

; The faction a person stands for (Daggerfall Unity Person.FactionData).
Int Function PersonFactionOf(Int n)
  If n < 0
    Return 0
  EndIf
  Int kind = PersonKind[n]
  If kind == 21
    Return FactionId
  ElseIf kind == -8 || kind == -1
    Return PersonFaction[n]
  ElseIf kind == -2
    Return Machine.FactionOfType(PersonFaction[n], Town)
  EndIf
  Return Machine.PeopleFaction(Town)
EndFunction

Function SetupItem(Int i)
  If ItemReward[i] >= 2
    If GoldMax[i] > 0
      itemGold[i] = Utility.RandomInt(GoldMin[i], GoldMax[i])
    Else
      itemGold[i] = Machine.RewardGold(FactionId)
    EndIf
    Return
  EndIf
  Form base = ItemBase[i]
  If !base
    base = Machine.RandomItemOf(ItemGroup[i], ItemReward[i] == 1)
  EndIf
  If base
    itemRef[i] = Machine.Holding.PlaceAtMe(base, 1, True, False)
    (GetAlias(ItemAlias + i) as ReferenceAlias).ForceRefTo(itemRef[i])
  EndIf
EndFunction

Bool Function PlayerHas(Int item)
  If item < 0
    Return False
  EndIf
  If !itemRef[item]
    Return ItemReward[item] >= 2 && Game.GetPlayer().GetGoldAmount() >= itemGold[item]
  EndIf
  Return holderOf[item] == Game.GetPlayer()
EndFunction

Function GiveToPlayer(Int item)
  Actor player = Game.GetPlayer()
  If item < 0
    Return
  EndIf
  If ItemReward[item] >= 2
    player.AddItem(Game.GetForm(0xF), itemGold[item])
  ElseIf itemRef[item]
    ObjectReference holder = holderOf[item]
    giving[item] = holder != player
    holderOf[item] = player
    If holder && holder != player
      holder.RemoveItem(itemRef[item], 1, True, player)
    ElseIf !holder
      player.AddItem(itemRef[item])
    EndIf
    If ItemReward[item] == 1 || ItemGroup[item] == 4 || ItemGroup[item] == 5
      itemKeep[item] = True   ; rewards are the player's to keep
    EndIf
  EndIf
EndFunction

Function TakeFromPlayer(Int item)
  If item < 0
    Return
  EndIf
  If ItemReward[item] >= 2
    Game.GetPlayer().RemoveItem(Game.GetForm(0xF), itemGold[item])
  ElseIf itemRef[item] && holderOf[item] == Game.GetPlayer()
    Game.GetPlayer().RemoveItem(itemRef[item], 1, True, Machine.Holding)
    holderOf[item] = Machine.Holding
  EndIf
EndFunction

Function GiveToPerson(Int item, Int n)
  If item < 0 || !itemRef[item] || !personRef[n]
    Return
  EndIf
  ObjectReference holder = holderOf[item]
  If holder
    holder.RemoveItem(itemRef[item], 1, True, personRef[n])
  Else
    personRef[n].AddItem(itemRef[item])
  EndIf
  holderOf[item] = personRef[n]
EndFunction

Function GiveToFoe(Int item, Int f)
  Int k = FoeSlot(f)
  If k >= 0 && itemRef[item]
    foeRefs[k].AddItem(itemRef[item])
    holderOf[item] = foeRefs[k]
  Else
    foeItem[f] = item   ; handed over when the foe appears
  EndIf
EndFunction

Function PlaceItemAt(Int item, Int place)
  If item < 0 || !itemRef[item] || place < 0
    Return
  EndIf
  ObjectReference spot = Machine.SiteRef(placeSite[place], 3)
  If spot
    ObjectReference holder = holderOf[item]
    If holder
      holder.RemoveItem(itemRef[item], 1, True, Machine.Holding)
      holderOf[item] = None
      Utility.Wait(0.1)
    EndIf
    itemRef[item].MoveTo(spot, 0.0, 0.0, 16.0)
    itemRef[item].Enable()
  EndIf
EndFunction

; A person to a place (-1: their home).
Function PlacePersonAt(Int n, Int place)
  Int site = personSite[n]
  If place >= 0
    site = placeSite[place]
  EndIf
  If site < 0
    Return
  EndIf
  ObjectReference spot = Machine.SiteRef(site, 2)
  If !personRef[n]
    personRef[n] = Machine.MakePerson(site, PersonFactionOf(n), PersonKind[n])
    (GetAlias(PersonAlias + n) as ReferenceAlias).ForceRefTo(personRef[n])
  ElseIf spot
    personRef[n].MoveTo(spot)
  EndIf
  personRef[n].Enable()
  personHidden[n] = False
  If place >= 0
    personSite[n] = site
  EndIf
EndFunction

Function PlaceFoeAt(Int f, Int place)
  If f < 0 || place < 0
    Return
  EndIf
  ObjectReference spot = Machine.SiteRef(placeSite[place], 2)
  If spot
    SpawnFoe(f, spot, FoeCount[f])
  EndIf
EndFunction

Function SpawnNearPlayer(Int f)
  SpawnFoe(f, Game.GetPlayer(), 1)
EndFunction

; The first spawned foe of a kind (-1 none).
Int Function FoeSlot(Int f)
  Int k = 0
  While k < 64
    If foeRefs[k] && foeKind[k] == f
      Return k
    EndIf
    k += 1
  EndWhile
  Return -1
EndFunction

Function SpawnFoe(Int f, ObjectReference spot, Int count)
  If !FoeBase[f]
    Return
  EndIf
  Int k = 0
  If count < 1
    count = 1
  EndIf
  While k < count
    ObjectReference a = spot.PlaceAtMe(FoeBase[f], 1, True, False)
    If spot == Game.GetPlayer()
      ; Daggerfall's quest spawns come from out of sight, a little away.
      Float angle = Utility.RandomFloat(0.0, 360.0)
      a.MoveTo(spot, 600.0 * Math.Sin(angle), 600.0 * Math.Cos(angle), 0.0)
    EndIf
    Int slot = foeRefs.Find(None)
    If slot < 0
      slot = Utility.RandomInt(0, 63)   ; full: the oldest are forgotten
    EndIf
    foeRefs[slot] = a
    foeKind[slot] = f
    foeCounted[slot] = False
    If foeItem[f] >= 0 && itemRef[foeItem[f]]
      a.AddItem(itemRef[foeItem[f]])
      holderOf[foeItem[f]] = a
      foeItem[f] = -1
    EndIf
    k += 1
  EndWhile
EndFunction

; add foe face / drop foe face / restrain foe.
Function FoesFollow(Int f, Int code)
  Int k = 0
  While k < 64
    Actor a = None
    If foeKind[k] == f
      a = foeRefs[k] as Actor
    EndIf
    k += 1
    If a
      If code == 0x3F
        a.StopCombat()
        a.SetPlayerTeammate(True)
      ElseIf code == 0x40
        a.SetPlayerTeammate(False)
      Else
        a.StopCombat()
        a.SetActorValue("Aggression", 0)
      EndIf
    EndIf
  EndWhile
EndFunction

Bool Function DroppedAt(Int item, Int place)
  If item < 0 || place < 0 || !itemRef[item] || holderOf[item]
    Return False
  EndIf
  Return Machine.RefAt(itemRef[item], placeSite[place])
EndFunction

; Days a clock gives: its minutes, and for a clock to a place the way there and back (Daggerfall Unity Clock: 2.5 x
; the cautious travel time, at least a day).
Float Function ClockDuration(Int c)
  Int minutes = ClockMin[c]
  If ClockMax[c] > ClockMin[c]
    minutes = Utility.RandomInt(ClockMin[c], ClockMax[c])
  EndIf
  If ClockKind[c] >= 2 || Math.LogicalAnd(ClockFlags[c], 16) != 0
    Int travel = 0
    Int p = 0
    While p < PlaceScope.Length
      travel += Machine.TravelMinutes(Town, placeSite[p])
      p += 1
    EndWhile
    travel = (travel * 5) / 2
    If travel < 1440
      travel = 1440
    EndIf
    minutes += travel
  EndIf
  If minutes <= 0
    minutes = 1440
  EndIf
  Return minutes / 1440.0
EndFunction

; ---- Text ----

; Shows message id (a random variant), names filled in; returns the button pressed.
Int Function Say(Int id)
  Int first = MsgIds.Find(id)
  If first < 0
    Return 0
  EndIf
  Int count = 1
  While first + count < MsgIds.Length && MsgIds[first + count] == id
    count += 1
  EndWhile
  RefreshTags()
  Return Msgs[first + Utility.RandomInt(0, count - 1)].Show()
EndFunction

Function ShowLog(Int id, Int step)
  Int o = LogIds.Find(id)
  If o < 0 || step < 0 || step >= 10
    Return
  EndIf
  RefreshTags()
  If logShown[step] >= 0 && logShown[step] != o
    SetObjectiveDisplayed(logShown[step], False)
  EndIf
  SetObjectiveDisplayed(o, True)
  logShown[step] = o
EndFunction

Function RefreshTags()
  Int k = 0
  While k < TokenKind.Length
    If tags[k]
      tags[k].SetDisplayName(TokenText(k), True)
    EndIf
    k += 1
  EndWhile
EndFunction

String Function TokenText(Int k)
  Int kind = TokenKind[k]
  Int i = TokenIndex[k]
  Int mode = TokenMode[k]
  If kind == 5
    Return Machine.MacroText(i, self)
  ElseIf kind == 6
    Return Pronoun(i, mode)
  EndIf
  Return Name(kind, i, mode)
EndFunction

; A resource's name as Daggerfall's macros show it: mode 1 _x_, 2 __x_, 3 ___x_, 4 ____x_, 5 =x_, 6 ==x_.
String Function Name(Int kind, Int i, Int mode)
  If i < 0
    Return ""
  EndIf
  If kind == 0
    If ItemReward[i] >= 2
      Return itemGold[i]
    ElseIf itemRef[i]
      Return itemRef[i].GetDisplayName()
    EndIf
    Return "something"
  ElseIf kind == 1
    If mode == 1 || mode == 5
      If personRef[i]
        Return personRef[i].GetDisplayName()
      EndIf
      Return Machine.FactionName(PersonFactionOf(i))
    ElseIf mode == 6
      Return Machine.FactionName(PersonFactionOf(i))
    EndIf
    Int site = personSite[i]
    If site < 0 && personRef[i]
      site = Machine.SiteOfRef(personRef[i])
    EndIf
    Return Machine.SiteText(site, mode)
  ElseIf kind == 2
    Return Machine.SiteText(placeSite[i], mode)
  ElseIf kind == 3
    Int days = clockDays[i] as Int
    If clockEnd[i] > 0.0
      days = (clockEnd[i] - Utility.GetCurrentGameTime() + 0.5) as Int
    EndIf
    If days < 1
      days = 1
    EndIf
    Return days
  ElseIf kind == 4
    If FoeBase[i]
      Return FoeBase[i].GetName()
    EndIf
  EndIf
  Return ""
EndFunction

String Function Pronoun(Int n, Int which)
  Bool female = False
  Actor a = Questor
  If n >= 0 && personRef[n]
    a = personRef[n] as Actor
  EndIf
  If a
    female = a && a.GetLeveledActorBase().GetSex() == 1
  EndIf
  If which == 2
    If female
      Return "her"
    EndIf
    Return "him"
  ElseIf which == 3
    If female
      Return "her"
    EndIf
    Return "his"
  ElseIf which == 4
    If female
      Return "herself"
    EndIf
    Return "himself"
  EndIf
  If female
    Return "she"
  EndIf
  Return "he"
EndFunction

; ---- For the machine ----

Bool Function UsesIndividual(Int fid)
  Int n = 0
  While n < PersonKind.Length
    If (PersonKind[n] == -8 || PersonKind[n] == -1) && PersonFaction[n] == fid
      Return True
    EndIf
    n += 1
  EndWhile
  Return False
EndFunction

Int Function PlaceSiteOf(Int place)
  Return placeSite[place]
EndFunction
