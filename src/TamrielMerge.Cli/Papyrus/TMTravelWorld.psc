ScriptName TMTravelWorld extends Quest
{One converted world's travel (TamrielMerge Travel.esp, docs/travel.md): its towns by region and what Daggerfall's
travel prices (Daggerfall Unity's TravelTimeCalculator). A quest that never runs; TMTravel reads it.

Every world is priced as Daggerfall prices its map: one cell is one Daggerfall map pixel (the Iliac Bay is built at
that scale), crossed in (102 x transport / 256) x (512 - terrain) / 256 minutes, transport 256 on foot, 128 on a horse;
terrain by the map pixel's climate where the world has Daggerfall's climate map (Climate), else the woods' (250). The sea
costs 255 minutes a pixel, 51 by ship. Camping out instead of inns takes 300/256 as long; travelling recklessly half.}

String Property WorldName Auto
Int Property Story Auto
{The time this world belongs to: 0 Daggerfall, 1 Redguard, 2 Morrowind, 3 Oblivion, 4 Skyrim (TMStoryOrder).}
WorldSpace[] Property Worlds Auto
{Its exterior worldspaces: travel goes between towns in the one the traveller is in.}
String[] Property GroupNames Auto
String[] Property GroupTowns Auto
{Each group's towns, "|" between them, in the order of its GroupMarkers list.}
FormList[] Property GroupMarkers Auto
Float Property UnitsPerPixel = 4096.0 Auto
Float Property MapWestX Auto
Float Property MapNorthY Auto
{Daggerfall's map pixel (0,0)'s north-west corner (units); its pixels run east and south.}
TMTravelClimate Property Climate Auto
{Daggerfall's map climates, where the world has them.}

; Ships (the Iliac Bay): its harbours, each a town marker with the sea point ships leave from, and the sea's hubs with
; the routes between them (see TMTravel's voyage).
FormList Property Ports Auto
String Property PortNames Auto
{"|" between names, in the order of Ports.}
String Property PortSea Auto
{"x,y|x,y|...": each harbour's sea point (units).}
String Property PortHubs Auto
{"i|j|...": each harbour's nearest hub.}
String Property HubPoints Auto
{"x,y|...": the sea hubs (units).}
String[] Property HubRoutes Auto
{For each hub, "to:x,y x,y ...;to:..." the waypoints (units) to every other hub, ending at it.}

Int Function TownCount(Int g)
  If g < 0 || g >= GroupMarkers.Length
    Return 0
  EndIf
  Return GroupMarkers[g].GetSize()
EndFunction

String Function TownName(Int g, Int t)
  String[] names = StringUtil.Split(GroupTowns[g], "|")
  If t < 0 || t >= names.Length
    Return ""
  EndIf
  Return names[t]
EndFunction

ObjectReference Function TownMarker(Int g, Int t)
  If g < 0 || g >= GroupMarkers.Length
    Return None
  EndIf
  Return GroupMarkers[g].GetAt(t) as ObjectReference
EndFunction

Bool Function Has(WorldSpace w)
  Return w && Worlds.Find(w) >= 0
EndFunction

; ---- Daggerfall's travel time ----

; A position (units) as a map pixel.
Int Function PixelX(Float x)
  Return Math.Floor((x - MapWestX) / UnitsPerPixel)
EndFunction

Int Function PixelY(Float y)
  Return Math.Floor((MapNorthY - y) / UnitsPerPixel)
EndFunction

; A map pixel's terrain for travel: "O" the sea, else Daggerfall Unity's terrain index (0 desert .. 5 woods).
String Function TerrainAt(Int px, Int py)
  If !Climate
    Return "5"
  EndIf
  Return Climate.At(px, py)
EndFunction

; Daggerfall Unity's terrain movement modifiers, by index.
Int Function Modifier(String terrain)
  If terrain == "0"
    Return 240
  ElseIf terrain == "1"
    Return 220
  ElseIf terrain == "2" || terrain == "3"
    Return 200
  ElseIf terrain == "4"
    Return 230
  EndIf
  Return 250
EndFunction

; Sea pixels the last Minutes() crossed (for the ship's fare).
Int Property OceanPixels Auto Hidden

; Minutes on the road from one position to another (units), stepping pixel by pixel as Daggerfall does.
Int Function Minutes(Float fromX, Float fromY, Float toX, Float toY, Bool cautious, Bool inn, Bool ship, Bool horse)
  Int x = PixelX(fromX)
  Int y = PixelY(fromY)
  Int ex = PixelX(toX)
  Int ey = PixelY(toY)
  Int dx = ex - x
  Int dy = ey - y
  Int adx = dx
  If adx < 0
    adx = -adx
  EndIf
  Int ady = dy
  If ady < 0
    ady = -ady
  EndIf
  Int sx = 1
  If dx < 0
    sx = -1
  EndIf
  Int sy = 1
  If dy < 0
    sy = -1
  EndIf
  Int furthest = adx
  If ady > adx
    furthest = ady
  EndIf
  Int transport = 256
  If horse
    transport = 128
  EndIf
  Int perLand = (102 * transport) / 256
  Int total = 0
  Int acc = 0
  Int moves = 0
  OceanPixels = 0
  While moves < furthest
    If furthest == adx
      x += sx
      acc += ady
      If acc > adx
        acc -= adx
        y += sy
      EndIf
    Else
      y += sy
      acc += adx
      If acc > ady
        acc -= ady
        x += sx
      EndIf
    EndIf
    String terrain = TerrainAt(x, y)
    Int m
    If terrain == "O"
      OceanPixels += 1
      If ship
        m = 51
      Else
        m = 255
      EndIf
    Else
      m = (perLand * (512 - Modifier(terrain))) / 256
    EndIf
    If !inn
      m = (300 * m) / 256
    EndIf
    total += m
    moves += 1
  EndWhile
  If !cautious
    total = total / 2
  EndIf
  Return total
EndFunction

; The trip's price in gold (Daggerfall Unity's CalculateTripCost): inns 5 a day on land and one night at least; a ship's
; passage 25 per 24 sea pixels (and one).
Int Function Cost(Int minutes, Bool inn, Bool ship)
  Int hours = (minutes + 59) / 60
  Int gold = 0
  If inn
    gold = 5 * ((hours - OceanPixels) / 24)
    If gold < 0
      gold = 0
    EndIf
    gold += 5
  EndIf
  If ship && OceanPixels > 0
    gold += 25 * (OceanPixels / 24 + 1)
  EndIf
  Return gold
EndFunction

; ---- ships ----

Int Function PortCount()
  If !Ports
    Return 0
  EndIf
  Return Ports.GetSize()
EndFunction

; The harbour nearest a position (units), or -1.
Int Function NearestPort(Float x, Float y)
  Int best = -1
  Float bestD = 0.0
  Int i = 0
  Int n = PortCount()
  While i < n
    ObjectReference p = Ports.GetAt(i) as ObjectReference
    If p
      Float ddx = p.GetPositionX() - x
      Float ddy = p.GetPositionY() - y
      Float d = ddx * ddx + ddy * ddy
      If best < 0 || d < bestD
        best = i
        bestD = d
      EndIf
    EndIf
    i += 1
  EndWhile
  Return best
EndFunction

String Function PortName(Int i)
  String[] names = StringUtil.Split(PortNames, "|")
  If i < 0 || i >= names.Length
    Return ""
  EndIf
  Return names[i]
EndFunction

; A harbour's sea point "x,y".
String Function PortSeaPoint(Int i)
  String[] points = StringUtil.Split(PortSea, "|")
  If i < 0 || i >= points.Length
    Return ""
  EndIf
  Return points[i]
EndFunction

; The waypoints ("x,y x,y ...") a ship sails from one harbour to another: out to the first harbour's hub, along the
; hubs' route, into the second harbour.
String Function Voyage(Int fromPort, Int toPort)
  String[] hubs = StringUtil.Split(PortHubs, "|")
  If fromPort < 0 || toPort < 0 || fromPort >= hubs.Length || toPort >= hubs.Length
    Return ""
  EndIf
  Int a = hubs[fromPort] as Int
  Int b = hubs[toPort] as Int
  String route = PortSeaPoint(fromPort)
  If a != b && a >= 0 && a < HubRoutes.Length
    String[] routes = StringUtil.Split(HubRoutes[a], ";")
    Int i = 0
    While i < routes.Length
      Int colon = StringUtil.Find(routes[i], ":")
      If colon > 0 && (StringUtil.Substring(routes[i], 0, colon) as Int) == b
        route += " " + StringUtil.Substring(routes[i], colon + 1)
      EndIf
      i += 1
    EndWhile
  ElseIf a >= 0
    String[] points = StringUtil.Split(HubPoints, "|")
    If a < points.Length
      route += " " + points[a]
    EndIf
  EndIf
  Return route + " " + PortSeaPoint(toPort)
EndFunction
