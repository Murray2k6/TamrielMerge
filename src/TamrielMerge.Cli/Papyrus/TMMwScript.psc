ScriptName TMMwScript Hidden
{What Morrowind's script functions do, for the scripts TamrielMerge translates from Morrowind (MW_*, MWG_*, MWR_*):
global functions for the functions that are more than one Papyrus call. Where Skyrim has no counterpart the nearest
behaviour is chosen and said so beside it (docs/morrowind-scripts.md lists them).}

; ---- the story ----

; Whether the player is in Morrowind's lands (a cell of Morrowind.esm): Morrowind's start scripts wait for it.
; EnableRaceMenu: Skyrim's race menu, if the story held it back for Morrowind's own (a new game begun with Morrowind's
; opening: TMStoryOrder.ShowHeldRaceMenu); otherwise nothing, the character made at the journey's start.
Function RaceMenu() Global
  TMStoryOrder story = Game.GetFormFromFile(0x0003372B, "Skyrim.esm") as TMStoryOrder
  If story
    story.ShowHeldRaceMenu()
  EndIf
EndFunction

Bool Function InMorrowind() Global
  Actor player = Game.GetPlayer()
  Cell here = player.GetParentCell()
  If !here
    Return False
  EndIf
  Int index = Game.GetModByName("Morrowind.esm")
  If index < 0 || index > 254
    Return False
  EndIf
  Return Math.RightShift(here.GetFormID(), 24) == index
EndFunction

; ---- locals the dialogue tests ----

; A local the dialogue's conditions test, mirrored as the actor's rank in a hidden faction (Skyrim's conditions can't
; read a script's variables): GetFactionRank in the response's conditions reads it.
Function MirrorLocal(ObjectReference akRef, Faction akFaction, Int aiValue) Global
  Actor a = akRef as Actor
  If !a || !akFaction
    Return
  EndIf
  If aiValue > 127
    aiValue = 127
  ElseIf aiValue < -127
    aiValue = -127
  EndIf
  a.SetFactionRank(akFaction, aiValue)
EndFunction

; ---- references ----

; GetDistance: between two references in the same space; Morrowind's huge distance for one somewhere else (or missing).
Float Function Distance(ObjectReference a, ObjectReference b) Global
  If !a || !b
    Return 100000.0
  EndIf
  Cell ca = a.GetParentCell()
  Cell cb = b.GetParentCell()
  If ca && cb && ca != cb && (ca.IsInterior() || cb.IsInterior())
    Return 100000.0
  EndIf
  If a.GetWorldSpace() != b.GetWorldSpace()
    Return 100000.0
  EndIf
  Return a.GetDistance(b)
EndFunction

; SetDelete 1: gone for good.
Function SetDelete(ObjectReference r, Int aiDelete) Global
  If r && aiDelete != 0
    r.Disable()
    r.Delete()
  EndIf
EndFunction

; The one activating (OnActivate's), or the player.
ObjectReference Function Activator(ObjectReference akActionRef, Actor akPlayer) Global
  If akActionRef
    Return akActionRef
  EndIf
  Return akPlayer
EndFunction

; Morrowind holds back an object's activation while its script checks OnActivate, until the script calls Activate:
; the default activation is blocked or let go, and the object's own lock (TMMwLock) and swing (TMSwingDoor) wait for the
; script or act by themselves with it.
Function Block(ObjectReference r, Bool abBlock) Global
  If !r
    Return
  EndIf
  r.BlockActivation(abBlock)
  TMMwLock lck = r as TMMwLock
  If lck
    lck.Deferred = abBlock
  EndIf
  TMSwingDoor swingDoor = r as TMSwingDoor
  If swingDoor
    swingDoor.Scripted = abBlock
  EndIf
EndFunction

; Activate: what the object does when used, which a Morrowind OnActivate held back - a swinging door swings, a lock
; is picked or stays shut, a door opens, a container opens, a person talks, an item is taken.
Function DefaultActivate(ObjectReference r, ObjectReference akWho) Global
  If !r
    Return
  EndIf
  If !akWho
    akWho = Game.GetPlayer()
  EndIf
  TMSwingDoor swingDoor = r as TMSwingDoor
  If swingDoor
    swingDoor.OpenBy(akWho)
    Return
  EndIf
  TMMwLock lck = r as TMMwLock
  If lck && !lck.LetIn(akWho)
    Return
  EndIf
  r.Activate(akWho, True)
EndFunction

; Lock [level]: Morrowind's 1-100 on Skyrim's lock levels (and a swinging door's own lock).
Function LockMw(ObjectReference r, Int aiLevel) Global
  If !r
    Return
  EndIf
  TMMwLock lck = r as TMMwLock
  If lck
    lck.LockLevel = aiLevel
    lck.Locked = True
  EndIf
  If r as TMSwingDoor
    Return
  EndIf
  r.Lock(True)
  If aiLevel <= 20
    r.SetLockLevel(1)
  ElseIf aiLevel <= 40
    r.SetLockLevel(25)
  ElseIf aiLevel <= 60
    r.SetLockLevel(50)
  ElseIf aiLevel <= 80
    r.SetLockLevel(75)
  Else
    r.SetLockLevel(100)
  EndIf
EndFunction

Function UnlockMw(ObjectReference r) Global
  If !r
    Return
  EndIf
  TMMwLock lck = r as TMMwLock
  If lck
    lck.Unlock()
  EndIf
  If !(r as TMSwingDoor)
    r.Lock(False)
  EndIf
EndFunction

Bool Function IsLockedMw(ObjectReference r) Global
  If !r
    Return False
  EndIf
  TMMwLock lck = r as TMMwLock
  If lck
    Return lck.IsShut()
  EndIf
  Return r.IsLocked()
EndFunction

; ---- placement ----

; PositionCell / Position: coordinates in a cell are offsets from the converter's marker at that cell's origin.
Function PositionAt(ObjectReference r, ObjectReference akAnchor, Float x, Float y, Float z, Float afDegrees) Global
  If !r || !akAnchor
    Return
  EndIf
  r.MoveTo(akAnchor, x - akAnchor.GetPositionX(), y - akAnchor.GetPositionY(), z - akAnchor.GetPositionZ(), False)
  r.SetAngle(0.0, 0.0, afDegrees)
EndFunction

; PositionCell to fixed coordinates in an interior: onto the converter's marker on the floor under them.
Function PositionOn(ObjectReference r, ObjectReference akSpot, Float afDegrees) Global
  If !r || !akSpot
    Return
  EndIf
  r.MoveTo(akSpot, 0.0, 0.0, 0.0, False)
  r.SetAngle(0.0, 0.0, afDegrees)
EndFunction

; The marker to place things by: the one in the reference's own cell for an interior, the exterior's outside.
ObjectReference Function AnchorFor(ObjectReference r, ObjectReference akExterior) Global
  If r && r.IsInInterior()
    Return r
  EndIf
  Return akExterior
EndFunction

; PlaceItemCell / PlaceItem: at those coordinates of the anchor's cell.
Function PlaceItem(ObjectReference akAnchor, Form akWhat, Float x, Float y, Float z, Float afDegrees) Global
  If !akAnchor || !akWhat
    Return
  EndIf
  ObjectReference placed = akAnchor.PlaceAtMe(akWhat, 1)
  If placed
    placed.SetPosition(x, y, z)
    placed.SetAngle(0.0, 0.0, afDegrees)
  EndIf
EndFunction

; PlaceAtPC / PlaceAtMe: count of them, distance away in a direction (0 ahead, 1 behind, 2 left, 3 right).
Function PlaceNear(ObjectReference r, Form akWhat, Int aiCount, Float afDistance, Int aiDirection) Global
  If !r || !akWhat
    Return
  EndIf
  Float heading = r.GetAngleZ()
  If aiDirection == 1
    heading += 180.0
  ElseIf aiDirection == 2
    heading -= 90.0
  ElseIf aiDirection == 3
    heading += 90.0
  EndIf
  Int i = 0
  While i < aiCount
    ObjectReference placed = r.PlaceAtMe(akWhat, 1)
    If placed && afDistance > 0.0
      placed.MoveTo(r, afDistance * Math.Sin(heading), afDistance * Math.Cos(heading), 0.0, False)
    EndIf
    i += 1
  EndWhile
EndFunction

Function SetPos(ObjectReference r, String asAxis, Float v) Global
  If !r
    Return
  EndIf
  If asAxis == "X"
    r.SetPosition(v, r.GetPositionY(), r.GetPositionZ())
  ElseIf asAxis == "Y"
    r.SetPosition(r.GetPositionX(), v, r.GetPositionZ())
  Else
    r.SetPosition(r.GetPositionX(), r.GetPositionY(), v)
  EndIf
EndFunction

Function SetAngle(ObjectReference r, String asAxis, Float v) Global
  If !r
    Return
  EndIf
  If asAxis == "X"
    r.SetAngle(v, r.GetAngleY(), r.GetAngleZ())
  ElseIf asAxis == "Y"
    r.SetAngle(r.GetAngleX(), v, r.GetAngleZ())
  Else
    r.SetAngle(r.GetAngleX(), r.GetAngleY(), v)
  EndIf
EndFunction

; Move / MoveWorld: units per second along an axis (the object's own, or the world's), glided over the time that passed.
Function Move(ObjectReference r, String asAxis, Float afUnits, Float afSeconds, Bool abWorld) Global
  ; Only while it is in the loaded world: TranslateTo on a reference with no 3D logs an error (as Rotate, below).
  If !r || afSeconds <= 0.0 || !r.Is3DLoaded()
    Return
  EndIf
  Float d = afUnits * afSeconds
  Float dx = 0.0
  Float dy = 0.0
  Float dz = 0.0
  If asAxis == "Z"
    dz = d
  ElseIf abWorld
    If asAxis == "X"
      dx = d
    Else
      dy = d
    EndIf
  Else
    Float heading = r.GetAngleZ()
    If asAxis == "Y"
      dx = d * Math.Sin(heading)
      dy = d * Math.Cos(heading)
    Else
      dx = d * Math.Cos(heading)
      dy = -d * Math.Sin(heading)
    EndIf
  EndIf
  Float speed = afUnits
  If speed < 0.0
    speed = -speed
  EndIf
  r.TranslateTo(r.GetPositionX() + dx, r.GetPositionY() + dy, r.GetPositionZ() + dz, r.GetAngleX(), r.GetAngleY(), r.GetAngleZ(), speed + 1.0, 0.0)
EndFunction

; GetInterior: whether the object is in an interior; with no object (a global script), the player.
Bool Function InInterior(ObjectReference r, ObjectReference akPlayer) Global
  If r
    Return r.IsInInterior()
  EndIf
  Return akPlayer && akPlayer.IsInInterior()
EndFunction

; Rotate / RotateWorld: degrees per second about an axis.
Function Rotate(ObjectReference r, String asAxis, Float afDegrees, Float afSeconds) Global
  ; Only while it is in the loaded world: TranslateTo on a reference with no 3D logs an error, and Morrowind's scripts
  ; turn their objects every frame wherever the player is (a user's report, 2026-11-02: TMMwScript.Rotate() errors over
  ; and over in the Papyrus log through Morrowind's opening).
  If !r || afSeconds <= 0.0 || !r.Is3DLoaded()
    Return
  EndIf
  Float d = afDegrees * afSeconds
  Float ax = r.GetAngleX()
  Float ay = r.GetAngleY()
  Float az = r.GetAngleZ()
  If asAxis == "X"
    ax += d
  ElseIf asAxis == "Y"
    ay += d
  Else
    az += d
  EndIf
  Float speed = afDegrees
  If speed < 0.0
    speed = -speed
  EndIf
  r.TranslateTo(r.GetPositionX(), r.GetPositionY(), r.GetPositionZ(), ax, ay, az, 1.0, speed + 1.0)
EndFunction

; Face x y: turn towards that point.
Function Face(ObjectReference r, Float x, Float y) Global
  If !r
    Return
  EndIf
  Float dx = x - r.GetPositionX()
  Float dy = y - r.GetPositionY()
  Float heading = 0.0
  If dy != 0.0 || dx != 0.0
    heading = Math.atan(dx / dy)
    If dy == 0.0
      If dx > 0.0
        heading = 90.0
      Else
        heading = 270.0
      EndIf
    ElseIf dy < 0.0
      heading += 180.0
    EndIf
  EndIf
  r.SetAngle(r.GetAngleX(), r.GetAngleY(), heading)
EndFunction

; GetStandingPC: the player on top of it (within its footprint, at its top).
Bool Function StandingOn(ObjectReference r, Actor akWho) Global
  If !r || !akWho || r.GetParentCell() != akWho.GetParentCell()
    Return False
  EndIf
  Float half = r.GetWidth()
  If r.GetLength() > half
    half = r.GetLength()
  EndIf
  half = half / 2.0 + 16.0
  Float dx = akWho.GetPositionX() - r.GetPositionX()
  Float dy = akWho.GetPositionY() - r.GetPositionY()
  If dx * dx + dy * dy > half * half
    Return False
  EndIf
  Float feet = akWho.GetPositionZ() - r.GetPositionZ()
  Return feet >= -16.0 && feet <= r.GetHeight() + 32.0
EndFunction

; GetCollidingPC: the player touching it (within its bounds, a little beyond).
Bool Function Touching(ObjectReference r, Actor akWho) Global
  If !r || !akWho || r.GetParentCell() != akWho.GetParentCell()
    Return False
  EndIf
  Float reach = (r.GetWidth() + r.GetLength()) / 4.0 + 48.0
  Return r.GetDistance(akWho) <= reach
EndFunction

; GetStandingActor / GetCollidingActor: anyone on it (or touching it), the player or another actor of its cell
; (SKSE's cell references, type 62: actors). Until 2026-11-10 only the player was looked at.
Bool Function AnyoneOn(ObjectReference r, Bool abStanding) Global
  If !r
    Return False
  EndIf
  Actor player = Game.GetPlayer()
  If abStanding && StandingOn(r, player) || !abStanding && Touching(r, player)
    Return True
  EndIf
  Cell c = r.GetParentCell()
  If !c
    Return False
  EndIf
  Int n = c.GetNumRefs(62)
  Int i = 0
  While i < n
    Actor a = c.GetNthRef(i, 62) as Actor
    If a && a != player && !a.IsDead() && (abStanding && StandingOn(r, a) || !abStanding && Touching(r, a))
      Return True
    EndIf
    i += 1
  EndWhile
  Return False
EndFunction

; HurtStandingActor / HurtCollidingActor: everyone on it (or touching it) loses that much health (lava, traps).
Function HurtEveryone(ObjectReference r, Float afAmount, Bool abStanding) Global
  If !r || afAmount <= 0.0
    Return
  EndIf
  Actor player = Game.GetPlayer()
  HurtTouching(r, player, afAmount, abStanding)
  Cell c = r.GetParentCell()
  If !c
    Return
  EndIf
  Int n = c.GetNumRefs(62)
  Int i = 0
  While i < n
    Actor a = c.GetNthRef(i, 62) as Actor
    If a && a != player && !a.IsDead()
      HurtTouching(r, a, afAmount, abStanding)
    EndIf
    i += 1
  EndWhile
EndFunction

; DisablePlayerJumping / EnablePlayerJumping: Skyrim's controls have no jump switch of their own, but a jump of no
; height is none: the jump's height setting (fJumpHeightMin, 76 in Skyrim) is put to 0 and back. Skyrim keeps a
; game setting changed this way for the session only: a game loaded while jumping is off has it on again (Morrowind
; switches it off only during the character's making).
Function PlayerJumping(Bool abAllowed) Global
  If abAllowed
    Game.SetGameSettingFloat("fJumpHeightMin", 76.0)
  Else
    Game.SetGameSettingFloat("fJumpHeightMin", 0.0)
  EndIf
EndFunction

Bool Function PlayerJumpingDisabled() Global
  Return Game.GetGameSettingFloat("fJumpHeightMin") <= 0.0
EndFunction

; DisableVanityMode / EnableVanityMode: the idle camera that swings round a player left alone. Skyrim starts it after
; fAutoVanityModeDelay seconds (120); a delay no one waits out keeps it off.
Function VanityMode(Bool abAllowed) Global
  If abAllowed
    Game.SetGameSettingFloat("fAutoVanityModeDelay", 120.0)
  Else
    Game.SetGameSettingFloat("fAutoVanityModeDelay", 100000000.0)
  EndIf
EndFunction

Bool Function VanityModeDisabled() Global
  Return Game.GetGameSettingFloat("fAutoVanityModeDelay") > 1000000.0
EndFunction

Function HurtTouching(ObjectReference r, Actor akWho, Float afAmount, Bool abStanding) Global
  If abStanding && StandingOn(r, akWho) || !abStanding && Touching(r, akWho)
    akWho.DamageActorValue("Health", afAmount)
  EndIf
EndFunction

; GetPCCell: the player in a cell of that name (interiors starting with it, exterior cells of that name).
Bool Function InCells(Actor akWho, FormList akCells) Global
  If !akWho || !akCells
    Return False
  EndIf
  Return akCells.HasForm(akWho.GetParentCell())
EndFunction

; GetWaterLevel: Morrowind's sea is at 0 outdoors; indoors the cell's own water, which Papyrus can't read.
; GetWaterLevel: the water height of the reference's cell (the converter keeps Morrowind's; an exterior's is the
; worldspace's, 0), 0 where there is none.
Float Function WaterLevel(ObjectReference r) Global
  If !r
    Return 0.0
  EndIf
  Cell c = r.GetParentCell()
  If !c
    Return 0.0
  EndIf
  Float level = c.GetActualWaterLevel()
  If level < -1000000.0
    Return 0.0
  EndIf
  Return level
EndFunction

; ---- actors ----

; ForceGreeting: the person starts talking to the player (activated by them, which opens the conversation).
Function ForceGreeting(Actor a, Actor akPlayer) Global
  If a && !a.IsDead() && !a.IsInCombat()
    a.Activate(akPlayer, True)
  EndIf
EndFunction

; Disposition towards the player: Variable09 holds it minus 50 (Skyrim's Disposition, TMMwInfo), kept within 0-100.
; GetDisposition: what the actor feels for the player (a Morrowind person's OpenMW's derived disposition, which
; TMMwActorAI keeps in Variable09, Charm on top).
Int Function GetDisposition(Actor a) Global
  If !a
    Return 0
  EndIf
  Return (50.0 + a.GetActorValue("Variable09")) as Int
EndFunction

; SetDisposition / ModDisposition: a Morrowind person's base disposition, from which its derived one follows; anyone
; else's Variable09 as it is.
Function SetDisposition(Actor a, Float afValue) Global
  If !a
    Return
  EndIf
  TMMwActorAI mw = a as TMMwActorAI
  If mw && mw.IsPerson
    mw.SetBaseDisposition(afValue)
    Return
  EndIf
  If afValue < 0.0
    afValue = 0.0
  ElseIf afValue > 100.0
    afValue = 100.0
  EndIf
  a.SetActorValue("Variable09", afValue - 50.0)
EndFunction

Function ModDisposition(Actor a, Float afBy) Global
  If !a
    Return
  EndIf
  TMMwActorAI mw = a as TMMwActorAI
  If mw && mw.IsPerson
    mw.SetBaseDisposition(mw.GetBaseDisposition() + afBy)
    Return
  EndIf
  SetDisposition(a, GetDisposition(a) + afBy)
EndFunction

; Hello, Fight, Flee and Alarm (0-100): Morrowind's own on its people and creatures (TMMwActorAI, from their records,
; which also does what Morrowind's engine does with them). Anyone else (a Skyrim actor): kept in Variable05-08, set on
; Skyrim's nearest values - Fight on Aggression, Flee on Confidence, Alarm on Morality.
Int Function GetAi(Actor a, String asWhich) Global
  If !a
    Return 0
  EndIf
  TMMwActorAI mw = a as TMMwActorAI
  If mw
    Return mw.GetSetting(asWhich)
  EndIf
  Float stored = a.GetActorValue(AiVariable(asWhich))
  If stored != 0.0
    Return stored as Int
  EndIf
  If asWhich == "fight"
    Float aggression = a.GetActorValue("Aggression")
    If aggression >= 2.0
      Return 90
    ElseIf aggression >= 1.0
      Return 80
    EndIf
    Return 30
  ElseIf asWhich == "flee"
    Float confidence = a.GetActorValue("Confidence")
    Return (100.0 - confidence * 25.0) as Int
  ElseIf asWhich == "alarm"
    Return (a.GetActorValue("Morality") >= 3.0) as Int * 100
  EndIf
  Return 30
EndFunction

Function SetAi(Actor a, String asWhich, Float afValue) Global
  If !a
    Return
  EndIf
  TMMwActorAI mw = a as TMMwActorAI
  If mw
    mw.SetSetting(asWhich, afValue)
    a.EvaluatePackage()
    Return
  EndIf
  If afValue < 0.0
    afValue = 0.0
  ElseIf afValue > 100.0
    afValue = 100.0
  EndIf
  ; 0 is "not set" in the stored copy, so 0 is kept as a tiny number.
  Float stored = afValue
  If stored == 0.0
    stored = 0.01
  EndIf
  a.SetActorValue(AiVariable(asWhich), stored)
  If asWhich == "fight"
    If afValue >= 90.0
      a.SetActorValue("Aggression", 2.0)
    ElseIf afValue >= 80.0
      a.SetActorValue("Aggression", 1.0)
    Else
      a.SetActorValue("Aggression", 0.0)
    EndIf
  ElseIf asWhich == "flee"
    a.SetActorValue("Confidence", ((100.0 - afValue) / 25.0) as Int)
  ElseIf asWhich == "alarm"
    If afValue >= 100.0
      a.SetActorValue("Morality", 3.0)
    Else
      a.SetActorValue("Morality", 0.0)
    EndIf
  EndIf
  a.EvaluatePackage()
EndFunction

; SayDone: a Morrowind person has said its line (TMMwSayLine marks it from its first word to its last), or the
; recording's own length is up (afUntil, from the file as the script was translated: with no voice file there, voices
; not generated, the line's end is never marked, and the dock guard's CharGenRaceNPC kept the player's controls off for a
; minute a line - a user, 2026-10-27), or a minute has passed in any case; anyone else (a line played as a sound) by the
; recording's length alone.
Bool Function SayDone(ObjectReference r, Float afUntil) Global
  Float now = Utility.GetCurrentRealTime()
  TMMwActorAI mw = r as TMMwActorAI
  If mw
    ; Never before the recording's length is up: the line begins some 0.7 s after Say (seen in game 2026-11-09: Say at
    ; 274.13, its begin fragment at 274.80), and until then nothing is marked as being said - Jiub's script took that
    ; for the line's end and ran through all his lines in two seconds. After it, the line's own end, at most 2 s more.
    If afUntil > 0.0
      If now < afUntil
        Return False
      EndIf
      Return !mw.Saying || now >= afUntil + 2.0
    EndIf
    Return !mw.Saying || now - mw.SaidAt > 60.0
  EndIf
  Return now >= afUntil
EndFunction

; GetAttacked: Morrowind's people and creatures keep it (TMMwActorAI, Variable03: struck by the player, until the
; crime is dealt with); anyone else: in combat.
Bool Function GetAttacked(Actor a) Global
  If !a
    Return False
  EndIf
  If a as TMMwActorAI
    Return a.GetActorValue("Variable03") > 0.0
  EndIf
  Return a.IsInCombat()
EndFunction

; Reputation: the player's in a global (MWReputation, which the dialogue's PC Reputation reads), another's on its
; TMMwActorAI (from its record's NPDT).
Float Function GetReputation(Actor a, GlobalVariable akPlayerReputation) Global
  If !a
    Return 0.0
  EndIf
  If a == Game.GetPlayer()
    Return akPlayerReputation.GetValue()
  EndIf
  TMMwActorAI mw = a as TMMwActorAI
  If mw
    Return mw.Reputation
  EndIf
  Return 0.0
EndFunction

Function SetReputation(Actor a, GlobalVariable akPlayerReputation, Float afValue) Global
  If !a
    Return
  EndIf
  If a == Game.GetPlayer()
    akPlayerReputation.SetValue(afValue)
    Return
  EndIf
  TMMwActorAI mw = a as TMMwActorAI
  If mw
    mw.SetReputationValue(afValue as Int)
  EndIf
EndFunction

String Function AiVariable(String asWhich) Global
  If asWhich == "hello"
    Return "Variable05"
  ElseIf asWhich == "fight"
    Return "Variable06"
  ElseIf asWhich == "flee"
    Return "Variable07"
  EndIf
  Return "Variable08"
EndFunction

; SetLevel: Skyrim's levels come from the actor's record; only the player's can change (by script, it can't).
Function SetLevel(Actor a, Int aiLevel) Global
EndFunction

; GetDeadCount: how many of that person or creature have died.
Int Function DeadCount(Form akBase) Global
  ActorBase b = akBase as ActorBase
  If !b
    Return 0
  EndIf
  Return b.GetDeadCount()
EndFunction

Bool Function SpellReadied(Actor a) Global
  Return a && a.IsWeaponDrawn() && (a.GetEquippedItemType(0) == 9 || a.GetEquippedItemType(1) == 9)
EndFunction

Bool Function IsRace(Actor a, Race akRace) Global
  Return a && akRace && a.GetRace() == akRace
EndFunction

; SameFaction: the actor and the player share a faction (Morrowind: the actor's own faction).
Bool Function SameFaction(Faction akFaction, Actor akPlayer) Global
  Return akFaction && akPlayer && akPlayer.IsInFaction(akFaction)
EndFunction

; PCRaiseRank / PCLowerRank / RaiseRank: one rank up or down (joining at the lowest).
Function ModRank(Actor a, Faction f, Int aiBy) Global
  If !a || !f
    Return
  EndIf
  Int rank = a.GetFactionRank(f)
  If rank < 0
    a.SetFactionRank(f, 0)
    Return
  EndIf
  rank += aiBy
  If rank < 0
    rank = 0
  EndIf
  a.SetFactionRank(f, rank)
EndFunction

; ClearForceSneak: Skyrim's actors stop sneaking when their package lets them.
Function StopSneaking(Actor a) Global
  If a
    a.EvaluatePackage()
  EndIf
EndFunction

; ---- items, spells and effects ----

; GetWeaponType: Morrowind's numbers (-1 none, 0 short blade, 1 long blade, 2 two-handed long blade, 3 blunt, 4 two-handed
; blunt, 6 spear, 7 axe, 8 two-handed axe, 9 bow, 10 crossbow) from Skyrim's weapon types.
Int Function WeaponType(Actor a) Global
  If !a
    Return -1
  EndIf
  Weapon w = a.GetEquippedWeapon()
  If !w
    Return -1
  EndIf
  Int t = w.GetWeaponType()
  If t == 1
    Return 1
  ElseIf t == 2
    Return 0
  ElseIf t == 3
    Return 7
  ElseIf t == 4
    Return 3
  ElseIf t == 5
    Return 2
  ElseIf t == 6
    Return 8
  ElseIf t == 7
    Return 9
  ElseIf t == 9
    Return 10
  EndIf
  Return -1
EndFunction

; GetArmorType part: what is worn on that part (0 helmet, 1 cuirass, 2/3 pauldrons, 4/5 greaves... 8 boots): -1 nothing,
; 0 light, 1 medium, 2 heavy. Skyrim has no medium class: Morrowind's medium pieces (by weight, as Morrowind classes them)
; are the MWMediumArmor list's.
Int Function ArmorType(Actor a, Int aiPart, FormList akMedium) Global
  If !a
    Return -1
  EndIf
  Int mask = 0x4
  If aiPart == 0
    mask = 0x1
  ElseIf aiPart == 6 || aiPart == 7
    mask = 0x80
  ElseIf aiPart == 8 || aiPart == 9
    mask = 0x8
  ElseIf aiPart == 10
    mask = 0x200
  EndIf
  Armor worn = a.GetWornForm(mask) as Armor
  If !worn
    Return -1
  ElseIf akMedium && akMedium.HasForm(worn)
    Return 1
  ElseIf worn.GetWeightClass() == 1
    Return 2
  ElseIf worn.GetWeightClass() == 0
    Return 0
  EndIf
  Return -1
EndFunction

; HasSoulGem: Skyrim's soul gems hold a size of soul, not a creature's: the filled soul gems the actor carries.
; HasSoulGem: the gems holding that creature's soul (its list, every size).
Int Function CountIn(ObjectReference a, FormList akList) Global
  If !a || !akList
    Return 0
  EndIf
  Return a.GetItemCount(akList)
EndFunction

; RemoveSoulGem / DropSoulGem: that many of them taken away, or dropped at the actor's feet.
Function RemoveIn(ObjectReference a, FormList akList, Int aiCount, Bool abDrop) Global
  If !a || !akList
    Return
  EndIf
  Int i = 0
  While i < akList.GetSize() && aiCount > 0
    Form f = akList.GetAt(i)
    Int have = a.GetItemCount(f)
    If have > aiCount
      have = aiCount
    EndIf
    If have > 0
      If abDrop
        a.DropObject(f, have)
      Else
        a.RemoveItem(f, have)
      EndIf
      aiCount -= have
    EndIf
    i += 1
  EndWhile
EndFunction

Int Function SoulGems(Actor a, Form akCreature) Global
  If !a
    Return 0
  EndIf
  Int count = 0
  Int i = a.GetNumItems()
  While i > 0
    i -= 1
    SoulGem gem = a.GetNthForm(i) as SoulGem
    If gem && gem.GetSoulSize() > 0
      count += a.GetItemCount(gem)
    EndIf
  EndWhile
  Return count
EndFunction

Function RemoveSoulGem(Actor a, Form akCreature, Int aiCount) Global
  If !a
    Return
  EndIf
  Int i = a.GetNumItems()
  While i > 0 && aiCount > 0
    i -= 1
    SoulGem gem = a.GetNthForm(i) as SoulGem
    If gem && gem.GetSoulSize() > 0
      Int have = a.GetItemCount(gem)
      If have > aiCount
        have = aiCount
      EndIf
      a.RemoveItem(gem, have)
      aiCount -= have
    EndIf
  EndWhile
EndFunction

; GetSpellEffects: under any of the spell's effects.
Bool Function HasSpellEffects(Actor a, Spell akSpell) Global
  If !a || !akSpell
    Return False
  EndIf
  Int i = akSpell.GetNumEffects()
  While i > 0
    i -= 1
    MagicEffect e = akSpell.GetNthEffectMagicEffect(i)
    If e && a.HasMagicEffect(e)
      Return True
    EndIf
  EndWhile
  Return False
EndFunction

; GetEffect: under any of the converted magic effects of that Morrowind effect (a list the converter makes).
Bool Function HasEffectIn(Actor a, FormList akEffects) Global
  If !a || !akEffects
    Return False
  EndIf
  Int i = akEffects.GetSize()
  While i > 0
    i -= 1
    MagicEffect e = akEffects.GetAt(i) as MagicEffect
    If e && a.HasMagicEffect(e)
      Return True
    EndIf
  EndWhile
  Return False
EndFunction

; RemoveEffects: ends every spell the actor has with one of those effects (Skyrim dispels by spell).
Function RemoveEffectsIn(Actor a, FormList akEffects) Global
  If !HasEffectIn(a, akEffects)
    Return
  EndIf
  Int i = a.GetSpellCount()
  While i > 0
    i -= 1
    Spell s = a.GetNthSpell(i)
    If s
      Int k = s.GetNumEffects()
      While k > 0
        k -= 1
        If akEffects.HasForm(s.GetNthEffectMagicEffect(k))
          a.DispelSpell(s)
          k = 0
        EndIf
      EndWhile
    EndIf
  EndWhile
EndFunction

; GetCommonDisease / GetBlightDisease: any of Morrowind's diseases of that kind (lists the converter makes).
Bool Function HasAny(Actor a, FormList akSpells) Global
  If !a || !akSpells
    Return False
  EndIf
  Int i = akSpells.GetSize()
  While i > 0
    i -= 1
    Spell s = akSpells.GetAt(i) as Spell
    If s && a.HasSpell(s)
      Return True
    EndIf
  EndWhile
  Return False
EndFunction

; Werewolves: Skyrim's beast form. IsWerewolf while in it; BecomeWerewolf turns the player (Skyrim's own change spell).
Bool Function IsWerewolf(Actor a) Global
  If !a
    Return False
  EndIf
  Race werewolf = Game.GetFormFromFile(0x0CDD84, "Skyrim.esm") as Race
  Return werewolf && a.GetRace() == werewolf
EndFunction

Function BecomeWerewolf(Actor a) Global
  If a && a == Game.GetPlayer() && !IsWerewolf(a)
    ; The night's kills start at none (OpenMW NpcStats::setWerewolf) and the bounty is noted for the change's witnesses
    ; (TMMwAfflictions, the player alias on MWAfflictions).
    Quest afflictions = Quest.GetQuest("MWAfflictions")
    If afflictions
      TMMwAfflictions forms = afflictions.GetAlias(0) as TMMwAfflictions
      If forms
        forms.ChangeBegins()
      EndIf
    EndIf
    Spell change = Game.GetFormFromFile(0x092C48, "Skyrim.esm") as Spell
    If change
      change.Cast(a, a)
    EndIf
  EndIf
EndFunction

; UndoWerewolf: the beast form ends at once (Skyrim's werewolf quest turns the player back when its timer runs out).
Function UndoWerewolf(Actor a) Global
  If a && a == Game.GetPlayer() && IsWerewolf(a)
    GlobalVariable shiftBack = Game.GetFormFromFile(0x02BA19, "Skyrim.esm") as GlobalVariable   ; PlayerWerewolfShiftBackTime
    If shiftBack
      shiftBack.SetValue(Utility.GetCurrentGameTime())
    EndIf
  EndIf
EndFunction

; ---- stats ----

; Morrowind's attributes (and Athletics, Acrobatics, Unarmored and some effects), which Skyrim's actors don't have, are
; kept by TMMwState (the quest MWScriptState).

; SetHealth / SetMagicka / SetFatigue and the skills: the base (and with it the current) value.
Function SetActorValueMw(Actor a, String asValue, Float afValue) Global
  If a
    a.SetActorValue(asValue, afValue)
    ; Morrowind's SetHealth 0 kills (a dynamic stat at nothing is death); Skyrim's base value at 0 doesn't.
    If asValue == "Health" && afValue <= 0.0 && !a.IsDead()
      a.Kill()
    EndIf
  EndIf
EndFunction

; ModCurrentHealth and the like: damage (negative) or restore (positive), as Morrowind's current values move.
Function ModCurrent(Actor a, String asValue, Float afBy) Global
  If !a
    Return
  EndIf
  If afBy < 0.0
    a.DamageActorValue(asValue, -afBy)
  Else
    a.RestoreActorValue(asValue, afBy)
  EndIf
EndFunction

; ---- factions, crime ----

; SetFactionReaction / ModFactionReaction: Morrowind's reaction numbers (about -10 to 10) on Skyrim's: enemy below 0,
; neutral at 0, friend above (and ally from 5).
Function SetReaction(Faction a, Faction b, Float afValue) Global
  If !a || !b
    Return
  EndIf
  If afValue < 0.0
    a.SetEnemy(b)
  ElseIf afValue >= 5.0
    a.SetAlly(b, False, False)
  ElseIf afValue > 0.0
    a.SetAlly(b, True, True)
  Else
    a.SetEnemy(b, True, True)
  EndIf
EndFunction

Float Function ReactionValue(Faction a, Faction b) Global
  If !a || !b
    Return 0.0
  EndIf
  Int r = a.GetReaction(b)
  If r == 1
    Return -5.0
  ElseIf r == 2
    Return 5.0
  ElseIf r == 3
    Return 3.0
  EndIf
  Return 0.0
EndFunction

; Morrowind's bounty: one crime faction for its lands (MWCrime).
Function SetCrime(Faction akCrime, Int aiGold) Global
  If !akCrime
    Return
  EndIf
  If aiGold < 0
    aiGold = 0
  EndIf
  akCrime.SetCrimeGold(aiGold)
  If aiGold == 0
    akCrime.SetCrimeGoldViolent(0)
  EndIf
EndFunction

; PayFine, PayFineThief and GoToJail are Morrowind's law's (TMMwLaw on the quest MWLaw).

; ---- the journal ----

; Journal / SetJournalIndex: Morrowind's journal index is the journal's global (the dialogue's conditions and
; GetJournalIndex read it), so it can go back as Morrowind's does; a quest's stage can't be undone. Journal also adds the
; entry: the quest's stage, or, when the journal goes back to an entry already in it, that entry shown again as the
; quest's objective (until the journal moves on). SetJournalIndex sets the index only, as in Morrowind.
Function Journal(Quest q, GlobalVariable akIndex, Int aiIndex, Bool abEntry) Global
  If !q
    Return
  EndIf
  Int was = -1
  If akIndex
    was = akIndex.GetValueInt()
    akIndex.SetValueInt(aiIndex)
  EndIf
  If !abEntry
    Return
  EndIf
  If was >= 0 && was != aiIndex && q.IsObjectiveDisplayed(was)
    q.SetObjectiveDisplayed(was, False)
  EndIf
  If q.IsStageDone(aiIndex) || !q.SetStage(aiIndex)
    If was != aiIndex
      q.SetObjectiveDisplayed(aiIndex, True, True)
    EndIf
  EndIf
EndFunction

; ---- the interface ----

; WakeUpPC: the player's rest ends. Morrowind's scripts look at each hour of a rest and wake the player in one of them;
; here the script runs as the sleep starts, so the wake comes a random part of the way through (afStart and afEnd are
; the sleep's game times), and the sleep menu is closed as its Cancel does (SKSE).
; WakeUpPC: the rest ends at once, the hours so far passed (OpenMW wakeUpPlayer stops the waiting there).
Function WakeUpPC(Float afStart, Float afEnd) Global
  If UI.IsMenuOpen("Sleep/Wait Menu")
    UI.Invoke("Sleep/Wait Menu", "_root.SleepWaitMenu_mc.onCancelPress")
  EndIf
EndFunction

; StreamMusic: Morrowind's track, a music type of its own that plays once over the place's music (then that resumes).
Function StreamMusic(MusicType akMusic) Global
  If akMusic
    akMusic.Add()
  EndIf
EndFunction

; ShowRestMenu: Skyrim's wait menu (sleeping needs a bed), opened as its key would (SKSE).
Function ShowRestMenu() Global
  Int waitKey = Input.GetMappedKey("Wait")
  If waitKey > 0
    Input.TapKey(waitKey)
  EndIf
EndFunction

; ShowMap: the map markers of that place become known.
Function ShowMap(FormList akMarkers) Global
  If !akMarkers
    Return
  EndIf
  Int i = akMarkers.GetSize()
  While i > 0
    i -= 1
    ObjectReference marker = akMarkers.GetAt(i) as ObjectReference
    If marker
      marker.AddToMap(False)
    EndIf
  EndWhile
EndFunction

Function FadeOut(Float afSeconds) Global
  Game.FadeOutGame(True, True, 0.0, afSeconds)
EndFunction

Function FadeIn(Float afSeconds) Global
  Game.FadeOutGame(False, True, 0.0, afSeconds)
EndFunction

; FadeTo alpha seconds: to black above half, back from it below.
Function FadeTo(Float afAlpha, Float afSeconds) Global
  If afAlpha >= 50.0
    FadeOut(afSeconds)
  Else
    FadeIn(afSeconds)
  EndIf
EndFunction

Bool Function InThirdPerson() Global
  Return Game.GetCameraState() != 0
EndFunction

; PlayGroup / LoopGroup: an animation group on an object (the converted model's own sequence of that name); on a creature
; with its own graph, as Morrowind plays it (OpenMW's playGroup): aiMode 0 after the one playing (queued), 1 at once from
; its start, 2 at once from its loop start; its loop part aiLoops more times (PlayGroup's -1: on and on), then to its end;
; "idle" stops it. On a person (Papyrus compares strings ignoring case): a knockout its bleed-out, a death its death,
; "idle" back to standing (Morrowind's scripts play only Death1 on people: mw-group-census --scripts).
Function PlayGroup(ObjectReference r, String asGroup, Int aiMode = 0, Int aiLoops = -1) Global
  If !r
    Return
  EndIf
  Actor a = r as Actor
  If a && a.GetAnimationVariableInt("iTMPlayStop") > 0
    ; One of its graph's groups (each a number: iTMPlay_<group>, from 1).
    If asGroup == "idle" || asGroup == "Idle"
      Debug.SendAnimationEvent(a, "TMPlayStop")
      Return
    EndIf
    Int k = a.GetAnimationVariableInt("iTMPlay_" + asGroup)
    If k <= 0
      Return
    EndIf
    If aiMode == 0 && a.GetAnimationVariableBool("bTMScripted")
      a.SetAnimationVariableInt("iTMQueuedLoops", aiLoops)
      a.SetAnimationVariableInt("iTMQueued", k)
      Return
    EndIf
    a.SetAnimationVariableInt("iTMLoops", aiLoops)
    If aiMode == 2
      Debug.SendAnimationEvent(a, "TMPlayLoop_" + asGroup)
    Else
      Debug.SendAnimationEvent(a, "TMPlay_" + asGroup)
    EndIf
    Return
  EndIf
  If a
    If asGroup == "knockout"
      Debug.SendAnimationEvent(a, "BleedOutStart")
    ElseIf asGroup == "death1" || asGroup == "death2" || asGroup == "death3" || asGroup == "death4" || asGroup == "death5" || asGroup == "deathknockdown" || asGroup == "deathknockout" || asGroup == "swimdeath" || asGroup == "swimdeath2" || asGroup == "swimdeath3" || asGroup == "swimdeathknockdown" || asGroup == "swimdeathknockout"
      ; A death group (the Black Dart gang drowned: SetHealth 0, PlayGroup Death1): dead and fallen, as Skyrim's people fall.
      If !a.IsDead()
        a.Kill()
      EndIf
    ElseIf asGroup == "idle" || asGroup == "Idle"
      Debug.SendAnimationEvent(a, "IdleForceDefaultState")
    EndIf
    Return
  EndIf
  ; An object's own animation groups (banners, flags, machinery) aren't converted: Morrowind's models come over without
  ; their keyframes (MwMeshConverter writes no controller manager), so PlayGamebryoAnimation could only fail, and it did,
  ; many times a second (2026-11-02: 1,938 "has no animation controller manager" from MW_OutsideBanner in twelve minutes).
  ; docs/script-gaps.md.
EndFunction

; ChangeWeather region type: that weather now.
; Disable/EnablePlayerMagic: the perk that puts every spell's cost out of reach, had or not.
Function PlayerMagic(Perk akNoMagic, Bool abEnable) Global
  Actor player = Game.GetPlayer()
  If !akNoMagic
    Return
  EndIf
  If abEnable
    player.RemovePerk(akNoMagic)
  ElseIf !player.HasPerk(akNoMagic)
    player.AddPerk(akNoMagic)
    player.InterruptCast()
  EndIf
EndFunction

Function ChangeWeather(Weather akWeather) Global
  If akWeather
    akWeather.ForceActive(False)
  EndIf
EndFunction

; GetCurrentWeather: Morrowind's numbers (0 clear, 1 cloudy, 2 foggy, 3 overcast, 4 rain, 5 thunder, 6 ash, 7 blight,
; 8 snow, 9 blizzard) from Skyrim's classification.
Int Function CurrentWeather() Global
  Weather w = Weather.GetCurrentWeather()
  If !w
    Return 0
  EndIf
  Int c = w.GetClassification()
  If c == 1
    Return 1
  ElseIf c == 2
    Return 4
  ElseIf c == 3
    Return 8
  EndIf
  Return 0
EndFunction

; GetCurrentWeather on Morrowind's own weathers (akWeathers: MWWeatherList, in Morrowind's numbering): the current
; weather's place in it; Skyrim's classification for a weather not Morrowind's (another world's).
Int Function CurrentWeatherIn(FormList akWeathers) Global
  Weather w = Weather.GetCurrentWeather()
  If w && akWeathers
    Int i = akWeathers.Find(w)
    If i >= 0
      Return i
    EndIf
  EndIf
  Return CurrentWeather()
EndFunction

; A number with this many decimals, for message boxes (%.2f).
String Function Fixed(Float afValue, Int aiDecimals) Global
  Float scale = Math.Pow(10.0, aiDecimals)
  Float rounded = Math.Floor(afValue * scale + 0.5) / scale
  String s = rounded as String
  Int dot = StringUtil.Find(s, ".")
  If dot >= 0 && aiDecimals >= 0
    If aiDecimals == 0
      Return StringUtil.Substring(s, 0, dot)
    EndIf
    Return StringUtil.Substring(s, 0, dot + 1 + aiDecimals)
  EndIf
  Return s
EndFunction
