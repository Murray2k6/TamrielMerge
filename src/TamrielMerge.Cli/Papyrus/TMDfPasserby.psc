ScriptName TMDfPasserby extends Actor
{One of a Daggerfall town's street people. A region's few wanderer records are placed in every town of it, so each
placement carries the name its town gives it (Daggerfall's name generator, seeded by the town and the place:
TownPeople.WandererName) and takes it when it loads (user, 2026-10-25: the same names in every town). Needs SKSE
(SetDisplayName); without it they keep their record's name.}

String Property TownName = "" Auto

Event OnLoad()
  If TownName != "" && SKSE.GetVersion() > 0
    SetDisplayName(TownName, True)
  EndIf
EndEvent
