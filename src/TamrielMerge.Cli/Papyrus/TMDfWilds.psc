ScriptName TMDfWilds extends Quest
{Daggerfall's random encounters outdoors (Daggerfall Unity PlayerEntity.IntermittentEnemySpawn, RandomEncounters.
ChooseRandomEnemy, FormulaHelper.RollRandomSpawn_*; MIT), on the Iliac Bay's overland (user, 2026-10-27: "the overland
with the open towns, and everything else on it"):
- In the first 12 minutes of every 144 (classic minute / 12 mod 12 = 0) a spawn is rolled each minute until one comes:
  1 in 36 a minute by day in the wild, 1 in 24 at night, 1 in 24 at night in a town (never by day there). Here the
  roll is made once a block, at the odds of those twelve minutes together (1 - (35/36)^12 = 29%; 1 - (23/24)^12 = 40%).
- One enemy from the climate's table (desert, mountain, rainforest, subtropical, swamp and the woods; haunted
  woodlands count as the woods here: the map's travel squares hold the six travel terrains), in or out of a town, day
  (6:00 to 18:00) or night, by the player's level (ChooseRandomEnemy's window: the leveled list), placed out of sight
  behind the player 10 m and more away (FoeSpawner), as the quests' foes are.
- Not while swimming, not after a jump in time (PreventSpawns: a journey, jail, a vampire's turning), not indoors.}

Actor Property PlayerRef Auto
Quest Property Climate Auto
{TMTravelClimate: Daggerfall's map in squares, "O" the sea, else the terrain 0-5.}
Float Property MapWestX Auto
Float Property MapNorthY Auto
Float Property UnitsPerPixel = 2048.0 Auto
Location Property IliacBay Auto
ActorBase[] Property Tables Auto
{The encounter tables' actors (leveled), Daggerfall's tables 20 to 37 in order: a climate's "in location, night",
"not in location, day", "not in location, night" for the desert, the mountains, the rainforest, the subtropics, the
swamp and woods, the haunted woods; None where the build has none.}
GlobalVariable Property PreventSpawns Auto
{Set to 1 by whatever moves time on (a journey, jail, the vampire's turning): no spawn for that block.}
String[] Property RegionRows Auto
{Daggerfall's political map (POLITIC.PAK) in squares of RegionStep pixels: "." the sea, else the region as a character
48 + its index (RegionHere: where a vampire was bitten).}
Int Property RegionStep = 2 Auto

Int Property BlockMinutes = 144 AutoReadOnly
Float Property TickHours = 0.2 AutoReadOnly   ; 12 game minutes
Float Property DayChance = 0.2873 AutoReadOnly     ; 1 - (35/36)^12
Float Property NightChance = 0.4006 AutoReadOnly   ; 1 - (23/24)^12

Int lastBlock = -1

Event OnInit()
  RegisterForUpdateGameTime(TickHours)
EndEvent

; Classic minutes since the game's epoch: Skyrim's days and hours.
Int Function ClassicMinutes()
  Float days = Utility.GetCurrentGameTime()
  Return (days * 1440.0) as Int
EndFunction

Event OnUpdateGameTime()
  Int minutes = ClassicMinutes()
  Int block = minutes / BlockMinutes
  If block == lastBlock
    Return
  EndIf
  Bool fresh = lastBlock >= 0
  lastBlock = block
  If !fresh
    Return
  EndIf
  If PreventSpawns && PreventSpawns.GetValueInt() != 0
    PreventSpawns.SetValueInt(0)
    Return
  EndIf
  ; Only in the window's own minutes (the first twelve of the block), outdoors on the Iliac Bay, not swimming.
  If minutes % BlockMinutes >= 12 || PlayerRef.IsInInterior() || PlayerRef.IsSwimming() || PlayerRef.IsDead()
    Return
  EndIf
  If !IliacBay || !PlayerRef.IsInLocation(IliacBay)
    Return
  EndIf
  Int hour = (minutes % 1440) / 60
  Bool day = hour >= 6 && hour < 18
  ; In a town (its cells carry the town's location, a child of the Iliac Bay's): at night only.
  Location here = PlayerRef.GetCurrentLocation()
  Bool inTown = here && here != IliacBay
  If inTown && day
    Return
  EndIf
  Float chance = NightChance
  If day
    chance = DayChance
  EndIf
  If Utility.RandomFloat(0.0, 1.0) >= chance
    Return
  EndIf
  Int terrain = TerrainHere()
  If terrain < 0
    Return
  EndIf
  ; Tables 20-37: three a climate, the climates in Daggerfall's order desert, mountain, rainforest, subtropical, woods
  ; (the swamp's table is the woods'); haunted woods as the woods.
  Int climateRow = terrain
  If terrain == 3
    climateRow = 4   ; swamp: the swamp/woodlands tables
  ElseIf terrain == 4
    climateRow = 3   ; subtropical
  EndIf
  Int index = climateRow * 3
  If !inTown
    index += 1
    If !day
      index += 1
    EndIf
  EndIf
  If index < 0 || index >= Tables.Length || !Tables[index]
    Return
  EndIf
  Spawn(Tables[index])
EndEvent

; The map pixel under the player: the whole map's units (the tile's origin added: TMDfOverland holds the tiles'
; origins), then pixels from the map's north-west corner.
Int Function PixelX()
  TMDfOverland overland = (Self as Quest) as TMDfOverland
  Float x = PlayerRef.GetPositionX()
  If overland
    Int t = overland.Tiles.Find(PlayerRef.GetWorldSpace())
    If t >= 0
      x += overland.OriginX[t]
    EndIf
  EndIf
  Return Math.Floor((x - MapWestX) / UnitsPerPixel)
EndFunction

Int Function PixelY()
  TMDfOverland overland = (Self as Quest) as TMDfOverland
  Float y = PlayerRef.GetPositionY()
  If overland
    Int t = overland.Tiles.Find(PlayerRef.GetWorldSpace())
    If t >= 0
      y += overland.OriginY[t]
    EndIf
  EndIf
  Return Math.Floor((MapNorthY - y) / UnitsPerPixel)
EndFunction

; The region under the player (PlayerGPS.CurrentRegionIndex: the political map), or -1 at sea, off the map or off the
; Iliac Bay.
Int Function RegionHere()
  If !RegionRows || RegionRows.Length == 0 || RegionStep <= 0 || !IliacBay || !PlayerRef.IsInLocation(IliacBay)
    Return -1
  EndIf
  Int ry = PixelY() / RegionStep
  Int rx = PixelX() / RegionStep
  If ry < 0 || ry >= RegionRows.Length || rx < 0
    Return -1
  EndIf
  String row = RegionRows[ry]
  If rx >= StringUtil.GetLength(row)
    Return -1
  EndIf
  String c = StringUtil.GetNthChar(row, rx)
  If c == "." || c == ""
    Return -1
  EndIf
  Return StringUtil.AsOrd(c) - 48
EndFunction

; The travel map's terrain under the player (0 desert, 1 mountain, 2 rainforest, 3 swamp, 4 subtropical, 5 woods), or -1
; at sea or off the map.
Int Function TerrainHere()
  TMTravelClimate map = Climate as TMTravelClimate
  If !map
    Return -1
  EndIf
  String c = map.At(PixelX(), PixelY())
  If c == "O" || c == ""
    Return -1
  EndIf
  Return c as Int
EndFunction

; One enemy out of sight behind the player, 10 m and more away (FoeSpawner's placement: outside the view, a way off).
; ActorBase: the tables hold the leveled actors' bases (NPC_ records); typed Actor, every element bound as None
; and no encounter ever spawned (a user's Papyrus log, 2026-11-02: "Element of property Tables ... cannot be bound").
Function Spawn(ActorBase base)
  ObjectReference a = PlayerRef.PlaceAtMe(base, 1, True, False)
  If !a
    Return
  EndIf
  Float angle = PlayerRef.GetAngleZ() + 180.0 + Utility.RandomFloat(-60.0, 60.0)
  Float d = Utility.RandomFloat(800.0, 2000.0)
  a.MoveTo(PlayerRef, d * Math.Sin(angle), d * Math.Cos(angle), 0.0)
EndFunction
