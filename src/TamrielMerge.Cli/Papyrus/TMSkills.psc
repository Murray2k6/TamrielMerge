ScriptName TMSkills extends Quest
{The new skills (docs/progression.md "New skills"; ProgressionSkills builds them): the skills Skyrim lacks from the
games converted. A skill's level is a global (TMSkill<Id>Level) and its experience toward the next level another
(TMSkill<Id>XP). Use adds experience on Skyrim's curve (a level L needs ImproveMult x L^1.95 + ImproveOffset); a rise
adds character experience as a Skyrim skill rise does (the new level x fXPPerSkillRank, SKSE SetPlayerExperience), so
the new skills count toward the character's level. What a level does: an ability whose actor values grow with it
(SKSE SetNthEffectMagnitude, the ability added again) and a hidden perk whose entry points grow with it (SKSE
SetNthEntryValue); both are set again after loading, as SKSE's changes to forms aren't saved. Each skill has a small
perk tree taken in the Tamriel menu with Skyrim's perk points, each rank gated by the skill's level.

How the skills rise, cheaply: a poll every 2 seconds (distance run, sprinted or swum; health recovered), animation
events for jumps and landings, the player's spell casts (the Mysticism and Thaumaturgy lists), a combat hit spell on
whoever the player strikes (TMSkillHitEffect: unarmed hits, spears, sneak attacks, critical strikes), the player being
struck (medium armour or none), gold changing hands while the barter menu is open, and sleep.}

Actor Property PlayerRef Auto
String[] Property SkillNames Auto
String[] Property SkillIds Auto
GlobalVariable[] Property Levels Auto
GlobalVariable[] Property Experience Auto
Float[] Property ImproveMult Auto
Float[] Property ImproveOffset Auto
Float[] Property UseExperience Auto
{Experience one use gives (a use: a hit, a jump, a point of a spell's cost, 100 units run, a coin changing hands).}
Spell[] Property Abilities Auto
Int[] Property AbilityFirst Auto
Float[] Property AbilityPerLevel Auto
Perk[] Property EffectPerks Auto
Int[] Property EntryFirst Auto
Int[] Property EntryCount Auto
Float[] Property EntryBase Auto
Float[] Property EntryPerLevel Auto
Perk[] Property TreePerks Auto
Int[] Property TreeSkill Auto
Int[] Property TreeLevel Auto
Int[] Property TreeAfter Auto
{The rank before (an index into TreePerks), -1 for a first rank.}
FormList[] Property Lists Auto
{Mysticism spells, Thaumaturgy spells, spears, medium armour: filled from ListPlugins / ListIds / ListOf.}
String[] Property ListPlugins Auto
Int[] Property ListIds Auto
Int[] Property ListOf Auto
Int Property ListStamp Auto
GlobalVariable Property MediumWorn Auto
{Pieces of medium armour worn (perk conditions read it).}
GlobalVariable Property UnarmoredOn Auto
{1 while wearing no armour.}
Perk Property HitPerk Auto
MiscObject Property Gold Auto
Keyword Property ArmorLight Auto
Keyword Property ArmorHeavy Auto
String[] Property DfSkillNames Auto
Int[] Property DfSkillIds Auto
{Daggerfall's own skills (DFSkill<name> in Daggerfall.esm, raised by TMDfSensesControl): listed in the menu as they are.}
Float Property BreathMult = 0.2 Auto
{Skyrim's fActorSwimBreathMult; Swimming raises it up to twice this.}

String Property MenuName = "CustomMenu" AutoReadOnly
Float Property Tick = 2.0 AutoReadOnly
Int Property MaxLevel = 100 AutoReadOnly

Int iMysticism = -1
Int iThaumaturgy = -1
Int iAthletics = -1
Int iAcrobatics = -1
Int iHandToHand = -1
Int iSpear = -1
Int iMedium = -1
Int iMercantile = -1
Int iUnarmored = -1
Int iSwimming = -1
Int iBackstab = -1
Int iCritical = -1
Int iMedical = -1

Int filledStamp = 0
Bool filledAll = False
Float lastX
Float lastY
Float lastHealth = -1.0
Bool wasSwimming = False
Float jumpZ = 0.0
Bool airborne = False
Float lastStruck = 0.0
Bool bartering = False
Float sleepStart = -1.0

Int Function Index(String id)
  Int i = 0
  While i < SkillIds.Length
    If SkillIds[i] == id
      Return i
    EndIf
    i += 1
  EndWhile
  Return -1
EndFunction

; At the start and after each load (TMProgression.Setup): the lists, perks, abilities and entry values, and the events.
Function Setup()
  If !SkillIds || SKSE.GetVersion() <= 0
    Return
  EndIf
  iMysticism = Index("Mysticism")
  iThaumaturgy = Index("Thaumaturgy")
  iAthletics = Index("Athletics")
  iAcrobatics = Index("Acrobatics")
  iHandToHand = Index("HandToHand")
  iSpear = Index("Spear")
  iMedium = Index("MediumArmor")
  iMercantile = Index("Mercantile")
  iUnarmored = Index("Unarmored")
  iSwimming = Index("Swimming")
  iBackstab = Index("Backstabbing")
  iCritical = Index("CriticalStrike")
  iMedical = Index("Medical")
  FillLists()
  If HitPerk && !PlayerRef.HasPerk(HitPerk)
    PlayerRef.AddPerk(HitPerk)
  EndIf
  Int i = 0
  While i < SkillIds.Length
    If EffectPerks[i] && !PlayerRef.HasPerk(EffectPerks[i])
      PlayerRef.AddPerk(EffectPerks[i])
    EndIf
    Apply(i)
    i += 1
  EndWhile
  UpdateArmor()
  RegisterForAnimationEvent(PlayerRef, "JumpUp")
  RegisterForAnimationEvent(PlayerRef, "JumpFall")
  RegisterForAnimationEvent(PlayerRef, "JumpDown")
  RegisterForMenu("BarterMenu")
  RegisterForSleep()
  RegisterForModEvent("TM_Skills", "OnSkillsMenu")
  lastX = PlayerRef.GetPositionX()
  lastY = PlayerRef.GetPositionY()
  lastHealth = PlayerRef.GetActorValue("Health")
  RegisterForSingleUpdate(Tick)
EndFunction

; The lists' records, from the converted plugins (those loaded; filled again when a later build lists others or a game
; that was missing is loaded now).
Function FillLists()
  If filledStamp == ListStamp && filledAll
    Return
  EndIf
  Int k = 0
  While k < Lists.Length
    Lists[k].Revert()
    k += 1
  EndWhile
  filledAll = True
  String plugin = ""
  Bool loaded = False
  Int i = 0
  While i < ListIds.Length
    If ListPlugins[i] != plugin
      plugin = ListPlugins[i]
      loaded = Game.GetModByName(plugin) != 255
      If !loaded
        filledAll = False
      EndIf
    EndIf
    If loaded
      Form f = Game.GetFormFromFile(ListIds[i], plugin)
      If f
        Lists[ListOf[i]].AddForm(f)
      EndIf
    EndIf
    i += 1
  EndWhile
  filledStamp = ListStamp
EndFunction

; ---- levels and experience ----

Float Function Needed(Int i, Int level)
  Return ImproveMult[i] * Math.pow(level as Float, 1.95) + ImproveOffset[i]
EndFunction

Function AddUse(Int i, Float uses)
  If i >= 0 && uses > 0.0
    AddExperience(i, uses * UseExperience[i])
  EndIf
EndFunction

Function AddExperience(Int i, Float xp)
  Int level = Levels[i].GetValueInt()
  If level >= MaxLevel
    Return
  EndIf
  Float have = Experience[i].GetValue() + xp
  Float need = Needed(i, level)
  Bool rose = False
  Float perRank = Game.GetGameSettingFloat("fXPPerSkillRank")
  If perRank <= 0.0
    perRank = 1.0
  EndIf
  While have >= need && level < MaxLevel
    have -= need
    level += 1
    rose = True
    ; As a Skyrim skill rise: character experience of the new level times fXPPerSkillRank.
    Game.SetPlayerExperience(Game.GetPlayerExperience() + level * perRank)
    Debug.Notification(SkillNames[i] + " increased to " + level + ".")
    need = Needed(i, level)
  EndWhile
  If level >= MaxLevel
    have = 0.0
  EndIf
  Levels[i].SetValue(level)
  Experience[i].SetValue(have)
  If rose
    Apply(i)
  EndIf
EndFunction

; What skill i's level does: its ability's magnitudes and its effect perk's entry values.
Function Apply(Int i)
  Float level = Levels[i].GetValue()
  Spell ability = Abilities[i]
  If ability
    Int n = ability.GetNumEffects()
    Int k = 0
    While k < n
      ability.SetNthEffectMagnitude(k, AbilityPerLevel[AbilityFirst[i] + k] * level)
      k += 1
    EndWhile
    PlayerRef.RemoveSpell(ability)
    PlayerRef.AddSpell(ability, False)
  EndIf
  Perk p = EffectPerks[i]
  If p
    Int k = 0
    While k < EntryCount[i]
      Int e = EntryFirst[i] + k
      p.SetNthEntryValue(k, 0, EntryBase[e] + EntryPerLevel[e] * level)
      k += 1
    EndWhile
  EndIf
  If i == iSwimming
    ; Breath: Skyrim's breath multiplier, up to twice its own at 100.
    Game.SetGameSettingFloat("fActorSwimBreathMult", BreathMult * (1.0 + level / 100.0))
  EndIf
  If i == iAthletics || i == iSwimming
    RefreshSpeed()
  EndIf
EndFunction

; A change of SpeedMult takes effect when the carry weight changes.
Function RefreshSpeed()
  PlayerRef.ModActorValue("CarryWeight", 0.1)
  PlayerRef.ModActorValue("CarryWeight", -0.1)
EndFunction

; ---- how they rise ----

; Every 2 seconds: the distance covered on foot (sprinting counts fully, running half), swum, and health recovered.
Event OnUpdate()
  Float x = PlayerRef.GetPositionX()
  Float y = PlayerRef.GetPositionY()
  Float dx = x - lastX
  Float dy = y - lastY
  Float d = Math.sqrt(dx * dx + dy * dy)
  lastX = x
  lastY = y
  Bool swimming = PlayerRef.IsSwimming()
  If d < 3000.0 && !PlayerRef.IsOnMount() && !Utility.IsInMenuMode()
    If swimming
      AddUse(iSwimming, d / 100.0)
      AddUse(iAthletics, d / 200.0)
    ElseIf PlayerRef.IsSprinting()
      AddUse(iAthletics, d / 100.0)
    ElseIf PlayerRef.IsRunning() && !PlayerRef.IsSneaking()
      AddUse(iAthletics, d / 200.0)
    EndIf
  EndIf
  If swimming != wasSwimming
    wasSwimming = swimming
    If iSwimming >= 0
      RefreshSpeed()
    EndIf
  EndIf
  If iMedical >= 0
    Float health = PlayerRef.GetActorValue("Health")
    If lastHealth >= 0.0 && health > lastHealth
      AddUse(iMedical, health - lastHealth)
    EndIf
    lastHealth = health
  EndIf
  RegisterForSingleUpdate(Tick)
EndEvent

; Jumps (a use each) and falls (a use per 100 units fallen past the first 150).
Event OnAnimationEvent(ObjectReference akSource, String asEventName)
  If iAcrobatics < 0
    Return
  EndIf
  If asEventName == "JumpUp"
    airborne = True
    jumpZ = PlayerRef.GetPositionZ()
    AddUse(iAcrobatics, 1.0)
  ElseIf asEventName == "JumpFall"
    If !airborne
      airborne = True
      jumpZ = PlayerRef.GetPositionZ()
    EndIf
  ElseIf asEventName == "JumpDown"
    If airborne
      Float fall = jumpZ - PlayerRef.GetPositionZ()
      If fall > 150.0
        AddUse(iAcrobatics, (fall - 150.0) / 100.0)
      EndIf
    EndIf
    airborne = False
  EndIf
EndEvent

; The player cast a spell (TMProgressionPlayer.OnSpellCast): Mysticism or Thaumaturgy by the spell's cost.
Function SpellCast(Form akSpell)
  Spell s = akSpell as Spell
  If !s
    Return
  EndIf
  If iMysticism >= 0 && Lists[0].HasForm(s)
    AddUse(iMysticism, s.GetMagickaCost())
  ElseIf iThaumaturgy >= 0 && Lists[1].HasForm(s)
    AddUse(iThaumaturgy, s.GetMagickaCost())
  EndIf
EndFunction

; The player struck someone (TMSkillHitEffect): Hand-to-Hand for fists, Spear for spears, Backstabbing for a sneak
; attack, and Critical Strike's own critical hits (Daggerfall's: a level/4 % chance, extra damage).
Function PlayerHit(Actor target)
  If !target || target == PlayerRef
    Return
  EndIf
  Int kind = PlayerRef.GetEquippedItemType(1)
  Weapon w = PlayerRef.GetEquippedWeapon(False)
  If kind == 0
    AddUse(iHandToHand, 1.0)
  ElseIf w && iSpear >= 0 && Lists[2].HasForm(w)
    AddUse(iSpear, 1.0)
  EndIf
  If iBackstab >= 0 && PlayerRef.IsSneaking() && (!target.IsInCombat() || !PlayerRef.IsDetectedBy(target))
    AddUse(iBackstab, 1.0)
  EndIf
  If iCritical >= 0
    Float level = Levels[iCritical].GetValue()
    If Utility.RandomFloat(0.0, 100.0) < level / 4.0
      Float damage = 2.0 + PlayerRef.GetActorValue("UnarmedDamage") * 0.5
      If w
        damage = w.GetBaseDamage() * (0.25 + level / 200.0)
      EndIf
      If damage < 2.0
        damage = 2.0
      EndIf
      target.DamageActorValue("Health", damage)
      AddUse(iCritical, 1.0)
      Debug.Notification("Critical strike!")
    EndIf
  EndIf
EndFunction

; The player was struck by a weapon (TMProgressionPlayer.OnHit): Medium Armor when wearing any, else Unarmored when
; wearing no armour; at most one use every half second.
Function Struck(ObjectReference akAggressor, Form akSource)
  If akAggressor == PlayerRef || !(akSource as Weapon)
    Return
  EndIf
  Float now = Utility.GetCurrentRealTime()
  If now - lastStruck < 0.5
    Return
  EndIf
  lastStruck = now
  If MediumWorn.GetValueInt() > 0
    AddUse(iMedium, 1.0)
  ElseIf UnarmoredOn.GetValueInt() == 1
    AddUse(iUnarmored, 1.0)
  EndIf
EndFunction

; What the player wears (on each change): the medium armour pieces and whether any armour at all.
Function UpdateArmor()
  If !MediumWorn || !UnarmoredOn
    Return
  EndIf
  Int[] masks = new Int[10]
  masks[0] = 0x00000001
  masks[1] = 0x00000002
  masks[2] = 0x00000004
  masks[3] = 0x00000008
  masks[4] = 0x00000080
  masks[5] = 0x00000200
  masks[6] = 0x00800000
  masks[7] = 0x08000000
  masks[8] = 0x10000000
  masks[9] = 0x40000000
  Form[] seen = new Form[10]
  Int medium = 0
  Bool armoured = False
  Int i = 0
  While i < masks.Length
    Armor a = PlayerRef.GetWornForm(masks[i]) as Armor
    If a && seen.Find(a) < 0
      seen[i] = a
      If a.HasKeyword(ArmorLight) || a.HasKeyword(ArmorHeavy)
        armoured = True
      EndIf
      If iMedium >= 0 && Lists[3].HasForm(a)
        medium += 1
      EndIf
    EndIf
    i += 1
  EndWhile
  MediumWorn.SetValue(medium)
  If armoured
    UnarmoredOn.SetValue(0)
  Else
    UnarmoredOn.SetValue(1)
  EndIf
EndFunction

; Gold changing hands (TMProgressionPlayer's gold filter) while bartering: Mercantile by the coins.
Function GoldChanged(Int count)
  If bartering && count > 0
    AddUse(iMercantile, count)
  EndIf
EndFunction

Event OnMenuOpen(String menuName)
  If menuName == "BarterMenu"
    bartering = True
  EndIf
EndEvent

Event OnMenuClose(String menuName)
  If menuName == "BarterMenu"
    bartering = False
  EndIf
EndEvent

; Sleep heals: Medical as for 8 points of health an hour slept.
Event OnSleepStart(Float afSleepStartTime, Float afDesiredSleepEndTime)
  sleepStart = Utility.GetCurrentGameTime()
EndEvent

Event OnSleepStop(Bool abInterrupted)
  If sleepStart >= 0.0
    AddUse(iMedical, (Utility.GetCurrentGameTime() - sleepStart) * 24.0 * 8.0)
  EndIf
  sleepStart = -1.0
EndEvent

; ---- the Tamriel menu's Skills tab ----

; "state", or "take:<n>" (a perk of the trees, by number).
Event OnSkillsMenu(String eventName, String strArg, Float numArg, Form sender)
  If StringUtil.Find(strArg, "take:") == 0
    TakePerk(StringUtil.Substring(strArg, 5) as Int)
  Else
    SendState("")
  EndIf
EndEvent

Function TakePerk(Int n)
  If n < 0 || n >= TreePerks.Length || !TreePerks[n]
    SendState("")
    Return
  EndIf
  Perk p = TreePerks[n]
  Int skill = TreeSkill[n]
  String msg
  If PlayerRef.HasPerk(p)
    msg = "You already have " + p.GetName() + "."
  ElseIf Levels[skill].GetValue() < TreeLevel[n]
    msg = p.GetName() + " needs " + SkillNames[skill] + " " + TreeLevel[n] + "."
  ElseIf TreeAfter[n] >= 0 && !PlayerRef.HasPerk(TreePerks[TreeAfter[n]])
    msg = "Take " + TreePerks[TreeAfter[n]].GetName() + " first."
  ElseIf Game.GetPerkPoints() < 1
    msg = "No perk points to spend: each new level brings one."
  Else
    PlayerRef.AddPerk(p)
    Game.ModPerkPoints(-1)
    msg = "You took " + p.GetName() + "."
  EndIf
  SendState(msg)
EndFunction

; Each skill's "level,experience,needed"; Daggerfall's own "level,progress"; the perks owned ("1"/"0" each); the perk
; points; a message.
Function SendState(String msg)
  String skills = ""
  Int i = 0
  While i < SkillIds.Length
    Int level = Levels[i].GetValueInt()
    If i > 0
      skills += ";"
    EndIf
    skills += level + "," + (Experience[i].GetValue() as Int) + "," + (Needed(i, level) as Int)
    i += 1
  EndWhile
  String df = ""
  If DfSkillIds && DfSkillIds.Length > 0 && Game.GetModByName("Daggerfall.esm") != 255
    TMDfSensesControl senses = Game.GetFormFromFile(0xDF0300, "Daggerfall.esm") as TMDfSensesControl
    i = 0
    While i < DfSkillIds.Length
      GlobalVariable g = Game.GetFormFromFile(DfSkillIds[i], "Daggerfall.esm") as GlobalVariable
      If i > 0
        df += ";"
      EndIf
      If g
        Int progress = 0
        If senses
          ; TMDfSensesControl numbers them as their globals are numbered (DFSkill<name> at 0xDF0310 + its number).
          progress = (senses.SkillProgress(DfSkillIds[i] - 0xDF0310) * 100.0) as Int
        EndIf
        df += g.GetValueInt() + "," + progress
      Else
        df += "-1,0"
      EndIf
      i += 1
    EndWhile
  EndIf
  String owned = ""
  i = 0
  While i < TreePerks.Length
    If TreePerks[i] && PlayerRef.HasPerk(TreePerks[i])
      owned += "1"
    Else
      owned += "0"
    EndIf
    i += 1
  EndWhile
  UI.InvokeString(MenuName, "_root.SkillState", skills + "#" + df + "#" + owned + "#" + Game.GetPerkPoints() + "#" + msg)
EndFunction
