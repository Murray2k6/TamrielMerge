ScriptName TMDisposition extends Actor Hidden
{A converted NPC's starting disposition (Morrowind's NPDT value): Variable09 holds disposition minus 50, set when
the NPC first appears (and again when it respawns, as Morrowind resets it).}

Int Property Start = 50 Auto

Event OnInit()
  SetActorValue("Variable09", (Start - 50) as Float)
EndEvent
