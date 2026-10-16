ScriptName TMMwLaw extends Quest
{Morrowind's law (docs/progression.md, "Crime by game and area"): its one bounty is the crime faction MWCrimeFaction
(Morrowind's people answer to it, its guards are Skyrim's guards of it, who pursue a wanted player and force-greet them
with Morrowind's own arrest greetings), and what Morrowind's engine does for its dialogue and scripts (as OpenMW does):
- the dialogue's crime globals, kept up to date: PCHasCrimeGold (the gold covers the bounty), CrimeGoldDiscount (the
  Thieves Guild's price, bounty x fCrimeGoldDiscountMult) and PCHasGoldDiscount, CrimeGoldTurnIn (bounty x
  fCrimeGoldTurnInMult) and PCHasTurnIn;
- PayFine: the bounty cleared and the stolen goods confiscated (the gold is the dialogue's own RemoveItem); PayFineThief
  (the Thieves Guild, or a writ): the bounty cleared, the goods kept;
- GoToJail: max(1, bounty / iDaysinPrisonMod) days at hard labour: the stolen goods confiscated, the bounty cleared,
  the player taken to the nearest of Morrowind's prison markers (from inside, the nearest to the interior's door to the
  world), the days passed, and for each day one skill at random changed by one: Sneak and Security up, any other down.}

Faction Property Crime Auto
Actor Property PlayerRef Auto
ObjectReference[] Property Markers Auto
{Morrowind's prison markers.}
Float[] Property MarkerX Auto
Float[] Property MarkerY Auto
{Where each marker is in the world (an interior's: its door to the outside).}
Static Property WorldSpot Auto
{The marker in each converted interior linked to its door to the world (TesMagic).}
GlobalVariable Property PCHasCrimeGold Auto
GlobalVariable Property PCHasGoldDiscount Auto
GlobalVariable Property CrimeGoldDiscount Auto
GlobalVariable Property CrimeGoldTurnIn Auto
GlobalVariable Property PCHasTurnIn Auto
Float Property DiscountMult = 0.5 Auto
Float Property TurnInMult = 0.9 Auto
Int Property DaysPerGold = 100 Auto
GlobalVariable Property GameHour Auto
MiscObject Property Coin Auto
Bool Property InJail = False Auto Hidden

Event OnInit()
  RegisterForSingleUpdate(2.0)
EndEvent

Event OnUpdate()
  UpdateGlobals()
  RegisterForSingleUpdate(1.0)
EndEvent

Function SetIf(GlobalVariable g, Float value)
  If g && g.GetValue() != value
    g.SetValue(value)
  EndIf
EndFunction

; World::updateDialogueGlobals.
Function UpdateGlobals()
  If !Crime
    Return
  EndIf
  Int bounty = Crime.GetCrimeGold()
  Int gold = PlayerRef.GetGoldAmount()
  Int discount = (bounty * DiscountMult) as Int
  Int turnIn = (bounty * TurnInMult) as Int
  If bounty > 0
    If discount < 1
      discount = 1
    EndIf
    If turnIn < 1
      turnIn = 1
    EndIf
  EndIf
  SetIf(PCHasCrimeGold, (bounty <= gold) as Float)
  SetIf(PCHasGoldDiscount, (discount <= gold) as Float)
  SetIf(CrimeGoldDiscount, discount)
  SetIf(CrimeGoldTurnIn, turnIn)
  SetIf(PCHasTurnIn, (turnIn <= gold) as Float)
EndFunction

; The stolen goods the player carries go to the law's evidence (Skyrim's own payment does that: a coin is paid for it).
Function Confiscate()
  If !Crime || !Coin
    Return
  EndIf
  Int bounty = Crime.GetCrimeGold()
  If bounty <= 0
    Crime.SetCrimeGold(1)
    bounty = 1
  EndIf
  PlayerRef.AddItem(Coin, bounty, True)
  Crime.PlayerPayCrimeGold(True, False)
EndFunction

Function PayFine(Bool abTakeStolen)
  If !Crime
    Return
  EndIf
  If abTakeStolen
    Confiscate()
  EndIf
  Crime.SetCrimeGold(0)
  Crime.SetCrimeGoldViolent(0)
  UpdateGlobals()
EndFunction

; The nearest prison marker to where the player is in the world.
ObjectReference Function NearestMarker()
  If !Markers || Markers.Length == 0
    Return None
  EndIf
  ; One in the player's own cell first (Mournhold's palace courtyard is an interior with no door to the world).
  Cell here = PlayerRef.GetParentCell()
  Int k = 0
  While k < Markers.Length
    If Markers[k] && Markers[k].GetParentCell() == here
      Return Markers[k]
    EndIf
    k += 1
  EndWhile
  ObjectReference from = PlayerRef
  If PlayerRef.IsInInterior()
    from = OutsideSpot(here)
  EndIf
  If !from
    Return Markers[0]
  EndIf
  Float x = from.GetPositionX()
  Float y = from.GetPositionY()
  ObjectReference best = Markers[0]
  Float bestDistance = -1.0
  Int i = 0
  While i < Markers.Length
    If Markers[i] && i < MarkerX.Length
      Float dx = MarkerX[i] - x
      Float dy = MarkerY[i] - y
      Float d = dx * dx + dy * dy
      If bestDistance < 0.0 || d < bestDistance
        best = Markers[i]
        bestDistance = d
      EndIf
    EndIf
    i += 1
  EndWhile
  Return best
EndFunction

; The outside door an interior leads to, through its WorldSpot marker (SKSE Cell.GetNthRef; statics are form type 34).
ObjectReference Function OutsideSpot(Cell here)
  If !here || !WorldSpot || SKSE.GetVersion() <= 0
    Return None
  EndIf
  Int n = here.GetNumRefs(34)
  Int i = 0
  While i < n
    ObjectReference r = here.GetNthRef(i, 34)
    If r && r.GetBaseObject() == WorldSpot
      Return r.GetLinkedRef()
    EndIf
    i += 1
  EndWhile
  Return None
EndFunction

; Skyrim's skills in Morrowind's order, for the day's skill change.
String Function SkillName(Int i)
  String[] names = new String[18]
  names[0] = "OneHanded"
  names[1] = "TwoHanded"
  names[2] = "Marksman"
  names[3] = "Block"
  names[4] = "Smithing"
  names[5] = "HeavyArmor"
  names[6] = "LightArmor"
  names[7] = "Pickpocket"
  names[8] = "Lockpicking"
  names[9] = "Sneak"
  names[10] = "Alchemy"
  names[11] = "Speechcraft"
  names[12] = "Alteration"
  names[13] = "Conjuration"
  names[14] = "Destruction"
  names[15] = "Illusion"
  names[16] = "Restoration"
  names[17] = "Enchanting"
  Return names[i]
EndFunction

Function GoToJail()
  If !Crime
    Return
  EndIf
  Int bounty = Crime.GetCrimeGold()
  Int per = DaysPerGold
  If per < 1
    per = 100
  EndIf
  Int days = bounty / per
  If days < 1
    days = 1
  EndIf
  Confiscate()
  Crime.SetCrimeGold(0)
  Crime.SetCrimeGoldViolent(0)
  InJail = True
  Game.DisablePlayerControls()
  Game.FadeOutGame(True, True, 0.5, 2.0)
  ObjectReference spot = NearestMarker()
  If spot
    PlayerRef.MoveTo(spot)
  EndIf
  If GameHour
    GameHour.Mod(24.0 * days)
  EndIf
  Game.IncrementStat("Days Jailed", days)
  ; Each day, a skill at random: Sneak and Security (Lockpicking) rise, the rest fall.
  Int d = 0
  While d < days
    String skill = SkillName(Utility.RandomInt(0, 17))
    If skill == "Sneak" || skill == "Lockpicking"
      Game.IncrementSkillBy(skill, 1)
    Else
      Float base = PlayerRef.GetBaseActorValue(skill)
      If base > 1.0
        PlayerRef.SetActorValue(skill, base - 1.0)
      EndIf
    EndIf
    d += 1
  EndWhile
  Debug.Notification("You served " + days + " days of hard labour.")
  Utility.Wait(1.0)
  Game.FadeOutGame(False, True, 0.5, 2.0)
  Game.EnablePlayerControls()
  InJail = False
  UpdateGlobals()
EndFunction
