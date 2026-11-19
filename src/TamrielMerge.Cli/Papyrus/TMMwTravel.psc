ScriptName TMMwTravel extends Quest
{Morrowind's travel services (silt striders, boats, gondolas, the Mages Guild's guild guides), on the quest MWTravel:
whoever has travel destinations (NPC_ DODT/DNAM) offers the Travel topic, whose response calls Offer, as OpenMW's
travel window does (apps/openmw/mwgui/travelwindow.cpp):
- each destination is listed with its price: a service inside (the guild guides) fMagesGuildTravel flat; outside, the
  distance from the player to the destination over fTravelMult, at least 1, through Morrowind's barter offer
  (mechanicsmanagerimp.cpp getBarterOffer), times one more than the followers travelling along;
- the chosen journey takes the gold (to the service's pockets), to an outside destination passes the horizontal
  distance over fTravelTimeMult whole hours with the player resting through them (Actors::rest(hours, sleep)), plays
  "mysticism area" for a guild guide, and moves the player and their followers to the destination's marker (its
  position and heading);
- GetPCTraveling (Traveling) is true from the choice until a moment after the arrival (OpenMW setPlayerTraveling,
  reset on the next world update; the translated scripts tick every half second).}

FormList Property ServiceNpcs Auto
{The people offering travel, in the order of Destinations and Menus.}
FormList Property Destinations Auto
{Per service, a form list of its destination markers (XMarkerHeading at each DODT).}
FormList Property Menus Auto
{Per service, the message listing its destinations (a button each, Cancel last; nine prices at most).}
Actor Property PlayerRef Auto
GlobalVariable Property GameHour Auto
MiscObject Property Coin Auto
TMMwRest Property Rest Auto
{The rest through the journey's hours.}
Float Property TravelMult = 4000.0 Auto
{fTravelMult: units of distance per gold outside.}
Float Property GuildTravel = 10.0 Auto
{fMagesGuildTravel: the flat price of a service inside.}
Float Property TravelTimeMult = 16000.0 Auto
{fTravelTimeMult: units of horizontal distance per hour of the journey.}
Float Property FatigueBase = 1.25 Auto
Float Property FatigueMult = 0.5 Auto
{fFatigueBase, fFatigueMult: the fatigue term of the barter offer (CreatureStats::getFatigueTerm).}
Sound Property GuildSound Auto
{"mysticism area": a guild guide's teleport.}
TMMwState Property MwState Auto
{The kept attributes and skills (Mercantile, Luck, Personality) for the barter offer.}
TMMwAI Property MwAI Auto
{Morrowind's companions (AIFollow on the player) travel along.}
Bool Property Traveling = False Auto Hidden
{GetPCTraveling.}

; Offered by the Travel topic's response (TMMwTravelInfo): the speaker's destinations with their prices now, and the
; journey chosen.
Function Offer(Actor akSpeaker)
  If !akSpeaker || !ServiceNpcs || !Destinations || !Menus
    Return
  EndIf
  Int who = ServiceNpcs.Find(akSpeaker.GetBaseObject())
  If who < 0
    Return
  EndIf
  FormList dests = Destinations.GetAt(who) as FormList
  Message menu = Menus.GetAt(who) as Message
  If !dests || !menu
    Return
  EndIf
  Int n = dests.GetSize()
  If n > 9
    n = 9
  EndIf
  Int followers = CountFollowers()
  Float[] prices = new Float[9]
  Int i = 0
  While i < n
    prices[i] = Price(akSpeaker, dests.GetAt(i) as ObjectReference, followers) as Float
    i += 1
  EndWhile
  Int choice = menu.Show(prices[0], prices[1], prices[2], prices[3], prices[4], prices[5], prices[6], prices[7], prices[8])
  If choice < 0 || choice >= n
    Return
  EndIf
  Go(akSpeaker, dests.GetAt(choice) as ObjectReference, prices[choice] as Int)
EndFunction

; TravelWindow::addDestination: inside, fMagesGuildTravel; outside, the straight distance from the player to the
; destination over fTravelMult; at least 1; the barter offer; and one more than the followers (OpenMW: "unlike vanilla
; the first follower doesn't travel for free").
Int Function Price(Actor akSpeaker, ObjectReference akDest, Int aiFollowers)
  Int price
  If akSpeaker.IsInInterior()
    price = GuildTravel as Int
  ElseIf akDest
    Float dx = akDest.GetPositionX() - PlayerRef.GetPositionX()
    Float dy = akDest.GetPositionY() - PlayerRef.GetPositionY()
    Float dz = akDest.GetPositionZ() - PlayerRef.GetPositionZ()
    price = (Math.sqrt(dx * dx + dy * dy + dz * dz) / TravelMult) as Int
  EndIf
  If price < 1
    price = 1
  EndIf
  price = BarterOffer(akSpeaker, price)
  Return price * (1 + aiFollowers)
EndFunction

; MechanicsManager::getBarterOffer, buying: the player's Mercantile (at most 100), Luck/10 and Personality/5 (at most
; 10 each) and the seller's disposition against the seller's own, each side times its fatigue term; the price times
; 0.01 x (100 - (pcTerm - npcTerm) / 2), at least 1. A creature's price is the base price.
Int Function BarterOffer(Actor akSeller, Int aiBase)
  If aiBase == 0
    Return 0
  EndIf
  TMMwActorAI mw = akSeller as TMMwActorAI
  If mw && !mw.IsPerson
    Return aiBase
  EndIf
  Int disposition = TMMwScript.GetDisposition(akSeller)
  Float a = 0.0
  Float b = 0.0
  Float c = 0.0
  Float d = 0.0
  Float e = 0.0
  Float f = 0.0
  If MwState
    ; Stats: 0 Strength .. 6 Personality, 7 Luck; 8 + skill, Mercantile being skill 24.
    a = Least(MwState.GetStat(PlayerRef, 32, -1), 100.0)
    b = Least(0.1 * MwState.GetStat(PlayerRef, 7, -1), 10.0)
    c = Least(0.2 * MwState.GetStat(PlayerRef, 6, -1), 10.0)
    d = Least(MwState.GetStat(akSeller, 32, -1), 100.0)
    e = Least(0.1 * MwState.GetStat(akSeller, 7, -1), 10.0)
    f = Least(0.2 * MwState.GetStat(akSeller, 6, -1), 10.0)
  EndIf
  Float pcTerm = (disposition - 50 + a + b + c) * FatigueTerm(PlayerRef)
  Float npcTerm = (d + e + f) * FatigueTerm(akSeller)
  Float buyTerm = 0.01 * (100.0 - 0.5 * (pcTerm - npcTerm))
  Int offer = (aiBase * buyTerm) as Int
  If offer < 1
    offer = 1
  EndIf
  Return offer
EndFunction

Float Function Least(Float a, Float b)
  If a < b
    Return a
  EndIf
  Return b
EndFunction

; CreatureStats::getFatigueTerm: fFatigueBase - fFatigueMult x (1 - fatigue / its base); Skyrim's stamina stands in.
Float Function FatigueTerm(Actor a)
  Float normalised = a.GetActorValuePercentage("Stamina")
  If normalised < 0.0
    normalised = 0.0
  ElseIf normalised > 1.0
    normalised = 1.0
  EndIf
  Return FatigueBase - FatigueMult * (1.0 - normalised)
EndFunction

; Those travelling with the player (ActionTeleport::getFollowers): Morrowind's companions following the player
; (TMMwAI's follow aliases) and Skyrim's teammates in the player's cell, alive. Followers(akMoveTo) moves them.
Int Function CountFollowers()
  Return Followers(None)
EndFunction

Int Function Followers(ObjectReference akMoveTo)
  Int count = 0
  If MwAI && MwAI.FollowSlots
    Int i = 0
    While i < MwAI.FollowSlots.Length
      If MwAI.FollowSlots[i]
        Actor a = MwAI.FollowSlots[i].GetActorReference()
        If a && a != PlayerRef && !a.IsDead()
          If akMoveTo
            a.MoveTo(akMoveTo, 64.0 * (count + 1), 0.0, 0.0)
          EndIf
          count += 1
        EndIf
      EndIf
      i += 1
    EndWhile
  EndIf
  Cell here = PlayerRef.GetParentCell()
  If here && SKSE.GetVersion() > 0
    ; Actors are form type 43 (SKSE Cell.GetNthRef).
    Int n = here.GetNumRefs(43)
    Int k = 0
    While k < n
      Actor t = here.GetNthRef(k, 43) as Actor
      If t && t != PlayerRef && t.IsPlayerTeammate() && !t.IsDead() && !AmongFollowSlots(t)
        If akMoveTo
          t.MoveTo(akMoveTo, 64.0 * (count + 1), 0.0, 0.0)
        EndIf
        count += 1
      EndIf
      k += 1
    EndWhile
  EndIf
  Return count
EndFunction

Bool Function AmongFollowSlots(Actor a)
  If !MwAI || !MwAI.FollowSlots
    Return False
  EndIf
  Int i = 0
  While i < MwAI.FollowSlots.Length
    If MwAI.FollowSlots[i] && MwAI.FollowSlots[i].GetActorReference() == a
      Return True
    EndIf
    i += 1
  EndWhile
  Return False
EndFunction

; TravelWindow::onTravelButtonClick: the gold (the player must have it; Morrowind greys out what they can't afford),
; the guild guide's sound, the hours of an outside journey rested through and passed, the followers and the player
; moved, the screen fading out and in.
Function Go(Actor akSpeaker, ObjectReference akDest, Int aiPrice)
  If !akDest
    Return
  EndIf
  If PlayerRef.GetGoldAmount() < aiPrice
    Debug.Notification("You do not have enough gold.")
    Return
  EndIf
  Traveling = True
  If akSpeaker.IsInInterior() && GuildSound
    GuildSound.Play(PlayerRef)
  EndIf
  If Coin && aiPrice > 0
    PlayerRef.RemoveItem(Coin, aiPrice, True, akSpeaker)
  EndIf
  If !akDest.IsInInterior()
    Float dx = akDest.GetPositionX() - PlayerRef.GetPositionX()
    Float dy = akDest.GetPositionY() - PlayerRef.GetPositionY()
    Int hours = (Math.sqrt(dx * dx + dy * dy) / TravelTimeMult) as Int
    If hours > 0
      If Rest
        Rest.Restore(hours, True)
      EndIf
      If GameHour
        GameHour.Mod(hours as Float)
      EndIf
    EndIf
  EndIf
  Game.FadeOutGame(True, True, 0.5, 1.0)
  Followers(akDest)
  PlayerRef.MoveTo(akDest)
  Game.FadeOutGame(False, True, 0.5, 1.0)
  ; One tick of the translated scripts sees the travel (they run every 0.5 s).
  Utility.Wait(1.0)
  Traveling = False
EndFunction
