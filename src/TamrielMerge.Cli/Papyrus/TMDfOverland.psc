ScriptName TMDfOverland extends Quest
{The Iliac Bay's overland: its tiles and Daggerfall's travel.

Tiles. The overland is split into tiles, each its own worldspace: Skyrim loses position precision far from a
worldspace's origin, and Daggerfall's map is far bigger than that. Each tile has its neighbours' land a few cells past
its own edge; walking past the edge moves the player, with their horse and followers, into the neighbouring tile at the
same spot. Tiles run west to east, then south to north (Tiles[row * Columns + column]); every tile's core is
CoreMinX..CoreMaxX by CoreMinY..CoreMaxY in its own coordinates, so its neighbours' cores sit a core's width away.
A map small enough for one worldspace is one tile, and nothing ever moves.

Travel (until 2026-10-12; now TMTravel, which reads RegionNames, RegionTowns and RegionMarkers from here). Skyrim's map travels within a tile; the Tamriel menu's Travel tab went anywhere in the Iliac Bay, as
Daggerfall's travel map does: a region, then one of its towns, taking as long as Daggerfall Unity's travel on foot
(108 minutes per map pixel; half that on a horse). The menu sends mod event TM_Travel ("regions"; "region" with the
region's index; "go:region:town") and gets ClearTravel / AddTravel("r|index|name|towns" or "t|index|name|hours") /
ShowTravel(title) and TravelStatus(text).}

Actor Property PlayerRef Auto
GlobalVariable Property GameHour Auto
Location Property IliacBay Auto
WorldSpace[] Property Tiles Auto
ObjectReference[] Property Origins Auto
{An XMarker at 0,0,0 in each tile, to move to.}
Float[] Property OriginX Auto
Float[] Property OriginY Auto
{Each tile's 0,0 on the whole map (units).}
Int Property Columns = 1 Auto
Float Property CoreMinX Auto
Float Property CoreMaxX Auto
Float Property CoreMinY Auto
Float Property CoreMaxY Auto
Float Property Hysteresis = 2048.0 Auto
{How far past the edge the move happens, so standing on the edge doesn't flip back and forth.}
Float Property UnitsPerPixel = 2048.0 Auto
{Units per Daggerfall map pixel on this overland.}
Float Property MinutesPerPixel = 108.0 Auto
String[] Property RegionNames Auto
String[] Property RegionTowns Auto
{Each region's towns, "|" between them, in the order of its RegionMarkers list.}
FormList[] Property RegionMarkers Auto

String Property MenuName = "CustomMenu" AutoReadOnly

; Where on the whole map the player last stood on the overland (for travel from a town or a dungeon).
Float lastX
Float lastY
Bool knownLast

Event OnInit()
  Setup()
  RegisterForSingleUpdate(2.0)
EndEvent

Function Setup()
  ; The Travel tab is TMTravel's since 2026-10-12 (TamrielMerge Travel.esp: every world, travelled in real time);
  ; this quest's regions and towns are where it reads the Iliac Bay's.
  UnregisterForModEvent("TM_Travel")
EndFunction

; ---- tiles ----

Event OnUpdate()
  Float wait = 5.0
  Int t = Tiles.Find(PlayerRef.GetWorldSpace())
  If t >= 0
    wait = 1.0
    Float x = PlayerRef.GetPositionX()
    Float y = PlayerRef.GetPositionY()
    lastX = x + OriginX[t]
    lastY = y + OriginY[t]
    knownLast = True
    Int dc = 0
    Int dr = 0
    If x > CoreMaxX + Hysteresis
      dc = 1
    ElseIf x < CoreMinX - Hysteresis
      dc = -1
    EndIf
    If y > CoreMaxY + Hysteresis
      dr = 1
    ElseIf y < CoreMinY - Hysteresis
      dr = -1
    EndIf
    If dc != 0 || dr != 0
      Int col = t % Columns + dc
      Int row = t / Columns + dr
      If col >= 0 && col < Columns && row >= 0 && row < Tiles.Length / Columns
        Int to = row * Columns + col
        Hop(to, x + OriginX[t] - OriginX[to], y + OriginY[t] - OriginY[to], PlayerRef.GetPositionZ())
      EndIf
    EndIf
  EndIf
  RegisterForSingleUpdate(wait)
EndEvent

; Into tile "to" at (x, y, z), its own coordinates, with the horse and followers.
Function Hop(Int to, Float x, Float y, Float z)
  ObjectReference origin = Origins[to]
  If !origin
    Return
  EndIf
  ; Who comes along: followers and summons near the player (and where they stand from the player).
  Actor[] party = new Actor[8]
  Float[] dx = new Float[8]
  Float[] dy = new Float[8]
  Float[] dz = new Float[8]
  Int n = 0
  Int i = 0
  While i < 24 && n < 8
    Actor a = Game.FindRandomActorFromRef(PlayerRef, 3000.0)
    If a && a != PlayerRef && !a.IsDead() && (a.IsPlayerTeammate() || a.IsCommandedActor()) && party.Find(a) < 0
      party[n] = a
      dx[n] = a.GetPositionX() - PlayerRef.GetPositionX()
      dy[n] = a.GetPositionY() - PlayerRef.GetPositionY()
      dz[n] = a.GetPositionZ() - PlayerRef.GetPositionZ()
      n += 1
    EndIf
    i += 1
  EndWhile
  Actor horse = None
  If PlayerRef.IsOnMount()
    horse = Game.GetPlayersLastRiddenHorse()
  EndIf
  Float angle = PlayerRef.GetAngleZ()
  ; The land is the same in both tiles where they overlap, so the height carries over.
  PlayerRef.MoveTo(origin, x, y, z, False)
  PlayerRef.SetAngle(0.0, 0.0, angle)
  If horse && horse.GetWorldSpace() != PlayerRef.GetWorldSpace()
    horse.MoveTo(PlayerRef)
  EndIf
  i = 0
  While i < n
    party[i].MoveTo(origin, x + dx[i], y + dy[i], z + dz[i] + 16.0, False)
    i += 1
  EndWhile
EndFunction

; ---- travel ----

Bool Function InIliacBay()
  Return Tiles.Find(PlayerRef.GetWorldSpace()) >= 0 || (IliacBay && PlayerRef.IsInLocation(IliacBay))
EndFunction

; Hours on the road from where the player is (or last was on the overland) to a marker.
Float Function HoursTo(ObjectReference marker)
  Int t = Tiles.Find(marker.GetWorldSpace())
  If t < 0 || !knownLast
    Return 0.0
  EndIf
  Float dx = marker.GetPositionX() + OriginX[t] - lastX
  Float dy = marker.GetPositionY() + OriginY[t] - lastY
  Float hours = Math.Sqrt(dx * dx + dy * dy) / UnitsPerPixel * MinutesPerPixel / 60.0
  If PlayerRef.IsOnMount()
    hours = hours / 2.0
  EndIf
  Return hours
EndFunction

String Function Duration(Float hours)
  Int h = Math.Ceiling(hours)
  If h < 24
    Return "" + h + " hours"
  EndIf
  Return "" + (h / 24) + " days " + (h % 24) + " hours"
EndFunction

Event OnTravel(String eventName, String what, Float number, Form sender)
  If !InIliacBay()
    UI.InvokeString(MenuName, "_root.TravelStatus", "Travel here goes about the Iliac Bay; you are not in it.")
    Return
  EndIf
  Int t = Tiles.Find(PlayerRef.GetWorldSpace())
  If t >= 0
    lastX = PlayerRef.GetPositionX() + OriginX[t]
    lastY = PlayerRef.GetPositionY() + OriginY[t]
    knownLast = True
  EndIf
  Int r = 0
  Int i = 0
  If what == "regions"
    UI.InvokeString(MenuName, "_root.ClearTravel", "")
    While r < RegionNames.Length
      UI.InvokeString(MenuName, "_root.AddTravel", "r|" + r + "|" + RegionNames[r] + "|" + RegionMarkers[r].GetSize())
      r += 1
    EndWhile
    UI.InvokeString(MenuName, "_root.ShowTravel", "The Iliac Bay")
  ElseIf what == "region"
    r = number as Int
    If r < 0 || r >= RegionNames.Length
      Return
    EndIf
    String[] names = StringUtil.Split(RegionTowns[r], "|")
    UI.InvokeString(MenuName, "_root.ClearTravel", "")
    While i < names.Length
      ObjectReference marker = RegionMarkers[r].GetAt(i) as ObjectReference
      If marker
        UI.InvokeString(MenuName, "_root.AddTravel", "t|" + r + ":" + i + "|" + names[i] + "|" + Duration(HoursTo(marker)))
      EndIf
      i += 1
    EndWhile
    UI.InvokeString(MenuName, "_root.ShowTravel", RegionNames[r])
  ElseIf StringUtil.Find(what, "go:") == 0
    String[] p = StringUtil.Split(StringUtil.Substring(what, 3), ":")
    If p.Length == 2
      Go(p[0] as Int, p[1] as Int)
    EndIf
  EndIf
EndEvent

Function Go(Int r, Int i)
  If r < 0 || r >= RegionNames.Length
    Return
  EndIf
  ObjectReference marker = RegionMarkers[r].GetAt(i) as ObjectReference
  If !marker
    Return
  EndIf
  String[] names = StringUtil.Split(RegionTowns[r], "|")
  Float hours = HoursTo(marker)
  Actor horse = None
  If PlayerRef.IsOnMount()
    horse = Game.GetPlayersLastRiddenHorse()
  EndIf
  Utility.Wait(0.1) ; the menu has closed
  PlayerRef.MoveTo(marker)
  If horse && horse.GetWorldSpace() != PlayerRef.GetWorldSpace()
    horse.MoveTo(PlayerRef)
  EndIf
  Int t = Tiles.Find(marker.GetWorldSpace())
  If t >= 0
    lastX = marker.GetPositionX() + OriginX[t]
    lastY = marker.GetPositionY() + OriginY[t]
  EndIf
  ; The time on the road, a day at most at a time (the calendar rolls over as it goes).
  Float left = hours
  While left > 0.0
    Float step = left
    If step > 20.0
      step = 20.0
    EndIf
    GameHour.SetValue(GameHour.GetValue() + step)
    left -= step
    Utility.Wait(0.05)
  EndWhile
  If i < names.Length
    Debug.Notification("You reach " + names[i] + " after " + Duration(hours) + " on the road.")
  EndIf
EndFunction
