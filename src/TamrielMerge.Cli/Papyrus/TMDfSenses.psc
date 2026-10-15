ScriptName TMDfSenses extends Actor
{Daggerfall's enemy senses on one of Daggerfall's hostile actors (docs/daggerfall-senses.md; user, 2026-10-15:
"daggerfall has a enemy detection system that needs in for the daggerfall part only"), as Daggerfall Unity's
EnemySenses and EnemyMotor (MIT) decide them:

- Until it has noticed the player it doesn't fight: its Aggression and Assistance are 0, so Skyrim's own detection
  can't start a fight (Skyrim's detection settings stay as they are; nothing else is touched).
- It notices the player by sight (within 4096 classic units, a 180 degree field of view, a clear line from its eyes),
  by hearing once it has noticed (within 25 m), or by Daggerfall's Stealth check (Skyrim's Sneak): once a game minute
  while the player is within 1024 classic units and in the area classic Daggerfall would have it spawned in, the player
  stays unnoticed with a 2 x ((distance x Stealth) >> 10) percent chance; sneaking or standing still, the check is made
  only on even minutes; moving faster, an enemy that has met the player before always knows where the player is.
  Invisibility hides the player from all but the monsters that see through it.
- Noticed, it fights (its own Aggression and Assistance back, StartCombat). Struck by the player (or the player's
  followers), it turns on them at once.
- It keeps after the player while it notices them; once it hasn't for 200 classic updates (12.5 seconds) it gives up
  (StopCombat) and is unaware again. Skyrim's combat AI does the chasing and the searching in that time (it heads for
  where the player was last seen).
Distances are classic units x 2 (BlockMeshBuilder.Scale: Daggerfall's dungeons and towns are built at 2 Skyrim units a
classic unit, 80 a metre). Only in the Iliac Bay (TMDfSensesControl): elsewhere it has Skyrim's own senses.}

TMDfSensesControl Property Senses Auto
{The control quest (TMDfSensesQuest): the player's state, the loaded enemies.}
Bool Property SeesInvisible = False Auto
{Daggerfall's monsters that see through invisibility (EnemyBasics.SeesThroughInvisibility).}

; ---- Daggerfall Unity's numbers in Skyrim units (classic units x 2) ----
Float SightRange = 8192.0       ; SightRadius: 4096 classic units (102.4 m)
Float HalfView = 90.0           ; FieldOfView 180 (classic's)
Float HearingRange = 2000.0     ; HearingRadius 25 m (1000 classic units)
Float StealthRange = 2048.0     ; StealthCheck: within 1024 classic units
Float SpawnRange = 2188.0       ; classic spawn: nothing beyond 1094 classic units
Float SpawnXZ = 2048.0          ; inside, spawn distance type 0: 1024 across...
Float SpawnY = 256.0            ; ...and 128 up or down to spawn...
Float DespawnY = 768.0          ; ...384 to despawn
Float RestRange = 960.0         ; AreEnemiesNearby(resting): an unaware enemy within 12 m
Float GiveUpSeconds = 12.5      ; GiveUpTimer: 200 classic updates of 1/16 s

Bool begun = False
Bool hostile = False
Bool active = False             ; Daggerfall's senses in use (the player in the Iliac Bay)
Bool aware = False              ; it has noticed the player and fights
Bool detected = False           ; EnemySenses.DetectedTarget
Bool encountered = False        ; HasEncounteredPlayer
Bool spawned = False            ; WouldBeSpawnedInClassic
Int lastCheck = -1              ; the game minute of its last Stealth check
Float aggression = 1.0
Float assistance = 0.0
Float lastDetected = 0.0
Float lastPoll = 0.0
Float stoppedAt = -100.0

Event OnLoad()
  Begin()
EndEvent

Event OnUnload()
  UnregisterForUpdate()
  If Senses
    Senses.Unregister(Self)
  EndIf
EndEvent

Event OnDeath(Actor akKiller)
  UnregisterForUpdate()
  If Senses
    Senses.Unregister(Self)
  EndIf
EndEvent

; Its own Aggression and Assistance, read once; one that isn't aggressive (a passive one) is left as it is.
Function Begin()
  If !Senses || IsDead()
    Return
  EndIf
  If !begun
    begun = True
    aggression = GetActorValue("Aggression")
    assistance = GetActorValue("Assistance")
    hostile = aggression > 0.0
  EndIf
  If !hostile
    Return
  EndIf
  Senses.Register(Self)
  RegisterForSingleUpdate(Utility.RandomFloat(0.1, 0.6))
EndFunction

Event OnUpdate()
  If !hostile || IsDead() || !Is3DLoaded()
    Senses.Unregister(Self)
    Return
  EndIf
  Int st = Senses.Status()
  If st % 2 == 0
    ; Not in the Iliac Bay: Skyrim's own senses (and not counted among Daggerfall's enemies).
    Release()
    Senses.Unregister(Self)
    RegisterForSingleUpdate(5.0)
    Return
  EndIf
  Actor player = Senses.PlayerRef
  If !active
    active = True
    Senses.Register(Self)
    If !aware && GetCombatTarget() == player
      ; Already fighting the player (struck before its first look): it knows.
      Noticed()
      Engage(player)
    ElseIf !aware
      Unaware()
    EndIf
  EndIf
  Float d = GetDistance(player)
  Sense(player, d, (st / 2) % 2 == 1, (st / 4) % 2 == 1, (st / 8) % 2 == 1)
  RegisterForSingleUpdate(Interval(d))
EndEvent

; Polled often near the player, rarely far off (the time the player needs to come within sight at a run).
Float Function Interval(Float d)
  If aware || d < SpawnRange
    Return 0.5
  ElseIf d < SightRange * 0.5
    Return 1.0
  ElseIf d < SightRange
    Return 1.5
  EndIf
  Float t = (d - SightRange) / 700.0
  If t < 1.5
    Return 1.5
  ElseIf t > 10.0
    Return 10.0
  EndIf
  Return t
EndFunction

; EnemySenses.FixedUpdate for the player as its target.
Function Sense(Actor player, Float d, Bool slow, Bool inside, Bool invisible)
  Float now = Utility.GetCurrentRealTime()
  If now < lastPoll
    ; The game was started again (real time counts from launch).
    lastDetected = now
    stoppedAt = -100.0
  EndIf
  lastPoll = now
  spawned = Spawned(player, d, inside)
  Bool blocked = invisible && !SeesInvisible
  Bool sees = Sees(player, d)
  Bool hears = False
  If detected && !sees
    hears = Hears(player, d)
  EndIf
  If !blocked && (sees || hears)
    detected = True
  ElseIf !blocked && StealthCheck(player, d, slow)
    detected = True
  Else
    detected = False
  EndIf
  If detected
    encountered = True
    lastDetected = now
  EndIf
  If !aware
    If detected
      Engage(player)
    EndIf
  ElseIf detected
    If !IsInCombat()
      ; Skyrim lost the player where Daggerfall's senses haven't.
      StartCombat(player)
    EndIf
  ElseIf now - lastDetected > GiveUpSeconds
    GiveUp()
  EndIf
EndFunction

; CanSeeTarget: within sight, inside the field of view, a clear line from its eyes.
Bool Function Sees(Actor player, Float d)
  If d >= SightRange
    Return False
  EndIf
  Float a = GetHeadingAngle(player)
  If a >= HalfView || a <= -HalfView
    Return False
  EndIf
  Return HasLOS(player)
EndFunction

; CanHearTarget: within hearing and nothing solid between (Papyrus has no ray through doors: a clear line from its eyes,
; or, behind it, Skyrim's own detection, which hears through what Skyrim's sound passes).
Bool Function Hears(Actor player, Float d)
  If d >= HearingRange
    Return False
  EndIf
  Return HasLOS(player) || player.IsDetectedBy(Self)
EndFunction

; WouldBeSpawnedInClassic (spawn distance type 0): within 1094 classic units; inside, 1024 across and 128 up or down
; (384 once spawned); outside, the 1094.
Bool Function Spawned(Actor player, Float d, Bool inside)
  If d >= SpawnRange
    Return False
  ElseIf !inside
    Return True
  EndIf
  Float dz = Math.abs(GetPositionZ() - player.GetPositionZ())
  Float across = d * d - dz * dz
  If across > SpawnXZ * SpawnXZ
    Return False
  ElseIf spawned
    Return dz <= DespawnY
  EndIf
  Return dz <= SpawnY
EndFunction

; StealthCheck and FormulaHelper.CalculateStealthChance: once a game minute; the player stays unnoticed with
; 2 x ((distance in classic units x Stealth) >> 10) percent.
Bool Function StealthCheck(Actor player, Float d, Bool slow)
  If !spawned || d > StealthRange
    Return False
  EndIf
  Int minute = Math.Floor(Utility.GetCurrentGameTime() * 1440.0)
  If minute == lastCheck
    Return detected
  EndIf
  If slow
    If minute % 2 == 1
      Return detected
    EndIf
  ElseIf encountered
    Return True
  EndIf
  Senses.StealthUsed(minute)
  lastCheck = minute
  Int units = (d / 2.0) as Int
  Int chance = 2 * ((units * (player.GetActorValue("Sneak") as Int)) / 1024)
  Return Utility.RandomInt(0, 99) >= chance
EndFunction

; Unaware: Skyrim's detection can't start a fight.
Function Unaware()
  SetActorValue("Aggression", 0.0)
  SetActorValue("Assistance", 0.0)
EndFunction

Function Engage(Actor player)
  aware = True
  SetActorValue("Aggression", aggression)
  SetActorValue("Assistance", assistance)
  StartCombat(player)
EndFunction

; GiveUpTimer run out: back to unaware.
Function GiveUp()
  aware = False
  detected = False
  stoppedAt = Utility.GetCurrentRealTime()
  Unaware()
  StopCombat()
EndFunction

; Skyrim's own senses (outside the Iliac Bay).
Function Release()
  If active
    active = False
    aware = False
    detected = False
    SetActorValue("Aggression", aggression)
    SetActorValue("Assistance", assistance)
  EndIf
EndFunction

; MakeEnemyHostileToAttacker: struck by the player (or the player's followers), it knows where they are.
Event OnHit(ObjectReference akAggressor, Form akSource, Projectile akProjectile, Bool abPowerAttack, Bool abSneakAttack, Bool abBashAttack, Bool abHitBlocked)
  If !active || aware || IsDead()
    Return
  EndIf
  Actor by = akAggressor as Actor
  If by && (by == Senses.PlayerRef || by.IsPlayerTeammate())
    Noticed()
    Engage(Senses.PlayerRef)
  EndIf
EndEvent

; A fight with the player that Daggerfall's senses didn't start (a spell, a quest, its allies' cries) counts as being
; struck; one Skyrim picks just after it gave up is stopped again.
Event OnCombatStateChanged(Actor akTarget, Int aeCombatState)
  If !active || aware || aeCombatState == 0 || !akTarget || IsDead()
    Return
  EndIf
  If akTarget != Senses.PlayerRef && !akTarget.IsPlayerTeammate()
    Return
  EndIf
  If stoppedAt >= 0.0 && Utility.GetCurrentRealTime() - stoppedAt < 3.0
    StopCombat()
    Return
  EndIf
  Noticed()
  Engage(Senses.PlayerRef)
EndEvent

Function Noticed()
  detected = True
  encountered = True
  lastDetected = Utility.GetCurrentRealTime()
EndFunction

; GameManager.AreEnemiesNearby for this enemy: it sees the player, or the player is where classic would have it spawned
; (resting: an unaware one only within 12 m).
Bool Function NearPlayer(Bool resting)
  If !hostile || !active || IsDead() || !Is3DLoaded()
    Return False
  EndIf
  Actor player = Senses.PlayerRef
  Float d = GetDistance(player)
  Bool sees = Sees(player, d)
  If resting && !sees && d > RestRange
    Return False
  EndIf
  Return sees || Spawned(player, d, player.IsInInterior())
EndFunction
