ScriptName TMStoryOrder extends Quest
{TamrielMerge story order, attached to the vanilla opening quest MQ101.
The engine starts MQ101 on every new game. TamrielMerge Start.esp points MQ101's stage 0
normal-start fragment (QF_MQ101_0003372B.Fragment_2, gated on MQQuickstart == 0) at
Fragment_Takeover below, so a new game begins with the first story instead of Helgen.

Stories run in order: 0 Daggerfall, 1 Redguard, 2 Morrowind, 3 Oblivion, 4 Skyrim. A story that
isn't installed is skipped. When a story's main quest ends, StoryCompleted asks whether to go on;
saying no grants the Begin the Next Story power instead. MQ101 is Run Once, so it is never
stopped: it waits at stage 0 until Skyrim's story begins.}

Int Property StoryDaggerfall = 0 AutoReadOnly
Int Property StoryRedguard = 1 AutoReadOnly
Int Property StoryMorrowind = 2 AutoReadOnly
Int Property StoryOblivion = 3 AutoReadOnly
Int Property StorySkyrim = 4 AutoReadOnly
Int Property StoryCount = 5 AutoReadOnly

GlobalVariable Property GameHour Auto
; Daggerfall's start marker (Privateer's Hold), looked up at runtime like the others' so Daggerfall stays optional.
Int Property DaggerfallStartFormId = 0x010C81 Auto
String Property DaggerfallPlugin = "Daggerfall.esm" Auto
ObjectReference Property StashChest Auto
{Holds the player's items while Skyrim's opening strips them.}
Spell Property NextStoryPower Auto
Message Property ConfirmNextStory Auto
{Buttons: 0 Yes, 1 No.}
Message Property ContinuePrompt Auto
{Buttons: 0 Continue to the next story, 1 Stay and explore.}
Message Property NoMoreStories Auto
Form[] Property StartingItems Auto
MiscObject Property Gold Auto
String[] Property CoinNames Auto
{Each story's coin: gold pieces, gold, drakes, septims, Skyrim's gold.}
Float[] Property CoinWeights Auto
{What one coin of each story weighs (Daggerfall's 400 to the kilogram).}
Int Property StartingGold = 100 Auto
Light Property Torch Auto
Int Property StartingTorches = 5 Auto

; Each converted game's start marker, looked up at runtime so the game stays optional. TamrielMerge
; creates it as the first record of the game's plugin, so it is always FormID 0x800 there.
Int Property RedguardStartFormId = 0x800 Auto
String Property RedguardPlugin = "Redguard.esm" Auto
Int Property MorrowindStartFormId = 0x800 Auto
String Property MorrowindPlugin = "Morrowind.esm" Auto
Int Property OblivionStartFormId = 0x800 Auto
String Property OblivionPlugin = "Oblivion.esm" Auto

; Travel Through Time: each story is a time and place in Tamriel's history. One persistent marker per story
; records where the player last was there, and each story keeps its own calendar (when). The menu lists the
; times in history's order (TravelOrder: button -> story); its buttons are shown by the TravelShow globals.
Spell Property TravelPower Auto
Message Property TravelMenu Auto
{Buttons: one per story in history's order (TravelOrder), then Stay in this time.}
Int[] Property TravelOrder Auto
String[] Property TimelineEra Auto
String[] Property TimelinePlace Auto
Int[] Property TimelineYear Auto
Int[] Property TimelineMonth Auto
Int[] Property TimelineDay Auto
GlobalVariable Property GameYear Auto
GlobalVariable Property GameMonth Auto
GlobalVariable Property GameDay Auto
Int[] Property SavedYear Auto Hidden
Int[] Property SavedMonth Auto Hidden
Int[] Property SavedDay Auto Hidden
Message Property TravelNowhere Auto
GlobalVariable[] Property TravelShow Auto
ObjectReference[] Property TravelMarkers Auto
Bool[] Property Visited Auto Hidden

Int Property CurrentStory = -1 Auto Hidden
; Money (see ChangeMoney): the story whose coin the player carries, and the coin put away for every other story.
Int Property MoneyStory = -1 Auto Hidden
Int[] Property Purse Auto Hidden
; Fast travel works in every converted game's time (Daggerfall's world takes days to cross on foot). Skyrim's opening
; locks it until Helgen is escaped; the converted times never run that opening, so the lock is lifted there.
Bool Property FastTravelOnByUs = False Auto Hidden
; Each story's opening cinematic (remastered, Data\Video) plays once per playthrough, when the story first begins.
Bool[] Property IntroPlayed Auto Hidden
; The player's own race (its non-vampire form) while they are a Redguard in Redguard's time; None otherwise.
Race Property OriginalRace Auto Hidden
Bool Property StartingNewGame = False Auto Hidden
Bool Property PendingFirstStory = False Auto Hidden
Int Property PendingIntro = -1 Auto Hidden
; Silence above everything (TMSilence, priority 1, cutting abruptly): the main menu's theme stops when a story begins
; instead of playing on into its intro (user, 2026-10-09: "skyrim menu music does not bleed into the game intros").
MusicType Property Silence Auto
; Asked before each story's opening cinematic: Watch, Skip, or Skip all intros (for the rest of the playthrough).
Message Property IntroPrompt Auto
Bool Property SkipIntros = False Auto Hidden
Bool Property AwaitingHelgenEscape = False Auto Hidden
Bool Property AwaitingRaceMenu = False Auto Hidden
Int Property PendingKit = -1 Auto Hidden

; How the game begins (user, 2026-10-12: "the games start in the corrisponding story sequence if chosen. we need an
; alternative start if one is traveled to or game started with the tutorial quest done and skyrims starting in riverwood
; with the imperial or stormcloak npc is chosen", "and player owned games equivelant"): the stories in order, or a
; time of the player's choosing (of the games they have), its opening played or done.
Message Property StartHow Auto
{Buttons: 0 the stories in order, 1 a time of my choosing.}
Message Property StartWhich Auto
{Buttons: one per story (story order), shown by StartShow.}
GlobalVariable[] Property StartShow Auto
Message Property StartOpening Auto
{Buttons: 0 play the opening, 1 begin after it.}
Message Property StartSkyrim Auto
{Buttons: 0 play Helgen, 1 escaped with Hadvar (Imperial) to Riverwood, 2 escaped with Ralof (Stormcloak) to Riverwood.}
; Each converted game after its opening (AltStarts, by story 0-3): the reference (its plugin's FormID), the offset from
; it, the quest its opening hands on and the stage, and what the opening gives; 0 for none.
Int[] Property AfterOpeningIds Auto
Float[] Property AfterOpeningOffsets Auto
Int[] Property OpeningQuestIds Auto
Int[] Property OpeningQuestStages Auto
Int[] Property OpeningItemIds Auto
; Skyrim after Helgen, as vanilla's own quickstart leaves it (QF_MQ101 Fragment_27), then on to Riverwood.
Quest Property MQ102A Auto
Quest Property MQ102B Auto
ObjectReference Property Hadvar Auto
ObjectReference Property Ralof Auto
ObjectReference Property RiverwoodMarker Auto
MusicType Property HelgenMusicDungeon Auto
MusicType Property HelgenMusicBoss Auto
ObjectReference Property HelgenAttackSound Auto
Form[] Property KitImperial Auto
Form[] Property KitStormcloak Auto
; A new game's way in, chosen once the world is up: -1 not yet.
Int Property ChosenWay = -1 Auto Hidden

Int Property WayOpening = 0 AutoReadOnly
Int Property WayAfterOpening = 1 AutoReadOnly
Int Property WayHadvar = 2 AutoReadOnly
Int Property WayRalof = 3 AutoReadOnly

; Stage 0 normal-start fragment. Warp straight into the first story's opening dungeon.
Function Fragment_Takeover()
  Debug.Trace("[TamrielMerge] MQ101 stage 0: beginning the first story")
  If Silence
    Silence.Add()
  EndIf
  Game.DisablePlayerControls()
  Game.FadeOutGame(true, true, 0.0, 0.5)
  BeginFirstStory()
EndFunction

Function BeginFirstStory()
  Int first = NextAvailableStory(-1)
  If first == StorySkyrim
    ; Skyrim alone: the world must be up before the start can be chosen, so the player waits at Riverwood (Helgen's
    ; cart, if chosen, takes them from there itself).
    CurrentStory = StorySkyrim
    Game.GetPlayer().MoveTo(RiverwoodMarker)
    PendingIntro = -1
    PendingFirstStory = True
    RegisterForSingleUpdate(0.5)
    Return
  EndIf
  ; The first converted story begins the game (a missing one is skipped: without Daggerfall, Redguard's
  ; opening, and so on). Daggerfall's start kit is Daggerfall Unity's (StartingItems: clothes, then the sword);
  ; Cyrus lands on Stros M'Kai armed, so Redguard keeps the sword too; Morrowind and Oblivion open with the
  ; player a prisoner, so there only the clothes.
  Actor player = Game.GetPlayer()
  player.RemoveAllItems()
  ; Given now, put on only once the world is up and the race menu has closed (EquipStartKit): equipped here, while
  ; the new game still loads under the main menu, the sword stayed invisible even in a swing (user, 2026-10-09: "with
  ; daggerfall my weapon that i start with is invisible even when i attack").
  Int i = 0
  While i < StartingItems.Length
    If first == StoryDaggerfall || first == StoryRedguard || StartingItems[i] as Armor
      player.AddItem(StartingItems[i], 1, true)
    EndIf
    i += 1
  EndWhile
  If first == StoryDaggerfall
    player.AddItem(Gold, StartingGold, true)
    If Torch
      player.AddItem(Torch, StartingTorches, true)
    EndIf
  EndIf
  PendingKit = first
  GameHour.SetValue(8)
  StartingNewGame = True
  BeginStory(first)
  StartingNewGame = False
  ; A new game runs this (stage 0) while still loading under the main menu: moving the player there is what lets the
  ; world load and the menu close. The intro (and the prompt before it), the music and the race menu wait for an
  ; update, which runs once the world does, so the intro doesn't play over the main menu and its music.
  PendingIntro = first
  PendingFirstStory = True
  RegisterForSingleUpdate(0.5)
EndFunction

Function AfterFirstArrival()
  Game.FadeOutGame(false, true, 0.0, 0.5)
  Game.EnablePlayerControls()
  ; How the game begins; Helgen's own opening shows the race menu itself, at its ledger.
  If ChooseStart()
    Return
  EndIf
  PlayIntro(PendingIntro)
  If Silence
    Silence.Remove()
  EndIf
  Game.ShowRaceMenu()
  ; Changing race in the race menu leaves what the player wears invisible until it is put on again: re-equip it
  ; when the menu closes (SKSE's menu event, or by checking every two seconds without SKSE).
  AwaitingRaceMenu = True
  RegisterForMenu("RaceSex Menu")
  RegisterForSingleUpdate(2.0)
EndFunction

Event OnMenuClose(String menuName)
  If menuName == "RaceSex Menu" && AwaitingRaceMenu
    AwaitingRaceMenu = False
    EquipStartKit()
    RefreshEquipment()
  EndIf
EndEvent

; The first story's kit put on, once the player is loaded and the race menu's rebuild of the body is done: the
; clothes, the sword (Daggerfall, Redguard) and, in Privateer's Hold's dark, a lit torch in the left hand.
Function EquipStartKit()
  If PendingKit < 0
    Return
  EndIf
  Int story = PendingKit
  PendingKit = -1
  Actor player = Game.GetPlayer()
  WaitForPlayer3D()
  If story >= 100
    Form[] kit = KitStormcloak
    If story == 100 + WayHadvar
      kit = KitImperial
    EndIf
    Int k = 0
    While kit && k < kit.Length
      player.EquipItem(kit[k], false, true)
      k += 1
    EndWhile
    Return
  EndIf
  Int i = 0
  While i < StartingItems.Length
    If story == StoryDaggerfall || story == StoryRedguard || StartingItems[i] as Armor
      player.EquipItem(StartingItems[i], false, true)
    EndIf
    i += 1
  EndWhile
  If story == StoryDaggerfall && Torch
    player.EquipItem(Torch, false, true)
  EndIf
  Debug.Trace("[TamrielMerge] start kit put on (story " + story + "), right hand " + player.GetEquippedWeapon(false))
EndFunction

; Until the player's 3D is loaded, then a moment more: closing the race menu rebuilds the body a frame or two later,
; and what is put on before that rebuild is not drawn.
Function WaitForPlayer3D()
  Actor player = Game.GetPlayer()
  Int waited = 0
  While !player.Is3DLoaded() && waited < 40
    Utility.Wait(0.25)
    waited += 1
  EndWhile
  Utility.Wait(1.5)
EndFunction

; Skyrim shows nothing of what an actor wears after its race changes (SetRace, the race menu) or after items are
; equipped while it is still loading, until each item is equipped again. Waits for the player to be loaded, then
; takes off and puts back on the worn armour, both hands' weapons and the shield.
Function RefreshEquipment()
  Actor player = Game.GetPlayer()
  WaitForPlayer3D()
  Form[] worn = new Form[32]
  Int count = 0
  Int slot = 30
  While slot <= 61
    Armor piece = player.GetEquippedArmorInSlot(slot)
    If piece && worn.Find(piece) < 0
      worn[count] = piece
      count += 1
    EndIf
    slot += 1
  EndWhile
  Weapon right = player.GetEquippedWeapon(false)
  Weapon left = player.GetEquippedWeapon(true)
  Armor shield = player.GetEquippedShield()
  Int i = 0
  While i < count
    player.UnequipItem(worn[i], false, true)
    i += 1
  EndWhile
  If right
    player.UnequipItem(right, false, true)
  EndIf
  If left && left != right
    player.UnequipItem(left, false, true)
  EndIf
  If shield && worn.Find(shield) < 0
    player.UnequipItem(shield, false, true)
  EndIf
  Utility.Wait(0.1)
  i = 0
  While i < count
    player.EquipItem(worn[i], false, true)
    i += 1
  EndWhile
  If right
    player.EquipItemEx(right, 1, false, false)
  EndIf
  If left && left != right
    player.EquipItemEx(left, 2, false, false)
  EndIf
  If shield && worn.Find(shield) < 0
    player.EquipItem(shield, false, true)
  EndIf
  ; SKSE: redraw the body and what it holds now (without SKSE this one call fails and the rest stands).
  player.QueueNiNodeUpdate()
  Debug.Trace("[TamrielMerge] equipment refreshed: " + count + " worn, right hand " + right + ", left hand " + left)
EndFunction

; Where a story begins: a placed reference (a missing plugin, or a form that isn't a reference,
; gives None, so that story is skipped). Skyrim has no marker; its opening is MQ101's.
ObjectReference Function StartOf(Int story)
  If story == StoryDaggerfall
    Return Game.GetFormFromFile(DaggerfallStartFormId, DaggerfallPlugin) as ObjectReference
  ElseIf story == StoryRedguard
    Return Game.GetFormFromFile(RedguardStartFormId, RedguardPlugin) as ObjectReference
  ElseIf story == StoryMorrowind
    Return Game.GetFormFromFile(MorrowindStartFormId, MorrowindPlugin) as ObjectReference
  ElseIf story == StoryOblivion
    Return Game.GetFormFromFile(OblivionStartFormId, OblivionPlugin) as ObjectReference
  EndIf
  Return None
EndFunction

Bool Function IsStoryAvailable(Int story)
  If story == StorySkyrim
    Return True
  EndIf
  Return StartOf(story) != None
EndFunction

Int Function NextAvailableStory(Int after)
  Int story = after + 1
  While story < StoryCount
    If IsStoryAvailable(story)
      Return story
    EndIf
    story += 1
  EndWhile
  Return -1
EndFunction

; Called by the Begin the Next Story power.
Function BeginNextStory()
  Int next = NextAvailableStory(CurrentStory)
  If next < 0
    NoMoreStories.Show()
    Return
  EndIf
  If ConfirmNextStory.Show() == 0
    BeginStory(next)
  EndIf
EndFunction

; Hook for each story's main quest ending.
Function StoryCompleted(Int story)
  If story != CurrentStory || NextAvailableStory(CurrentStory) < 0
    Return
  EndIf
  If ContinuePrompt.Show() == 0
    BeginStory(NextAvailableStory(CurrentStory))
  Else
    Game.GetPlayer().AddSpell(NextStoryPower)
  EndIf
EndFunction

Function BeginStory(Int story)
  Debug.Trace("[TamrielMerge] beginning story " + story)
  ; Before anything moves: Skyrim's opening sets the player's items aside, and only Skyrim's coin may go with them.
  ChangeMoney(CurrentStory, story)
  If CurrentStory >= 0
    RecordLocation()
  EndIf
  CurrentStory = story
  If TravelPower
    Game.GetPlayer().AddSpell(TravelPower, false)
  EndIf
  If Silence
    Silence.Add()
  EndIf
  If !StartingNewGame
    PlayIntro(story)   ; a new game's waits until the world is up (AfterFirstArrival)
  EndIf
  If story == StorySkyrim
    BeginSkyrim(True)
  Else
    TravelTo(StartOf(story))
  EndIf
  ; Arrived: the place's own music again (a new game's after its intro).
  If Silence && !StartingNewGame
    Silence.Remove()
  EndIf
  ArriveInTime(story)
  ; Until each story's main quest is converted there is no ending to trigger StoryCompleted, so the
  ; power is available from the start; it asks for confirmation before moving on.
  If NextAvailableStory(CurrentStory) >= 0
    Game.GetPlayer().AddSpell(NextStoryPower, false)
  Else
    Game.GetPlayer().RemoveSpell(NextStoryPower)
  EndIf
EndFunction

; The game's own opening cinematic, remastered: Daggerfall's Emperor, Redguard's prologue, Morrowind's dream,
; Oblivion's prophecy. Skyrim's opening is its cart ride.
String Function IntroVideo(Int story)
  If story == StoryDaggerfall
    Return "TamrielMerge_DaggerfallIntro.bik"
  ElseIf story == StoryRedguard
    Return "TamrielMerge_RedguardIntro.bik"
  ElseIf story == StoryMorrowind
    Return "TamrielMerge_MorrowindIntro.bik"
  ElseIf story == StoryOblivion
    Return "TamrielMerge_OblivionIntro.bik"
  EndIf
  Return ""
EndFunction

; Plays a story's opening cinematic the first time that story begins (Escape skips it, as Skyrim's own).
Function PlayIntro(Int story)
  If !IntroPlayed
    IntroPlayed = new Bool[5]
  EndIf
  If story < 0 || story >= IntroPlayed.Length || IntroPlayed[story]
    Return
  EndIf
  IntroPlayed[story] = True
  String video = IntroVideo(story)
  If video == "" || SkipIntros
    Return
  EndIf
  If IntroPrompt
    Int choice = IntroPrompt.Show()
    If choice == 2
      SkipIntros = True
      Return
    ElseIf choice == 1
      Return
    EndIf
  EndIf
  Debug.Trace("[TamrielMerge] playing " + video)
  ; Interruptible: a key also ends it.
  Game.PlayBink(video, True, True, True, True)
EndFunction

Function TravelTo(ObjectReference destination)
  Actor player = Game.GetPlayer()
  Game.DisablePlayerControls()
  Game.FadeOutGame(true, true, 0.0, 0.5)
  Debug.Trace("[TamrielMerge] moving player to " + destination)
  player.MoveTo(destination)
  Int guard = 0
  While !player.Is3DLoaded() && guard < 300
    Utility.Wait(0.1)
    guard += 1
  EndWhile
  Debug.Trace("[TamrielMerge] arrived, 3D loaded = " + player.Is3DLoaded())
  Game.EnablePlayerControls()
  Game.FadeOutGame(false, true, 0.0, 1.0)
  ; A save where the story (or time travel) brought the player: dying reloads the latest save, which would
  ; otherwise be from before (user, 2026-10-09: "dying in any game returns you to riverwood inn").
  Utility.Wait(1.0)
  Game.RequestAutoSave()
EndFunction

; Skyrim's opening (stage 10, vanilla Fragment_4) strips the player, so set their items aside first
; and hand them back once the player has escaped Helgen (MQ101 completed).
Function BeginSkyrim(Bool keepItems)
  CurrentStory = StorySkyrim
  If keepItems
    Game.GetPlayer().RemoveAllItems(StashChest, true, true)
    AwaitingHelgenEscape = True
    RegisterForSingleUpdate(30.0)
  EndIf
  Game.EnablePlayerControls()
  Game.FadeOutGame(false, true, 0.0, 0.5)
  ; Vanilla QF_MQ101_0003372B.Fragment_2, verbatim.
  GameHour.SetValue(7)
  SetStage(10)
EndFunction

Event OnUpdate()
  If PendingFirstStory
    PendingFirstStory = False
    Debug.Trace("[TamrielMerge] the world is up: the first story's intro and race menu")
    AfterFirstArrival()
    Return
  EndIf
  If AwaitingRaceMenu
    If Utility.IsInMenuMode()
      RegisterForSingleUpdate(2.0)
      Return
    EndIf
    AwaitingRaceMenu = False
    EquipStartKit()
    RefreshEquipment()
    If AwaitingHelgenEscape
      RegisterForSingleUpdate(30.0)
    EndIf
    Return
  EndIf
  If AwaitingHelgenEscape
    If IsCompleted()
      AwaitingHelgenEscape = False
      StashChest.RemoveAllItems(Game.GetPlayer(), true, true)
      Debug.Notification("Your belongings from your earlier travels have been returned.")
    Else
      RegisterForSingleUpdate(30.0)
    EndIf
  EndIf
EndEvent

; ---- Alternative starts ----

; The game's plugin a story's references come from.
String Function PluginOf(Int story)
  If story == StoryDaggerfall
    Return DaggerfallPlugin
  ElseIf story == StoryRedguard
    Return RedguardPlugin
  ElseIf story == StoryMorrowind
    Return MorrowindPlugin
  ElseIf story == StoryOblivion
    Return OblivionPlugin
  EndIf
  Return "Skyrim.esm"
EndFunction

; Where a converted game begins after its opening, or None.
ObjectReference Function AfterOpeningOf(Int story)
  If !AfterOpeningIds || story < 0 || story >= AfterOpeningIds.Length || AfterOpeningIds[story] == 0
    Return None
  EndIf
  Return Game.GetFormFromFile(AfterOpeningIds[story], PluginOf(story)) as ObjectReference
EndFunction

; Asks how a time begins: its opening or after it; Skyrim's: Helgen, or escaped with Hadvar or Ralof to Riverwood.
Int Function ChooseWay(Int story)
  If story == StorySkyrim
    Int k = StartSkyrim.Show()
    If k == 1
      Return WayHadvar
    ElseIf k == 2
      Return WayRalof
    EndIf
    Return WayOpening
  EndIf
  If !AfterOpeningOf(story)
    Return WayOpening
  EndIf
  If StartOpening.Show() == 1
    Return WayAfterOpening
  EndIf
  Return WayOpening
EndFunction

; A new game, the world up (the player waiting at the first story's start, or Riverwood): the stories in order or a
; time of the player's choosing (of the games converted), and how it begins. True when it leaves nothing more to do
; here (Helgen's opening shows its own race menu); otherwise the intro and race menu follow.
Bool Function ChooseStart()
  Int first = NextAvailableStory(-1)
  Int available = 0
  Int story = 0
  While story < StoryCount
    Bool here = IsStoryAvailable(story)
    If StartShow && story < StartShow.Length && StartShow[story]
      StartShow[story].SetValue(here as Int)
    EndIf
    If here
      available += 1
    EndIf
    story += 1
  EndWhile
  Int chosen = first
  If available > 1 && StartHow && StartHow.Show() == 1
    chosen = StartWhich.Show()
    If !IsStoryAvailable(chosen)
      chosen = first
    EndIf
  EndIf
  Int way = ChooseWay(chosen)
  ChosenWay = way
  If chosen == first && way == WayOpening && chosen != StorySkyrim
    Return False   ; already at its start: its intro and the race menu follow
  EndIf
  ; Another start: the first story's kit goes, the chosen one's comes.
  Actor player = Game.GetPlayer()
  If chosen != first
    player.RemoveAllItems()
  EndIf
  PendingIntro = -1
  If chosen == StorySkyrim && way == WayOpening
    ; Helgen: its own music and race menu (the silence over the main menu's theme lifted first).
    If Silence
      Silence.Remove()
    EndIf
    ChangeMoney(MoneyHere(), StorySkyrim)
    CurrentStory = StorySkyrim
    BeginSkyrim(False)
    ArriveInTime(StorySkyrim)
    Return True
  EndIf
  If chosen != first
    GiveKit(chosen, way)
  ElseIf way >= WayHadvar
    GiveKit(chosen, way)
  EndIf
  If way == WayOpening
    ; Another game's opening, as the story would begin it (its intro asked for there; this time not recorded as left).
    CurrentStory = -1
    BeginStory(chosen)
  Else
    If chosen != StorySkyrim
      PendingIntro = chosen   ; its opening cinematic is still offered
    EndIf
    BeginAfterOpening(chosen, way, True)
  EndIf
  Return False
EndFunction

; A start's kit: Daggerfall Unity's (clothes, the sword, gold, torches) for Daggerfall, its clothes and sword for
; Redguard and for Oblivion after its tutorial, the clothes for a prisoner's opening; Skyrim after Helgen the side's.
Function GiveKit(Int story, Int way)
  Actor player = Game.GetPlayer()
  Form[] kit = StartingItems
  If way == WayHadvar
    kit = KitImperial
  ElseIf way == WayRalof
    kit = KitStormcloak
  EndIf
  Int i = 0
  While kit && i < kit.Length
    If kit[i] && (way >= WayHadvar || story == StoryDaggerfall || story == StoryRedguard || (story == StoryOblivion && way == WayAfterOpening) || kit[i] as Armor)
      player.AddItem(kit[i], 1, true)
    EndIf
    i += 1
  EndWhile
  If story == StoryDaggerfall
    player.AddItem(Gold, StartingGold, true)
    If Torch
      player.AddItem(Torch, StartingTorches, true)
    EndIf
  EndIf
  PendingKit = story
  If way >= WayHadvar
    PendingKit = 100 + way   ; Skyrim's side: everything put on (EquipStartKit)
  EndIf
EndFunction

; A time begun after its opening (a new game's chosen start with advance, or travelling to a time not yet reached
; without): the player where the opening ends, with what it hands on; Skyrim's at Riverwood with Hadvar or Ralof.
Function BeginAfterOpening(Int story, Int way, Bool advance)
  Int here = MoneyHere()
  ChangeMoney(here, story)
  If advance
    CurrentStory = story
  EndIf
  If TravelPower
    Game.GetPlayer().AddSpell(TravelPower, false)
  EndIf
  If story == StorySkyrim
    EscapedHelgen(way == WayHadvar)
  Else
    ObjectReference spot = AfterOpeningOf(story)
    Int o = story * 3
    TravelToOffset(spot, AfterOpeningOffsets[o], AfterOpeningOffsets[o + 1], AfterOpeningOffsets[o + 2])
    ; The quest the opening hands on, at its first entry, and what it gives.
    If OpeningQuestIds && OpeningQuestIds[story] != 0
      Quest q = Game.GetFormFromFile(OpeningQuestIds[story], PluginOf(story)) as Quest
      If q
        If !q.IsRunning()
          q.Start()
        EndIf
        q.SetStage(OpeningQuestStages[story])
      EndIf
    EndIf
    If OpeningItemIds && OpeningItemIds[story] != 0
      Form item = Game.GetFormFromFile(OpeningItemIds[story], PluginOf(story))
      If item
        Game.GetPlayer().AddItem(item, 1)
      EndIf
    EndIf
  EndIf
  ArriveInTime(story)
  If advance
    If NextAvailableStory(CurrentStory) >= 0
      Game.GetPlayer().AddSpell(NextStoryPower, false)
    Else
      Game.GetPlayer().RemoveSpell(NextStoryPower)
    EndIf
  EndIf
EndFunction

; Skyrim after Helgen: as vanilla's quickstart (QF_MQ101 Fragment_27) leaves it, the side's companion and its Before the
; Storm begun, Helgen's gates shut and its escape over; then their Before the Storm on past the escape (allied, Riverwood
; ahead, agreed to go, out of Helgen) and both at Riverwood, where the companion's scene with Alvor or Gerdur plays.
Function EscapedHelgen(Bool imperial)
  Actor player = Game.GetPlayer()
  Game.SetInChargen(false, false, false)
  Quest side = MQ102B
  Actor friend = Ralof as Actor
  If imperial
    side = MQ102A
    friend = Hadvar as Actor
  EndIf
  side.SetStage(1)
  SetStage(25)
  SetStage(26)
  SetStage(1000)
  If HelgenMusicDungeon
    HelgenMusicDungeon.Remove()
  EndIf
  If HelgenMusicBoss
    HelgenMusicBoss.Remove()
  EndIf
  If HelgenAttackSound
    HelgenAttackSound.Disable()
  EndIf
  side.SetStage(10)
  side.SetStage(20)
  side.SetStage(25)
  side.SetStage(30)
  TravelToOffset(RiverwoodMarker, 0.0, 0.0, 0.0)
  If friend
    friend.MoveTo(player, 160.0, 0.0, 0.0)
    friend.EvaluatePackage()
  EndIf
  ApplyTravelRules()
EndFunction

; As TravelTo, set down at an offset from the destination (a door's outside).
Function TravelToOffset(ObjectReference destination, Float dx, Float dy, Float dz)
  If !destination
    Return
  EndIf
  Actor player = Game.GetPlayer()
  Game.DisablePlayerControls()
  Game.FadeOutGame(true, true, 0.0, 0.5)
  player.MoveTo(destination, dx, dy, dz)
  Int guard = 0
  While !player.Is3DLoaded() && guard < 300
    Utility.Wait(0.1)
    guard += 1
  EndWhile
  Game.EnablePlayerControls()
  Game.FadeOutGame(false, true, 0.0, 1.0)
  Utility.Wait(1.0)
  Game.RequestAutoSave()
EndFunction

; ---- Travel Through Time ----

; Which story's world a reference is in, by the plugin its cell comes from: each converted game's cells are
; that game's plugin's own records. Anything else (Skyrim and its DLC, or no cell) is Skyrim.
Int Function WorldOf(ObjectReference ref)
  Cell here = ref.GetParentCell()
  If !here
    Return CurrentStory
  EndIf
  Int plugin = PluginKey(here)
  Int story = 0
  While story < StorySkyrim
    ObjectReference start = StartOf(story)
    If start && PluginKey(start) == plugin
      Return story
    EndIf
    story += 1
  EndWhile
  Return StorySkyrim
EndFunction

; The load-order slot of a form's plugin: the top byte of its FormID, plus the light-plugin slot (bits 12-23)
; for FE. Papyrus has no unsigned or bitwise arithmetic, so FormIDs at or above 0x80000000 (negative) are
; split through their complement.
Int Function PluginKey(Form f)
  Int id = f.GetFormID()
  Int top
  Int low
  If id >= 0
    top = id / 16777216
    low = id - top * 16777216
  Else
    Int inv = -(id + 1)
    Int invTop = inv / 16777216
    top = 255 - invTop
    low = 16777215 - (inv - invTop * 16777216)
  EndIf
  If top == 254
    Return 254 * 4096 + low / 4096
  EndIf
  Return top * 4096
EndFunction

; Remember where the player is in the world they're in now.
Function RecordLocation()
  If !TravelMarkers
    Return
  EndIf
  If !Visited
    Visited = new Bool[5]
  EndIf
  Actor player = Game.GetPlayer()
  Int world = WorldOf(player)
  If world < 0 || world >= TravelMarkers.Length
    Return
  EndIf
  TravelMarkers[world].MoveTo(player)
  Visited[world] = True
  ; Each time keeps its own calendar: remember this one's date.
  EnsureCalendars()
  If SavedYear
    SavedYear[world] = GameYear.GetValueInt()
    SavedMonth[world] = GameMonth.GetValueInt()
    SavedDay[world] = GameDay.GetValueInt()
  EndIf
  Debug.Trace("[TamrielMerge] recorded location in world " + world)
EndFunction

; Called by the Travel Through Time power: record this time, then offer the other times visited so far.
Function TravelBetweenWorlds()
  RecordLocation()
  Int here = WorldOf(Game.GetPlayer())
  Int shown = 0
  Int world = 0
  While world < TravelShow.Length
    ; A time visited, or one not yet reached (begun after, or with, its opening).
    If world != here && IsStoryAvailable(world)
      TravelShow[world].SetValue(1)
      shown += 1
    Else
      TravelShow[world].SetValue(0)
    EndIf
    world += 1
  EndWhile
  If shown == 0
    TravelNowhere.Show()
    Return
  EndIf
  Int choice = TravelMenu.Show()
  If !TravelOrder || choice < 0 || choice >= TravelOrder.Length
    Return
  EndIf
  Int story = TravelOrder[choice]
  If story != here && !HasVisited(story) && IsStoryAvailable(story)
    EnterNewTime(story)
    Return
  EndIf
  If story != here && Visited[story]
    Debug.Trace("[TamrielMerge] travelling in time from story " + here + " to story " + story)
    ChangeMoney(here, story)
    TravelTo(TravelMarkers[story])
    ArriveInTime(story)
  EndIf
EndFunction

; Travelling to a time not yet reached (Travel Through Time, or the Tamriel menu's Travel tab): it begins there, its
; opening played or done (Skyrim's: Helgen, or Riverwood with Hadvar or Ralof). The story order's own place stays.
Function EnterNewTime(Int story)
  If !IsStoryAvailable(story) || HasVisited(story)
    Return
  EndIf
  RecordLocation()
  Int way = ChooseWay(story)
  Debug.Trace("[TamrielMerge] entering a time not yet reached: story " + story + ", way " + way)
  If way == WayOpening
    If story == StorySkyrim
      ChangeMoney(MoneyHere(), story)
      BeginSkyrim(True)
      ArriveInTime(story)
    Else
      ChangeMoney(MoneyHere(), story)
      TravelTo(StartOf(story))
      ArriveInTime(story)
    EndIf
  Else
    BeginAfterOpening(story, way, False)
  EndIf
EndFunction

; Whether a time has been visited (Travel Through Time offers only those).
Bool Function HasVisited(Int story)
  Return Visited && story >= 0 && story < Visited.Length && Visited[story]
EndFunction

; A time's era and year, as the Travel tab labels a destination with it (user, 2026-10-12: "we need the era to be
; labled with the destination"): "3E 427", the year its calendar stands at (now for the time the player is in).
String Function EraLabel(Int story)
  If !TimelineEra || story < 0 || story >= TimelineEra.Length
    Return ""
  EndIf
  Int year = 0
  If story == MoneyHere() && GameYear
    year = GameYear.GetValueInt()
  Else
    EnsureCalendars()
    If SavedYear && story < SavedYear.Length
      year = SavedYear[story]
    EndIf
  EndIf
  If year <= 0
    Return TimelineEra[story]
  EndIf
  Return TimelineEra[story] + " " + year
EndFunction

; The Tamriel menu's Travel tab (TMTravel): to a town in another time already visited, as Travel Through Time goes,
; but to that town rather than where the player last stood there.
Function TravelThroughTimeTo(Int story, ObjectReference destination)
  RecordLocation()
  Int here = WorldOf(Game.GetPlayer())
  If story == here || !HasVisited(story) || !destination
    Return
  EndIf
  Debug.Trace("[TamrielMerge] travelling in time from story " + here + " to story " + story + " (a town)")
  ChangeMoney(here, story)
  TravelTo(destination)
  ArriveInTime(story)
EndFunction

; ---- Money ----

; Each time has its own coin: Daggerfall's gold pieces, Redguard's gold, Morrowind's drakes, Oblivion's septims and
; Skyrim's gold. All of them are Skyrim's gold item, so barter, dialogue and every converted script count them as they
; are; what makes them separate is that the player carries only the coin of the time they are in. Travelling to another
; time puts the coin carried away in its purse and takes out that time's (nothing on a first visit), and the gold item
; takes that coin's name. The Tamriel menu lists every purse (MoneyOf).
Function ChangeMoney(Int fromStory, Int toStory)
  If !Gold || toStory < 0 || toStory >= StoryCount
    Return
  EndIf
  If !Purse
    Purse = new Int[5]
  EndIf
  If MoneyStory < 0
    MoneyStory = fromStory   ; the first change (or a game saved before money was kept): what is carried is fromStory's
  EndIf
  If MoneyStory == toStory
    NameMoney()
    Return
  EndIf
  Actor player = Game.GetPlayer()
  Int carried = 0
  If MoneyStory >= 0 && MoneyStory < StoryCount
    carried = player.GetItemCount(Gold)
    Purse[MoneyStory] = Purse[MoneyStory] + carried
    If carried > 0
      player.RemoveItem(Gold, carried, true)
    EndIf
  EndIf
  Int fromWhere = MoneyStory
  MoneyStory = toStory
  Int held = Purse[toStory]
  Purse[toStory] = 0
  If held > 0
    player.AddItem(Gold, held, true)
  EndIf
  NameMoney()
  Debug.Trace("[TamrielMerge] money: put away " + carried + " " + CoinName(fromWhere) + ", took out " + held + " " + CoinName(toStory))
  If carried > 0
    Debug.Notification("You put away your " + carried + " " + CoinName(fromWhere) + ". Here you have " + held + " " + CoinName(toStory) + ".")
  ElseIf held > 0
    Debug.Notification("Here you have " + held + " " + CoinName(toStory) + ".")
  EndIf
EndFunction

; The story whose coin the player carries now.
Int Function MoneyHere()
  If MoneyStory >= 0
    Return MoneyStory
  ElseIf CurrentStory >= 0
    Return CurrentStory
  EndIf
  Return StorySkyrim
EndFunction

; How much of a story's coin the player has: what they carry for the time they're in, the purse for the others.
Int Function MoneyOf(Int story)
  If story == MoneyHere()
    Return Game.GetPlayer().GetItemCount(Gold)
  ElseIf Purse && story >= 0 && story < Purse.Length
    Return Purse[story]
  EndIf
  Return 0
EndFunction

String Function CoinName(Int story)
  If CoinNames && story >= 0 && story < CoinNames.Length
    Return CoinNames[story]
  EndIf
  Return "gold"
EndFunction

; The gold item takes the name and weight of the coin carried (SKSE changes the game doesn't save: the Tamriel menu's
; player alias calls this again after every load).
Function NameMoney()
  If !Gold || SKSE.GetVersion() <= 0
    Return
  EndIf
  Int here = MoneyHere()
  If CoinNames
    Gold.SetName(CoinName(here))
  EndIf
  If CoinWeights && here >= 0 && here < CoinWeights.Length
    Gold.SetWeight(CoinWeights[here])
  EndIf
EndFunction

; A money changer (TMMoneyChanger) changes up to amount of another story's purse into the coin carried, keeping
; feePercent (rounded up). Returns the coin paid out.
Int Function Exchange(Int fromStory, Int amount, Int feePercent)
  Int here = MoneyHere()
  If !Purse || fromStory == here || fromStory < 0 || fromStory >= Purse.Length || amount <= 0
    Return 0
  EndIf
  If amount > Purse[fromStory]
    amount = Purse[fromStory]
  EndIf
  If amount <= 0
    Return 0
  EndIf
  Int paid = amount - (amount * feePercent + 99) / 100
  Purse[fromStory] = Purse[fromStory] - amount
  If paid > 0
    Game.GetPlayer().AddItem(Gold, paid, True)
  EndIf
  Debug.Trace("[TamrielMerge] money: changed " + amount + " " + CoinName(fromStory) + " into " + paid + " " + CoinName(here))
  Return paid
EndFunction

String Function GameName(Int story)
  If story == StoryDaggerfall
    Return "Daggerfall"
  ElseIf story == StoryRedguard
    Return "Redguard"
  ElseIf story == StoryMorrowind
    Return "Morrowind"
  ElseIf story == StoryOblivion
    Return "Oblivion"
  EndIf
  Return "Skyrim"
EndFunction

; The Tamriel menu's Coin tab: each converted game's coin, in story order, "game|coin|amount|carried|story".
Function SendMoneyToMenu()
  String menu = "CustomMenu"
  UI.InvokeString(menu, "_root.ClearMoney", "")
  Int here = MoneyHere()
  Int i = 0
  While i < StoryCount
    If IsStoryAvailable(i)
      String carried = "0"
      If i == here
        carried = "1"
      EndIf
      UI.InvokeString(menu, "_root.AddMoney", GameName(i) + "|" + CoinName(i) + "|" + MoneyOf(i) + "|" + carried + "|" + i)
    EndIf
    i += 1
  EndWhile
  UI.InvokeString(menu, "_root.ShowMoney", "")
EndFunction

; ---- Fast travel ----

; In Daggerfall's, Redguard's, Morrowind's and Oblivion's times fast travel is on (Skyrim's opening lock doesn't belong
; there); in Skyrim's time, before Helgen is escaped, Skyrim's own rule returns. Called on arriving and after loading.
Function ApplyTravelRules()
  Int here = MoneyHere()
  If here >= 0 && here < StorySkyrim
    Game.EnableFastTravel(True)
    FastTravelOnByUs = True
  ElseIf FastTravelOnByUs && !IsCompleted()
    ; Back in Skyrim's time before its opening is over: Skyrim's own lock again.
    Game.EnableFastTravel(False)
    FastTravelOnByUs = False
  EndIf
EndFunction

; ---- Calendars ----

Function EnsureCalendars()
  If !SavedYear && TimelineYear
    SavedYear = new Int[5]
    SavedMonth = new Int[5]
    SavedDay = new Int[5]
    Int i = 0
    While i < 5 && i < TimelineYear.Length
      SavedYear[i] = TimelineYear[i]
      SavedMonth[i] = TimelineMonth[i]
      SavedDay[i] = TimelineDay[i]
      i += 1
    EndWhile
  EndIf
EndFunction

; Arriving in a story's time: its calendar resumes where it was left (its start date on a first visit), and the
; date is announced with its own era (the game's date display always says 4E).
Function ArriveInTime(Int story)
  If story == StoryRedguard
    BecomeRedguard()
  Else
    RestoreRace()
  EndIf
  ; After the move (and any change of race) the player's gear can be left invisible: put it back on.
  RefreshEquipment()
  ApplyTravelRules()
  EnsureCalendars()
  If !SavedYear || !GameYear || story < 0 || story >= SavedYear.Length
    Return
  EndIf
  GameYear.SetValueInt(SavedYear[story])
  GameMonth.SetValueInt(SavedMonth[story])
  GameDay.SetValueInt(SavedDay[story])
  String era = ""
  String place = ""
  If TimelineEra && story < TimelineEra.Length
    era = TimelineEra[story]
    place = TimelinePlace[story]
  EndIf
  Debug.Notification(era + " " + SavedYear[story] + ", " + Ordinal(SavedDay[story]) + " of " + MonthName(SavedMonth[story]) + ": " + place)
EndFunction

; Redguard is Cyrus's story: whoever arrives there does so as a Redguard, and leaving Redguard's time (the next
; story, or travelling to another time) gives them back their own race. Only the race changes (its look and racial
; abilities); level, skills, attributes, perks, spells and inventory all stay. Vampirism carries across both ways (a
; vampire becomes a Redguard vampire and back); someone in beast form (werewolf, Vampire Lord) is left as they are.
; A new game that starts in Redguard's time opens the character creator on Redguard, and whatever race is chosen
; there is the player's own.
Int Function RaceIndex(Race r)
  ; Playable races in Skyrim.esm, base and vampire forms: Argonian, Breton, Dark Elf, High Elf, Imperial, Khajiit,
  ; Nord, Orc, Redguard, Wood Elf.
  If !r
    Return -1
  EndIf
  Int id = r.GetFormID()
  If id >= 0x013740 && id <= 0x013749
    Return id - 0x013740
  EndIf
  Int i = 0
  While i < 10
    If VampireRaceId(i) == id
      Return i
    EndIf
    i += 1
  EndWhile
  Return -1
EndFunction

Int Function VampireRaceId(Int index)
  If index == 0
    Return 0x08883A
  ElseIf index == 1
    Return 0x08883C
  ElseIf index == 2
    Return 0x08883D
  ElseIf index == 3
    Return 0x088840
  ElseIf index == 4
    Return 0x088844
  ElseIf index == 5
    Return 0x088845
  ElseIf index == 6
    Return 0x088794
  ElseIf index == 7
    Return 0x0A82B9
  ElseIf index == 8
    Return 0x088846
  EndIf
  Return 0x088884
EndFunction

; The playable race at index, in its vampire form or not.
Race Function RaceForm(Int index, Bool vampire)
  If vampire
    Return Game.GetFormFromFile(VampireRaceId(index), "Skyrim.esm") as Race
  EndIf
  Return Game.GetFormFromFile(0x013740 + index, "Skyrim.esm") as Race
EndFunction

Bool Function IsVampire(Race r)
  Keyword vampire = Game.GetFormFromFile(0x0A82BB, "Skyrim.esm") as Keyword
  Return r && vampire && r.HasKeyword(vampire)
EndFunction

Function BecomeRedguard()
  Actor player = Game.GetPlayer()
  Race current = player.GetRace()
  Int index = RaceIndex(current)
  If index < 0
    Return   ; beast form or a non-playable race: left as is
  EndIf
  If !OriginalRace && !StartingNewGame
    OriginalRace = RaceForm(index, False)
  EndIf
  Race target = RaceForm(8, IsVampire(current))
  If target && current != target
    Debug.Trace("[TamrielMerge] arriving in Redguard's time as a Redguard (was " + current + ")")
    player.SetRace(target)
  EndIf
EndFunction

Function RestoreRace()
  If !OriginalRace
    Return
  EndIf
  Actor player = Game.GetPlayer()
  Race current = player.GetRace()
  Int index = RaceIndex(current)
  If index < 0
    Return   ; beast form: restored on a later arrival
  EndIf
  Race target = RaceForm(RaceIndex(OriginalRace), IsVampire(current))
  OriginalRace = None
  If target && current != target
    Debug.Trace("[TamrielMerge] leaving Redguard's time: back to " + target)
    player.SetRace(target)
  EndIf
EndFunction

String Function Ordinal(Int day)
  Int lastTwo = day % 100
  If lastTwo >= 11 && lastTwo <= 13
    Return day + "th"
  ElseIf day % 10 == 1
    Return day + "st"
  ElseIf day % 10 == 2
    Return day + "nd"
  ElseIf day % 10 == 3
    Return day + "rd"
  EndIf
  Return day + "th"
EndFunction

String Function MonthName(Int month)
  If month == 0
    Return "Morning Star"
  ElseIf month == 1
    Return "Sun's Dawn"
  ElseIf month == 2
    Return "First Seed"
  ElseIf month == 3
    Return "Rain's Hand"
  ElseIf month == 4
    Return "Second Seed"
  ElseIf month == 5
    Return "Midyear"
  ElseIf month == 6
    Return "Sun's Height"
  ElseIf month == 7
    Return "Last Seed"
  ElseIf month == 8
    Return "Hearthfire"
  ElseIf month == 9
    Return "Frostfall"
  ElseIf month == 10
    Return "Sun's Dusk"
  EndIf
  Return "Evening Star"
EndFunction
