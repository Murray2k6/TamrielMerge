ScriptName TMDfLaw extends Quest
{Daggerfall's law: the player's legal standing in the Iliac Bay's regions (TMDfQuestMachine.LegalOf: the quests' "legal
repute"), the crimes the regions see, the City Watch and the court, as Daggerfall Unity does (PlayerEntity,
DaggerfallCourtWindow, EnemyAttack; MIT):
- each region is a Skyrim crime faction (RegionCrime, DfLaw) whose people witness crimes; Skyrim's own guards do nothing
  about them. A rise in a region's crime gold is Daggerfall's crime, by what Skyrim added: violent 1000 or more murder,
  else assault; non-violent 25 pickpocketing, 5 trespassing, anything else theft. A street door bashed and noticed
  reports breaking and entering (CrimeReport). In a town the City Watch then come at once (2-5 of them, at most one group);
  in a dungeon nothing comes. Leaving the town forgets the crime, as leaving the location does in Daggerfall Unity;
- below -10 (banished: always), each game minute in that region's town streets has a 5% chance (banished: 10%) that the
  Watch come for criminal conspiracy;
- when the first reaches the player the standing falls by the crime's reputation loss (and the region's people think
  half that less of the player), and the player surrenders (to court) or fights;
- the court: punishment by the crime's penalty table (per standing point, base, least and most, FALL.EXE's), a point per
  40 gold a 40 gold fine or 3 days in prison at random, a fine beyond the player's gold served at 40 a day; a member of
  the Dark Brotherhood (assault, murder) or the Thieves Guild (breaking in, trespassing, pickpocketing) may be bribed free
  (rank 0-9 against 0-19); a standing below 0 risks banishment from the region (unless a -rep/2 and a -rep chance of 75 at
  most both fail); pleading guilty halves the sentence (or banishes); not guilty: debate (Etiquette) or lie (Streetwise),
  free with rep + (skill + Personality) / 2 percent (5-95; Skyrim's Speech for Personality), else banished or found
  guilty with rep + d100 under 25 doubling the fine and over 75 halving it;
- a sentence served (or a bribe, or freed) gives back half the crime's loss less one; prison is spent in Daggerfall's
  prison (JailMarker, DfLaw.BuildPrison) while the days count down, then 4 more hours pass and the player is let out at the
  town's gates (its map marker; elsewhere the region's jail exterior), healed. Daggerfall Unity takes no skills for it;
- every 112 days each region's standing moves 1 toward 0.}

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
Faction[] Property RegionCrime Auto
{Each region's crime faction (DFCrime00-61).}
GlobalVariable Property CrimeReport Auto
{A crime a street door saw (Daggerfall's crime number), or 0.}
Message[] Property ArrestFor Auto
{"Do you surrender?" for each crime (1-15 at 0-14).}
Message[] Property AccusedOf Auto
{The court's charge for each crime: guilty or not guilty.}
Message Property ThievesBribe Auto
Message Property BrotherhoodBribe Auto
ObjectReference[] Property JailMarker Auto
{Each region's prison marker in Daggerfall's prison.}
ObjectReference[] Property JailExterior Auto
{Each region's jail exterior (its largest town's gates on the overland).}

Float lastTime = -1.0
Float lastDrift = -1.0
Bool[] exiled
Actor[] guards
Bool pending = False
Int crime = 0
Int crimeRegion = -1
Int crimeTown = -1
Bool confronted = False
Bool sent = False
Int[] seenGold
Int[] seenViolent
Bool busy = False

Event OnInit()
  RegisterForSingleUpdate(5.0)
EndEvent

Event OnUpdate()
  If !busy
    Tick()
  EndIf
  RegisterForSingleUpdate(2.0)
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
  If !seenGold
    seenGold = new Int[64]
    seenViolent = new Int[64]
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
  Int town = Machine.CurrentTown
  Location here = Machine.SiteLocation(town)
  Bool inTown = here && PlayerRef.IsInLocation(here)
  Int region = Machine.SiteRegion(town)
  If inTown
    CheckCrimes(region)
  Else
    CheckCrimes(-1)
  EndIf
  ; Out of the town the crime was in: Daggerfall Unity forgets it.
  If crime > 0 && (!inTown || town != crimeTown)
    Forget()
  EndIf
  If pending
    GuardsApproach()
    Return
  EndIf
  If !inTown
    Return
  EndIf
  If crime > 0
    ; A crime seen in town: the Watch come at once (once for each crime).
    If !sent
      sent = True
      SpawnGuards(crimeRegion)
    EndIf
    Return
  EndIf
  If PlayerRef.IsInInterior()
    Return
  EndIf
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
    Commit(7, region, town)
    sent = True
    SpawnGuards(region)
  EndIf
EndFunction

; The crimes Skyrim saw since the last look, region by region, and what a street door reported; region: the town's the
; player is in, or -1 (out of town: each crime is its faction's region's).
Function CheckCrimes(Int region)
  Int town = Machine.CurrentTown
  If CrimeReport && CrimeReport.GetValueInt() > 0
    Int reported = CrimeReport.GetValueInt()
    CrimeReport.SetValue(0)
    If region >= 0
      Commit(reported, region, town)
    EndIf
  EndIf
  If !RegionCrime
    Return
  EndIf
  Int r = 0
  While r < RegionCrime.Length && r < 64
    Faction f = RegionCrime[r]
    If f
      Int gold = f.GetCrimeGold()
      If gold != seenGold[r]
        Int violent = f.GetCrimeGoldViolent()
        If gold > seenGold[r]
          Int kind = 13
          Int dv = violent - seenViolent[r]
          Int dn = (gold - violent) - (seenGold[r] - seenViolent[r])
          If dv >= 1000
            kind = 5
          ElseIf dv > 0
            kind = 4
          ElseIf dn == 25
            kind = 12
          ElseIf dn == 5
            kind = 2
          EndIf
          ; A shared interior is owned by one region's law: the crime is the region the player is in.
          If region >= 0
            Commit(kind, region, town)
          Else
            Commit(kind, r, town)
          EndIf
        EndIf
        seenGold[r] = gold
        seenViolent[r] = violent
      EndIf
    EndIf
    r += 1
  EndWhile
EndFunction

Function Commit(Int kind, Int region, Int town)
  If kind < 1 || kind > 15
    Return
  EndIf
  crime = kind
  crimeRegion = region
  crimeTown = town
  sent = False
EndFunction

; The crime forgotten: Skyrim's crime gold for it cleared too (Daggerfall's regions keep no bounty of Skyrim's).
Function Forget()
  If crimeRegion >= 0 && RegionCrime && crimeRegion < RegionCrime.Length && RegionCrime[crimeRegion]
    RegionCrime[crimeRegion].SetCrimeGold(0)
    RegionCrime[crimeRegion].SetCrimeGoldViolent(0)
    seenGold[crimeRegion] = 0
    seenViolent[crimeRegion] = 0
  EndIf
  crime = 0
  confronted = False
  If !pending
    ClearGuards()
  EndIf
EndFunction

Function SpawnGuards(Int region)
  If !Watch || pending
    Return
  EndIf
  Int n = Utility.RandomInt(2, 5)
  Float a = Utility.RandomFloat(0.0, 360.0)
  Bool inside = PlayerRef.IsInInterior()
  Int i = 0
  While i < n && i < guards.Length
    Actor g = PlayerRef.PlaceAtMe(Watch, 1, False, False) as Actor
    If g
      ; Out of sight in the streets; through the door indoors.
      Float r = Utility.RandomFloat(1200.0, 1800.0)
      If inside
        r = Utility.RandomFloat(150.0, 300.0)
      EndIf
      g.MoveTo(PlayerRef, r * Math.Sin(a + i * 15.0), r * Math.Cos(a + i * 15.0), 0.0)
      If RegionCrime && region >= 0 && region < RegionCrime.Length && RegionCrime[region]
        g.SetCrimeFaction(RegionCrime[region])
      EndIf
      g.SetActorValue("Aggression", 0.0)
      g.PathToReference(PlayerRef, 1.0)
      guards[i] = g
    EndIf
    i += 1
  EndWhile
  pending = True
EndFunction

; The City Watch reach the player: the standing falls, and the player surrenders or fights.
Function GuardsApproach()
  Bool near = False
  Bool any = False
  Int i = 0
  While i < guards.Length
    If guards[i] && !guards[i].IsDead()
      any = True
      If guards[i].GetDistance(PlayerRef) < 500.0
        near = True
      ElseIf !guards[i].IsInCombat()
        guards[i].PathToReference(PlayerRef, 1.0)
      EndIf
    EndIf
    i += 1
  EndWhile
  If !any
    pending = False
    confronted = False
    Return
  EndIf
  If !near || confronted
    Return
  EndIf
  confronted = True
  If crime <= 0
    Commit(7, Machine.SiteRegion(Machine.CurrentTown), Machine.CurrentTown)
  EndIf
  LowerRepForCrime(crimeRegion, crime)
  Message ask = Arrest
  If ArrestFor && crime - 1 < ArrestFor.Length && ArrestFor[crime - 1]
    ask = ArrestFor[crime - 1]
  EndIf
  If ask.Show() == 0
    pending = False
    busy = True
    Court(crimeRegion, crime)
    busy = False
  Else
    i = 0
    While i < guards.Length
      If guards[i]
        guards[i].SetActorValue("Aggression", 1.0)
        guards[i].StartCombat(PlayerRef)
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
  pending = False
EndFunction

; Dice100.FailedRoll: a d100 over the chance.
Bool Function Failed(Int chance)
  Return Utility.RandomInt(1, 100) > chance
EndFunction

; FALL.EXE's reputation loss for each crime (0 none, then 1-15).
Int Function RepLoss(Int c)
  Int[] t = new Int[16]
  t[1] = 10
  t[2] = 5
  t[3] = 10
  t[4] = 8
  t[5] = 20
  t[6] = 10
  t[7] = 2
  t[8] = 1
  t[9] = 2
  t[10] = 2
  t[11] = 75
  t[12] = 2
  t[13] = 8
  t[14] = 36
  t[15] = 10
  If c < 0 || c > 15
    Return 0
  EndIf
  Return t[c]
EndFunction

; The court's tables (DaggerfallCourtWindow, from FALL.EXE): which 0 per standing point, 1 base, 2 least, 3 most.
Int Function Penalty(Int which, Int c)
  Int[] t = new Int[15]
  If which == 0
    t[0] = 5
    t[1] = 5
    t[2] = 6
    t[3] = 6
    t[4] = 10
    t[5] = 5
    t[6] = 5
    t[7] = 3
    t[8] = 8
    t[9] = 8
    t[10] = 9
    t[11] = 6
    t[12] = 0
    t[13] = 8
    t[14] = 0
  ElseIf which == 1
    t[0] = 300
    t[1] = 200
    t[2] = 600
    t[3] = 1000
    t[4] = 10000
    t[5] = 200
    t[6] = 500
    t[7] = 100
    t[8] = 500
    t[9] = 500
    t[10] = 1200
    t[11] = 200
    t[12] = 200
    t[13] = 1000
    t[14] = 100
  ElseIf which == 2
    t[0] = 50
    t[1] = 10
    t[2] = 80
    t[3] = 10
    t[4] = 9000
    t[5] = 10
    t[6] = 10
    t[7] = 2
    t[8] = 10
    t[9] = 10
    t[10] = 160
    t[11] = 5
    t[12] = 5
    t[13] = 10
    t[14] = 4
  Else
    t[0] = 1000
    t[1] = 800
    t[2] = 1200
    t[3] = 1500
    t[4] = 12000
    t[5] = 12000
    t[6] = 1500
    t[7] = 700
    t[8] = 1500
    t[9] = 1500
    t[10] = 2000
    t[11] = 1000
    t[12] = 1000
    t[13] = 1500
    t[14] = 700
  EndIf
  If c < 0 || c > 14
    Return 0
  EndIf
  Return t[c]
EndFunction

; The region's people (Daggerfall's faction of type 15 there) think the amount better or worse of the player.
Function ChangePeople(Int region, Int amount)
  If Machine.Reputation && amount != 0
    Int people = Machine.Reputation.OfType(15, region)
    If people > 0
      Machine.Reputation.ChangeRep(people, amount, True)
    EndIf
  EndIf
EndFunction

; PlayerEntity.LowerRepForCrime.
Function LowerRepForCrime(Int region, Int c)
  If region < 0
    Return
  EndIf
  Int loss = RepLoss(c)
  Machine.SetLegalOf(region, Machine.LegalOf(region) - loss)
  ChangePeople(region, -(loss / 2))
EndFunction

; PlayerEntity.RaiseReputationForDoingSentence: half the loss, less one.
Function RaiseRepForSentence(Int region, Int c)
  If region < 0
    Return
  EndIf
  Int half = RepLoss(c) / 2
  Machine.SetLegalOf(region, Machine.LegalOf(region) + half - 1)
  ChangePeople(region, (half - 1) / 2)
EndFunction

; A guild bribes the judge for a member: rank (0-9) at least 0-19 at random.
Bool Function GuildBribes(Int g)
  If g < 0 || !Machine.Guilds || !Machine.Guilds.IsMember(g)
    Return False
  EndIf
  Return PlayerRef.GetFactionRank(Machine.Guilds.Members[g]) >= Utility.RandomInt(0, 19)
EndFunction

; DaggerfallCourtWindow for crime c (1-15) in a region.
Function Court(Int region, Int c)
  ClearGuards()
  Int k = c - 1
  If k < 0 || k > 14
    k = 6
  EndIf
  Int rep = Machine.LegalOf(region)
  ; Banishment possible unless both rolls fail.
  Int t1 = 0
  Int t2 = 0
  If rep < 0
    t1 = -rep
    t2 = -rep / 2
    If t1 > 75
      t1 = 75
    EndIf
    If t2 > 75
      t2 = 75
    EndIf
  EndIf
  Bool banishable = !(Failed(t2) && Failed(t1))
  Int penalty = Penalty(0, k) * rep + Penalty(1, k)
  If rep < 0
    penalty = Penalty(1, k) - Penalty(0, k) * rep
  EndIf
  If penalty > Penalty(3, k)
    penalty = Penalty(3, k)
  ElseIf penalty < Penalty(2, k)
    penalty = Penalty(2, k)
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
  Int gold = PlayerRef.GetGoldAmount()
  If fine > gold
    Int extra = (fine - gold) / 40
    days += extra
    fine = gold
    If extra > 0
      NoGold.Show(extra)
    EndIf
  EndIf
  ; The Dark Brotherhood for assault and murder, the Thieves Guild for breaking in, trespassing and pickpocketing.
  If (k == 3 || k == 4) && Machine.Guilds && GuildBribes(Machine.Guilds.BrotherhoodIndex)
    If BrotherhoodBribe
      BrotherhoodBribe.Show()
    EndIf
    RaiseRepForSentence(region, c)
    Release(region)
    Return
  EndIf
  If (k <= 2 || k == 11) && Machine.Guilds && GuildBribes(Machine.Guilds.ThievesIndex)
    If ThievesBribe
      ThievesBribe.Show()
    EndIf
    RaiseRepForSentence(region, c)
    Release(region)
    Return
  EndIf
  Message charge = Accused
  If AccusedOf && k < AccusedOf.Length && AccusedOf[k]
    charge = AccusedOf[k]
  EndIf
  If charge.Show() == 0
    ; Guilty: the court's mercy, or banishment.
    If banishable
      Banish(region)
      Return
    EndIf
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
    If !Failed(free)
      Freed.Show()
      RaiseRepForSentence(region, c)
      Release(region)
      Return
    EndIf
    If banishable
      Banish(region)
      Return
    EndIf
    Int roll = rep + Utility.RandomInt(1, 100)
    If roll < 25
      fine *= 2
    ElseIf roll > 75
      fine /= 2
    EndIf
  EndIf
  gold = PlayerRef.GetGoldAmount()
  If fine > gold
    days += (fine - gold) / 40
    fine = gold
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
  RaiseRepForSentence(region, c)
  If days > 0
    Serve(region, days)
  EndIf
  Release(region)
EndFunction

Function Banish(Int region)
  Banished.Show()
  If region >= 0 && region < 64
    exiled[region] = True
  EndIf
  Release(region)
EndFunction

; The days in Daggerfall's prison, counted down as its prison screen does, then passed.
Function Serve(Int region, Int days)
  Game.DisablePlayerControls()
  Game.FadeOutGame(True, True, 0.0, 1.0)
  If JailMarker && region >= 0 && region < JailMarker.Length && JailMarker[region]
    PlayerRef.MoveTo(JailMarker[region])
  EndIf
  Utility.Wait(1.0)
  Game.FadeOutGame(False, True, 0.5, 1.0)
  Int left = days
  While left > 0
    Debug.Notification(left + " days until freedom")
    Utility.Wait(0.3)
    left -= 1
  EndWhile
  If GameHour
    GameHour.Mod(24.0 * days)
  EndIf
  Game.IncrementStat("Days Jailed", days)
EndFunction

; ReleaseFromPrison: 4 hours pass, the crime is forgotten, and the player is healed and let out at the town's gates.
Function Release(Int region)
  Game.DisablePlayerControls()
  Game.FadeOutGame(True, True, 0.0, 1.0)
  If GameHour
    GameHour.Mod(4.0)
  EndIf
  PlayerRef.RestoreActorValue("Health", 100000.0)
  PlayerRef.RestoreActorValue("Magicka", 100000.0)
  PlayerRef.RestoreActorValue("Stamina", 100000.0)
  ObjectReference gate = None
  Int town = Machine.CurrentTown
  If town >= 0 && Machine.SiteRegion(town) == region
    gate = Machine.SiteRef(town, 0)
  EndIf
  If !gate && JailExterior && region >= 0 && region < JailExterior.Length
    gate = JailExterior[region]
  EndIf
  If gate
    PlayerRef.MoveTo(gate)
  EndIf
  crimeRegion = region
  Forget()
  Utility.Wait(0.5)
  Game.FadeOutGame(False, True, 0.5, 1.0)
  Game.EnablePlayerControls()
EndFunction
