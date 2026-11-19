ScriptName TMMwAlchemy extends Quest
{Morrowind's alchemy at Skyrim's bench (the quest MWAlchemy; MwApparatus). Using any apparatus opens the alchemy window
unless in combat (OpenMW mwworld/actionalchemy.cpp, sInventoryMessage3), the best of each kind carried in its slot
(mwmechanics/alchemy.cpp Alchemy::setAlchemist), and nothing can be made without a mortar (sNotifyMessage45). OpenMW's
rule (Alchemy::updateEffects, getAlchemyFactor, applyTools):

  strength  = (Alchemy + 0.1 x Intelligence + 0.1 x Luck) x mortar quality x fPotionStrengthMult
  magnitude = strength / fPotionT1MagMult / the effect's base cost, then the tools
  duration  = strength / fPotionT1DurMult / the effect's base cost, then the tools

  tools, harmful effect:  alembic and calcinator  value / (2 x alembic + 3 x calcinator)
                          alembic                 value / (1 + alembic)
                          calcinator              value + calcinator
  tools, other effect:    retort and calcinator   value + 2 x retort + calcinator
                          retort                  value + retort
                          calcinator              value + calcinator

Skyrim's bench already scales with the Alchemy skill, so the hidden perk MWBrewPerk (Mod Alchemy Effectiveness, one
entry for harmful effects and one for others, while MWBrewActive is 1) multiplies a brew by this over what a Journeyman's
mortar (quality 1) alone would do with the same skill, the mean of the magnitude's and the duration's ratios as Skyrim
has one strength for both. The effect's base cost is the median of the ingredients' effects, as a perk can't see it.
Skyrim's menu always succeeds: Morrowind's failure (getAlchemyFactor against a roll, sNotifyMessage8) can't be played.}

Actor Property PlayerRef Auto
Furniture Property Lab Auto
{Skyrim's alchemy workbench, placed at the player.}
Perk Property BrewPerk Auto
GlobalVariable Property Active Auto
{1 while the bench is open: the perk's entries apply.}
Quest Property MwState Auto
{MWScriptState (TMMwState), for Morrowind's Intelligence and Luck.}
FormList Property Mortars Auto
Float[] Property MortarQualities Auto
FormList Property Alembics Auto
Float[] Property AlembicQualities Auto
FormList Property Calcinators Auto
Float[] Property CalcinatorQualities Auto
FormList Property Retorts Auto
Float[] Property RetortQualities Auto
{Every apparatus of a kind, best first, with its quality.}
Float Property StrengthMult = 0.5 Auto
Float Property T1MagMult = 1.5 Auto
Float Property T1DurMult = 0.5 Auto
{fPotionStrengthMult, fPotionT1MagMult, fPotionT1DurMult.}
Float Property MedianBaseCost = 1.0 Auto
String Property NeedMortar Auto
String Property NoBattle Auto

Bool pending = False
Bool brewing = False

Event OnInit()
  Actor p = Game.GetPlayer()
  If BrewPerk && !p.HasPerk(BrewPerk)
    p.AddPerk(BrewPerk)
  EndIf
EndEvent

; An apparatus used: the bench opens from this quest's own thread (the item's effect is over at once).
Function RequestBrew()
  If brewing
    Return
  EndIf
  pending = True
  RegisterForSingleUpdate(0.1)
EndFunction

Event OnUpdate()
  If pending
    pending = False
    Brew()
  EndIf
EndEvent

; The quality of the best of a kind the player carries (the lists are best first), 0 with none.
Float Function Best(FormList akList, Float[] akQualities)
  If !akList || !akQualities
    Return 0.0
  EndIf
  Int i = 0
  Int n = akList.GetSize()
  While i < n && i < akQualities.Length
    If PlayerRef.GetItemCount(akList.GetAt(i)) > 0
      Return akQualities[i]
    EndIf
    i += 1
  EndWhile
  Return 0.0
EndFunction

; Alchemy::applyTools for an effect with a magnitude and a duration.
Float Function Tools(Float afValue, Float afTool, Float afCalcinator, Bool abHarmful)
  If afTool > 0.0 && afCalcinator > 0.0
    If abHarmful
      Return afValue / (2.0 * afTool + 3.0 * afCalcinator)
    EndIf
    Return afValue + 2.0 * afTool + afCalcinator
  ElseIf afTool > 0.0
    If abHarmful
      Return afValue / (1.0 + afTool)
    EndIf
    Return afValue + afTool
  ElseIf afCalcinator > 0.0
    Return afValue + afCalcinator
  EndIf
  Return afValue
EndFunction

; What a brew is multiplied by: Morrowind's strength over a quality 1 mortar's alone, magnitude and duration averaged.
Float Function Multiplier(Float afSkill, Float afFactor, Float afMortar, Float afTool, Float afCalcinator, Bool abHarmful)
  Float strength = afFactor * afMortar * StrengthMult / MedianBaseCost
  Float plain = afSkill * StrengthMult / MedianBaseCost
  Float magnitude = Tools(strength / T1MagMult, afTool, afCalcinator, abHarmful) / (plain / T1MagMult)
  Float duration = Tools(strength / T1DurMult, afTool, afCalcinator, abHarmful) / (plain / T1DurMult)
  Return 0.5 * (magnitude + duration)
EndFunction

; Skyrim's alchemy workbench placed at the player and opened, Morrowind's strength on while it is.
Function Brew()
  If brewing || !Lab || !BrewPerk || !Active
    Return
  EndIf
  brewing = True
  ; The item was used from a menu: wait for it to close.
  Int tries = 0
  While tries < 100 && (UI.IsMenuOpen("InventoryMenu") || UI.IsMenuOpen("ContainerMenu") || UI.IsMenuOpen("BarterMenu"))
    Utility.WaitMenuMode(0.2)
    tries += 1
  EndWhile
  If PlayerRef.IsInCombat()
    Debug.Notification(NoBattle)
    brewing = False
    Return
  EndIf
  Float mortar = Best(Mortars, MortarQualities)
  If mortar <= 0.0
    Debug.Notification(NeedMortar)
    brewing = False
    Return
  EndIf
  Float skill = PlayerRef.GetActorValue("Alchemy")
  Float intelligence = 50.0
  Float luck = 50.0
  TMMwState st = MwState as TMMwState
  If st
    ; Stats: 1 Intelligence, 7 Luck, 8 + skill, Alchemy being skill 16.
    skill = st.GetStat(PlayerRef, 24, -1)
    intelligence = st.GetStat(PlayerRef, 1, -1)
    luck = st.GetStat(PlayerRef, 7, -1)
  EndIf
  If skill < 1.0
    skill = 1.0
  EndIf
  Float factor = skill + 0.1 * intelligence + 0.1 * luck
  Float alembic = Best(Alembics, AlembicQualities)
  Float calcinator = Best(Calcinators, CalcinatorQualities)
  Float retort = Best(Retorts, RetortQualities)
  If !PlayerRef.HasPerk(BrewPerk)
    PlayerRef.AddPerk(BrewPerk)
  EndIf
  ; SKSE's changes to a perk aren't saved: set for each brew.
  BrewPerk.SetNthEntryValue(0, 0, Multiplier(skill, factor, mortar, alembic, calcinator, True))
  BrewPerk.SetNthEntryValue(1, 0, Multiplier(skill, factor, mortar, retort, calcinator, False))
  Active.SetValue(1.0)
  ObjectReference bench = PlayerRef.PlaceAtMe(Lab)
  bench.Activate(PlayerRef)
  Utility.Wait(1.0)
  While UI.IsMenuOpen("Crafting Menu") || PlayerRef.GetSitState() != 0
    Utility.Wait(0.5)
  EndWhile
  Active.SetValue(0.0)
  bench.Disable()
  bench.Delete()
  brewing = False
EndFunction
