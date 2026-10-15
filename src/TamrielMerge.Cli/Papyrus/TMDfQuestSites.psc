ScriptName TMDfQuestSites extends Quest
{Quest sites of one plugin (Daggerfall.esm, a town part, a dungeon part), made by the converter's QuestSites: its towns
(name, region, location, map marker, Daggerfall map pixel, buildings), its buildings (Daggerfall building type, street
door, arrival marker inside and the interior's quest markers 199.11 / 199.18), its dungeons (type, region, doors, start
and quest markers) and the named individuals standing in it. Starts with the game and registers with the machine.}

TMDfQuestMachine Property Machine Auto
Int[] Property Regions Auto
String[] Property TownName Auto
Int[] Property TownRegion Auto
Int[] Property TownLocId Auto
Int[] Property TownX Auto
Int[] Property TownY Auto
Int[] Property TownFirst Auto
Int[] Property TownCount Auto
Location[] Property TownLocation Auto
ObjectReference[] Property TownMap Auto
Int[] Property BType Auto
Int[] Property BTown Auto
ObjectReference[] Property BDoor Auto
ObjectReference[] Property BArrival Auto
ObjectReference[] Property BSpawn Auto
ObjectReference[] Property BItem Auto
String[] Property BName Auto
{Daggerfall's name for the building ("The Dancing Griffin"; empty for houses).}
String[] Property DName Auto
Int[] Property DRegion Auto
Int[] Property DLocId Auto
Int[] Property DType Auto
Int[] Property DX Auto
Int[] Property DY Auto
ObjectReference[] Property DMap Auto
ObjectReference[] Property DDoor Auto
ObjectReference[] Property DStart Auto
ObjectReference[] Property DSpawn Auto
ObjectReference[] Property DItem Auto
Int[] Property IFaction Auto
Actor[] Property IRef Auto

Event OnInit()
  RegisterForSingleUpdate(2.0)
EndEvent

Event OnUpdate()
  If Machine
    Machine.Register(self)
  EndIf
EndEvent
