ScriptName TMObConversations extends Quest
{Oblivion's conversations between people (ObConversations; the Construction Set wiki's Conversation Tab, read
2026-10-23), on OBDialogue:
- Every few seconds each of Oblivion's people near the player who isn't busy may open one with someone within
  fAISocialRadiusToTriggerConversation (Interior inside), at fAISocialchanceForConversation (Interior) percent, not
  within fAItalktoNPCtimer of its last one, nor fAItalktosameNPCtimer with the same partner. It opens with HELLO (a
  StartConversation with its own topic).
- Each line is a Say of the topic by the speaker, the listener in OBListener for what Oblivion runs on the target, the
  topic just said in OBConvFrom for the lines' Link From. The line said goes on by its Link To (the first topic, in
  order, with a line for the next speaker and not said yet in this one unless repeatable), the next speaker by its Next
  Speaker; one marked Goodbye ends it, and with nowhere left to go a GOODBYE does.
One conversation at a time, the nearest to the player (OBListener is one alias). How often Oblivion's people look for
someone to talk to isn't documented: every Poll seconds here.}

GlobalVariable Property ConvFrom Auto
FormList Property Topics Auto
{The conversation topics in order: a topic's number is its place + 1 (OBConvFrom, TMObLine.ConvTopic).}
FormList Property Repeatable Auto
{Topics said again in one conversation (a quest of theirs allows repeated conversation topics).}
Int Property ListenerAlias Auto
Actor Property PlayerRef Auto
Topic Property Hello Auto
Topic Property Goodbye Auto
Float Property RadiusExterior = 1800.0 Auto
Float Property RadiusInterior = 500.0 Auto
Float Property ChanceExterior = 100.0 Auto
Float Property ChanceInterior = 25.0 Auto
Float Property TalkAgain = 60.0 Auto
Float Property TalkSameAgain = 120.0 Auto

Float Property Poll = 5.0 AutoReadOnly
Float Property Hearing = 4096.0 AutoReadOnly       ; started only this near the player
Float Property NoLine = 2.0 AutoReadOnly           ; a topic whose line hasn't begun by then has none for the speaker
Float Property LineLimit = 60.0 AutoReadOnly       ; a line not ended by then (cut short): the conversation ends
Int Property MaxLines = 32 AutoReadOnly

Actor[] people
Float[] lastTalk
Actor[] lastPartner
Bool polling = False

Bool active = False
Actor speaker
Actor listener
Topic current
TMObLine lastLine
Form[] said
Int saidCount = 0
Int lines = 0
Int attempt = 0
Int phase = 0            ; 1 waiting for the line to begin, 2 for it to end, 3 going on
Bool goodbyeSaid = False

; ---- the people ----

Function Register(Actor a)
  If !a
    Return
  EndIf
  If !people
    people = new Actor[128]
    lastTalk = new Float[128]
    lastPartner = new Actor[128]
  EndIf
  If people.Find(a) >= 0
    Return
  EndIf
  Int i = people.Find(None)
  If i < 0
    Prune()
    i = people.Find(None)
  EndIf
  If i < 0
    Return
  EndIf
  people[i] = a
  ; Not everyone at once: as if its last were a random part of the wait ago.
  lastTalk[i] = Utility.GetCurrentRealTime() - Utility.RandomFloat(0.0, TalkAgain)
  lastPartner[i] = None
  If !polling && !active
    polling = True
    RegisterForSingleUpdate(Poll)
  EndIf
EndFunction

Function Unregister(Actor a)
  If !people
    Return
  EndIf
  Int i = people.Find(a)
  If i >= 0
    people[i] = None
  EndIf
EndFunction

Function Prune()
  Int i = 0
  While i < people.Length
    If people[i] && (people[i].IsDead() || !people[i].Is3DLoaded())
      people[i] = None
    EndIf
    i += 1
  EndWhile
EndFunction

FormList Property Busy Auto
{OBConvBusy: the packages made from Oblivion's "skip fallout behavior" ones. Their people don't stop to talk.}

Bool Function Free(Actor a)
  If !a || !a.Is3DLoaded() || a.IsDead() || a.IsInCombat() || a.IsInDialogueWithPlayer() || a.IsUnconscious() || a.GetSleepState() != 0
    Return False
  EndIf
  If Busy
    Package now = a.GetCurrentPackage()
    If now && Busy.HasForm(now)
      Return False
    EndIf
  EndIf
  Return True
EndFunction

; ---- starting ----

Event OnUpdate()
  If active
    Step()
    Return
  EndIf
  polling = False
  If !people
    Return
  EndIf
  Look()
  If !active && Anyone()
    polling = True
    RegisterForSingleUpdate(Poll)
  EndIf
EndEvent

Bool Function Anyone()
  Int i = 0
  While i < people.Length
    If people[i] && people[i].Is3DLoaded()
      Return True
    EndIf
    i += 1
  EndWhile
  Return False
EndFunction

; Someone free near the player, past its wait, opens one with the nearest free person in reach, at the chance.
Function Look()
  Float now = Utility.GetCurrentRealTime()
  Int i = 0
  While i < people.Length && !active
    Actor a = people[i]
    If Free(a) && now - lastTalk[i] >= TalkAgain && a.GetDistance(PlayerRef) <= Hearing
      Float radius = RadiusExterior
      Float chance = ChanceExterior
      If a.IsInInterior()
        radius = RadiusInterior
        chance = ChanceInterior
      EndIf
      Int best = -1
      Float bestD = radius + 1.0
      Int j = 0
      While j < people.Length
        Actor b = people[j]
        If j != i && Free(b) && now - lastTalk[j] >= TalkAgain && (lastPartner[i] != b || now - lastTalk[i] >= TalkSameAgain)
          Float d = a.GetDistance(b)
          If d <= radius && d < bestD
            best = j
            bestD = d
          EndIf
        EndIf
        j += 1
      EndWhile
      If best >= 0 && Utility.RandomFloat(0.0, 100.0) < chance
        Begin(a, people[best], Hello)
      EndIf
    EndIf
    i += 1
  EndWhile
EndFunction

; StartConversation from a translated script: OBDialogue's controller (SKSE Quest.GetQuest). Its value (Oblivion's
; returns nothing) is 0.
Float Function StartConversation(Actor a, Actor b, Topic akTopic = None) Global
  TMObConversations c = Quest.GetQuest("OBDialogue") as TMObConversations
  If c
    c.StartWith(a, b, akTopic)
  EndIf
  Return 0.0
EndFunction

; StartConversation (a script's): a with b, from the topic (HELLO without one). With the player, nothing here: Skyrim's
; own greeting does that part.
Function StartWith(Actor a, Actor b, Topic akTopic = None)
  If !a || !b || b == PlayerRef || a == PlayerRef
    Return
  EndIf
  If active
    Finish()
  EndIf
  If !akTopic
    akTopic = Hello
  EndIf
  Begin(a, b, akTopic)
EndFunction

Function Begin(Actor a, Actor b, Topic t)
  If !t
    Return
  EndIf
  active = True
  speaker = a
  listener = b
  lastLine = None
  said = new Form[32]
  saidCount = 0
  lines = 0
  goodbyeSaid = False
  ConvFrom.SetValue(0.0)
  SayTopic(t)
EndFunction

; ---- a line ----

Function SayTopic(Topic t)
  current = t
  phase = 1
  ReferenceAlias who = GetAlias(ListenerAlias) as ReferenceAlias
  If who && who.GetReference() != listener
    who.ForceRefTo(listener)
    Utility.Wait(0.05)
  EndIf
  speaker.SetLookAt(listener)
  listener.SetLookAt(speaker)
  speaker.Say(t)
  RegisterForSingleUpdate(NoLine)
EndFunction

; TMObLine.Fragment_Begin: the speaker's line of the topic asked for has begun.
Function Spoken(Actor a, TMObLine line)
  If !active || a != speaker || phase != 1
    Return
  EndIf
  phase = 2
  lastLine = line
  lines += 1
  If saidCount < said.Length && said.Find(current) < 0
    said[saidCount] = current
    saidCount += 1
  EndIf
  RegisterForSingleUpdate(LineLimit)
EndFunction

; TMObLine.Fragment_End: it is done (its result script runs next, before the conversation goes on).
Function Said(Actor a, TMObLine line)
  If !active || a != speaker || line != lastLine || phase != 2
    Return
  EndIf
  phase = 3
  RegisterForSingleUpdate(0.3)
EndFunction

Function Step()
  If phase == 1
    ; No line of this topic for the speaker: the next link, else the end.
    NextLink()
  ElseIf phase == 2
    ; The line never ended (cut short).
    Finish()
  ElseIf phase == 3
    GoOn()
  EndIf
EndFunction

Function GoOn()
  If !lastLine || lastLine.ConvGoodbye || lines >= MaxLines || !Free(speaker) || !Free(listener)
    Finish()
    Return
  EndIf
  ; Next Speaker: 0 the listener, 1 the speaker again, 2 either.
  Int next = lastLine.ConvNext
  If next == 2
    next = Utility.RandomInt(0, 1)
  EndIf
  If next == 0
    Actor was = speaker
    speaker = listener
    listener = was
  EndIf
  ConvFrom.SetValue((Topics.Find(current) + 1) as Float)
  attempt = 0
  NextLink()
EndFunction

; The said line's Link To, in order (each not said yet in this one unless repeatable); then a GOODBYE; then the end.
Function NextLink()
  If lines == 0
    Finish()
    Return
  EndIf
  Topic[] links = lastLine.ConvLinks
  While links && attempt < links.Length
    Topic t = links[attempt]
    attempt += 1
    If t && (said.Find(t) < 0 || Repeatable.HasForm(t))
      SayTopic(t)
      Return
    EndIf
  EndWhile
  If Goodbye && !goodbyeSaid && current != Goodbye
    goodbyeSaid = True
    attempt = 1000
    SayTopic(Goodbye)
    Return
  EndIf
  Finish()
EndFunction

Function Finish()
  active = False
  phase = 0
  ReferenceAlias who = GetAlias(ListenerAlias) as ReferenceAlias
  If who
    who.Clear()
  EndIf
  Float now = Utility.GetCurrentRealTime()
  If speaker
    speaker.ClearLookAt()
    Mark(speaker, listener, now)
  EndIf
  If listener
    listener.ClearLookAt()
    Mark(listener, speaker, now)
  EndIf
  ConvFrom.SetValue(0.0)
  speaker = None
  listener = None
  polling = True
  RegisterForSingleUpdate(Poll)
EndFunction

Function Mark(Actor a, Actor partner, Float now)
  If !people
    Return
  EndIf
  Int i = people.Find(a)
  If i >= 0
    lastTalk[i] = now
    lastPartner[i] = partner
  EndIf
EndFunction
