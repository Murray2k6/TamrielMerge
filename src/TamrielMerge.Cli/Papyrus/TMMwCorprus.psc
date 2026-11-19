ScriptName TMMwCorprus extends ActiveMagicEffect
{Morrowind's Corprus magic effect (MGEF 132) on the player, as the divine disease "corprus" (given by Dagoth Gares'
script CorprusOnPC and the corprus beasts' diseaseCorprus). OpenMW spelleffects.cpp applyMagicEffect and
activespells.cpp ActiveSpellParams::worsen: when the Corprus effect lands its spell's other applied-once effects land
with it (a worsening), and every CorprusStats::sWorseningPeriod = 24 hours after that they land again, with
sMagicCorprusWorsens to the player. Morrowind's corprus record has Drain Intelligence, Willpower, Personality and
Speed 1 point and Fortify Strength and Endurance 1 point, so each day the first four fall by one and the last two
rise by one, on Morrowind's own attributes (TMMwState, which the scripts and the dialogue read). The converter leaves
those six effects off the converted spell (MwMagic.EffectsOf) so this script is their only source, and converts the
spell as an ability, which Skyrim's Cure Disease can't take: in Morrowind no cure takes corprus (OpenMW spells.cpp
purgeBlightDisease spares a spell with the Corprus effect), only Divayth Fyr's potion does (his dialogue's result
removes the spell). The cure undoes the drains, a day's worth for each worsening, and keeps the fortifies (OpenMW
spelleffects.cpp removeMagicEffect's Corprus branch removes the harmful applied-once effects once per worsening):
TMMwAfflictions.CheckCorprus sees the spell gone and does it, by the count kept in MWCorprusWorsenings. A passing
Corprus effect (the ash curses' one-second touch) has no applied-once siblings and does nothing.}

Spell Property Disease Auto
{The converted "corprus".}
Int[] Property DrainAttributes Auto
{The record's Drain Attribute effects: the attributes (0 Strength .. 7 Luck) and their magnitudes.}
Float[] Property DrainMagnitudes Auto
Int[] Property FortifyAttributes Auto
{Its Fortify Attribute effects.}
Float[] Property FortifyMagnitudes Auto
GlobalVariable Property Worsenings Auto
{MWCorprusWorsenings: how many times the disease has landed (0: not had).}
String Property WorsensMessage Auto
{sMagicCorprusWorsens.}
Quest Property MwState Auto
{MWScriptState (TMMwState): Morrowind's attributes.}
Float Property Period = 24.0 AutoReadOnly
{CorprusStats::sWorseningPeriod, in game hours.}

Actor host

Event OnEffectStart(Actor akTarget, Actor akCaster)
  host = akTarget
  If host != Game.GetPlayer() || !Disease || !host.HasSpell(Disease)
    Return
  EndIf
  ; The first landing counts as a worsening (applyMagicEffect: spellParams.worsen() when Corprus is applied); on a
  ; later start (the spell reapplied by a race change, a load) the count stands.
  If Worsenings && Worsenings.GetValueInt() <= 0
    Worsen(False)
  EndIf
  RegisterForUpdateGameTime(Period)
EndEvent

Event OnUpdateGameTime()
  If !host || !Disease
    Return
  EndIf
  If host.HasSpell(Disease)
    Worsen(True)
  Else
    UnregisterForUpdateGameTime()
  EndIf
EndEvent

Function Worsen(Bool abMessage)
  TMMwState mw = MwState as TMMwState
  If mw
    Int i = 0
    While DrainAttributes && i < DrainAttributes.Length
      If DrainMagnitudes && i < DrainMagnitudes.Length
        mw.ModStat(host, DrainAttributes[i], -1, -DrainMagnitudes[i])
      EndIf
      i += 1
    EndWhile
    i = 0
    While FortifyAttributes && i < FortifyAttributes.Length
      If FortifyMagnitudes && i < FortifyMagnitudes.Length
        mw.ModStat(host, FortifyAttributes[i], -1, FortifyMagnitudes[i])
      EndIf
      i += 1
    EndWhile
  EndIf
  If Worsenings
    Worsenings.Mod(1.0)
  EndIf
  If abMessage && WorsensMessage != ""
    Debug.Notification(WorsensMessage)
  EndIf
EndFunction

Event OnEffectFinish(Actor akTarget, Actor akCaster)
  UnregisterForUpdateGameTime()
EndEvent
