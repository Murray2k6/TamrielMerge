ScriptName TMRgState extends Quest
{What Redguard's converted scripts (TMRgSoup) share, on the always-running quest RGScripts: the lists that name
Redguard's flags (globals, by SOUP386.DEF index), items (by ITEM.INI index), sound effects (MAIN.SFX, by index) and
script lines (spoken by RGLine index), Cyrus's log, the spells that stand in for Redguard's fireballs and dragon
breath, the potion mixing state (GetPotion/SetPotion), the player's selected item and how many scripts hold the
player's controls; and what stands in for Redguard's engine where Skyrim has none of its own: camera shots, group
synchronisation, animation groups as Skyrim's idles, the movies (Bink), the screen pictures (a page showing the
image), the hand item, the player flown along a script's path, and hanging from objects (TMRgMove).}

FormList Property Flags Auto
FormList Property Items Auto
FormList Property Sounds Auto
FormList Property Lines Auto
Quest Property LogQuest Auto
Spell Property Fireball Auto
Spell Property Breath Auto
Spell Property Zap Auto
GlobalVariable[] Property MenuShow Auto
{Which buttons of a script's menu show (RGMenuShow0-9), as Redguard's menuAddItem added them.}
FormList Property Idles Auto
{Redguard's animation groups (SOUP386.DEF order) as Skyrim's idles for people (an empty entry: no counterpart).}
FormList Property PlayerIdles Auto
{The same for Cyrus, whose numbered "misc" groups are his own (drinking, using an item).}
Idle Property IdleStop Auto
{IdleForceDefaultState: back to standing.}
FormList Property Pictures Auto
{The screen pictures (ViewGXA's images) as notes that show the image, by the scripts' picture number.}
FormList Property Shells Auto
{The programs OpenShell runs on their own (activators carrying them), by the translator's number.}
Race Property GremlinRace Auto
{The player's body while he is the gremlin (PushPlayer('gremlin') in MGUILD's SCENE4, PopPlayer in JAILINT's JOTO):
Redguard's GREMLIN actor has no model of its own that Skyrim can animate, so Skyrim's nearest small creature race
(Dragonborn's Riekling) stands in.}
FormList Property HandTwins Auto
{By item index (as Items): a weapon record wearing the item's hand model (ITEM.INI hand_object_file), for HandItem(n)
on items Skyrim can't otherwise hold; a non-weapon entry where the item has none (weapons and the torch are held as
themselves, potions drunk).}
TMRgMove Property Mover Auto
{Ledges, ropes and hanging (the quest RGMovement).}
GlobalVariable Property FirstPersonAfterShots Auto
{1: after a cutscene that showed Cyrus in third person, the camera goes back to first person (0: it stays, as
Redguard's own camera did).}

Float[] Property EffectSeconds Auto
{RGSounds starts with every effect by index as FlatSound(n, 0, 0) plays it (None where unused): how long each plays,
all its loops (0: until stopped).}
Int[] Property SoundKeys Auto
{The entries after those, the other ways scripts play an effect: (kind x 128 + effect) x 1024 + volume offset + 512,
kind 0 placed (Sound, AmbientSound: the offset's own output model), 1 centred (FlatSound: the effect's volume plus
the offset).}
Int[] Property SoundRates Auto
{Each of those entries' rate offset (its recording plays at the effect's rate plus it).}
Float[] Property SoundSeconds Auto
{How long each of those plays, all its loops (0: until stopped).}
Float[] Property SoundReach Auto
{How far off each of those is heard when placed: Redguard's volume is 0 from there on (0 for a centred one).}
Int[] Property EffectLoops Auto
{1 where the effect at that index loops (stopped once its loops have played), 0 where it plays once.}
Int[] Property SoundLoops Auto
{The same for the entries after them.}

Int[] potions
Int Property SelectedItem = 1 Auto Hidden
Int lockouts = 0
Float Property DialogDue = 0.0 Auto Hidden
Float Property ActivateHold = 0.0 Auto Hidden
{Redguard's activation hold-off: no object activates before this real time (22 ticks after one fires; Deactivate
clears it).}
Int Property RndState = 1 Auto Hidden
{The engine's shared random stream (Redguard Preservation, engine/SOUP.html "Rnd and QuickRnd"): an unsigned 32-bit
state, 1 at the start, stepped state * 1103515245 + 12345 for each draw.}

; One draw of the shared stream: (state >> 16) & 32767 after the step (Papyrus's Int wraps as the engine's does).
Int Function Draw()
  RndState = RndState * 1103515245 + 12345
  Return Math.LogicalAnd(Math.RightShift(RndState, 16), 32767)
EndFunction

Event OnInit()
  potions = new Int[32]
  Channels()
EndEvent

; ---- sound: Redguard's 16 sample channels (Redguard Preservation, engine/sound.html) ----
; A start takes the first idle channel at or above its start index (0 speech, 6 the scripts' sounds); with none idle
; the sound is dropped, never stealing one. Each remembers its owner (the object whose script started it) and effect,
; for EndSound (an effect, whoever's), EndMySound (an effect of the owner's), EndMySounds (all the owner's) and
; StopAllSounds (all 16). A channel is idle again when its sound has played out (all its loops; a sound looping until
; stopped holds it until then).

Int[] chanId
Int[] chanEffect
ObjectReference[] chanOwner
Float[] chanEnd
Bool[] chanLoop

Function Channels()
  If chanId
    Return
  EndIf
  chanId = new Int[16]
  chanEffect = new Int[16]
  chanOwner = new ObjectReference[16]
  chanEnd = new Float[16]
  chanLoop = new Bool[16]
  Int c = 0
  While c < 16
    chanId[c] = -1
    chanEffect[c] = -1
    c += 1
  EndWhile
EndFunction

Int Function Effects()
  If EffectSeconds
    Return EffectSeconds.Length
  EndIf
  Return 0
EndFunction

; The RGSounds entry for an effect played one way (kind 0 placed, 1 centred) at a volume and rate offset, -1 for none.
Int Function SoundEntry(Int kind, Int effect, Int off, Int rate)
  If effect < 0 || effect >= Effects()
    Return -1
  EndIf
  If kind == 1 && off == 0 && rate == 0
    If Sounds.GetAt(effect) as Sound
      Return effect
    EndIf
    Return -1
  EndIf
  If !SoundKeys
    Return -1
  EndIf
  If off < -512
    off = -512
  ElseIf off > 511
    off = 511
  EndIf
  Int wanted = (kind * 128 + effect) * 1024 + off + 512
  Int i = SoundKeys.Find(wanted)
  While i >= 0 && SoundRates[i] != rate
    i = SoundKeys.Find(wanted, i + 1)
  EndWhile
  If i < 0
    Return -1
  EndIf
  Return Effects() + i
EndFunction

Float Function SoundReachOf(Int entry)
  Int i = entry - Effects()
  If SoundReach && i >= 0 && i < SoundReach.Length
    Return SoundReach[i]
  EndIf
  Return 0.0
EndFunction

Float Function SoundSecondsOf(Int entry)
  Int i = entry - Effects()
  If i < 0
    If entry >= 0 && entry < Effects()
      Return EffectSeconds[entry]
    EndIf
  ElseIf SoundSeconds && i < SoundSeconds.Length
    Return SoundSeconds[i]
  EndIf
  Return 0.0
EndFunction

Bool Function SoundLoopsOf(Int entry)
  Int i = entry - Effects()
  If i < 0
    Return EffectLoops && entry >= 0 && entry < EffectLoops.Length && EffectLoops[entry] != 0
  EndIf
  Return SoundLoops && i < SoundLoops.Length && SoundLoops[i] != 0
EndFunction

Bool Function ChannelPlaying(Int c)
  Channels()
  If c < 0 || c >= 16 || chanId[c] < 0
    Return False
  EndIf
  If chanEnd[c] > 0.0 && Utility.GetCurrentRealTime() >= chanEnd[c]
    ; Played out: a sound looping a number of times is stopped after the last; one played once has ended by itself
    ; (stopping it again was an error each time, 2026-11-04).
    If chanLoop[c]
      Sound.StopInstance(chanId[c])
    EndIf
    chanId[c] = -1
    chanOwner[c] = None
    chanEffect[c] = -1
    Return False
  EndIf
  Return True
EndFunction

; Starts a sound on the first idle channel from first: the entry's sound at the given reference (placed, or the
; player for a centred one), its volume scaled by gain (1: as built). Returns the channel, -1 when dropped.
Int Function PlaySound(Sound s, Float seconds, Int effect, ObjectReference owner, ObjectReference at, Int first, Float gain, Bool loops = False)
  Channels()
  ; Nowhere to play it: a placed sound needs its object's 3D (one far off in an unloaded cell is out of hearing too).
  If !s || !at || !at.Is3DLoaded()
    Return -1
  EndIf
  Int c = first
  While c < 16 && ChannelPlaying(c)
    c += 1
  EndWhile
  If c >= 16
    Return -1
  EndIf
  Int id = s.Play(at)
  If id <= 0
    Return -1
  EndIf
  If gain < 1.0
    If gain < 0.0
      gain = 0.0
    EndIf
    Sound.SetInstanceVolume(id, gain)
  EndIf
  chanId[c] = id
  chanLoop[c] = loops
  chanEffect[c] = effect
  chanOwner[c] = owner
  If seconds > 0.0
    chanEnd[c] = Utility.GetCurrentRealTime() + seconds
  Else
    chanEnd[c] = 0.0
  EndIf
  Return c
EndFunction

Int Function PlayEntry(Int entry, Int effect, ObjectReference owner, ObjectReference at, Int first, Float gain)
  If entry < 0 || entry >= Sounds.GetSize()
    Return -1
  EndIf
  Return PlaySound(Sounds.GetAt(entry) as Sound, SoundSecondsOf(entry), effect, owner, at, first, gain, SoundLoopsOf(entry))
EndFunction

Function StopChannel(Int c)
  Channels()
  If c >= 0 && c < 16 && chanId[c] >= 0
    Sound.StopInstance(chanId[c])
    chanId[c] = -1
    chanOwner[c] = None
    chanEffect[c] = -1
  EndIf
EndFunction

; Stops every playing channel of the effect (any: -1) and owner (anyone's: None).
Function EndSounds(Int effect, ObjectReference owner)
  Channels()
  Int c = 0
  While c < 16
    If ChannelPlaying(c) && (effect < 0 || chanEffect[c] == effect) && (!owner || chanOwner[c] == owner)
      StopChannel(c)
    EndIf
    c += 1
  EndWhile
EndFunction

Function StopAllSounds()
  Channels()
  Int c = 0
  While c < 16
    StopChannel(c)
    c += 1
  EndWhile
EndFunction

Int Function GetPotion(Int i)
  If !potions
    potions = new Int[32]
  EndIf
  If i < 0 || i >= potions.Length
    Return 0
  EndIf
  Return potions[i]
EndFunction

Function SetPotion(Int i, Int v)
  If !potions
    potions = new Int[32]
  EndIf
  If i >= 0 && i < potions.Length
    potions[i] = v
  EndIf
EndFunction

; Redguard's lockoutPlayer(1/0): scripts nest it, so controls come back when the last one lets go (and a camera shot
; of the cutscene ends with it).
Function Lockout(Bool on, ObjectReference akBy = None)
  If on
    lockouts += 1
    If lockouts == 1
      Game.DisablePlayerControls(True, True, False, False, True, True, True)
    EndIf
  ElseIf lockouts > 0
    lockouts -= 1
    If lockouts == 0
      ; The shot ends first: leaving the free camera, the game puts the controls back as they were when the camera was
      ; taken (held, under this lockout), over anything enabled before it. In a new game the docks' cutscene let go
      ; (0 held) and Cyrus still could not walk: the console listed Menu disabled, and its EnablePlayerControls freed
      ; him (2026-11-12, user: "you cant still move after the opening while on the ship").
      EndShot()
      If !flying && !hangingOn
        Game.EnablePlayerControls()
      EndIf
    EndIf
  EndIf
  Debug.Trace("[TamrielMerge] Redguard lockout " + on + " by " + akBy + ": " + lockouts + " held")
EndFunction

; A map change ends every cutscene's hold on the player, as Redguard's engine does: its opening (START's SCENEST) locks
; Cyrus out and never lets go, the island's load does (2026-11-05, user: "redguard i still cant move when npcs can").
; LoadWorld calls it, and TMStoryOrder before every journey between times and places.
Function MapChanged()
  If lockouts > 0 || flying
    Debug.Trace("[TamrielMerge] Redguard: a map change released " + lockouts + " lockouts (flying " + flying + ")")
  EndIf
  lockouts = 0
  If flying
    Game.GetPlayer().StopTranslation()
    flying = False
  EndIf
  EndShot()
  If !hangingOn
    Game.EnablePlayerControls()
  EndIf
EndFunction

; Cyrus's log (Redguard's AddLog): the entry's stage set on the log quest, started if it isn't running, and the entry's
; objective shown in place of the one before it (the log is a diary: its entries stay in the journal, the newest is
; what the quest shows).
Int lastLog = 0

Function AddLog(Int stage)
  If !LogQuest || stage <= 0
    Return
  EndIf
  If !LogQuest.IsRunning()
    LogQuest.Start()
  EndIf
  If LogQuest.IsStageDone(stage)
    Return
  EndIf
  LogQuest.SetStage(stage)
  If lastLog > 0 && lastLog != stage
    LogQuest.SetObjectiveDisplayed(lastLog, False)
  EndIf
  LogQuest.SetObjectiveDisplayed(stage, True, True)
  lastLog = stage
  Debug.Trace("[TamrielMerge] Redguard log entry " + stage + " (quest running " + LogQuest.IsRunning() + ", stage done " + LogQuest.IsStageDone(stage) + ")")
EndFunction

Bool Function LockedOut()
  Return lockouts > 0
EndFunction

Function ClearMenu()
  If !MenuShow
    Return
  EndIf
  Int i = 0
  While i < MenuShow.Length
    MenuShow[i].SetValue(0)
    i += 1
  EndWhile
EndFunction

Function ShowMenuItem(Int i)
  If MenuShow && i >= 0 && i < MenuShow.Length
    MenuShow[i].SetValue(1)
  EndIf
EndFunction

Int Function Flag(Int i)
  GlobalVariable g = Flags.GetAt(i) as GlobalVariable
  If g
    Return g.GetValueInt()
  EndIf
  Return 0
EndFunction

Function SetFlag(Int i, Int v)
  GlobalVariable g = Flags.GetAt(i) as GlobalVariable
  If g
    g.SetValueInt(v)
  EndIf
EndFunction

Form Function Item(Int i)
  If i < 0 || i >= Items.GetSize()
    Return None
  EndIf
  Return Items.GetAt(i)
EndFunction

; ---- the torch (Redguard's torch_timer, object field 9: a lit torch burns 60 s, then goes out) ----

Float torchUntil = -1.0

Function TorchLit(Actor a)
  If a != Game.GetPlayer()
    Return
  EndIf
  torchUntil = Utility.GetCurrentRealTime() + 60.0
  RegisterForSingleUpdate(1.0)
EndFunction

Function TorchOut()
  torchUntil = -1.0
EndFunction

; Seconds left on the player's torch (0 when it isn't burning).
Int Function TorchTime()
  If torchUntil < 0.0
    Return 0
  EndIf
  Float left = torchUntil - Utility.GetCurrentRealTime()
  If left <= 0.0
    Return 0
  EndIf
  Return Math.Ceiling(left)
EndFunction

; A script setting the timer (0 puts the torch out).
Function SetTorchTime(Int seconds)
  Actor p = Game.GetPlayer()
  Form torch = Item(45)
  If seconds <= 0
    torchUntil = -1.0
    If torch && p.IsEquipped(torch)
      p.UnequipItem(torch, False, True)
    EndIf
    Return
  EndIf
  torchUntil = Utility.GetCurrentRealTime() + seconds
  RegisterForSingleUpdate(1.0)
EndFunction

; The torch's tick (the one OnUpdate below serves the flights too): True while it still burns.
Bool Function TorchTick()
  If torchUntil < 0.0
    Return False
  EndIf
  If Utility.GetCurrentRealTime() >= torchUntil
    torchUntil = -1.0
    Actor p = Game.GetPlayer()
    Form torch = Item(45)
    If torch && p.IsEquipped(torch)
      p.UnequipItem(torch, False, True)
      Debug.Notification("The torch has burnt out.")
    EndIf
    Return False
  EndIf
  Return True
EndFunction

Float Function Atan2(Float y, Float x)
  If x > 0.0
    Return Math.atan(y / x)
  ElseIf x < 0.0
    If y >= 0.0
      Return Math.atan(y / x) + 180.0
    EndIf
    Return Math.atan(y / x) - 180.0
  ElseIf y > 0.0
    Return 90.0
  ElseIf y < 0.0
    Return -90.0
  EndIf
  Return 0.0
EndFunction

; ---- camera shots ----

Bool shotThird = False

; A shot of Cyrus (showCyrus*: the camera somewhere looking at him): Skyrim's third-person camera shows him.
Function ShotOfCyrus()
  If !shotThird
    shotThird = True
    Game.ForceThirdPerson()
  EndIf
EndFunction

; ---- the placed camera (TamrielMergeNative 3: Skyrim's free camera put where Redguard's shot puts its camera) ----
; Redguard Preservation engine/SOUP.html (read 2026-11-11): a shot's vector is map units from the subject, used as
; given by showObj/showObjPan (the world's axes: x east, y down, z north) and turned by the subject's heading by the
; Loc forms (x to its right, z ahead of it); the camera looks at the subject at its target height; a pan goes from
; the camera's pose to the new one over its ticks. Not in the site: which side positive x is (taken as the right), and
; the height the vector starts from (taken as the target height, so y 0 is level with what is looked at).
Bool shotFree = False
Int nativeCamera = -1

Bool Function HasPlacedCamera()
  If nativeCamera < 0
    nativeCamera = 0
    If SKSE.GetPluginVersion("TamrielMergeNative") >= 3
      nativeCamera = 1
    EndIf
  EndIf
  Return nativeCamera == 1
EndFunction

; The camera at the vector from akSubject, looking at it; afSeconds: a pan's time (0: at once). False when there is no
; placed camera (the caller falls back to Skyrim's own).
Bool Function PlaceShot(ObjectReference akSubject, Float afLookHeight, Float ox, Float oy, Float oz, Bool abTurned, Float afSeconds)
  If !akSubject || flying || !HasPlacedCamera()
    Return False
  EndIf
  Float wx = ox
  Float wy = oz
  If abTurned
    Float h = akSubject.GetAngleZ()
    wx = ox * Math.cos(h) + oz * Math.sin(h)
    wy = -ox * Math.sin(h) + oz * Math.cos(h)
  EndIf
  Float flat = Math.sqrt(wx * wx + wy * wy)
  If flat < 1.0 && Math.abs(oy) < 1.0
    Return False
  EndIf
  Float tx = akSubject.GetPositionX()
  Float ty = akSubject.GetPositionY()
  Float tz = akSubject.GetPositionZ() + afLookHeight
  Float cx = tx + wx
  Float cy = ty + wy
  Float cz = tz - oy
  ; Looking from the camera at the subject: the heading clockwise from north, the pitch positive downwards.
  Float yaw = Atan2(-wx, -wy) * 0.0174533
  Float pitch = 0.0
  If flat >= 1.0
    pitch = Math.atan(-oy / flat) * 0.0174533
  ElseIf oy < 0.0
    pitch = 1.5
  Else
    pitch = -1.5
  EndIf
  If !shotFree
    If !shotThird
      shotThird = True
      Game.ForceThirdPerson()
    EndIf
    If !TMNative.FreeCamera(True)
      Return False
    EndIf
    shotFree = True
    ; The shot is Redguard's, not the player's to turn: in the free camera the mouse still looked about (seen in game,
    ; 2026-11-12, the docks shot). Looking alone is held, and given back when the shot ends.
    Game.DisablePlayerControls(False, False, False, True, False, False, False)
  EndIf
  If afSeconds > 0.05
    Float sx = TMNative.GetFreeCamera(0)
    Float sy = TMNative.GetFreeCamera(1)
    Float sz = TMNative.GetFreeCamera(2)
    Float sp = TMNative.GetFreeCamera(3)
    Float syaw = TMNative.GetFreeCamera(4)
    Float turn = yaw - syaw
    While turn > 3.14159
      turn -= 6.28319
    EndWhile
    While turn < -3.14159
      turn += 6.28319
    EndWhile
    Float began = Utility.GetCurrentRealTime()
    Float t = 0.0
    While t < 1.0 && shotFree
      t = (Utility.GetCurrentRealTime() - began) / afSeconds
      If t > 1.0
        t = 1.0
      EndIf
      TMNative.SetFreeCamera(sx + (cx - sx) * t, sy + (cy - sy) * t, sz + (cz - sz) * t, sp + (pitch - sp) * t, syaw + turn * t)
      Utility.Wait(0.01)
    EndWhile
  EndIf
  If shotFree
    TMNative.SetFreeCamera(cx, cy, cz, pitch, yaw)
  EndIf
  Return True
EndFunction

; The camera back on the player (showPlayer, XResetCamera, the end of a cutscene).
Function EndShot()
  If shotFree
    shotFree = False
    TMNative.FreeCamera(False)
    Game.EnablePlayerControls(False, False, False, True, False, False, False)
    ; Leaving the free camera restores the controls of the moment it was taken: with nothing holding the player now,
    ; they are all given back.
    If lockouts == 0 && !flying && !hangingOn
      Game.EnablePlayerControls()
    EndIf
  EndIf
  If shotThird && !flying
    shotThird = False
    If FirstPersonAfterShots && FirstPersonAfterShots.GetValueInt() == 1
      Game.ForceFirstPerson()
    EndIf
  EndIf
EndFunction

; A shot of something else (showObj*, lookObj): the player's view turned to look at it, at its target height.
Function ShotOf(ObjectReference target, Float lookHeight)
  Actor p = Game.GetPlayer()
  If !target || target == p || flying
    Return
  EndIf
  Float dx = target.GetPositionX() - p.GetPositionX()
  Float dy = target.GetPositionY() - p.GetPositionY()
  Float flat = Math.sqrt(dx * dx + dy * dy)
  If flat < 24.0
    Return
  EndIf
  ; The player's eyes are about 120 above their feet.
  Float dz = target.GetPositionZ() + lookHeight - (p.GetPositionZ() + 120.0)
  Float pitch = -Math.atan(dz / flat)
  If pitch > 60.0
    pitch = 60.0
  ElseIf pitch < -60.0
    pitch = -60.0
  EndIf
  p.SetAngle(pitch, 0.0, Atan2(dx, dy))
EndFunction

; ---- group synchronisation (SyncWithGroup) ----

Int[] syncCount
Int[] syncGen

Function SyncSetup()
  If !syncCount
    syncCount = new Int[128]
    syncGen = new Int[128]
  EndIf
EndFunction

; A member reaching a sync point: the barrier's generation it waits on, or -1 when it was the last to arrive (all go on).
Int Function SyncArrive(Int group, Int members)
  SyncSetup()
  If group < 0 || group >= 128
    Return -1
  EndIf
  syncCount[group] = syncCount[group] + 1
  If syncCount[group] >= members
    syncCount[group] = 0
    syncGen[group] = syncGen[group] + 1
    Return -1
  EndIf
  Return syncGen[group]
EndFunction

Int Function SyncGeneration(Int group)
  SyncSetup()
  If group < 0 || group >= 128
    Return 0
  EndIf
  Return syncGen[group]
EndFunction

; Releases everyone waiting (ResetGroupSync, or a member that waited too long for one that isn't running).
Function SyncReset(Int group)
  SyncSetup()
  If group >= 0 && group < 128
    syncCount[group] = 0
    syncGen[group] = syncGen[group] + 1
  EndIf
EndFunction

; ---- animation groups as idles ----

Idle Function IdleFor(Int anim, Bool cyrus)
  FormList l = Idles
  If cyrus && PlayerIdles
    l = PlayerIdles
  EndIf
  If !l || anim < 0 || anim >= l.GetSize()
    Return None
  EndIf
  Return l.GetAt(anim) as Idle
EndFunction

; ---- hand items (HandItem: what Cyrus holds for an animation) ----

Form handForm
Bool handGiven   ; the hand form was a twin given for the purpose (taken back with the empty hand)

; The twin of item i for the hand (None where it has none).
Form Function HandTwin(Int i)
  If !HandTwins || i < 0 || i >= HandTwins.GetSize()
    Return None
  EndIf
  Return HandTwins.GetAt(i) as Weapon
EndFunction

; A weapon or torch Skyrim equips (an item's hand twin given for the while); none (item 0's call, an empty hand) takes
; back what was equipped for it. Potions are Skyrim's own to show (drinking a potion plays its own sound and effect).
Function HandItem(Actor a, Form f)
  If !a
    Return
  EndIf
  If !f
    If handForm && a.IsEquipped(handForm)
      a.UnequipItem(handForm, False, True)
    EndIf
    If handForm && handGiven
      a.RemoveItem(handForm, 1, True)
    EndIf
    handForm = None
    handGiven = False
    Return
  EndIf
  If (f as Weapon) || (f as Light)
    If a.GetItemCount(f) == 0
      If !HandTwins || HandTwins.Find(f) < 0
        Return
      EndIf
      a.AddItem(f, 1, True)
      handGiven = True
    EndIf
    a.EquipItem(f, False, True)
    handForm = f
  EndIf
EndFunction

; ---- the gremlin (PushPlayer / PopPlayer) ----

Race pushedRace

; PushPlayer('gremlin'): the player goes on as the gremlin (his race swapped for GremlinRace; Redguard swaps the
; player object for the GREMLIN actor); PopPlayer gives Cyrus back.
Function PushPlayer()
  Actor p = Game.GetPlayer()
  If !GremlinRace || pushedRace
    Return
  EndIf
  pushedRace = p.GetRace()
  p.SetRace(GremlinRace)
EndFunction

Function PopPlayer()
  If !pushedRace
    Return
  EndIf
  Game.GetPlayer().SetRace(pushedRace)
  pushedRace = None
EndFunction

; ---- movies (QueueMovie: MENU.INI's movie_name[n], converted to Bink by the videos step) ----

Function PlayMovie(Int n)
  Game.PlayBink("TamrielMerge_RedguardMovie" + n + ".bik", True, True, True, True)
EndFunction

; ---- screen pictures ----

; ViewGXA: the image full page, as a note the player reads (closing it goes back to the game).
String[] Property PicturePaths Auto
{Each picture's texture as a book page names it (img://Textures/...png), by picture number.}
Int[] Property PictureSizes Auto
{Each picture's width and height, two numbers per picture.}
String Property PictureMenu = "TamrielMerge/rgpicture" AutoReadOnly

Int pictureShowing = -1
Float pictureSeconds = 0.0

; ViewGXA: the picture drawn over the game for the task's time (engine/SOUP.html), in the picture menu
; (Interface\TamrielMerge\rgpicture.swf, SKSE); without SKSE, its page opened.
Function ShowPictureFor(Int i, Float seconds)
  If SKSE.GetVersion() > 0 && PicturePaths && i >= 0 && i < PicturePaths.Length && PicturePaths[i] != "" && !UI.IsMenuOpen("CustomMenu")
    pictureShowing = i
    pictureSeconds = seconds
    RegisterForModEvent("TM_PictureReady", "OnPictureReady")
    UI.OpenCustomMenu(PictureMenu)
    Return
  EndIf
  ShowPicture(i)
EndFunction

Event OnPictureReady(String eventName, String strArg, Float numArg, Form sender)
  UnregisterForModEvent("TM_PictureReady")
  Int i = pictureShowing
  If i < 0
    UI.CloseCustomMenu()
    Return
  EndIf
  String[] args = new String[3]
  args[0] = PicturePaths[i]
  args[1] = PictureSizes[i * 2] as String
  args[2] = PictureSizes[i * 2 + 1] as String
  UI.InvokeStringA("CustomMenu", "_root.Show", args)
  ; Real time, whether or not the menu holds the game.
  Utility.WaitMenuMode(pictureSeconds)
  pictureShowing = -1
  If UI.IsMenuOpen("CustomMenu")
    UI.CloseCustomMenu()
  EndIf
EndEvent

Function ShowPicture(Int i)
  If !Pictures || i < 0 || i >= Pictures.GetSize()
    Return
  EndIf
  Form f = Pictures.GetAt(i)
  If !f
    Return
  EndIf
  Actor p = Game.GetPlayer()
  ObjectReference page = p.PlaceAtMe(f, 1, False, True)
  page.Enable()
  page.Activate(p)
  Utility.Wait(0.2)
  page.Disable()
  page.Delete()
EndFunction

; ShowBitmap/UnShowBitmap: COMMON's status icons while the strength (0) and ironskin (1) potions last, as a line when
; they come and go.
Bool[] statusShown

Function Status(Int i, Bool on)
  If !statusShown
    statusShown = new Bool[8]
  EndIf
  If i < 0 || i >= 8 || statusShown[i] == on
    Return
  EndIf
  statusShown[i] = on
  String what = "Strength potion"
  If i == 1
    what = "Ironskin potion"
  EndIf
  If on
    Debug.Notification(what + ": in effect")
  Else
    Debug.Notification(what + ": worn off")
  EndIf
EndFunction

; ---- the player flown along a script's path (the canah's flight between the shrines) ----

Bool flying = False
Float flyX
Float flyY
Float flyZ
Float flyUntil

Bool Function Flying()
  Return flying
EndFunction

; Redguard set Cyrus's position far away while he was held (a flight): Skyrim's player is carried there along a curve
; over the time the script gives it (a next leg redirects it).
Function FlyTo(Float x, Float y, Float z, Float seconds)
  Actor p = Game.GetPlayer()
  Float dx = x - p.GetPositionX()
  Float dy = y - p.GetPositionY()
  Float dz = z - p.GetPositionZ()
  Float d = Math.sqrt(dx * dx + dy * dy + dz * dz)
  If d < 1.0
    Return
  EndIf
  If seconds < 0.5
    seconds = 0.5
  EndIf
  Float speed = d / seconds
  If speed < 400.0
    speed = 400.0
  EndIf
  If !flying
    Game.DisablePlayerControls(True, True, False, False, True, True, True)
    If !shotThird
      shotThird = True
      Game.ForceThirdPerson()
    EndIf
  EndIf
  flying = True
  flyX = x
  flyY = y
  flyZ = z
  flyUntil = Utility.GetCurrentRealTime() + d / speed + 0.5
  p.SplineTranslateTo(x, y, z, 0.0, 0.0, Atan2(dx, dy), d * 0.3, speed, 0.0)
  RegisterForSingleUpdate(d / speed + 0.3)
EndFunction

; One update serves the flights (0.3 s while one runs) and the torch's burn-out (every second while it is lit).
Event OnUpdate()
  Float again = -1.0
  If flying
    If Utility.GetCurrentRealTime() < flyUntil
      again = 0.3
    Else
      Actor p = Game.GetPlayer()
      p.StopTranslation()
      p.SetPosition(flyX, flyY, flyZ)
      flying = False
      If lockouts == 0 && !hangingOn
        EndShot()
        Game.EnablePlayerControls()
      EndIf
    EndIf
  EndIf
  If TorchTick() && (again < 0.0 || again > 1.0)
    again = 1.0
  EndIf
  If again > 0.0
    RegisterForSingleUpdate(again)
  EndIf
EndEvent

; ---- hanging from objects (PlayerHanging; TMRgMove holds the player there) ----

ObjectReference hangingOn

ObjectReference Function HangingOn()
  Return hangingOn
EndFunction

Function SetHanging(ObjectReference r)
  hangingOn = r
EndFunction
