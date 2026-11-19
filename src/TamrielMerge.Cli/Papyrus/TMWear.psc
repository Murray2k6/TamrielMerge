ScriptName TMWear extends Quest
{Item durability and repair, one system for the games that have it (TamrielMerge, Skyrim/ItemWear.cs; TMWearMw and
TMWearOb extend this with each game's rules; a quest of each plugin: MWWear, OBWear). Skyrim has no item condition, so it
is kept here for the player's own use of that game's weapons and armour.

Per base item, not per copy: Papyrus sees what is worn as a base form, so two iron swords share one condition.

The maximum of each item is in form lists, not arrays: Papyrus arrays stop at 128 and the games have thousands of
weapons and armour pieces. MaxBits[k] holds every item whose maximum has bit k set, so a lookup is a HasForm per bit with
no limit on the items, and All holds every item (a form not in it is left alone: Skyrim's own, another game's).

The condition of what is damaged lives in four pages of 128 (512 items). A full store first forgets an item the player no
longer carries. Repair tools' uses left are kept there too, with a negative maximum.

What a condition does: Skyrim can't scale one item, so the hidden perk WearPerk multiplies Mod Armor Rating and Mod
Attack Damage for the items in a band list. Bands[b] holds the items at (b / 20) of their maximum, rounded up to a 5%
step, 0 to 125%, with no list at 100%. Lists changed by AddForm stay in the save, so this needs no work after a load, and
each hand and each worn piece has its own multiplier. The multipliers per band are each game's and set by the builder.

Events: the player alias TMWearPlayer (struck, what is equipped, loads), the weapon's hit from the progression's combat
hit spell (TMSkillHitEffect sends the mod event TMPlayerHit, because only one combat hit spell lands per hit, so a
second perk of ours would take the skill uses' place), and the swing animation events for the hand and Morrowind's cost of
a swing. Needs SKSE (GetWornForm, GetArmorRating, GetBaseDamage, mod events).}

Actor Property PlayerRef Auto
Perk Property WearPerk Auto
FormList Property All Auto
{Every item with a condition in this game.}
FormList[] Property MaxBits Auto
{MaxBits[k]: the items whose maximum condition has bit k set.}
FormList[] Property Bands Auto
{By band b (the condition in twentieths of the maximum): b below 20 at b, b above 20 at b - 1.}
Message Property Menu Auto
{Right hand, Left hand, Head, Body, Legs, Feet, Hands, Shield, then More (Morrowind) or Repair all (Oblivion), Cancel.}
Message Property MenuMore Auto
{Morrowind's other three slots: Left pauldron, Right pauldron, Left gauntlet, Back, Cancel. None in Oblivion.}
GlobalVariable[] Property Shown Auto
{1 shows a button (the messages' conditions): 0-10 by slot, 11 More or Repair all.}
Form Property Gold Auto
Keyword Property ActorNpc Auto
{ActorTypeNPC.}
Sound Property RepairSound Auto
Sound Property FailSound Auto
String Property BrokenEquip Auto
{Refused equipping a broken item.}
String Property NothingToRepair Auto
String Property NotEnoughGold Auto

Int Property SlotCount = 11 AutoReadOnly
Int Property PageSize = 128 AutoReadOnly
Int Property StoreSize = 512 AutoReadOnly
Int Property FullBand = 20 AutoReadOnly
Int Property TopBand = 25 AutoReadOnly

Form[] k0
Form[] k1
Form[] k2
Form[] k3
Float[] v0
Float[] v1
Float[] v2
Float[] v3
Float[] m0
Float[] m1
Float[] m2
Float[] m3

Form[] Listed
Int[] Prices
String listText = ""
Int lastHand = 1
Bool busy = False
Bool warned = False

Event OnInit()
  Setup()
EndEvent

; After a load too (TMWearPlayer): animation and mod events don't survive one.
Function Setup()
  If !k0
    k0 = new Form[128]
    k1 = new Form[128]
    k2 = new Form[128]
    k3 = new Form[128]
    v0 = new Float[128]
    v1 = new Float[128]
    v2 = new Float[128]
    v3 = new Float[128]
    m0 = new Float[128]
    m1 = new Float[128]
    m2 = new Float[128]
    m3 = new Float[128]
  EndIf
  If !Listed
    Listed = new Form[11]
    Prices = new Int[11]
  EndIf
  If WearPerk && !PlayerRef.HasPerk(WearPerk)
    PlayerRef.AddPerk(WearPerk)
  EndIf
  busy = False
  RegisterForModEvent("TMPlayerHit", "OnTMPlayerHit")
  RegisterForAnimationEvent(PlayerRef, "weaponSwing")
  RegisterForAnimationEvent(PlayerRef, "weaponLeftSwing")
  RegisterForAnimationEvent(PlayerRef, "arrowRelease")
  If !warned && (SKSE.GetVersion() <= 0 || !Quest.GetQuest("TMProgression"))
    warned = True
    Debug.Notification("Item condition needs SKSE and TamrielMerge Progression.esp.")
  EndIf
EndFunction

; ---- the store ----

Form Function KeyAt(Int i)
  Int n = i % PageSize
  Int p = i / PageSize
  If p == 0
    Return k0[n]
  ElseIf p == 1
    Return k1[n]
  ElseIf p == 2
    Return k2[n]
  EndIf
  Return k3[n]
EndFunction

Float Function ValueAt(Int i)
  Int n = i % PageSize
  Int p = i / PageSize
  If p == 0
    Return v0[n]
  ElseIf p == 1
    Return v1[n]
  ElseIf p == 2
    Return v2[n]
  EndIf
  Return v3[n]
EndFunction

Float Function MaxAt(Int i)
  Int n = i % PageSize
  Int p = i / PageSize
  If p == 0
    Return m0[n]
  ElseIf p == 1
    Return m1[n]
  ElseIf p == 2
    Return m2[n]
  EndIf
  Return m3[n]
EndFunction

Function PutAt(Int i, Form akItem, Float afValue, Float afMax)
  Int n = i % PageSize
  Int p = i / PageSize
  If p == 0
    k0[n] = akItem
    v0[n] = afValue
    m0[n] = afMax
  ElseIf p == 1
    k1[n] = akItem
    v1[n] = afValue
    m1[n] = afMax
  ElseIf p == 2
    k2[n] = akItem
    v2[n] = afValue
    m2[n] = afMax
  Else
    k3[n] = akItem
    v3[n] = afValue
    m3[n] = afMax
  EndIf
EndFunction

Int Function IndexOf(Form akItem)
  Int i = k0.Find(akItem)
  If i >= 0
    Return i
  EndIf
  i = k1.Find(akItem)
  If i >= 0
    Return PageSize + i
  EndIf
  i = k2.Find(akItem)
  If i >= 0
    Return 2 * PageSize + i
  EndIf
  i = k3.Find(akItem)
  If i >= 0
    Return 3 * PageSize + i
  EndIf
  Return -1
EndFunction

; A free place, else that of an item the player no longer carries (forgotten, its band list cleared), -1 with none.
Int Function FreeIndex()
  Int i = IndexOf(None)
  If i >= 0
    Return i
  EndIf
  i = 0
  While i < StoreSize
    Form f = KeyAt(i)
    If PlayerRef.GetItemCount(f) == 0
      If MaxAt(i) > 0.0
        FormList was = BandList(BandOf(ValueAt(i), MaxAt(i)))
        If was
          was.RemoveAddedForm(f)
        EndIf
      EndIf
      PutAt(i, None, 0.0, 0.0)
      Return i
    EndIf
    i += 1
  EndWhile
  Return -1
EndFunction

; ---- condition ----

; The item's maximum condition, 0 for an item without one here.
Float Function MaxOf(Form akItem)
  If !akItem
    Return 0.0
  EndIf
  Int i = IndexOf(akItem)
  If i >= 0
    Return MaxAt(i)
  EndIf
  If !All.HasForm(akItem)
    Return 0.0
  EndIf
  Int total = 0
  Int bitValue = 1
  Int k = 0
  While k < MaxBits.Length
    If MaxBits[k].HasForm(akItem)
      total += bitValue
    EndIf
    bitValue *= 2
    k += 1
  EndWhile
  Return total as Float
EndFunction

; The item's condition: its stored one, or whole.
Float Function ConditionOf(Form akItem, Float afMax)
  Int i = IndexOf(akItem)
  If i >= 0
    Return ValueAt(i)
  EndIf
  Return afMax
EndFunction

Int Function BandOf(Float afCondition, Float afMax)
  If afMax <= 0.0
    Return FullBand
  EndIf
  Int b = Math.Ceiling(afCondition / afMax * 20.0)
  If b < 0
    Return 0
  ElseIf b > TopBand
    Return TopBand
  EndIf
  Return b
EndFunction

FormList Function BandList(Int aiBand)
  If aiBand == FullBand
    Return None
  ElseIf aiBand > FullBand
    Return Bands[aiBand - 1]
  EndIf
  Return Bands[aiBand]
EndFunction

; The item's condition set, and its band list changed when the 5% step has. Exactly whole leaves the store.
Function SetCondition(Form akItem, Float afCondition, Float afMax)
  Int i = IndexOf(akItem)
  Int was = FullBand
  If i >= 0
    was = BandOf(ValueAt(i), afMax)
  EndIf
  If afCondition == afMax
    If i >= 0
      PutAt(i, None, 0.0, 0.0)
    EndIf
  Else
    If i < 0
      i = FreeIndex()
    EndIf
    If i < 0
      Return
    EndIf
    PutAt(i, akItem, afCondition, afMax)
  EndIf
  Int now = BandOf(afCondition, afMax)
  If now != was
    FormList oldList = BandList(was)
    If oldList
      oldList.RemoveAddedForm(akItem)
    EndIf
    FormList newList = BandList(now)
    If newList
      newList.AddForm(akItem)
    EndIf
  EndIf
  ConditionChanged()
EndFunction

; Loses condition; at 0 it breaks.
Function WearDown(Form akItem, Float afLoss)
  Float top = MaxOf(akItem)
  If top <= 0.0 || afLoss <= 0.0
    Return
  EndIf
  Float cond = ConditionOf(akItem, top)
  If cond <= 0.0
    Return
  EndIf
  cond -= afLoss
  If cond < 0.0
    cond = 0.0
  EndIf
  SetCondition(akItem, cond, top)
  If cond <= 0.0
    Broke(akItem)
  EndIf
EndFunction

; A tool's uses left (full when unused), kept in the store with a negative maximum.
Int Function ToolUses(Form akTool, Int aiFull)
  Int i = IndexOf(akTool)
  If i >= 0
    Return ValueAt(i) as Int
  EndIf
  Return aiFull
EndFunction

Function SetToolUses(Form akTool, Int aiUses)
  Int i = IndexOf(akTool)
  If i < 0
    i = FreeIndex()
  EndIf
  If i >= 0
    PutAt(i, akTool, aiUses as Float, -1.0)
  EndIf
EndFunction

Function ClearTool(Form akTool)
  Int i = IndexOf(akTool)
  If i >= 0
    PutAt(i, None, 0.0, 0.0)
  EndIf
EndFunction

; ---- what is worn ----

; Slots: 0 right hand, 1 left hand, 2 head, 3 body, 4 legs, 5 feet, 6 left pauldron, 7 right pauldron, 8 left gauntlet,
; 9 right gauntlet or hands, 10 shield. Biped masks: head 30 (1), hair 31 (2), circlet 42 (4096), body 32 (4), hands 33
; (8), forearms 34 (16), feet 37 (128), shield 39 (512), and the unused ones the converters give what Skyrim has no slot
; for: greaves 53, pauldrons 57 and 58, left gauntlets and bracers 60 (MwWorn, ObWorn).
Form Function WornAt(Int aiSlot)
  If aiSlot == 0
    Return PlayerRef.GetEquippedObject(1) as Weapon
  ElseIf aiSlot == 1
    Return PlayerRef.GetEquippedObject(0) as Weapon
  ElseIf aiSlot == 2
    Form head = PlayerRef.GetWornForm(1)
    If !head
      head = PlayerRef.GetWornForm(2)
    EndIf
    If !head
      head = PlayerRef.GetWornForm(4096)
    EndIf
    Return head
  ElseIf aiSlot == 3
    Return PlayerRef.GetWornForm(4)
  ElseIf aiSlot == 4
    Return PlayerRef.GetWornForm(8388608)
  ElseIf aiSlot == 5
    Return PlayerRef.GetWornForm(128)
  ElseIf aiSlot == 6
    Return PlayerRef.GetWornForm(134217728)
  ElseIf aiSlot == 7
    Return PlayerRef.GetWornForm(268435456)
  ElseIf aiSlot == 8
    Return PlayerRef.GetWornForm(1073741824)
  ElseIf aiSlot == 9
    Form hands = PlayerRef.GetWornForm(8)
    If !hands
      hands = PlayerRef.GetWornForm(16)
    EndIf
    Return hands
  ElseIf aiSlot == 10
    Return PlayerRef.GetWornForm(512)
  EndIf
  Return None
EndFunction

; The slot a carried item would be listed under (a broken one is unequipped, and must still reach the menu).
Int Function SlotOf(Form akItem)
  If akItem as Weapon
    Return 0
  EndIf
  Armor piece = akItem as Armor
  If !piece
    Return 0
  EndIf
  Int mask = piece.GetSlotMask()
  If Math.LogicalAnd(mask, 8388608) != 0
    Return 4
  ElseIf Math.LogicalAnd(mask, 134217728) != 0
    Return 6
  ElseIf Math.LogicalAnd(mask, 268435456) != 0
    Return 7
  ElseIf Math.LogicalAnd(mask, 1073741824) != 0
    Return 8
  ElseIf Math.LogicalAnd(mask, 4) != 0
    Return 3
  ElseIf Math.LogicalAnd(mask, 4099) != 0
    Return 2
  ElseIf Math.LogicalAnd(mask, 24) != 0
    Return 9
  ElseIf Math.LogicalAnd(mask, 128) != 0
    Return 5
  EndIf
  Return 10
EndFunction

; ---- events ----

Event OnAnimationEvent(ObjectReference akSource, String asEventName)
  If asEventName == "weaponLeftSwing"
    lastHand = 0
  Else
    lastHand = 1
  EndIf
  Weapon w = HandWeapon()
  If w && All.HasForm(w)
    Swung(w)
  EndIf
EndEvent

; The player's hit landed (TMSkillHitEffect): the weapon of the hand that last swung.
Event OnTMPlayerHit(String eventName, String strArg, Float numArg, Form sender)
  Weapon w = HandWeapon()
  If w && All.HasForm(w)
    WeaponHit(w)
  EndIf
EndEvent

Weapon Function HandWeapon()
  Weapon w = PlayerRef.GetEquippedWeapon(lastHand == 0)
  If !w
    w = PlayerRef.GetEquippedWeapon(lastHand != 0)
  EndIf
  Return w
EndFunction

; The player was struck (TMWearPlayer.OnHit): by a weapon or a creature's blow, not by the hit's enchantment or a spell.
Function Struck(ObjectReference akAggressor, Form akSource, Bool abBlocked)
  Actor attacker = akAggressor as Actor
  If !attacker || attacker == PlayerRef
    Return
  EndIf
  If akSource && !(akSource as Weapon)
    Return
  EndIf
  ArmorStruck(attacker, RealWeapon(akSource), abBlocked)
EndFunction

; The form Skyrim gives fists and creatures' blows (Unarmed, 0x1F4) is no weapon.
Weapon Function RealWeapon(Form akSource)
  Weapon w = akSource as Weapon
  If w && w.GetFormID() == 500
    Return None
  EndIf
  Return w
EndFunction

; Equipped (TMWearPlayer): a broken item that can't be used comes off again.
Function Equipped(Form akItem)
  ConditionChanged()
  If !All.HasForm(akItem)
    Return
  EndIf
  Int i = IndexOf(akItem)
  If i >= 0 && MaxAt(i) > 0.0 && ValueAt(i) <= 0.0 && BrokenCantBeUsed(akItem)
    PlayerRef.UnequipItem(akItem, False, True)
    Debug.Notification(BrokenEquip)
  EndIf
EndFunction

; ---- the menus ----

; Fills Listed: each slot's worn piece that can be repaired, then broken items carried (they have come off) under their
; own slot, one a slot: the next shows when that one is mended. Returns how many, and the text naming them.
Int Function RefreshList(Actor akService)
  Int count = 0
  listText = ""
  Int s = 0
  While s < SlotCount
    Listed[s] = None
    Prices[s] = 0
    s += 1
  EndWhile
  s = 0
  While s < SlotCount
    Form f = WornAt(s)
    If f && Listed.Find(f) < 0
      Float top = MaxOf(f)
      If top > 0.0
        count += ListOne(s, f, top, akService)
      EndIf
    EndIf
    s += 1
  EndWhile
  Int i = 0
  While i < StoreSize
    If MaxAt(i) > 0.0 && ValueAt(i) <= 0.0
      Form item = KeyAt(i)
      If item && Listed.Find(item) < 0 && PlayerRef.GetItemCount(item) > 0
        s = SlotOf(item)
        If !Listed[s]
          count += ListOne(s, item, MaxAt(i), akService)
        EndIf
      EndIf
    EndIf
    i += 1
  EndWhile
  s = 0
  While s < SlotCount
    If Shown && s < Shown.Length && Shown[s]
      If Listed[s]
        Shown[s].SetValueInt(1)
      Else
        Shown[s].SetValueInt(0)
      EndIf
    EndIf
    s += 1
  EndWhile
  Return count
EndFunction

Int Function ListOne(Int aiSlot, Form akItem, Float afMax, Actor akService)
  Float cond = ConditionOf(akItem, afMax)
  If !CanMend(akItem, cond, afMax, akService)
    Return 0
  EndIf
  Listed[aiSlot] = akItem
  String line = akItem.GetName() + ": " + (cond as Int) + " / " + (afMax as Int)
  If akService
    Prices[aiSlot] = Price(akItem, cond, afMax, akService)
    line += " - " + Prices[aiSlot] + " gold"
  EndIf
  listText += line + "\n"
  Return 1
EndFunction

; The player's choice: a slot, -1 Cancel, -2 Repair all. Skyrim's message buttons are fixed text, so a button names the
; slot; the box before it names the items. Message.Show gives the button's place among all the buttons, shown or not.
Int Function Choose(Bool abRepairAll)
  Bool more = abRepairAll
  If MenuMore
    more = Listed[6] || Listed[7] || Listed[8]
  EndIf
  If Shown.Length > 11 && Shown[11]
    If more
      Shown[11].SetValueInt(1)
    Else
      Shown[11].SetValueInt(0)
    EndIf
  EndIf
  While True
    Int c = Menu.Show()
    If c >= 0 && c <= 5
      Return c
    ElseIf c == 6
      Return 9
    ElseIf c == 7
      Return 10
    ElseIf c == 8
      If !MenuMore
        Return -2
      EndIf
      Int d = MenuMore.Show()
      If d >= 0 && d <= 2
        Return 6 + d
      ElseIf d != 3
        Return -1
      EndIf
    Else
      Return -1
    EndIf
  EndWhile
  Return -1
EndFunction

; A repair tool used from the inventory (TMWearTool): one attempt a choice, until the tool, the damage or the player's
; patience runs out.
Function UseTool(Form akTool, Float afQuality, Int aiUses)
  If busy || !CanUseTool()
    Return
  EndIf
  busy = True
  String said = ""
  Bool going = True
  While going
    Int count = RefreshList(None)
    If count == 0
      Debug.MessageBox(said + ToolNothingText())
      going = False
    Else
      Debug.MessageBox(said + listText)
      Int choice = Choose(False)
      If choice < 0 || !Listed[choice]
        going = False
      Else
        said = Attempt(akTool, afQuality, aiUses, Listed[choice]) + "\n\n"
        If PlayerRef.GetItemCount(akTool) <= 0
          Debug.MessageBox(said)
          going = False
        EndIf
      EndIf
    EndIf
  EndWhile
  busy = False
EndFunction

; The Repair topic (TMWearService): the person mends what the player picks, for gold, to whole.
Function Service(Actor akPerson)
  If busy || !akPerson
    Return
  EndIf
  busy = True
  Bool going = True
  While going
    Int count = RefreshList(akPerson)
    If count == 0
      Debug.MessageBox(NothingToRepair)
      going = False
    Else
      Debug.MessageBox(listText)
      Int choice = Choose(HasRepairAll())
      If choice == -1 || (choice >= 0 && !Listed[choice])
        going = False
      ElseIf choice == -2
        Int total = 0
        Int s = 0
        While s < SlotCount
          total += Prices[s]
          s += 1
        EndWhile
        If PlayerRef.GetItemCount(Gold) < total
          Debug.MessageBox(NotEnoughGold)
          going = False
        Else
          s = 0
          While s < SlotCount
            If Listed[s]
              MendWhole(Listed[s])
            EndIf
            s += 1
          EndWhile
          Pay(akPerson, total)
        EndIf
      ElseIf PlayerRef.GetItemCount(Gold) < Prices[choice]
        Debug.MessageBox(NotEnoughGold)
        going = False
      Else
        Int price = Prices[choice]
        MendWhole(Listed[choice])
        Pay(akPerson, price)
      EndIf
    EndIf
  EndWhile
  busy = False
EndFunction

Function MendWhole(Form akItem)
  Float top = MaxOf(akItem)
  If top > 0.0
    SetCondition(akItem, top, top)
  EndIf
EndFunction

Function Pay(Actor akPerson, Int aiPrice)
  PlayerRef.RemoveItem(Gold, aiPrice, True)
  PlayRepair(True)
  PaidTo(akPerson, aiPrice)
EndFunction

Function PlayRepair(Bool abSuccess)
  If abSuccess && RepairSound
    RepairSound.Play(PlayerRef)
  ElseIf !abSuccess && FailSound
    FailSound.Play(PlayerRef)
  EndIf
EndFunction

; ---- each game's rules (TMWearMw, TMWearOb) ----

Function Swung(Weapon akWeapon)
EndFunction

Function WeaponHit(Weapon akWeapon)
EndFunction

Function ArmorStruck(Actor akAttacker, Weapon akWeapon, Bool abBlocked)
EndFunction

Function Broke(Form akItem)
EndFunction

Bool Function BrokenCantBeUsed(Form akItem)
  Return True
EndFunction

Function ConditionChanged()
EndFunction

Bool Function CanUseTool()
  Return True
EndFunction

Bool Function CanMend(Form akItem, Float afCondition, Float afMax, Actor akService)
  Return afCondition < afMax
EndFunction

String Function ToolNothingText()
  Return NothingToRepair
EndFunction

String Function Attempt(Form akTool, Float afQuality, Int aiUses, Form akItem)
  Return ""
EndFunction

Int Function Price(Form akItem, Float afCondition, Float afMax, Actor akService)
  Return 1
EndFunction

Bool Function HasRepairAll()
  Return False
EndFunction

Function PaidTo(Actor akPerson, Int aiPrice)
EndFunction
