ScriptName TMDfLaw extends Quest
{The player's legal standing in the Iliac Bay's regions (TMDfQuestMachine.LegalOf: the quests' "legal repute") and what
reads it, as Daggerfall Unity does (PlayerEntity, DaggerfallCourtWindow; MIT):
- below -10 (banished: always), each game minute in that region's town streets has a 5% chance (banished: 10%) that 2-5
  of the City Watch come for the player for criminal conspiracy; at most one group at a time;
- when the first reaches the player: come quietly (to court) or fight;
- the court: a standing below 0 may bring banishment (unless both a -rep and a -rep/2 chance of 75 at most fail); else
  the penalty, 5 x rep + 500 (500 - 5 x rep below 0) gold-weight within 10-1500, a point per 40 adds a 40 gold fine or
  3 days in prison at random; pleading guilty halves both; pleading not guilty, debating (Etiquette) or lying
  (Streetwise) frees the player with rep + (skill + Personality) / 2 percent (5-95; Skyrim's Speech for Personality),
  else rep + d100 under 25 doubles the sentence and over 75 halves it; a fine beyond the player's gold is served at 40
  gold a day;
- every 112 days each region's standing moves 1 toward 0.
Daggerfall's towns have no crimes of Skyrim's: the standing changes only by the quests and the drift.}

TMDfQuestMachine Property Machine Auto
Actor Property PlayerRef Auto
Form Property Watch Auto
{The City Watch as Daggerfall's enemy (CreatureBuilder: hostile, with Daggerfall's senses).}
TMDfSensesControl Property Senses Auto
{Etiquette and Streetwise (Daggerfall's language skills).}
Message Property Arrest Auto
Message Property Accused Auto
Message Property Convince Auto
Message Property SentenceFine Auto
Message Property SentencePrison Auto
Message Property SentenceBoth Auto
Message Property NoGold Auto
Message Property Freed Auto
Message Property Banished Auto
GlobalVariable Property GameHour Auto

Float lastTime = -1.0
Float lastDrift = -1.0
Bool[] exiled
Actor[] guards
Bool pending = False

Event OnInit()
  RegisterForSingleUpdate(5.0)
EndEvent

Event OnUpdate()
  Tick()
  RegisterForSingleUpdate(5.0)
EndEvent

Function Tick()
  If !Machine
    Return
  EndIf
  If !exiled
    exiled = new Bool[64]
  EndIf
  If !guards
    guards = new Actor[5]
  EndIf
  Float now = Utility.GetCurrentGameTime()
  If lastTime < 0.0
    lastTime = now
    lastDrift = now
    Return
  EndIf
  Float minutes = (now - lastTime) * 1440.0
  lastTime = now
  ; Every 112 days the standing drifts toward 0.
  If now - lastDrift >= 112.0
    lastDrift = now
    Int r = 0
    While r < 62
      Int v = Machine.LegalOf(r)
      If v > 0
        Machine.SetLegalOf(r, v - 1)
      ElseIf v < 0
        Machine.SetLegalOf(r, v + 1)
      EndIf
      r += 1
    EndWhile
  EndIf
  If pending
    GuardsApproach()
    Return
  EndIf
  Int town = Machine.CurrentTown
  Location here = Machine.SiteLocation(town)
  If !here || PlayerRef.IsInInterior() || !PlayerRef.IsInLocation(here)
    Return
  EndIf
  Int region = Machine.SiteRegion(town)
  Int chance = 0
  If region >= 0 && region < 64 && exiled[region]
    chance = 10
  ElseIf Machine.LegalOf(region) < -10
    chance = 5
  EndIf
  If chance == 0 || minutes <= 0.0
    Return
  EndIf
  ; At least one hit in the minutes passed, each with the chance.
  Float quiet = Math.pow(1.0 - chance / 100.0, minutes)
  If Utility.RandomFloat(0.0, 1.0) >= quiet
    SpawnGuards()
  EndIf
EndFunction

Function SpawnGuards()
  If !Watch
    Return
  EndIf
  Int n = Utility.RandomInt(2, 5)
  Float a = Utility.RandomFloat(0.0, 360.0)
  Int i = 0
  While i < n && i < guards.Length
    Actor g = PlayerRef.PlaceAtMe(Watch, 1, False, False) as Actor
    If g
      Float r = Utility.RandomFloat(1200.0, 1800.0)
      g.MoveTo(PlayerRef, r * Math.Sin(a + i * 15.0), r * Math.Cos(a + i * 15.0), 0.0)
      g.SetActorValue("Aggression", 0.0)
      g.PathToReference(PlayerRef, 1.0)
      guards[i] = g
    EndIf
    i += 1
  EndWhile
  pending = True
EndFunction

; The City Watch reach the player: come quietly or fight.
Function GuardsApproach()
  Bool near = False
  Bool any = False
  Int i = 0
  While i < guards.Length
    If guards[i] && !guards[i].IsDead()
      any = True
      If guards[i].GetDistance(PlayerRef) < 500.0
        near = True
      Else
        guards[i].PathToReference(PlayerRef, 1.0)
      EndIf
    EndIf
    i += 1
  EndWhile
  If !any
    pending = False
    Return
  EndIf
  If !near
    Return
  EndIf
  pending = False
  If Arrest.Show() == 0
    ClearGuards()
    Court(Machine.SiteRegion(Machine.CurrentTown))
  Else
    i = 0
    While i < guards.Length
      If guards[i]
        guards[i].SetActorValue("Aggression", 1.0)
        guards[i].StartCombat(PlayerRef)
        guards[i] = None
      EndIf
      i += 1
    EndWhile
  EndIf
EndFunction

Function ClearGuards()
  Int i = 0
  While i < guards.Length
    If guards[i]
      guards[i].Disable()
      guards[i].Delete()
      guards[i] = None
    EndIf
    i += 1
  EndWhile
EndFunction

; Dice100.FailedRoll: a d100 over the chance.
Bool Function Failed(Int chance)
  Return Utility.RandomInt(1, 100) > chance
EndFunction

; DaggerfallCourtWindow for criminal conspiracy (crime 7: per point 5, base 500, 10-1500).
Function Court(Int region)
  Int rep = Machine.LegalOf(region)
  If rep < 0
    Int t1 = -rep
    Int t2 = -rep / 2
    If t1 > 75
      t1 = 75
    EndIf
    If t2 > 75
      t2 = 75
    EndIf
    If !(Failed(t2) && Failed(t1))
      Banished.Show()
      If region >= 0 && region < 64
        exiled[region] = True
      EndIf
      Return
    EndIf
  EndIf
  Int penalty = 500 + 5 * rep
  If rep < 0
    penalty = 500 - 5 * rep
  EndIf
  If penalty < 10
    penalty = 10
  ElseIf penalty > 1500
    penalty = 1500
  EndIf
  Int fine = 0
  Int days = 0
  Int points = penalty / 40
  While points > 0
    If Utility.RandomInt(0, 1) == 0
      fine += 40
    Else
      days += 3
    EndIf
    points -= 1
  EndWhile
  If Accused.Show() == 0
    fine /= 2
    days /= 2
  Else
    Int skill = 0
    If Convince.Show() == 1
      skill = 1
    EndIf
    Int value = 0
    If Senses
      value = Senses.SkillValue(skill)
      Senses.Tally(skill, 1)
    EndIf
    Int free = rep + (value + (PlayerRef.GetActorValue("Speechcraft") as Int)) / 2
    If free < 5
      free = 5
    ElseIf free > 95
      free = 95
    EndIf
    If Utility.RandomInt(1, 100) <= free
      Freed.Show()
      Return
    EndIf
    Int roll = rep + Utility.RandomInt(1, 100)
    If roll < 25
      fine *= 2
      days *= 2
    ElseIf roll > 75
      fine /= 2
      days /= 2
    EndIf
  EndIf
  Int gold = PlayerRef.GetGoldAmount()
  If fine > gold
    Int extra = (fine - gold + 39) / 40
    fine = gold
    days += extra
    NoGold.Show(extra)
  EndIf
  If fine > 0 && days > 0
    SentenceBoth.Show(fine, days)
  ElseIf fine > 0
    SentenceFine.Show(fine)
  ElseIf days > 0
    SentencePrison.Show(days)
  EndIf
  If fine > 0
    PlayerRef.RemoveItem(Game.GetForm(0xF), fine)
  EndIf
  If days > 0 && GameHour
    ; The days in prison pass.
    Game.FadeOutGame(False, True, 2.0, 1.0)
    GameHour.Mod(24.0 * days)
  EndIf
EndFunction
