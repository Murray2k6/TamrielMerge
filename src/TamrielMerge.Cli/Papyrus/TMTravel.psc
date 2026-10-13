ScriptName TMTravel extends Quest
{Daggerfall's travel in every converted world (TamrielMerge Travel.esp, docs/travel.md; user, 2026-10-12: "i need the
travel like daggerfalls, like it can take days to get to a town", in every world of the user's games, seen "realtime
seeing yourself travel without loading", speed adjustable, encounters stopping it, ships sailing in real time).

The Tamriel menu's Travel tab lists the worlds (TMTravelWorld), then a world's regions, then a region's towns. Picking
a town shows Daggerfall's trip: its options (cautiously or recklessly, inns or camping, by ship, on the horse), the
days it takes and its price, and Begin. In the world the player is in, the journey is made in real time: the player is
walked there by the game's own pathing (on the horse if riding one), the clock running so that Daggerfall's time has
passed on arrival, at the pace the speed slider sets (1 the natural pace, 10 the fastest the land can be loaded at:
uGridsToLoad bounds it). Across the sea (a town on another landmass, or by ship when chosen) the player walks to the
nearest harbour, sails to the one nearest the town on a ship, and walks on. A fight (or pressing a movement key) stops
the journey where it is; Continue picks it up. Arriving cautiously, the traveller is rested and arrives by day. A town in
another time already visited is reached through time (TMStoryOrder), as the Travel Through Time power goes. Skyrim's
own map travel within a world takes Daggerfall's time too.

The menu sends mod event TM_Travel: "worlds"; "world:w"; "group:w:g"; "pick:w:g:t"; "opt:cautious|inn|ship|horse";
"speed" (number 1-10); "go"; "continue". It gets ClearTravel / AddTravel(line) / ShowTravel(title) / TravelStatus(text)
/ TripInfo(line) (see tamriel.as).}

Actor Property PlayerRef Auto
GlobalVariable Property GameHour Auto
GlobalVariable Property TimeScale Auto
MiscObject Property Gold Auto
Quest Property StoryOrderQuest Auto
{MQ101, with TMStoryOrder (TamrielMerge Start.esp).}
TMTravelWorld[] Property Worlds Auto
ObjectReference Property Ship Auto
{The ship that sails (disabled until a voyage).}
ObjectReference Property Waypoint Auto
{A marker the walk heads for.}
Float Property ShipWaterZ = -250.0 Auto
{Where the ship's origin rides (ShipLarge01: hull from 0, so it sits 250 low in the water).}
Float Property ShipDeckZ = 330.0 Auto
{The player's height on its deck (its planking 560-590 above the origin).}
Float Property ShipSpeed = 600.0 Auto
{The ship's speed at the natural pace (units a second).}
Float Property CellsPerSecond = 1.5 Auto
{The fastest the land is crossed at uGridsToLoad 5 (cells a second); more grids loaded, proportionally slower.}
Float Property RunSpeed = 380.0 Auto
{The player's running speed at 100% (units a second).}
String Property MenuName = "CustomMenu" AutoReadOnly

; ---- options ----
Bool cautious = True
Bool inn = True
Bool useShip = True
Bool useHorse = True
Int speed = 5

; ---- the town picked in the menu ----
Int pickWorld = -1
Int pickGroup = -1
Int pickTown = -1

; ---- where the player last stood outdoors (for travel from indoors, and Skyrim's map travel) ----
Int lastWorld = -1
Float lastX
Float lastY
Int jumpSeen = 0

; ---- the journey ----
Bool travelling = False
Bool paused = False
Int jWorld = -1
ObjectReference jDestination
String jName
Float jMinutes          ; Daggerfall's minutes for the whole trip
Float jStartTime        ; game days when it began
Float jPassed           ; game minutes passed on earlier stretches (before a stop)
Int jLeg                ; 0 walking to the harbour or the town, 1 sailing, 2 walking from the harbour
Int jFromPort = -1
Int jToPort = -1
String[] jVoyage
Int jStop
Float jSavedScale = 20.0
Float jSavedMult = 100.0
Float jLastX
Float jLastY
Float jLastClock
Float jSpeed            ; measured, units a second
Int jStuck
Float fastest           ; units a second uGridsToLoad allows

Event OnInit()
  Setup()
  RegisterForSingleUpdate(2.0)
EndEvent

Function Setup()
  RegisterForModEvent("TM_Travel", "OnTravel")
EndFunction

TMStoryOrder Function StoryOrder()
  Return StoryOrderQuest as TMStoryOrder
EndFunction

; The world (index in Worlds) the player is outdoors in, or -1.
Int Function CurrentWorld()
  WorldSpace here = PlayerRef.GetWorldSpace()
  Int i = 0
  While i < Worlds.Length
    If Worlds[i] && Worlds[i].Has(here)
      Return i
    EndIf
    i += 1
  EndWhile
  Return -1
EndFunction

; A time's era and year ("3E 427"), labelling a destination with it.
String Function Era(Int story)
  TMStoryOrder so = StoryOrder()
  If so
    Return so.EraLabel(story)
  EndIf
  Return ""
EndFunction

; The time the player is in (TMStoryOrder), Skyrim's when there is none.
Int Function TimeHere()
  TMStoryOrder so = StoryOrder()
  If so
    Return so.MoneyHere()
  EndIf
  Return 4
EndFunction

; ---- the clock ----

; Passes game hours, a day at most at a time (the calendar rolls over as it goes).
Function PassHours(Float hours)
  Float left = hours
  While left > 0.0
    Float stepH = left
    If stepH > 20.0
      stepH = 20.0
    EndIf
    GameHour.SetValue(GameHour.GetValue() + stepH)
    left -= stepH
    Utility.Wait(0.05)
  EndWhile
EndFunction

String Function Duration(Float minutes)
  Int h = Math.Ceiling(minutes / 60.0)
  If h < 24
    Return "" + h + " hours"
  EndIf
  Int days = h / 24
  If h % 24 > 0
    days += 1
  EndIf
  Return "" + days + " days"
EndFunction

; ---- keeping track ----

Event OnUpdate()
  If travelling
    Watch()
    RegisterForSingleUpdate(0.5)
    Return
  EndIf
  Track()
  RegisterForSingleUpdate(2.0)
EndEvent

; Where the player stands outdoors; a jump of more than 10 cells (map travel) waits a moment for Skyrim's travel
; event, which prices it from where the player was.
Function Track()
  Int w = CurrentWorld()
  If w < 0
    Return
  EndIf
  Float x = PlayerRef.GetPositionX()
  Float y = PlayerRef.GetPositionY()
  If lastWorld == w && jumpSeen < 2
    Float dx = x - lastX
    Float dy = y - lastY
    If dx * dx + dy * dy > 40960.0 * 40960.0
      jumpSeen += 1
      Return
    EndIf
  EndIf
  lastWorld = w
  lastX = x
  lastY = y
  jumpSeen = 0
EndFunction

; Skyrim's map travel ended (TMTravelPlayer): Daggerfall's time for the way, less what Skyrim passed.
Function FastTravelled(Float skyrimHours)
  Int w = CurrentWorld()
  If w >= 0 && lastWorld == w && !travelling
    Float x = PlayerRef.GetPositionX()
    Float y = PlayerRef.GetPositionY()
    Float minutes = Worlds[w].Minutes(lastX, lastY, x, y, cautious, inn, False, PlayerRef.IsOnMount()) as Float
    Float extra = minutes / 60.0 - skyrimHours
    If extra > 0.0
      PassHours(extra)
      Debug.Notification("The road took " + Duration(minutes) + ".")
    EndIf
  EndIf
  jumpSeen = 2
  Track()
EndFunction

; ---- the menu ----

Event OnTravel(String eventName, String what, Float number, Form sender)
  String[] p = StringUtil.Split(what, ":")
  If p.Length == 0
    Return
  EndIf
  String verb = p[0]
  If verb == "worlds"
    ListWorlds()
  ElseIf verb == "world" && p.Length >= 2
    ListGroups(p[1] as Int)
  ElseIf verb == "group" && p.Length >= 3
    ListTowns(p[1] as Int, p[2] as Int)
  ElseIf verb == "pick" && p.Length >= 4
    pickWorld = p[1] as Int
    pickGroup = p[2] as Int
    pickTown = p[3] as Int
    ShowTrip()
  ElseIf verb == "opt" && p.Length >= 2
    If p[1] == "cautious"
      cautious = !cautious
    ElseIf p[1] == "inn"
      inn = !inn
    ElseIf p[1] == "ship"
      useShip = !useShip
    ElseIf p[1] == "horse"
      useHorse = !useHorse
    EndIf
    ShowTrip()
  ElseIf verb == "speed"
    speed = number as Int
    If speed < 1
      speed = 1
    ElseIf speed > 10
      speed = 10
    EndIf
    ShowTrip()
  ElseIf verb == "go"
    Begin()
  ElseIf verb == "continue"
    Resume()
  EndIf
EndEvent

Function Invoke(String fn, String arg)
  UI.InvokeString(MenuName, "_root." + fn, arg)
EndFunction

Function ListWorlds()
  Invoke("ClearTravel", "")
  Int here = TimeHere()
  TMStoryOrder so = StoryOrder()
  Int i = 0
  While i < Worlds.Length
    If Worlds[i]
      String status = "another time, not begun yet"
      If Worlds[i].Story == here
        status = "this time"
      ElseIf so && so.HasVisited(Worlds[i].Story)
        status = "another time"
      EndIf
      String era = Era(Worlds[i].Story)
      If era != ""
        status = era + ", " + status
      EndIf
      Invoke("AddTravel", "w|" + i + "|" + Worlds[i].WorldName + "|" + status)
    EndIf
    i += 1
  EndWhile
  If paused && jDestination
    Invoke("TravelStatus", "Your journey to " + jName + " was stopped. Continue it from its town's page, or choose anew.")
  EndIf
  Invoke("ShowTravel", "Worlds")
EndFunction

Function ListGroups(Int w)
  If w < 0 || w >= Worlds.Length || !Worlds[w]
    Return
  EndIf
  TMTravelWorld world = Worlds[w]
  Invoke("ClearTravel", "")
  Int g = 0
  While g < world.GroupNames.Length
    Invoke("AddTravel", "g|" + w + ":" + g + "|" + world.GroupNames[g] + "|" + world.TownCount(g))
    g += 1
  EndWhile
  Invoke("ShowTravel", world.WorldName + "  (" + Era(world.Story) + ")")
EndFunction

; Where a trip in world w starts: where the player stands, or last stood outdoors there.
Bool Function StartPoint(Int w)
  If CurrentWorld() == w
    lastWorld = w
    lastX = PlayerRef.GetPositionX()
    lastY = PlayerRef.GetPositionY()
  EndIf
  Return lastWorld == w
EndFunction

; Towns with roughly how long the road is (straight, the woods' pace; the trip shows Daggerfall's own on picking).
Function ListTowns(Int w, Int g)
  If w < 0 || w >= Worlds.Length || !Worlds[w]
    Return
  EndIf
  TMTravelWorld world = Worlds[w]
  Bool known = StartPoint(w)
  Invoke("ClearTravel", "")
  Int n = world.TownCount(g)
  String[] names = StringUtil.Split(world.GroupTowns[g], "|")
  Float perPixel = 104.0
  If !inn
    perPixel = perPixel * 300.0 / 256.0
  EndIf
  If !cautious
    perPixel = perPixel / 2.0
  EndIf
  If useHorse && HasHorse()
    perPixel = perPixel / 2.0
  EndIf
  Int t = 0
  While t < n && t < names.Length
    ObjectReference m = world.TownMarker(g, t)
    String time = ""
    If m && known && world.Story == TimeHere()
      Float dx = (m.GetPositionX() - lastX) / world.UnitsPerPixel
      Float dy = (m.GetPositionY() - lastY) / world.UnitsPerPixel
      Float pixels = Math.Sqrt(dx * dx + dy * dy)
      time = "about " + Duration(pixels * perPixel)
    EndIf
    Invoke("AddTravel", "t|" + w + ":" + g + ":" + t + "|" + names[t] + "|" + time)
    t += 1
  EndWhile
  Invoke("ShowTravel", world.GroupNames[g] + ", " + world.WorldName + "  (" + Era(world.Story) + ")")
EndFunction

Bool Function HasHorse()
  If PlayerRef.IsOnMount()
    Return True
  EndIf
  Actor horse = Game.GetPlayersLastRiddenHorse()
  Return horse && !horse.IsDead() && horse.GetWorldSpace() == PlayerRef.GetWorldSpace() && horse.GetDistance(PlayerRef) < 4096.0
EndFunction

; ---- the trip ----

; Plans the trip to the picked town: whether by ship (and which harbours), its minutes and price.
Int planMinutes
Int planCost
Bool planShip
Bool planPossible
String planNote

Function Plan(TMTravelWorld world, ObjectReference dest)
  planPossible = True
  planNote = ""
  planShip = False
  jFromPort = -1
  jToPort = -1
  Bool horse = useHorse && HasHorse()
  Bool sameLand = True
  If world.Climate
    String a = world.Climate.LandAt(world.PixelX(lastX), world.PixelY(lastY))
    String b = world.Climate.LandAt(world.PixelX(dest.GetPositionX()), world.PixelY(dest.GetPositionY()))
    sameLand = a == b || a == "." || b == "."
  EndIf
  ; Priced first as Daggerfall does (straight across, the sea by ship when chosen).
  planMinutes = world.Minutes(lastX, lastY, dest.GetPositionX(), dest.GetPositionY(), cautious, inn, useShip, horse)
  Bool crossesSea = world.OceanPixels > 0
  If (!sameLand || (useShip && crossesSea)) && world.PortCount() > 0
    If !useShip
      planPossible = False
      planNote = "Across the sea: only by ship."
    Else
      jFromPort = NearestPortOn(world, lastX, lastY)
      jToPort = NearestPortOn(world, dest.GetPositionX(), dest.GetPositionY())
      If jFromPort < 0 || jToPort < 0
        planPossible = False
        planNote = "No harbour serves this crossing."
      ElseIf jFromPort == jToPort
        planShip = False
      Else
        planShip = True
        planNote = "By ship from " + world.PortName(jFromPort) + " to " + world.PortName(jToPort) + "."
      EndIf
    EndIf
  ElseIf !sameLand
    planPossible = False
    planNote = "Across the sea, and no ship sails there."
  EndIf
  planCost = world.Cost(planMinutes, inn, planShip)
EndFunction

; The harbour nearest a point on the same landmass as it.
Int Function NearestPortOn(TMTravelWorld world, Float x, Float y)
  If !world.Climate
    Return world.NearestPort(x, y)
  EndIf
  String land = world.Climate.LandAt(world.PixelX(x), world.PixelY(y))
  Int best = -1
  Float bestD = 0.0
  Int i = 0
  Int n = world.PortCount()
  While i < n
    ObjectReference p = world.Ports.GetAt(i) as ObjectReference
    If p && world.Climate.LandAt(world.PixelX(p.GetPositionX()), world.PixelY(p.GetPositionY())) == land
      Float dx = p.GetPositionX() - x
      Float dy = p.GetPositionY() - y
      Float d = dx * dx + dy * dy
      If best < 0 || d < bestD
        best = i
        bestD = d
      EndIf
    EndIf
    i += 1
  EndWhile
  Return best
EndFunction

Function ShowTrip()
  If pickWorld < 0 || pickWorld >= Worlds.Length || !Worlds[pickWorld]
    Return
  EndIf
  TMTravelWorld world = Worlds[pickWorld]
  ObjectReference dest = world.TownMarker(pickGroup, pickTown)
  String name = world.TownName(pickGroup, pickTown)
  If !dest
    Return
  EndIf
  Int carried = PlayerRef.GetItemCount(Gold)
  String kind = "road"
  String note = ""
  Int minutes = 0
  Int cost = 0
  If world.Story != TimeHere()
    kind = "time"
    TMStoryOrder so = StoryOrder()
    If !so
      kind = "none"
    ElseIf !so.HasVisited(world.Story)
      note = "A time not reached yet: you begin there, with its opening or after it."
    Else
      note = "In another time: you travel there through time."
    EndIf
  ElseIf !StartPoint(pickWorld)
    note = "Step outside in this world first."
    kind = "none"
  Else
    Plan(world, dest)
    minutes = planMinutes
    cost = planCost
    note = planNote
    If !planPossible
      kind = "none"
    ElseIf cost > carried
      note = "You don't have enough gold for this journey."
      kind = "poor"
    EndIf
  EndIf
  String days = ""
  If minutes > 0
    days = Duration(minutes)
  EndIf
  ; name|kind|time|cost|gold|cautious|inn|ship|horse|horse available|speed|note|world|era
  Invoke("TripInfo", name + "|" + kind + "|" + days + "|" + cost + "|" + carried + "|" + (cautious as Int) + "|" + (inn as Int) + "|" + (useShip as Int) + "|" + (useHorse as Int) + "|" + (HasHorse() as Int) + "|" + speed + "|" + note + "|" + world.WorldName + "|" + Era(world.Story))
EndFunction

; ---- the journey ----

Function Begin()
  If pickWorld < 0 || !Worlds[pickWorld]
    Return
  EndIf
  TMTravelWorld world = Worlds[pickWorld]
  ObjectReference dest = world.TownMarker(pickGroup, pickTown)
  If !dest
    Return
  EndIf
  If world.Story != TimeHere()
    TMStoryOrder so = StoryOrder()
    If so && so.HasVisited(world.Story)
      Utility.Wait(0.1)
      so.TravelThroughTimeTo(world.Story, dest)
    ElseIf so
      ; Not reached yet: it begins there, its opening played or done (TMStoryOrder.EnterNewTime asks).
      Utility.Wait(0.1)
      so.EnterNewTime(world.Story)
    EndIf
    Return
  EndIf
  If CurrentWorld() != pickWorld
    Debug.MessageBox("Step outside first: the journey starts from the road.")
    Return
  EndIf
  StartPoint(pickWorld)
  Plan(world, dest)
  If !planPossible
    Debug.MessageBox(planNote)
    Return
  EndIf
  If PlayerRef.GetItemCount(Gold) < planCost
    Debug.MessageBox("You don't have enough gold for this journey.")
    Return
  EndIf
  If planCost > 0
    PlayerRef.RemoveItem(Gold, planCost, True)
  EndIf
  jWorld = pickWorld
  jDestination = dest
  jName = world.TownName(pickGroup, pickTown)
  jMinutes = planMinutes as Float
  jPassed = 0.0
  If planShip
    jLeg = 0
    jVoyage = StringUtil.Split(world.Voyage(jFromPort, jToPort), " ")
  Else
    jLeg = 2
    jVoyage = None
  EndIf
  Utility.Wait(0.1) ; the menu has closed
  Debug.Notification("You set out for " + jName + ": " + Duration(jMinutes) + " on the road.")
  StartJourney()
EndFunction

; Continues a stopped journey from where the player is, the time left being the part of the trip still ahead.
Function Resume()
  If !paused || !jDestination || jWorld != CurrentWorld()
    Return
  EndIf
  TMTravelWorld world = Worlds[jWorld]
  Bool horse = useHorse && HasHorse()
  Float left = world.Minutes(PlayerRef.GetPositionX(), PlayerRef.GetPositionY(), jDestination.GetPositionX(), jDestination.GetPositionY(), cautious, inn, useShip, horse) as Float
  jPassed = 0.0
  jMinutes = left
  Utility.Wait(0.1)
  StartJourney()
EndFunction

Function StartJourney()
  travelling = True
  paused = False
  jStartTime = Utility.GetCurrentGameTime()
  jSavedScale = TimeScale.GetValue()
  Actor horse = None
  If PlayerRef.IsOnMount()
    horse = Game.GetPlayersLastRiddenHorse()
  EndIf
  jSavedMult = PlayerRef.GetActorValue("SpeedMult")
  jLastX = PlayerRef.GetPositionX()
  jLastY = PlayerRef.GetPositionY()
  jLastClock = Utility.GetCurrentRealTime()
  jSpeed = 0.0
  jStuck = 0
  Game.ForceThirdPerson()
  Game.SetPlayerAIDriven(True)
  SetPace(horse)
  (GetAlias(0) as TMTravelPlayer).WatchKeys(True)
  NextLeg()
  RegisterForSingleUpdate(0.5)
EndFunction

; How fast the traveller moves: the slider between the natural pace (1) and the fastest uGridsToLoad allows (10).
Function SetPace(Actor horse)
  Int grids = Utility.GetINIInt("uGridsToLoad:General")
  If grids < 5
    grids = 5
  EndIf
  fastest = CellsPerSecond * 5.0 / grids * 4096.0
  Float maxMult = fastest / RunSpeed * 100.0
  If maxMult < 100.0
    maxMult = 100.0
  EndIf
  Float mult = 100.0 + (speed - 1) * (maxMult - 100.0) / 9.0
  ApplyMult(PlayerRef, mult)
  If horse
    ApplyMult(horse, mult)
  EndIf
EndFunction

; Speed multipliers only take effect once carry weight changes: nudge it.
Function ApplyMult(Actor a, Float mult)
  a.SetActorValue("SpeedMult", mult)
  a.ModActorValue("CarryWeight", 0.1)
  a.ModActorValue("CarryWeight", -0.1)
EndFunction

; The next stretch: walking to the harbour, sailing, walking to the town.
Function NextLeg()
  TMTravelWorld world = Worlds[jWorld]
  If jLeg == 0
    Walk(world.Ports.GetAt(jFromPort) as ObjectReference)
  ElseIf jLeg == 1
    Sail()
  Else
    Walk(jDestination)
  EndIf
EndFunction

Function Walk(ObjectReference target)
  Waypoint.MoveTo(target)
  (GetAlias(0) as TMTravelPlayer).WalkTo(Waypoint)
EndFunction

; The walk ended (TMTravelPlayer): at the harbour the ship, at the town the journey's end.
Function WalkEnded(Bool reached)
  If !travelling
    Return
  EndIf
  If !reached && Waypoint.GetDistance(PlayerRef) > 2048.0
    StopJourney("You find no road on from here. (Continue the journey from the Travel tab, or walk on.)")
    Return
  EndIf
  If jLeg == 0
    jLeg = 1
    NextLeg()
  Else
    Arrive()
  EndIf
EndFunction

; ---- by ship ----

Int voyageStep
Float shipHeading

Function Sail()
  TMTravelWorld world = Worlds[jWorld]
  If !jVoyage || jVoyage.Length < 2
    jLeg = 2
    NextLeg()
    Return
  EndIf
  voyageStep = 0
  String[] first = StringUtil.Split(jVoyage[0], ",")
  Float x = first[0] as Float
  Float y = first[1] as Float
  Ship.Disable()
  Ship.MoveTo(PlayerRef)
  Ship.SetPosition(x, y, ShipWaterZ)
  Ship.Enable()
  PlayerRef.SetPosition(x, y, ShipDeckZ)
  Debug.Notification("You board a ship at " + world.PortName(jFromPort) + ".")
  SailOn()
EndFunction

; Heads the ship (and the player on its deck) for the next waypoint.
Function SailOn()
  voyageStep += 1
  If voyageStep >= jVoyage.Length
    Landfall()
    Return
  EndIf
  String[] p = StringUtil.Split(jVoyage[voyageStep], ",")
  Float x = p[0] as Float
  Float y = p[1] as Float
  Float dx = x - Ship.GetPositionX()
  Float dy = y - Ship.GetPositionY()
  shipHeading = Math.atan(dx / (dy + 0.0001))
  If dy < 0.0
    shipHeading += 180.0
  EndIf
  Float pace = ShipSpeed * speed
  If pace > fastest
    pace = fastest
  EndIf
  Ship.TranslateTo(x, y, ShipWaterZ, 0.0, 0.0, shipHeading, pace, 60.0)
  PlayerRef.TranslateTo(x, y, ShipDeckZ, 0.0, 0.0, shipHeading, pace, 60.0)
EndFunction

; The ship reached a waypoint (checked from Watch).
Bool Function AtWaypoint()
  If voyageStep <= 0 || voyageStep >= jVoyage.Length
    Return False
  EndIf
  String[] p = StringUtil.Split(jVoyage[voyageStep], ",")
  Float dx = (p[0] as Float) - Ship.GetPositionX()
  Float dy = (p[1] as Float) - Ship.GetPositionY()
  Return dx * dx + dy * dy < 256.0 * 256.0
EndFunction

Function Landfall()
  Ashore()
  NextLeg()
EndFunction

; Off the ship at the harbour ahead.
Function Ashore()
  TMTravelWorld world = Worlds[jWorld]
  ObjectReference port = world.Ports.GetAt(jToPort) as ObjectReference
  PlayerRef.StopTranslation()
  Ship.StopTranslation()
  PlayerRef.MoveTo(port)
  Ship.Disable()
  Debug.Notification("You come ashore at " + world.PortName(jToPort) + ".")
  jLeg = 2
EndFunction

; ---- on the road ----

; Every half second: how fast the traveller is going, the clock set so Daggerfall's time is up on arrival, a fight
; stopping it, a stuck walk noticed.
Function Watch()
  If PlayerRef.IsInCombat()
    StopJourney("You are set upon on the road! (Continue the journey from the Travel tab when the way is clear.)")
    Return
  EndIf
  Float now = Utility.GetCurrentRealTime()
  Float x = PlayerRef.GetPositionX()
  Float y = PlayerRef.GetPositionY()
  Float dt = now - jLastClock
  If dt > 0.2
    Float dx = x - jLastX
    Float dy = y - jLastY
    Float v = Math.Sqrt(dx * dx + dy * dy) / dt
    jSpeed = jSpeed * 0.7 + v * 0.3
    jLastX = x
    jLastY = y
    jLastClock = now
    If v < 20.0
      jStuck += 1
    Else
      jStuck = 0
    EndIf
  EndIf
  If jLeg == 1 && AtWaypoint()
    SailOn()
  EndIf
  If jStuck > 40
    StopJourney("You can't find the way on. (Continue the journey from the Travel tab, or walk on.)")
    Return
  EndIf
  ; The clock: what's left of Daggerfall's time over what's left of the way at this speed.
  Float passed = jPassed + (Utility.GetCurrentGameTime() - jStartTime) * 1440.0
  Float leftMinutes = jMinutes - passed
  Float leftUnits = RemainingUnits()
  Float pace = jSpeed
  If pace < 100.0
    pace = 100.0
  EndIf
  Float scale = 1.0
  If leftMinutes > 0.0
    scale = leftMinutes * 60.0 / (leftUnits / pace + 1.0)
  EndIf
  If scale < 1.0
    scale = 1.0
  ElseIf scale > 20000.0
    scale = 20000.0
  EndIf
  TimeScale.SetValue(scale)
EndFunction

; Units still to go (straight, through the harbours when sailing).
Float Function RemainingUnits()
  Float x = PlayerRef.GetPositionX()
  Float y = PlayerRef.GetPositionY()
  Float d = 0.0
  If jLeg == 0
    TMTravelWorld world = Worlds[jWorld]
    ObjectReference a = world.Ports.GetAt(jFromPort) as ObjectReference
    ObjectReference b = world.Ports.GetAt(jToPort) as ObjectReference
    d = Dist(x, y, a.GetPositionX(), a.GetPositionY()) + Dist(a.GetPositionX(), a.GetPositionY(), b.GetPositionX(), b.GetPositionY())
    Return d + Dist(b.GetPositionX(), b.GetPositionY(), jDestination.GetPositionX(), jDestination.GetPositionY())
  ElseIf jLeg == 1
    TMTravelWorld world = Worlds[jWorld]
    ObjectReference b = world.Ports.GetAt(jToPort) as ObjectReference
    d = Dist(x, y, b.GetPositionX(), b.GetPositionY())
    Return d + Dist(b.GetPositionX(), b.GetPositionY(), jDestination.GetPositionX(), jDestination.GetPositionY())
  EndIf
  Return Dist(x, y, jDestination.GetPositionX(), jDestination.GetPositionY())
EndFunction

Float Function Dist(Float ax, Float ay, Float bx, Float by)
  Float dx = bx - ax
  Float dy = by - ay
  Return Math.Sqrt(dx * dx + dy * dy)
EndFunction

; Control back to the player, the clock and pace as they were.
Function Release()
  travelling = False
  (GetAlias(0) as TMTravelPlayer).WatchKeys(False)
  PlayerRef.StopTranslation()
  Game.SetPlayerAIDriven(False)
  PlayerRef.EvaluatePackage()
  TimeScale.SetValue(jSavedScale)
  ApplyMult(PlayerRef, jSavedMult)
  If PlayerRef.IsOnMount()
    ApplyMult(Game.GetPlayersLastRiddenHorse(), 100.0)
  EndIf
EndFunction

; Stopped on the way (a fight, the player, no road): the time passed so far stays passed; the rest can be continued.
Function StopJourney(String why)
  If !travelling
    Return
  EndIf
  jPassed += (Utility.GetCurrentGameTime() - jStartTime) * 1440.0
  Release()
  If jLeg == 1
    Ashore()   ; not left at sea: ashore at the harbour ahead
  EndIf
  paused = True
  Debug.Notification(why)
EndFunction

; Movement keys during the journey (TMTravelPlayer): the player takes over.
Function KeyPressed()
  If travelling
    StopJourney("You stop on the road.")
  EndIf
EndFunction

Function Arrive()
  Float passed = jPassed + (Utility.GetCurrentGameTime() - jStartTime) * 1440.0
  Release()
  paused = False
  ; Whatever of Daggerfall's time didn't pass on the way passes now.
  If jMinutes > passed
    PassHours((jMinutes - passed) / 60.0)
  EndIf
  If cautious
    ; Rested, and never arriving in the dark (Daggerfall Unity: after 7:10 when it would be before, or after 17:00).
    PlayerRef.RestoreActorValue("Health", 100000.0)
    PlayerRef.RestoreActorValue("Stamina", 100000.0)
    PlayerRef.RestoreActorValue("Magicka", 100000.0)
    Float hour = GameHour.GetValue()
    If hour < 7.17
      PassHours(7.17 - hour)
    ElseIf hour > 17.0
      PassHours(24.0 - hour + 7.17)
    EndIf
  EndIf
  Debug.Notification("You reach " + jName + ".")
  jDestination = None
  Game.RequestAutoSave()
EndFunction
