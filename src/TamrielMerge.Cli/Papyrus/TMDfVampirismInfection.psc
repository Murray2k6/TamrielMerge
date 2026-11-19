ScriptName TMDfVampirismInfection extends ActiveMagicEffect
{Daggerfall's vampirism, caught (Daggerfall Unity VampirismInfection, MIT): a disease with no effects of its own (Skyrim's
cures end it while it lasts). Not caught by a vampire or a lycanthrope. The day after, the dream (ANIM0004.VID); more
than three days after, with the dream seen, the player dies (ANIM0012.VID), two weeks pass to an hour after dusk, and
they wake at a random cemetery of the region the bite was taken in (its dungeon's door), the clan that region's
province's (FACTION.TXT "vam"), with Daggerfall's word on it (TEXT.RSC 401) and the curse (TMDfVampirism); no encounter
follows the jump in time, and the new vampire counts as fed.}

Spell Property Infection Auto
{This infection's spell.}
Spell Property Curse Auto
{The vampirism it becomes (TMDfVampirism).}
FormList Property Lycanthropy Auto
{Daggerfall's lycanthropy (werewolf infection, wereboar infection, werewolf, wereboar): a lycanthrope already.}
Spell Property BeastForm Auto
{Skyrim's beast form (WerewolfChange): a lycanthrope already.}
Quest Property Machine Auto
{TMDfQuestMachine: the region's cemeteries, the current town's region.}
GlobalVariable Property PreventSpawns Auto
{DFPreventSpawns: no random encounter after the two weeks.}
GlobalVariable Property VampireClan Auto
{DFVampireClan: the clan's faction (150 Vraseth .. 158 Selenu).}
GlobalVariable Property FedDay Auto
{DFVampireFedDay: the game day of the last feeding.}
Int[] Property RegionClan Auto
{By region: its province's vampire clan (0 none).}
Int Property DefaultClan = 150 Auto
{The Vraseth, for a region whose province names no clan.}
String Property TurnText Auto
{TEXT.RSC 401: waking as a vampire.}
Int Property CemeteryType = 18 AutoReadOnly
Float Property WakeHour = 19.0 AutoReadOnly
Int Property GraveDays = 14 AutoReadOnly

Actor host
Int startingDay
Int infectionRegion = -1
Bool dreamed = False
Bool deployed = False

Int Function Today()
  Return Math.Floor(Utility.GetCurrentGameTime())
EndFunction

Event OnEffectStart(Actor akTarget, Actor akCaster)
  host = akTarget
  Bool vampire = Curse && host.HasSpell(Curse)
  Bool lycanthrope = BeastForm && host.HasSpell(BeastForm)
  If Lycanthropy && Lycanthropy.GetSize() > 3
    lycanthrope = lycanthrope || host.HasSpell(Lycanthropy.GetAt(2) as Spell) || host.HasSpell(Lycanthropy.GetAt(3) as Spell)
  EndIf
  If host != Game.GetPlayer() || vampire || lycanthrope
    End()
    Return
  EndIf
  startingDay = Today()
  ; The region the bite was taken in (PlayerGPS.CurrentRegionIndex): the political map under the player, else the town's.
  infectionRegion = RegionNow()
  RegisterForUpdateGameTime(1.0)
EndEvent

Int Function RegionNow()
  Int r = -1
  TMDfWilds wilds = Quest.GetQuest("DFOverlandQuest") as TMDfWilds
  If wilds
    r = wilds.RegionHere()
  EndIf
  If r < 0
    TMDfQuestMachine m = Machine as TMDfQuestMachine
    If m
      r = m.SiteRegion(m.CurrentTown)
    EndIf
  EndIf
  Return r
EndFunction

; ProgressDisease, each game hour.
Event OnUpdateGameTime()
  If deployed || !host
    Return
  EndIf
  Int daysPast = Today() - startingDay
  If daysPast > 0 && !dreamed
    dreamed = True
    Game.PlayBink("TamrielMerge_DfAnim4.bik", True)
  ElseIf daysPast > 3 && dreamed
    Deploy()
  EndIf
EndEvent

; DeployVampirism: the death, the clan, two weeks in the grave, the cemetery, the word, the curse.
Function Deploy()
  deployed = True
  Game.PlayBink("TamrielMerge_DfAnim12.bik", True)
  Int clan = DefaultClan
  If RegionClan && infectionRegion >= 0 && infectionRegion < RegionClan.Length && RegionClan[infectionRegion] > 0
    clan = RegionClan[infectionRegion]
  EndIf
  If VampireClan
    VampireClan.SetValueInt(clan)
  EndIf
  PassTime(GraveDays, WakeHour)
  If PreventSpawns
    PreventSpawns.SetValueInt(1)
  EndIf
  TMDfQuestMachine m = Machine as TMDfQuestMachine
  If m
    Int[] unused = new Int[1]
    unused[0] = -1
    Int site = m.DungeonSite(infectionRegion, CemeteryType, unused, 0)
    If site < 0
      site = m.DungeonSite(-1, CemeteryType, unused, 0)
    EndIf
    If site >= 0
      ObjectReference wakeAt = m.SiteRef(site, 0)
      If wakeAt
        host.MoveTo(wakeAt)
      EndIf
    EndIf
  EndIf
  If FedDay
    FedDay.SetValue(Utility.GetCurrentGameTime())
  EndIf
  If TurnText != ""
    Debug.MessageBox(TurnText)
  EndIf
  If Curse
    host.AddSpell(Curse, False)
  EndIf
  End()
EndFunction

; Passes whole days to the hour given, a day at most at a time (the calendar rolls over as it goes).
Function PassTime(Int days, Float toHour)
  GlobalVariable hour = Game.GetFormFromFile(0x38, "Skyrim.esm") as GlobalVariable
  If !hour
    Return
  EndIf
  Float left = days * 24.0 + toHour - hour.GetValue()
  While left > 0.0
    Float stepH = left
    If stepH > 20.0
      stepH = 20.0
    EndIf
    hour.SetValue(hour.GetValue() + stepH)
    left -= stepH
    Utility.Wait(0.05)
  EndWhile
EndFunction

Function End()
  UnregisterForUpdateGameTime()
  If host && Infection
    host.RemoveSpell(Infection)
  EndIf
EndFunction

Event OnEffectFinish(Actor akTarget, Actor akCaster)
  UnregisterForUpdateGameTime()
EndEvent
