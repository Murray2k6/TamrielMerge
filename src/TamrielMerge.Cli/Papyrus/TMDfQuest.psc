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
; "Tell me about" switches, one per resource key (place n, person 20 + n, item 40 + n): 0 shown, 1 hidden by "dialog link"
; (Daggerfall Unity TalkManager availableForDialog). The topics' responses ask for them (GetVMQuestVariable).
Int Property Hide0 = 0 Auto Hidden
Int Property Hide1 = 0 Auto Hidden
Int Property Hide2 = 0 Auto Hidden
Int Property Hide3 = 0 Auto Hidden
Int Property Hide4 = 0 Auto Hidden
Int Property Hide5 = 0 Auto Hidden
Int Property Hide6 = 0 Auto Hidden
Int Property Hide7 = 0 Auto Hidden
Int Property Hide8 = 0 Auto Hidden
Int Property Hide9 = 0 Auto Hidden
Int Property Hide10 = 0 Auto Hidden
Int Property Hide11 = 0 Auto Hidden
Int Property Hide12 = 0 Auto Hidden
Int Property Hide13 = 0 Auto Hidden
Int Property Hide14 = 0 Auto Hidden
Int Property Hide15 = 0 Auto Hidden
Int Property Hide16 = 0 Auto Hidden
Int Property Hide17 = 0 Auto Hidden
Int Property Hide18 = 0 Auto Hidden
Int Property Hide19 = 0 Auto Hidden
Int Property Hide20 = 0 Auto Hidden
Int Property Hide21 = 0 Auto Hidden
Int Property Hide22 = 0 Auto Hidden
Int Property Hide23 = 0 Auto Hidden
Int Property Hide24 = 0 Auto Hidden
Int Property Hide25 = 0 Auto Hidden
Int Property Hide26 = 0 Auto Hidden
Int Property Hide27 = 0 Auto Hidden
Int Property Hide28 = 0 Auto Hidden
Int Property Hide29 = 0 Auto Hidden
Int Property Hide30 = 0 Auto Hidden
Int Property Hide31 = 0 Auto Hidden
Int Property Hide32 = 0 Auto Hidden
Int Property Hide33 = 0 Auto Hidden
Int Property Hide34 = 0 Auto Hidden
Int Property Hide35 = 0 Auto Hidden
Int Property Hide36 = 0 Auto Hidden
Int Property Hide37 = 0 Auto Hidden
Int Property Hide38 = 0 Auto Hidden
Int Property Hide39 = 0 Auto Hidden
Int Property Hide40 = 0 Auto Hidden
Int Property Hide41 = 0 Auto Hidden
Int Property Hide42 = 0 Auto Hidden
Int Property Hide43 = 0 Auto Hidden
Int Property Hide44 = 0 Auto Hidden
Int Property Hide45 = 0 Auto Hidden
Int Property Hide46 = 0 Auto Hidden
Int Property Hide47 = 0 Auto Hidden
Int Property Hide48 = 0 Auto Hidden
Int Property Hide49 = 0 Auto Hidden
Int Property Hide50 = 0 Auto Hidden
Int Property Hide51 = 0 Auto Hidden
Int Property Hide52 = 0 Auto Hidden
Int Property Hide53 = 0 Auto Hidden
Int Property Hide54 = 0 Auto Hidden
Int Property Hide55 = 0 Auto Hidden
Int Property Hide56 = 0 Auto Hidden
Int Property Hide57 = 0 Auto Hidden
Int Property Hide58 = 0 Auto Hidden
Int Property Hide59 = 0 Auto Hidden
Int[] Property MsgRevealFirst Auto
Int[] Property MsgRevealCount Auto
{Each message's named resources (their keys) in RevealKeys: shown once it is.}
Int[] Property LogRevealFirst Auto
Int[] Property LogRevealCount Auto
Int[] Property RevealKeys Auto

Bool Property Running = False Auto Hidden
Bool Property Success = False Auto Hidden
Int Property FactionId = 0 Auto Hidden
{The faction whose regard the quest's end changes (Daggerfall: +5 done, -2 failed): the guild or the questor's.}
Actor Property Questor Auto Hidden
Int Property Town = -1 Auto Hidden
{The site town the quest was given in (TMDfQuestMachine).}
Int Property LastPlaceSite = -1 Auto Hidden
{The site of the place its text last named (Daggerfall Unity Quest.LastPlaceReferenced), for %di.}
Int Property LogDay = 1 Auto Hidden
Int Property LogMonth = 0 Auto Hidden
{The date of its latest log entry, else of its start (Quest.GetCurrentLogMessageTime), for %qdt.}

Bool[] states
Bool[] doneA
Bool[] doneB
Bool[] primary
ObjectReference[] personRef
Int[] personSite
Bool[] personHidden
Bool[] personMuted       ; "mute npc": clicks on them are ignored (Daggerfall Unity QuestResource.SetPlayerClicked)
Bool[] personDestroyed   ; "destroy npc": gone for good, never placed or clicked again (Person.DestroyNPC)
Bool[] foeRestrained     ; "restrain foe": each one made is no longer hostile until struck
Int[] foeSpell           ; "cast spell on foe": up to 4 queued per foe, cast on each one made (-1 none)
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
Bool[] spawnSaid
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
      ; A muted person speaks again once the action can run again (Daggerfall Unity MuteNpc.RearmAction).
      If Code(i) == 0x52 && Arg(i, 1) >= 0 && Arg(i, 1) < 16
        personMuted[Arg(i, 1)] = False
      EndIf
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
  personMuted = new Bool[16]
  personDestroyed = new Bool[16]
  foeRestrained = new Bool[8]
  foeSpell = new Int[32]
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
  spawnSaid = new Bool[8]
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
  While i < 32
    foeSpell[i] = -1
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
  LastPlaceSite = -1
  KeepDate()
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
  ; Those travelling with the player for it stop.
  Machine.DropFaces(self)
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
    ; "send": only once the player is in a location (Daggerfall Unity IsPlayerInLocationRect); then as "create".
    If op >= 0 && now >= spawnNext[i] && IsSet(Arg(op, 0)) && (Code(op) != 0x57 || Machine.CurrentTown >= 0 || Game.GetPlayer().IsInInterior())
      Int code = Code(op)
      spawnNext[i] = now + Arg(op, 2) / 1440.0
      If code == 0x54
        Machine.PlayDfSound(Arg(op, 1))
        spawnLeft[i] = spawnLeft[i] - 1
      ElseIf Utility.RandomInt(1, 100) <= Arg(op, 3)
        SpawnNearPlayer(Arg(op, 1))
        spawnLeft[i] = spawnLeft[i] - 1
        ; Its message ("msg N"), once a quest, on the first spawn.
        If !spawnSaid[i] && MsgOf(op) >= 0
          spawnSaid[i] = True
          Say(MsgOf(op))
        EndIf
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
      spawnSaid[slot] = False
      If code == 0x54
        spawnNext[slot] = Utility.GetCurrentGameTime() + Arg(i, 2) / 1440.0
      Else
        ; Daggerfall Unity CreateFoe: the first spawn time is as if the last were a random part of an interval ago.
        spawnNext[slot] = Utility.GetCurrentGameTime() + Utility.RandomFloat(0.0, Arg(i, 2) as Float) / 1440.0
      EndIf
      If code == 0x54
        ; "play sound N every M minutes K times" (Daggerfall Unity PlaySound: 0 times is for ever).
        spawnLeft[slot] = Arg(i, 3)
        If spawnLeft[slot] <= 0
          spawnLeft[slot] = -1
        EndIf
      Else
        spawnLeft[slot] = Arg(i, 4)
      EndIf
    EndIf
    Return False
  ElseIf code == 0x13 || code == 0x1B || code == 0x11
    ; reveal, reveal on the map, and a place's location marker (0x11).
    Machine.RevealSite(placeSite[Arg(i, 1)])
  ElseIf code == 0x0A || code == 0x0B
    ; "add dialog" shows the resources' "Tell me about" topics; "dialog link" hides them, the resource and the ones
    ; linked to it, until named or added (Daggerfall Unity TalkManager.AddDialogForQuestInfoResource /
    ; DialogLinkForQuestInfoResource).
    Bool show = code == 0x0A
    If Arg(i, 1) >= 0
      ShowAbout(Arg(i, 1), show)
    EndIf
    If Arg(i, 2) >= 0
      ShowAbout(20 + Arg(i, 2), show)
    EndIf
    If Arg(i, 3) >= 0
      ShowAbout(40 + Arg(i, 3), show)
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
    ; Destroyed: removed from the world for good (restore and place no longer bring them back), their name still known.
    ObjectReference p = personRef[Arg(i, 1)]
    If p
      p.Disable()
    EndIf
    personHidden[Arg(i, 1)] = True
    personDestroyed[Arg(i, 1)] = True
  ElseIf code == 0x2E
    If personRef[Arg(i, 1)]
      personRef[Arg(i, 1)].Disable()
    EndIf
    personHidden[Arg(i, 1)] = True
  ElseIf code == 0x30 && personDestroyed[Arg(i, 1)]
    ; Restoring a destroyed person does nothing (Daggerfall Unity: a destroyed person is hidden again each tick).
  ElseIf code == 0x30
    If personRef[Arg(i, 1)]
      personRef[Arg(i, 1)].Enable()
    EndIf
    personHidden[Arg(i, 1)] = False
  ElseIf code == 0x37
    Machine.Face(self, Name(1, Arg(i, 1), 1), True)
  ElseIf code == 0x38
    Machine.Face(self, Name(1, Arg(i, 1), 1), False)
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
    Machine.CurePlayer(0, Arg(i, 1))
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
  ElseIf code == 0x3F
    Machine.Face(self, Name(4, Arg(i, 1), 1), True)
  ElseIf code == 0x40
    Machine.Face(self, Name(4, Arg(i, 1), 1), False)
  ElseIf code == 0x44
    RestrainFoe(Arg(i, 1))
  ElseIf code == 0x45
    ; Daggerfall Unity CastSpellOnFoe: queued on the foe (Foe.QueueSpell), cast on each one there now and each made later.
    Int f = Arg(i, 1)
    If f >= 0 && f < 8
      Int q = 0
      While q < 4 && foeSpell[f * 4 + q] >= 0 && foeSpell[f * 4 + q] != Arg(i, 2)
        q += 1
      EndWhile
      If q < 4
        foeSpell[f * 4 + q] = Arg(i, 2)
      EndIf
    EndIf
    Int k = 0
    While k < 64
      If foeRefs[k] && foeKind[k] == f
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
    If Arg(i, 1) >= 0 && Arg(i, 1) < 16
      personMuted[Arg(i, 1)] = True
    EndIf
  ElseIf code == 0x53
    Machine.TransferPlayer(placeSite[Arg(i, 1)])
  ElseIf code == 0x55
    ; "add npc as questor" (Daggerfall Unity Quest.AddQuestor): the quest's questor is this person.
    If personRef[Arg(i, 1)]
      Questor = personRef[Arg(i, 1)] as Actor
    EndIf
  ElseIf code == 0x56
    ; "drop npc as questor" (Quest.DropQuestor): this person is the questor no longer.
    If personRef[Arg(i, 1)] && Questor == personRef[Arg(i, 1)] as Actor
      Questor = None
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
  If Running && n >= 0 && n < 16 && !personMuted[n] && !personDestroyed[n]
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
  If n < 0 || n >= 16 || personDestroyed[n]
    ; A destroyed person can't be placed (Person.DestroyNPC).
    Return
  EndIf
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

; Daggerfall Unity CreatePendingFoeSpawn: the foe's count of them (Foe.SpawnCount), placed where the player isn't looking
; (PlaceFoeFreely: 5-20 m inside, 8-25 m out; Skyrim units 80 a metre).
Function SpawnNearPlayer(Int f)
  Int count = 1
  If FoeCount && f >= 0 && f < FoeCount.Length && FoeCount[f] > 0
    count = FoeCount[f]
  EndIf
  SpawnFoe(f, Game.GetPlayer(), count)
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
      ; Daggerfall's quest spawns come from out of sight: behind the player (outside the field of view), 5-20 m away
      ; inside, 8-25 m out (PlaceFoeFreely).
      Float near = 400.0
      Float far = 1600.0
      If !spot.IsInInterior()
        near = 640.0
        far = 2000.0
      EndIf
      Float angle = spot.GetAngleZ() + 180.0 + Utility.RandomFloat(-60.0, 60.0)
      Float d = Utility.RandomFloat(near, far)
      a.MoveTo(spot, d * Math.Sin(angle), d * Math.Cos(angle), 0.0)
    EndIf
    Int slot = foeRefs.Find(None)
    If slot < 0
      slot = Utility.RandomInt(0, 63)   ; full: the oldest are forgotten
    EndIf
    foeRefs[slot] = a
    foeKind[slot] = f
    foeCounted[slot] = False
    If foeRestrained[f]
      Restrain(a as Actor)
    EndIf
    ; Spells queued on the foe go on each made (QuestResourceBehaviour.CastSpellQueue).
    Int q = 0
    While q < 4
      If foeSpell[f * 4 + q] >= 0
        Machine.CastDfSpell(foeSpell[f * 4 + q], a)
      EndIf
      q += 1
    EndWhile
    If foeItem[f] >= 0 && itemRef[foeItem[f]]
      a.AddItem(itemRef[foeItem[f]])
      holderOf[foeItem[f]] = a
      foeItem[f] = -1
    EndIf
    k += 1
  EndWhile
EndFunction

; "restrain foe" (Daggerfall Unity Foe.SetRestrained, QuestResourceBehaviour): every one of the foe made so far, and every
; one made after, is no longer hostile (EnemyMotor.IsHostile false) until struck.
Function RestrainFoe(Int f)
  If f < 0 || f >= 8
    Return
  EndIf
  foeRestrained[f] = True
  Int k = 0
  While k < 64
    If foeKind[k] == f && foeRefs[k]
      Restrain(foeRefs[k] as Actor)
    EndIf
    k += 1
  EndWhile
EndFunction

Function Restrain(Actor a)
  If !a || a.IsDead()
    Return
  EndIf
  TMDfSenses senses = a as TMDfSenses
  If senses
    senses.Pacify()
  Else
    a.StopCombat()
    a.SetActorValue("Aggression", 0)
  EndIf
EndFunction

Bool Function DroppedAt(Int item, Int place)
  If item < 0 || place < 0 || !itemRef[item] || holderOf[item]
    Return False
  EndIf
  Return Machine.RefAt(itemRef[item], placeSite[place])
EndFunction

; Days a clock gives (Daggerfall Unity Clock.SetResource): no time, a random one from a minute to a week; one, that; two,
; a random one between them (the first if the second isn't more). Flag 16 makes it the travel time to every place of the
; quest (each one way, at least a day, the sum x 2.5: there and back with time to spare); flag 1 with a range and no
; time the same.
Float Function ClockDuration(Int c)
  Int minutes
  If ClockMin[c] <= 0 && ClockMax[c] <= 0
    minutes = Utility.RandomInt(1, 7 * 1440)
  ElseIf ClockMax[c] > ClockMin[c]
    minutes = Utility.RandomInt(ClockMin[c], ClockMax[c])
  Else
    minutes = ClockMin[c]
  EndIf
  Bool travel = Math.LogicalAnd(ClockFlags[c], 16) != 0
  If !travel && Math.LogicalAnd(ClockFlags[c], 1) != 0 && ClockKind[c] > 0 && minutes == 0
    travel = True
  EndIf
  If travel
    Int total = 0
    Int p = 0
    While p < PlaceScope.Length
      total += Machine.ClockTravelMinutes(placeSite[p], Town)
      p += 1
    EndWhile
    minutes = (total * 5) / 2
  EndIf
  If minutes <= 0
    minutes = 1
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
  Int pick = first + Utility.RandomInt(0, count - 1)
  ; What it names is shown in "Tell me about" (QuestMacroHelper reveals dialog links as a message is expanded).
  If MsgRevealFirst && pick < MsgRevealFirst.Length
    RevealFrom(MsgRevealFirst[pick], MsgRevealCount[pick])
  EndIf
  Return Msgs[pick].Show()
EndFunction

; A resource's "Tell me about" topic shown or hidden (key: place n, person 20 + n, item 40 + n).
Function ShowAbout(Int aiKey, Bool show)
  If aiKey < 0 || aiKey >= 60
    Return
  EndIf
  If show
    SetHide(aiKey, 0)
  Else
    SetHide(aiKey, 1)
  EndIf
EndFunction

Function RevealFrom(Int first, Int count)
  If !RevealKeys
    Return
  EndIf
  Int k = 0
  While k < count && first + k < RevealKeys.Length
    ShowAbout(RevealKeys[first + k], True)
    k += 1
  EndWhile
EndFunction

; Today's date, kept as the quest's start or latest log entry.
Function KeepDate()
  GlobalVariable month = Game.GetFormFromFile(0x36, "Skyrim.esm") as GlobalVariable
  GlobalVariable day = Game.GetFormFromFile(0x37, "Skyrim.esm") as GlobalVariable
  If month && day
    LogMonth = month.GetValueInt()
    LogDay = day.GetValueInt()
  EndIf
EndFunction

Function ShowLog(Int id, Int step)
  Int o = LogIds.Find(id)
  If o < 0 || step < 0 || step >= 10
    Return
  EndIf
  KeepDate()
  RefreshTags()
  If LogRevealFirst && o < LogRevealFirst.Length
    RevealFrom(LogRevealFirst[o], LogRevealCount[o])
  EndIf
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
    ; A person's place named is the place last referenced (Person.ExpandMacro: LastPlaceReferenced).
    If site >= 0
      LastPlaceSite = site
    EndIf
    Return Machine.SiteText(site, mode)
  ElseIf kind == 2
    If placeSite[i] >= 0
      LastPlaceSite = placeSite[i]
    EndIf
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

; A "Tell me about" switch by key (Papyrus has no property by number).
Function SetHide(Int aiKey, Int value)
  If aiKey == 0
    Hide0 = value
  ElseIf aiKey == 1
    Hide1 = value
  ElseIf aiKey == 2
    Hide2 = value
  ElseIf aiKey == 3
    Hide3 = value
  ElseIf aiKey == 4
    Hide4 = value
  ElseIf aiKey == 5
    Hide5 = value
  ElseIf aiKey == 6
    Hide6 = value
  ElseIf aiKey == 7
    Hide7 = value
  ElseIf aiKey == 8
    Hide8 = value
  ElseIf aiKey == 9
    Hide9 = value
  ElseIf aiKey == 10
    Hide10 = value
  ElseIf aiKey == 11
    Hide11 = value
  ElseIf aiKey == 12
    Hide12 = value
  ElseIf aiKey == 13
    Hide13 = value
  ElseIf aiKey == 14
    Hide14 = value
  ElseIf aiKey == 15
    Hide15 = value
  ElseIf aiKey == 16
    Hide16 = value
  ElseIf aiKey == 17
    Hide17 = value
  ElseIf aiKey == 18
    Hide18 = value
  ElseIf aiKey == 19
    Hide19 = value
  ElseIf aiKey == 20
    Hide20 = value
  ElseIf aiKey == 21
    Hide21 = value
  ElseIf aiKey == 22
    Hide22 = value
  ElseIf aiKey == 23
    Hide23 = value
  ElseIf aiKey == 24
    Hide24 = value
  ElseIf aiKey == 25
    Hide25 = value
  ElseIf aiKey == 26
    Hide26 = value
  ElseIf aiKey == 27
    Hide27 = value
  ElseIf aiKey == 28
    Hide28 = value
  ElseIf aiKey == 29
    Hide29 = value
  ElseIf aiKey == 30
    Hide30 = value
  ElseIf aiKey == 31
    Hide31 = value
  ElseIf aiKey == 32
    Hide32 = value
  ElseIf aiKey == 33
    Hide33 = value
  ElseIf aiKey == 34
    Hide34 = value
  ElseIf aiKey == 35
    Hide35 = value
  ElseIf aiKey == 36
    Hide36 = value
  ElseIf aiKey == 37
    Hide37 = value
  ElseIf aiKey == 38
    Hide38 = value
  ElseIf aiKey == 39
    Hide39 = value
  ElseIf aiKey == 40
    Hide40 = value
  ElseIf aiKey == 41
    Hide41 = value
  ElseIf aiKey == 42
    Hide42 = value
  ElseIf aiKey == 43
    Hide43 = value
  ElseIf aiKey == 44
    Hide44 = value
  ElseIf aiKey == 45
    Hide45 = value
  ElseIf aiKey == 46
    Hide46 = value
  ElseIf aiKey == 47
    Hide47 = value
  ElseIf aiKey == 48
    Hide48 = value
  ElseIf aiKey == 49
    Hide49 = value
  ElseIf aiKey == 50
    Hide50 = value
  ElseIf aiKey == 51
    Hide51 = value
  ElseIf aiKey == 52
    Hide52 = value
  ElseIf aiKey == 53
    Hide53 = value
  ElseIf aiKey == 54
    Hide54 = value
  ElseIf aiKey == 55
    Hide55 = value
  ElseIf aiKey == 56
    Hide56 = value
  ElseIf aiKey == 57
    Hide57 = value
  ElseIf aiKey == 58
    Hide58 = value
  ElseIf aiKey == 59
    Hide59 = value
  EndIf
EndFunction
