ScriptName TMMwRest extends Quest
{Morrowind's rest rules in Morrowind's lands, on the quest MWRest (OpenMW apps/openmw/mwworld/worldimp.cpp canRest,
mwgui/waitdialog.cpp startWaiting/onFrame, mwmechanics/actors.cpp restoreDynamicStats, worldimp.cpp
spawnRandomCreature):
- where resting is allowed - anywhere but a cell flagged "illegal to sleep here" (MWNoSleepCells), on the ground and
  out of the water - Skyrim's wait is Morrowind's rest: a sleep without a bed. In a no-sleep cell it is Morrowind's
  wait (sRestIllegal shown), and a bed is a bed anywhere. In the air or under water the menu is closed with
  sNotifyMessage1; enemies near, Skyrim refuses the menu itself (Morrowind's sNotifyMessage2).
- a rest restores an hour: health 0.1 x Endurance, magicka fRestMagicMult x Intelligence (none under Stunted Magicka,
  the Atronach sign); resting or waiting, fatigue 3,600 x (fFatigueReturnBase + fFatigueReturnMult x (1 -
  encumbrance)) x fEndFatigueMult x Endurance, unless it is full. Each is applied as the share of Morrowind's own
  maximum it is (health (Strength + Endurance) / 2 + fLevelUpHealthEndMult x Endurance a level; magicka
  fPCbaseMagickaMult x Intelligence; fatigue Strength + Willpower + Agility + Endurance) of Skyrim's.
- sleeping outside in a region with a sleep list: a roll of 0..hours-1 under fSleepRandMod x hours interrupts the
  sleep (fSleepRestMod x hours) whole hours before its end, when sSleepInterrupt is shown and 1 + rand(iNumberCreatures)
  of the list's creatures are placed 220 units in front of the player. The hours asked for are read from the menu's
  slider as the rest begins; if that can't be read, the roll is made as the rest ends over the hours slept, the
  restoration shortened by the hours the interruption would have cut.}

FormList Property NoSleepCells Auto
{Interiors where only waiting is allowed (CELL DATA flag 4).}
FormList Property RegionSleepLists Auto
{Each region's sleep creature list (REGN BNAM), as the actor that places it; indexed by the grid's letters.}
String[] Property RegionRows Auto
{A row of Morrowind cells each, from the southernmost: a letter per cell from the westernmost ('A' + index into
RegionSleepLists; '.' no sleep list).}
Int Property GridMinX Auto
Int Property GridMinY Auto
Int Property GridWidth Auto
Actor Property PlayerRef Auto
GlobalVariable Property GameHour Auto
GlobalVariable Property GameDaysPassed Auto
Float Property SleepRandMod = 0.25 Auto
Float Property SleepRestMod = 0.3 Auto
Int Property NumberCreatures = 1 Auto
Float Property RestMagicMult = 0.15 Auto
Float Property FatigueReturnBase = 2.5 Auto
Float Property FatigueReturnMult = 0.02 Auto
Float Property EndFatigueMult = 0.04 Auto
Float Property LevelUpHealthEndMult = 0.1 Auto
Float Property PCbaseMagickaMult = 1.0 Auto
String Property RestIllegal = "Resting here is illegal. You'll need to find a bed." Auto
String Property OnlyOnGround = "You can only rest on solid ground." Auto
String Property SleepInterrupt = "Your rest has been interrupted." Auto
Spell Property StuntedSign Auto
{Skyrim's Atronach Stone: Morrowind's Atronach sign's Stunted Magicka.}
TMMwState Property MwState Auto
{The player's kept attributes; without it Morrowind's usual starting value, 40, stands for each.}

Bool bedSleep = False
Int bedHours = 0
Bool watching = False
Bool resting = False
Float openedAt = 0.0
Int hoursAsked = 0
Int interruptAt = -1
Bool interrupted = False

Event OnInit()
  Watch()
EndEvent

Function Watch()
  RegisterForMenu("Sleep/Wait Menu")
  RegisterForSleep()
  RegisterForModEvent("TMMwGameLoaded", "OnTMMwGameLoaded")
EndFunction

Event OnTMMwGameLoaded()
  Watch()
EndEvent

; A bed: Morrowind's rest wherever it stands, its hours known from the start.
Event OnSleepStart(Float afSleepStartTime, Float afDesiredSleepEndTime)
  bedSleep = True
  bedHours = ((afDesiredSleepEndTime - afSleepStartTime) * 24.0 + 0.5) as Int
EndEvent

Event OnSleepStop(Bool abInterrupted)
EndEvent

Event OnMenuOpen(String asMenuName)
  If asMenuName != "Sleep/Wait Menu" || !TMMwScript.InMorrowind()
    Return
  EndIf
  watching = True
  interrupted = False
  interruptAt = -1
  hoursAsked = 0
  openedAt = GameDaysPassed.GetValue()
  ; World::canRest: not in the air, not under water (Rest_PlayerIsInAir, Rest_PlayerIsUnderwater: sNotifyMessage1).
  If PlayerRef.GetAnimationVariableBool("bInJumpState") || PlayerRef.GetPositionZ() < TMMwScript.WaterLevel(PlayerRef)
    Debug.Notification(OnlyOnGround)
    watching = False
    UI.Invoke("Sleep/Wait Menu", "_root.SleepWaitMenu_mc.onCancelPress")
    Return
  EndIf
  ; Rest_OnlyWaiting in a no-sleep cell (ESM::Cell::NoSleep), unless it is a bed.
  Cell here = PlayerRef.GetParentCell()
  resting = bedSleep || !(here && NoSleepCells && NoSleepCells.HasForm(here))
  If !resting
    Debug.Notification(RestIllegal)
  EndIf
  If bedSleep && bedHours > 0
    hoursAsked = bedHours
    RollInterruption(hoursAsked)
  EndIf
  ; Through the rest (menu time): the hours asked for from the slider once time starts moving, and the interruption
  ; when its hour comes, closing the menu as its Cancel does (WaitDialog::onFrame stopWaiting).
  Int ticks = 0
  While watching && ticks < 2400 && UI.IsMenuOpen("Sleep/Wait Menu")
    Utility.WaitMenuMode(0.25)
    ticks += 1
    Float passed = (GameDaysPassed.GetValue() - openedAt) * 24.0
    If hoursAsked < 1 && passed > 0.01
      hoursAsked = UI.GetFloat("Sleep/Wait Menu", "_root.SleepWaitMenu_mc.SleepWaitSlider.value") as Int
      If hoursAsked > 0
        RollInterruption(hoursAsked)
      Else
        hoursAsked = -1
      EndIf
    EndIf
    If interruptAt >= 0 && passed >= interruptAt as Float
      interrupted = True
      interruptAt = -1
      UI.Invoke("Sleep/Wait Menu", "_root.SleepWaitMenu_mc.onCancelPress")
    EndIf
  EndWhile
EndEvent

; WaitDialog::startWaiting: sleeping outside, in a region with a sleep list, rolling 0..hours-1 under fSleepRandMod x
; hours wakes the player int(fSleepRestMod x hours) hours before the end, when that is at least one.
Function RollInterruption(Int aiHours)
  interruptAt = -1
  If !resting || aiHours < 1 || PlayerRef.IsInInterior() || !SleepListHere()
    Return
  EndIf
  Int x = Utility.RandomInt(0, aiHours - 1)
  If x < SleepRandMod * aiHours
    Int remaining = (SleepRestMod * aiHours) as Int
    If remaining != 0
      interruptAt = aiHours - remaining
    EndIf
  EndIf
EndFunction

Event OnMenuClose(String asMenuName)
  If asMenuName != "Sleep/Wait Menu" || !watching
    Return
  EndIf
  watching = False
  Int hours = ((GameDaysPassed.GetValue() - openedAt) * 24.0 + 0.5) as Int
  If hours > 0
    ; The slider unread: the roll over the hours slept, the restoration cut where the sleep would have ended.
    If resting && hoursAsked < 0 && !interrupted
      RollInterruption(hours)
      If interruptAt >= 0
        hours = interruptAt
        interruptAt = -1
        interrupted = True
      EndIf
    EndIf
    Restore(hours, resting)
    If interrupted
      Interrupt()
    EndIf
  EndIf
  bedSleep = False
  bedHours = 0
  interrupted = False
  interruptAt = -1
EndEvent

; The player's Morrowind attribute (0 Strength, 1 Intelligence, 2 Willpower, 3 Agility, 4 Speed, 5 Endurance).
Float Function Attribute(Int aiStat)
  If MwState
    Return MwState.GetStat(PlayerRef, aiStat, -1)
  EndIf
  Return 40.0
EndFunction

; Actors::restoreDynamicStats for the player: a sleep's health (getRestorationPerHourOfSleep: 0.1 x Endurance an
; hour) and magicka (fRestMagicMult x Intelligence, 0 under Stunted Magicka); then fatigue, resting or waiting, unless
; it is at or over its base: 3,600 x (fFatigueReturnBase + fFatigueReturnMult x (1 - encumbrance, at most 1)) x
; fEndFatigueMult x Endurance an hour. Each as its share of Morrowind's maximum, of Skyrim's.
Function Restore(Int aiHours, Bool abSleep)
  If aiHours <= 0 || PlayerRef.IsDead()
    Return
  EndIf
  Float strength = Attribute(0)
  Float intelligence = Attribute(1)
  Float willpower = Attribute(2)
  Float agility = Attribute(3)
  Float endurance = Attribute(5)
  If abSleep
    ; Morrowind's maximum health: (Strength + Endurance) / 2 at level 1, fLevelUpHealthEndMult x Endurance each level
    ; after (OpenMW NpcStats::updateHealth, Class::getLevelHealthBonus).
    Float maxHealth = (strength + endurance) / 2.0 + LevelUpHealthEndMult * endurance * (PlayerRef.GetLevel() - 1)
    If maxHealth > 0.0
      Float healthShare = 0.1 * endurance * aiHours / maxHealth
      If healthShare > 1.0
        healthShare = 1.0
      EndIf
      PlayerRef.RestoreActorValue("Health", healthShare * PlayerRef.GetBaseActorValue("Health"))
    EndIf
    ; Morrowind's maximum magicka: fPCbaseMagickaMult x Intelligence (NpcStats::updateHealth's magicka).
    Bool stunted = StuntedSign && PlayerRef.HasSpell(StuntedSign)
    Float maxMagicka = PCbaseMagickaMult * intelligence
    If !stunted && maxMagicka > 0.0
      Float magickaShare = RestMagicMult * intelligence * aiHours / maxMagicka
      If magickaShare > 1.0
        magickaShare = 1.0
      EndIf
      PlayerRef.RestoreActorValue("Magicka", magickaShare * PlayerRef.GetBaseActorValue("Magicka"))
    EndIf
  EndIf
  If PlayerRef.GetActorValuePercentage("Stamina") >= 1.0
    Return
  EndIf
  Float carry = PlayerRef.GetActorValue("CarryWeight")
  Float encumbrance = 0.0
  If carry > 0.0
    encumbrance = PlayerRef.GetActorValue("InventoryWeight") / carry
  EndIf
  If encumbrance > 1.0
    encumbrance = 1.0
  EndIf
  Float x = (FatigueReturnBase + FatigueReturnMult * (1.0 - encumbrance)) * EndFatigueMult * endurance
  ; Morrowind's maximum fatigue: Strength + Willpower + Agility + Endurance (CreatureStats, Class::getFatigue).
  Float maxFatigue = strength + willpower + agility + endurance
  If maxFatigue > 0.0
    Float fatigueShare = 3600.0 * x * aiHours / maxFatigue
    If fatigueShare > 1.0
      fatigueShare = 1.0
    EndIf
    PlayerRef.RestoreActorValue("Stamina", fatigueShare * PlayerRef.GetBaseActorValue("Stamina"))
  EndIf
EndFunction

; The sleep list of the region the player stands in outside (none inside, or where the region has none): the grid's
; letter for the player's Morrowind cell (8,192 units).
Form Function SleepListHere()
  If PlayerRef.IsInInterior() || !RegionRows || !RegionSleepLists || GridWidth <= 0
    Return None
  EndIf
  Int gx = Math.Floor(PlayerRef.GetPositionX() / 8192.0) - GridMinX
  Int gy = Math.Floor(PlayerRef.GetPositionY() / 8192.0) - GridMinY
  If gx < 0 || gy < 0 || gx >= GridWidth || gy >= RegionRows.Length
    Return None
  EndIf
  String row = RegionRows[gy]
  If StringUtil.GetLength(row) <= gx
    Return None
  EndIf
  Int index = StringUtil.AsOrd(StringUtil.GetNthChar(row, gx)) - 65
  If index < 0 || index >= RegionSleepLists.GetSize()
    Return None
  EndIf
  Return RegionSleepLists.GetAt(index)
EndFunction

; WaitDialog::onFrame at the interruption: sSleepInterrupt, and World::spawnRandomCreature: 1 + rand(iNumberCreatures)
; of the region's list, each placed 220 units in the direction the player faces (safePlaceObject direction 0).
Function Interrupt()
  Form list = SleepListHere()
  Debug.MessageBox(SleepInterrupt)
  If !list
    Return
  EndIf
  Int count = 1
  If NumberCreatures > 1
    count = 1 + Utility.RandomInt(0, NumberCreatures - 1)
  EndIf
  Float angle = PlayerRef.GetAngleZ()
  Float dx = 220.0 * Math.sin(angle)
  Float dy = 220.0 * Math.cos(angle)
  Int i = 0
  While i < count
    ObjectReference spawned = PlayerRef.PlaceAtMe(list, 1)
    If spawned
      spawned.MoveTo(PlayerRef, dx, dy, 0.0)
      Actor a = spawned as Actor
      If a
        a.SetLookAt(PlayerRef)
      EndIf
    EndIf
    i += 1
  EndWhile
EndFunction
