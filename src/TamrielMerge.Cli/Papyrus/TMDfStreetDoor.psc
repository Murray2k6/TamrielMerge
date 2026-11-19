ScriptName TMDfStreetDoor extends ObjectReference
{A Daggerfall town's building door. Buildings with the same layout share one interior, so the door remembers that the
player came in through it (and in which town) for the way back out (TMDfExitDoor), leaves this town's names for the
people inside (they take them when they load and on this door's TMDfEntered event, TMDfOccupant; SKSE SetDisplayName),
and takes the player in.
Buildings keep Daggerfall's hours (Daggerfall Unity PlayerActivate.openHours/closeHours, by building type); outside
them the door is locked (lock = quality / 2): sneaking tries to pick it with Daggerfall's exterior chance,
Lockpicking - 5 x lock (5-95%), once per skill level, and hitting it may bash it open (25 - lock in %).}

TMDfTalk Property Talk Auto
TMDfTalk Property Way Auto
{The conversation quest every shared interior reads the way back and its people's names from (Daggerfall.esm's, for
a plugin built in parts); the same as Talk in Daggerfall.esm itself.}
ObjectReference Property Arrival Auto
{The interior's anchor (its one persistent reference); the player is then set at ArrivalAt.}
Float[] Property ArrivalAt Auto
{Where this door's way in arrives inside: x, y, z and the heading in degrees.}
Int Property Town = -1 Auto
String[] Property Names Auto
{This town's names for the people inside, by their Index (they take them when they load: TMDfOccupant).}
Int Property BuildingType = 17 Auto
Int Property LockValue = 0 Auto
String Property BuildingName = "" Auto
{Daggerfall's name for the building ("The Dancing Griffin"), empty for houses; the quests name it so.}
String Property DoorName = "" Auto
{What the door shows (SKSE SetDisplayName, when it loads): the building's name, else as Daggerfall Unity calls it ("The
Gilmore Residence", "House for sale", "Temple"). Until 2026-11-04 every door was just "Door".}
GlobalVariable Property BreakIns Auto
{Shops broken into (the Thieves Guild takes notice, TMDfGuilds).}
GlobalVariable Property CrimeReport Auto
{A crime seen at the door (Daggerfall's crime number: 1 attempted breaking and entering, 3 breaking and entering), for
TMDfLaw to send the City Watch: a failed bash is noticed one time in ten, and so is a door bashed open (Daggerfall Unity
PlayerActivate).}
Int failedAtSkill = -1
Float brokenInUntil = 0.0

Int Function OpenHour(Int t)
  Int[] open = new Int[25]
  open[0] = 7
  open[1] = 8
  open[2] = 9
  open[3] = 8
  open[4] = 0
  open[5] = 9
  open[6] = 10
  open[7] = 10
  open[8] = 9
  open[9] = 6
  open[10] = 9
  open[11] = 11
  open[12] = 9
  open[13] = 9
  open[14] = 0
  open[15] = 0
  open[16] = 10
  open[17] = 0
  open[18] = 6
  open[19] = 6
  open[20] = 6
  open[21] = 6
  open[22] = 6
  open[23] = 6
  open[24] = 0
  If t < 0 || t > 24
    Return 0
  EndIf
  Return open[t]
EndFunction

Int Function CloseHour(Int t)
  Int[] close = new Int[25]
  close[0] = 22
  close[1] = 16
  close[2] = 19
  close[3] = 15
  close[4] = 25
  close[5] = 21
  close[6] = 19
  close[7] = 20
  close[8] = 18
  close[9] = 23
  close[10] = 23
  close[11] = 23
  close[12] = 20
  close[13] = 20
  close[14] = 25
  close[15] = 25
  close[16] = 16
  close[17] = 0
  close[18] = 18
  close[19] = 18
  close[20] = 18
  close[21] = 18
  close[22] = 18
  close[23] = 18
  close[24] = 25
  If t < 0 || t > 24
    Return 25
  EndIf
  Return close[t]
EndFunction

Bool Function IsOpen()
  Float now = Utility.GetCurrentGameTime()
  If now < brokenInUntil
    Return True
  EndIf
  Int hour = ((now - Math.Floor(now)) * 24.0) as Int
  Return OpenHour(BuildingType) <= hour && CloseHour(BuildingType) > hour
EndFunction

Event OnLoad()
  If DoorName != "" && SKSE.GetVersion() > 0
    SetDisplayName(DoorName, True)
  EndIf
EndEvent

; Within the building's hours (not counting a picked lock).
Bool Function InHours()
  Float now = Utility.GetCurrentGameTime()
  Int hour = ((now - Math.Floor(now)) * 24.0) as Int
  Return OpenHour(BuildingType) <= hour && CloseHour(BuildingType) > hour
EndFunction

Event OnActivate(ObjectReference akActionRef)
  Actor player = Game.GetPlayer()
  If akActionRef != player
    Return
  EndIf
  ; Come in by its hours, the building is open to the public (Skyrim's trespass doesn't apply); picked open, it isn't
  ; (2026-11-04, user: "npcs kick you out of certain ones that you are suppose to go in": the shops', bank's and houses'
  ; interiors were owned and never public, so their people warned the player off and the Watch came).
  Bool legal = InHours()
  If !IsOpen()
    If !player.IsSneaking()
      Debug.Notification("This building is closed. It is open from " + OpenHour(BuildingType) + ":00 to " + CloseHour(BuildingType) + ":00.")
      Return
    EndIf
    Int skill = player.GetActorValue("Lockpicking") as Int
    If skill <= failedAtSkill
      Debug.Notification("You can't pick this lock until your skill improves.")
      Return
    EndIf
    Int chance = skill - 5 * LockValue
    If chance < 5
      chance = 5
    ElseIf chance > 95
      chance = 95
    EndIf
    Game.AdvanceSkill("Lockpicking", 2.0)
    If Utility.RandomInt(1, 100) > chance
      Debug.Notification("You fail to pick the lock.")
      failedAtSkill = skill
      Return
    EndIf
    Debug.Notification("You pick the lock.")
    If BreakIns && (BuildingType < 17 || BuildingType > 23)
      BreakIns.Mod(1)
    EndIf
    brokenInUntil = Utility.GetCurrentGameTime() + 0.1
  EndIf
  Talk.LastDoor = Self
  Talk.LastTown = Town
  If Way
    Way.LastDoor = Self
    Way.LastTown = Town
    Way.CurrentNames = Names
    If Arrival
      Way.CurrentCell = Arrival.GetParentCell()
    EndIf
  EndIf
  If Arrival
    Cell inside = Arrival.GetParentCell()
    If inside
      inside.SetPublic(legal)
    EndIf
    player.MoveTo(Arrival)
    If ArrivalAt && ArrivalAt.Length >= 4
      player.SetPosition(ArrivalAt[0], ArrivalAt[1], ArrivalAt[2])
      player.SetAngle(0.0, 0.0, ArrivalAt[3])
    EndIf
    ; The people inside become this building's (TMDfOccupant), also when the interior was still loaded from another
    ; town's building of the same layout and none of them loads again.
    If Way && SKSE.GetVersion() > 0
      SendModEvent("TMDfEntered")
    EndIf
  EndIf
EndEvent

Event OnHit(ObjectReference akAggressor, Form akSource, Projectile akProjectile, Bool abPowerAttack, Bool abSneakAttack, Bool abBashAttack, Bool abHitBlocked)
  If akAggressor != Game.GetPlayer() || IsOpen() || !(akSource as Weapon)
    Return
  EndIf
  If Utility.RandomInt(1, 100) <= 25 - LockValue
    Debug.Notification("You bash the door open.")
    brokenInUntil = Utility.GetCurrentGameTime() + 0.1
    Report(3)
  Else
    Report(1)
  EndIf
EndEvent

; One time in ten someone notices (Dice100.SuccessRoll(10)).
Function Report(Int crime)
  If CrimeReport && Utility.RandomInt(1, 100) <= 10
    CrimeReport.SetValue(crime)
  EndIf
EndFunction
