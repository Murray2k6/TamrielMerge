ScriptName TMMwPlayerState extends ReferenceAlias
{The player, on the quest MWScriptState: what Morrowind's dialogue asks of the player that Skyrim's conditions can't,
kept in globals the converted dialogue reads - PCClothingModifier (OpenMW filter.cpp: the worth of what is worn in the
slots before the hands, i.e. every armour and clothing piece but a shield), kept as each is put on or taken off.}

GlobalVariable Property ClothingValue Auto
Faction Property Crime Auto
{MWCrimeFaction: the player's bounty in Morrowind's lands (a failed pickpocketing raises it).}
Float Property AlarmRadius = 2000.0 Auto
{fAlarmRadius: how far a crime is seen (OpenMW commitCrime's neighbours).}
GlobalVariable Property LoadCount Auto
{Games loaded (TMMwActorAI starts its friendly hits again at each, as Morrowind doesn't save them).}

Event OnInit()
  UpdateClothing()
  Watch()
EndEvent

Function Watch()
  RegisterForMenu("ContainerMenu")
  RegisterForMenu("Lockpicking Menu")
EndFunction

Event OnPlayerLoadGame()
  UpdateClothing()
  Watch()
  If LoadCount
    LoadCount.Mod(1.0)
  EndIf
  Int handle = ModEvent.Create("TMMwGameLoaded")
  If handle
    ModEvent.Send(handle)
  EndIf
EndEvent

Event OnObjectEquipped(Form akBaseObject, ObjectReference akReference)
  UpdateClothing()
EndEvent

Event OnObjectUnequipped(Form akBaseObject, ObjectReference akReference)
  UpdateClothing()
EndEvent

; ---- crimes seen: the witnesses' voices (OpenMW mechanicsmanagerimp.cpp commitCrime, reportCrime) ----

Int bountyAtOpen = 0
ObjectReference pickTarget
ObjectReference lockTarget
Bool lockWasLocked

; Taking what someone else owns (not in barter): theft; every Morrowind person in the alarm radius who sees it says
; Thief, though he will do nothing about it.
Event OnItemAdded(Form akBaseItem, Int aiItemCount, ObjectReference akItemReference, ObjectReference akSourceContainer)
  If UI.IsMenuOpen("BarterMenu") || UI.IsMenuOpen("GiftMenu")
    Return
  EndIf
  Actor p = GetActorReference()
  Actor fromActor = akSourceContainer as Actor
  If fromActor
    ; Taken from a living person's pockets: pickpocketing, a crime only when it fails (OnMenuClose).
    Return
  EndIf
  Form owner = None
  If akItemReference
    owner = akItemReference.GetActorOwner()
    If !owner
      owner = akItemReference.GetFactionOwner()
    EndIf
  ElseIf akSourceContainer
    owner = akSourceContainer.GetActorOwner()
    If !owner
      owner = akSourceContainer.GetFactionOwner()
    EndIf
  EndIf
  If !owner && !akSourceContainer && p.GetParentCell()
    owner = p.GetParentCell().GetActorOwner()
    If !owner
      owner = p.GetParentCell().GetFactionOwner()
    EndIf
  EndIf
  If !owner || owner == p.GetActorBase() || (owner as Faction && p.IsInFaction(owner as Faction))
    Return
  EndIf
  Witnesses(None, "Thief", False)
EndEvent

Event OnMenuOpen(String asMenuName)
  If asMenuName == "ContainerMenu"
    pickTarget = Game.GetCurrentCrosshairRef()
    If Crime
      bountyAtOpen = Crime.GetCrimeGold()
    EndIf
  ElseIf asMenuName == "Lockpicking Menu"
    lockTarget = Game.GetCurrentCrosshairRef()
    lockWasLocked = lockTarget && lockTarget.IsLocked()
  EndIf
EndEvent

Event OnMenuClose(String asMenuName)
  If asMenuName == "ContainerMenu"
    ; Caught picking a pocket: the bounty rose while the pockets were open; the victim knows, the others if they see.
    Actor victim = pickTarget as Actor
    If victim && !victim.IsDead() && Crime && Crime.GetCrimeGold() > bountyAtOpen
      Witnesses(victim, "Thief", False)
    EndIf
    pickTarget = None
  ElseIf asMenuName == "Lockpicking Menu"
    ; An owned lock picked: trespassing, which those whose Alarm is 100 or more report, saying Intruder.
    If lockTarget && lockWasLocked && !lockTarget.IsLocked() && (lockTarget.GetActorOwner() || lockTarget.GetFactionOwner())
      Witnesses(None, "Intruder", True)
    EndIf
    lockTarget = None
  EndIf
EndEvent

; Each Morrowind person near the player (within fAlarmRadius, the victim wherever) who can report a crime (canReportCrime:
; alive, not fighting the victim, conscious, not the player's companion) and sees it (in sight and aware of the player;
; the victim of a pickpocketing aware anyway) says the topic; abReporting: only those who report crimes (Alarm 100).
Function Witnesses(Actor akVictim, String asTopic, Bool abReporting)
  Quest q = Quest.GetQuest("MWVoices")
  If !q
    Return
  EndIf
  TMMwVoices voices = q as TMMwVoices
  Topic t = voices.ThiefTopic
  If asTopic == "Intruder"
    t = voices.IntruderTopic
  EndIf
  Actor p = GetActorReference()
  Cell here = p.GetParentCell()
  If !here || !t
    Return
  EndIf
  Int i = here.GetNumRefs(62)
  While i > 0
    i -= 1
    TMMwActorAI mw = here.GetNthRef(i, 62) as TMMwActorAI
    If mw && mw.IsPerson && !mw.IsDead() && !mw.IsUnconscious() && !mw.IsPlayerTeammate() && !(akVictim && mw.IsInCombat() && mw.GetCombatTarget() == akVictim)
      If (mw == akVictim || mw.GetDistance(p) <= AlarmRadius) && (!abReporting || mw.Alarm >= 100)
        If (mw == akVictim) || (mw.HasLOS(p) && p.IsDetectedBy(mw))
          voices.Speak(mw, t)
        EndIf
      EndIf
    EndIf
  EndWhile
EndFunction

Function UpdateClothing()
  Actor p = GetActorReference()
  If !p || !ClothingValue
    Return
  EndIf
  Form[] seen = new Form[32]
  Int n = 0
  Int total = 0
  Int slot = 30
  While slot < 62
    Armor worn = p.GetWornForm(Armor.GetMaskForSlot(slot)) as Armor
    If worn && !worn.IsShield() && seen.Find(worn) < 0
      seen[n] = worn
      n += 1
      total += worn.GetGoldValue()
    EndIf
    slot += 1
  EndWhile
  ClothingValue.SetValue(total)
EndFunction
