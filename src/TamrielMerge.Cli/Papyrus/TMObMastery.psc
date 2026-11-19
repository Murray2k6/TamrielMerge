ScriptName TMObMastery extends Quest
{Oblivion's skill mastery perks (TamrielMerge, ObMasteryPerks): each of its 21 skills gives a fixed perk at Apprentice
(25), Journeyman (50), Expert (75) and Master (100) (UESP Oblivion:Skills). Every ten seconds, and after each load,
the player's Oblivion skills are read where the converter keeps them (Skyrim's own actor value for the skills Skyrim
has, the progression's TMSkills for the others: Athletics, Mysticism, Blade...) and the hidden rank perks are given or
taken back as the thresholds are crossed, with Oblivion's notice of the new rank. Mercantile's Journeyman, Expert and
Master are Skyrim's own Merchant, Investor and Master Trader perks (its vendor code reads those). Also: how many set
tumblers a broken pick leaves set (TMObLock, the Security ranks), and the mortar's brewing (TMObApparatus).}

Actor Property PlayerRef Auto
Perk[] Property Perks Auto
{84: skill s (Oblivion's order, ObStats.StatSkills) rank r (1-4) at s * 4 + r - 1.}
String[] Property Rules Auto
{What each perk gives, shown with the rank notice.}
String[] Property SkillNames Auto
String[] Property PlayerStatAv Auto
{Each skill's Skyrim actor value where Skyrim keeps the player's ("" otherwise).}
String[] Property PlayerStatId Auto
{Each skill's progression skill (TMSkills id) where the progression keeps the player's ("" otherwise).}
Perk Property MerchantPerk Auto
Perk Property InvestorPerk Auto
Perk Property MasterTraderPerk Auto
Furniture Property Lab Auto
{Skyrim's alchemy workbench, placed at the player by a mortar.}
Spell Property Boost Auto
{The Fortify Alchemy ability of the apparatus in use (its magnitude set here).}

Int Property SkillCount = 21 AutoReadOnly
Int Property RankCount = 4 AutoReadOnly
Int Property MercantileSkill = 17 AutoReadOnly
Int Property SecuritySkill = 18 AutoReadOnly

Bool[] gaveSkyrimPerk
Bool brewPending = False
Float brewBoost = 0.0
Bool brewing = False
Bool warnedProgression = False

Event OnInit()
  RegisterForSingleUpdate(2.0)
EndEvent

; A load (TMObMasteryPlayer): the ranks checked again soon.
Function Loaded()
  RegisterForSingleUpdate(2.0)
EndFunction

Event OnUpdate()
  If brewPending
    brewPending = False
    Brew(brewBoost)
  EndIf
  Check()
  RegisterForSingleUpdate(10.0)
EndEvent

; Oblivion's rank of a skill level: 0 Novice, 1 Apprentice (25), 2 Journeyman (50), 3 Expert (75), 4 Master (100).
Int Function RankOf(Int aiLevel)
  If aiLevel >= 100
    Return 4
  ElseIf aiLevel >= 75
    Return 3
  ElseIf aiLevel >= 50
    Return 2
  ElseIf aiLevel >= 25
    Return 1
  EndIf
  Return 0
EndFunction

String Function RankName(Int aiRank)
  If aiRank == 1
    Return "Apprentice"
  ElseIf aiRank == 2
    Return "Journeyman"
  ElseIf aiRank == 3
    Return "Expert"
  ElseIf aiRank == 4
    Return "Master"
  EndIf
  Return "Novice"
EndFunction

; The player's Oblivion skill as the converter tracks it (TMObState.PlayerStat does the same for the scripts).
Int Function SkillLevel(Int aiSkill)
  If aiSkill < 0 || aiSkill >= SkillCount
    Return 0
  EndIf
  If PlayerStatAv[aiSkill] != ""
    Return PlayerRef.GetActorValue(PlayerStatAv[aiSkill]) as Int
  EndIf
  Quest q = Quest.GetQuest("TMProgression")
  TMSkills skills = q as TMSkills
  If skills
    Int i = skills.Index(PlayerStatId[aiSkill])
    If i >= 0
      Return skills.EffectiveLevel(i)
    EndIf
    Return 0
  EndIf
  If !warnedProgression
    warnedProgression = True
    Debug.Notification("Oblivion's skill mastery needs TamrielMerge Progression.esp.")
  EndIf
  Return 0
EndFunction

; The rank the player holds by the perks given (what the game has, not the level: TMObLock asks).
Int Function HeldRank(Int aiSkill)
  Int r = RankCount
  While r > 0
    Perk p = Perks[aiSkill * RankCount + r - 1]
    If p && PlayerRef.HasPerk(p)
      Return r
    EndIf
    r -= 1
  EndWhile
  Return 0
EndFunction

; Every skill's rank perks brought in line with its level: each rank up to the level's given (Oblivion's notice of the
; new rank), the ones above taken back (a skill lowered by a script, drain or a legendary reset).
Function Check()
  If !Perks || !PlayerRef
    Return
  EndIf
  ; Oblivion's notice of a new rank is for a rank earned: one at a time. Several at once are a character being made
  ; or arriving (the race menu's skill bonuses, skills brought from another time): those go unsaid (seen 2026-11-12:
  ; Oblivion's opening began with four notices over the prison cell).
  Int notices = 0
  String notice = ""
  Int s = 0
  While s < SkillCount
    Int rank = RankOf(SkillLevel(s))
    Int r = 1
    While r <= RankCount
      Int at = s * RankCount + r - 1
      Perk p = None
      If at < Perks.Length
        p = Perks[at]
      EndIf
      If p
        If r <= rank
          If !PlayerRef.HasPerk(p)
            PlayerRef.AddPerk(p)
            String rule = ""
            If Rules && at < Rules.Length
              rule = Rules[at]
            EndIf
            ; Oblivion's notice, in Oblivion's lands only (2026-11-09: a new game in Daggerfall opened with four of them).
            TMStoryOrder order = Game.GetFormFromFile(0x3372B, "Skyrim.esm") as TMStoryOrder
            If !order || order.WorldOf(PlayerRef) == order.StoryOblivion
              notices += 1
              notice = "Your " + SkillNames[s] + " skill has reached " + RankName(r) + " level. " + rule
            EndIf
          EndIf
        ElseIf PlayerRef.HasPerk(p)
          PlayerRef.RemovePerk(p)
        EndIf
      EndIf
      r += 1
    EndWhile
    If s == MercantileSkill
      SkyrimPerk(0, MerchantPerk, rank >= 2)
      SkyrimPerk(1, InvestorPerk, rank >= 3)
      SkyrimPerk(2, MasterTraderPerk, rank >= 4)
    EndIf
    s += 1
  EndWhile
  If notices == 1
    Debug.Notification(notice)
  EndIf
EndFunction

; One of Skyrim's own perks standing for a Mercantile rank: given with the rank, taken back only if given here (the
; player may have taken it in Skyrim's Speech tree).
Function SkyrimPerk(Int aiSlot, Perk akPerk, Bool abWanted)
  If !akPerk
    Return
  EndIf
  If !gaveSkyrimPerk
    gaveSkyrimPerk = new Bool[3]
  EndIf
  If abWanted
    If !PlayerRef.HasPerk(akPerk)
      PlayerRef.AddPerk(akPerk)
      gaveSkyrimPerk[aiSlot] = True
    EndIf
  ElseIf gaveSkyrimPerk[aiSlot]
    gaveSkyrimPerk[aiSlot] = False
    If PlayerRef.HasPerk(akPerk)
      PlayerRef.RemovePerk(akPerk)
    EndIf
  EndIf
EndFunction

; How many set tumblers stay set when a lockpick breaks (UESP Oblivion:Security): none below Journeyman, one at
; Journeyman, two at Expert, all (-1) at Master. The Journeyman and Expert counts are uncertain.
Int Function TumblersKept()
  Int rank = HeldRank(SecuritySkill)
  If rank >= 4
    Return -1
  ElseIf rank == 3
    Return 2
  ElseIf rank == 2
    Return 1
  EndIf
  Return 0
EndFunction

; ---- brewing with a mortar (TMObApparatus) ----

; A mortar used: the workbench opens from this quest's own thread (the item's effect is over at once), once the
; inventory is closed.
Function RequestBrew(Float afBoost)
  If brewing
    Return
  EndIf
  brewBoost = afBoost
  brewPending = True
  RegisterForSingleUpdate(0.1)
EndFunction

; Skyrim's alchemy workbench placed at the player and opened (as TMObState.ShowEnchanting does the enchanting
; table), the apparatus boost on while it is; both gone when the menu closes.
Function Brew(Float afBoost)
  If brewing || !Lab || !PlayerRef
    Return
  EndIf
  brewing = True
  ; The item was used from a menu: wait for it to close (Utility.Wait would wait with it).
  Int tries = 0
  While tries < 100 && (UI.IsMenuOpen("InventoryMenu") || UI.IsMenuOpen("ContainerMenu") || UI.IsMenuOpen("BarterMenu"))
    Utility.WaitMenuMode(0.2)
    tries += 1
  EndWhile
  If PlayerRef.IsInCombat()
    ; Oblivion's sCanNotEquipAlchemy.
    Debug.Notification("You cannot create potions when enemies are nearby.")
    brewing = False
    Return
  EndIf
  If Boost && afBoost > 0.0
    Boost.SetNthEffectMagnitude(0, afBoost)
    PlayerRef.AddSpell(Boost, False)
  EndIf
  ObjectReference bench = PlayerRef.PlaceAtMe(Lab)
  bench.Activate(PlayerRef)
  Utility.Wait(1.0)
  While UI.IsMenuOpen("Crafting Menu") || PlayerRef.GetSitState() != 0
    Utility.Wait(0.5)
  EndWhile
  If Boost
    PlayerRef.RemoveSpell(Boost)
  EndIf
  bench.Disable()
  bench.Delete()
  brewing = False
EndFunction
