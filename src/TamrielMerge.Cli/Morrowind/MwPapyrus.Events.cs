using System.Text;

namespace TamrielMerge.Morrowind;

/// <summary>The events a translated script runs on, and the per-script helper functions it uses.</summary>
public sealed partial class MwPapyrus
{
    void WriteEvents(StringBuilder sb, bool polls, float interval)
    {
        string iv = F(interval);
        bool sleep = uses.Contains("pcsleep");
        if (kind == Kind.Global)
        {
            sb.AppendLine("Event OnInit()");
            if (sleep) sb.AppendLine("  RegisterForSleep()");
            sb.AppendLine("  RegisterForSingleUpdate(0.5)");
            sb.AppendLine("EndEvent");
            sb.AppendLine();
            sb.AppendLine("; StartScript: runs the script (on akSelf, when the call names a reference).");
            sb.AppendLine("Function MwStartScript(ObjectReference akSelf)");
            sb.AppendLine("  If akSelf");
            sb.AppendLine("    MwSelf = akSelf");
            sb.AppendLine("  EndIf");
            sb.AppendLine("  If !IsRunning()");
            sb.AppendLine("    Start()");
            sb.AppendLine("  EndIf");
            if (sleep) sb.AppendLine("  RegisterForSleep()");
            sb.AppendLine("  RegisterForSingleUpdate(0.05)");
            sb.AppendLine("EndFunction");
            sb.AppendLine();
            sb.AppendLine("Event OnUpdate()");
            sb.AppendLine("  If !IsRunning()");
            sb.AppendLine("    Return");
            sb.AppendLine("  EndIf");
            // Morrowind's story begins when the player first comes to its lands (its start scripts wait for that), and
            // its global scripts pause while the player is in another story's (no Dark Brotherhood ambush in Daggerfall).
            sb.AppendLine("  If !TMMwScript.InMorrowind()");
            sb.AppendLine("    RegisterForSingleUpdate(5.0)");
            sb.AppendLine("    Return");
            sb.AppendLine("  EndIf");
            sb.AppendLine($"  RegisterForSingleUpdate({iv})");
            sb.AppendLine("  MwTick()");
            sb.AppendLine("EndEvent");
            sb.AppendLine();
            if (sleep) WriteSleepEvents(sb, global: true);
            return;
        }
        if (kind != Kind.Object) return;
        bool activate = uses.Contains("onactivate");
        var mirrored = locals.Keys.Where(v => world.IsDialogueLocal(v) && ctx.OnActor).ToList();
        bool startPos = uses.Contains("startpos");
        bool items = ctx.IsItem;
        if (activate || polls || mirrored.Count > 0 || startPos || sleep)
        {
            sb.AppendLine("Event OnInit()");
            sb.AppendLine("  MwInit()");
            sb.AppendLine("EndEvent");
            sb.AppendLine();
            sb.AppendLine("Event OnLoad()");
            sb.AppendLine("  MwInit()");
            sb.AppendLine("EndEvent");
            sb.AppendLine();
            sb.AppendLine("Function MwInit()");
            if (activate)
            {
                // Morrowind's OnActivate holds the default activation back (Activate performs it); the state is kept
                // across loads.
                sb.AppendLine("  If !__blockSet");
                sb.AppendLine("    __blockSet = True");
                sb.AppendLine("    __blocking = True");
                sb.AppendLine("  EndIf");
                sb.AppendLine("  TMMwScript.Block(Self, __blocking)");
            }
            foreach (var v in mirrored)
            {
                string f = properties.Values.First(p => p.Fill.Equals("local:" + v, StringComparison.OrdinalIgnoreCase)).Name;
                sb.AppendLine($"  TMMwScript.MirrorLocal(Self, {f}, {Var(v)}{(locals[v] == T.Float ? " as Int" : "")})");
            }
            if (startPos)
            {
                sb.AppendLine("  If !__startSet");
                sb.AppendLine("    __startSet = True");
                sb.AppendLine("    __sx = GetPositionX()");
                sb.AppendLine("    __sy = GetPositionY()");
                sb.AppendLine("    __sz = GetPositionZ()");
                sb.AppendLine("    __sax = GetAngleX()");
                sb.AppendLine("    __say = GetAngleY()");
                sb.AppendLine("    __saz = GetAngleZ()");
                sb.AppendLine("  EndIf");
            }
            if (sleep) sb.AppendLine("  RegisterForSleep()");
            if (polls) sb.AppendLine("  RegisterForSingleUpdate(0.1)");
            sb.AppendLine("EndFunction");
            sb.AppendLine();
        }
        if (polls)
        {
            sb.AppendLine("Event OnUnload()");
            if (items)
            {
                sb.AppendLine("  If !__carried");
                sb.AppendLine("    UnregisterForUpdate()");
                sb.AppendLine("  EndIf");
            }
            else sb.AppendLine("  UnregisterForUpdate()");
            sb.AppendLine("EndEvent");
            sb.AppendLine();
            sb.AppendLine("Event OnUpdate()");
            sb.AppendLine(items ? "  If Is3DLoaded() || __carried" : "  If Is3DLoaded()");
            sb.AppendLine($"    RegisterForSingleUpdate({iv})");
            sb.AppendLine("    MwTick()");
            sb.AppendLine("  EndIf");
            sb.AppendLine("EndEvent");
            sb.AppendLine();
        }
        if (activate && !polls)
        {
            // After Activate, a run of the script to see whether it holds the activation back again.
            sb.AppendLine("Event OnUpdate()");
            sb.AppendLine("  MwTick()");
            sb.AppendLine("EndEvent");
            sb.AppendLine();
        }
        // Morrowind opens a companion creature's inventory when the player activates it while its companion local is 1
        // (people offer Companion Share in their dialogue, TamrielMerge's topic).
        bool share = ctx.OnCreature && locals.ContainsKey("companion");
        if (activate || share)
        {
            sb.AppendLine("Event OnActivate(ObjectReference akActionRef)");
            if (share)
            {
                sb.AppendLine($"  If akActionRef == PlayerRef && {Var("companion")} == 1{(activate ? " && !__blocking" : "")}");
                sb.AppendLine("    ((Self as ObjectReference) as Actor).OpenInventory(True)");
                sb.AppendLine("  EndIf");
            }
            if (activate)
            {
                sb.AppendLine("  __activated = True");
                sb.AppendLine("  __actionRef = akActionRef");
                sb.AppendLine("  MwTick()");
            }
            sb.AppendLine("EndEvent");
            sb.AppendLine();
        }
        // minimumProfit: Morrowind's companion share counts what the player takes from a hired companion (minus) and
        // gives them (plus); the companion's script ends the contract when it falls below 0.
        if (ctx.OnActor && locals.ContainsKey("minimumProfit"))
        {
            string profit = Var("minimumProfit");
            string warning = Escape(world.Setting("sCompanionWarningMessage") ?? "");
            sb.AppendLine("Event OnItemAdded(Form akBaseItem, Int aiItemCount, ObjectReference akItemReference, ObjectReference akSourceContainer)");
            sb.AppendLine("  If akSourceContainer == PlayerRef && UI.IsMenuOpen(\"ContainerMenu\")");
            sb.AppendLine($"    {profit} = {profit} + akBaseItem.GetGoldValue() * aiItemCount");
            sb.AppendLine("  EndIf");
            sb.AppendLine("EndEvent");
            sb.AppendLine();
            sb.AppendLine("Event OnItemRemoved(Form akBaseItem, Int aiItemCount, ObjectReference akItemReference, ObjectReference akDestContainer)");
            sb.AppendLine("  If akDestContainer == PlayerRef && UI.IsMenuOpen(\"ContainerMenu\")");
            sb.AppendLine($"    Bool was = {profit} >= 0");
            sb.AppendLine($"    {profit} = {profit} - akBaseItem.GetGoldValue() * aiItemCount");
            if (warning.Length > 0)
            {
                sb.AppendLine($"    If was && {profit} < 0");
                sb.AppendLine($"      Debug.Notification(\"{warning}\")");
                sb.AppendLine("    EndIf");
            }
            sb.AppendLine("  EndIf");
            sb.AppendLine("EndEvent");
            sb.AppendLine();
        }
        if (ctx.OnActor && (uses.Contains("ondeath") || uses.Contains("onmurder")))
        {
            sb.AppendLine("Event OnDeath(Actor akKiller)");
            if (uses.Contains("ondeath")) sb.AppendLine("  __died = True");
            if (uses.Contains("onmurder")) sb.AppendLine("  __murdered = akKiller == PlayerRef");
            sb.AppendLine("  MwTick()");
            sb.AppendLine("EndEvent");
            sb.AppendLine();
        }
        if (ctx.OnActor && uses.Contains("onknockout"))
        {
            sb.AppendLine("Event OnEnterBleedout()");
            sb.AppendLine("  __knockedOut = True");
            sb.AppendLine("  MwTick()");
            sb.AppendLine("EndEvent");
            sb.AppendLine();
        }
        bool equipLocal = locals.ContainsKey("OnPCEquip"), skipLocal = locals.ContainsKey("PCSkipEquip");
        bool addLocal = locals.ContainsKey("OnPCAdd"), dropLocal = locals.ContainsKey("OnPCDrop");
        if (items || uses.Contains("onpcadd") || uses.Contains("onpcdrop") || addLocal || dropLocal)
        {
            sb.AppendLine("Event OnContainerChanged(ObjectReference akNewContainer, ObjectReference akOldContainer)");
            sb.AppendLine("  __carried = akNewContainer == PlayerRef");
            if (uses.Contains("onpcadd"))
            {
                sb.AppendLine("  If __carried");
                sb.AppendLine("    __pcAdded = True");
                sb.AppendLine("  EndIf");
            }
            if (uses.Contains("onpcdrop"))
            {
                sb.AppendLine("  If akOldContainer == PlayerRef && !akNewContainer");
                sb.AppendLine("    __pcDropped = True");
                sb.AppendLine("  EndIf");
            }
            // The special locals of those names: set by the game, reset by the script.
            if (addLocal)
            {
                sb.AppendLine("  If __carried");
                sb.AppendLine($"    {Var("OnPCAdd")} = 1");
                sb.AppendLine("  EndIf");
            }
            if (dropLocal)
            {
                sb.AppendLine("  If akOldContainer == PlayerRef && !akNewContainer");
                sb.AppendLine($"    {Var("OnPCDrop")} = 1");
                sb.AppendLine("  EndIf");
            }
            if (polls)
            {
                sb.AppendLine("  If __carried");
                sb.AppendLine("    RegisterForSingleUpdate(0.1)");
                sb.AppendLine("  EndIf");
            }
            if (uses.Contains("onpcadd") || uses.Contains("onpcdrop") || addLocal || dropLocal) sb.AppendLine("  MwTick()");
            sb.AppendLine("EndEvent");
            sb.AppendLine();
        }
        if (uses.Contains("onpcequip") || equipLocal || skipLocal)
        {
            sb.AppendLine("Event OnEquipped(Actor akActor)");
            sb.AppendLine("  If akActor != PlayerRef");
            sb.AppendLine("    Return");
            sb.AppendLine("  EndIf");
            if (skipLocal)
            {
                // PCSkipEquip: the item does its thing instead of being worn (books that open a menu...).
                sb.AppendLine($"  If {Var("PCSkipEquip")} != 0");
                sb.AppendLine("    akActor.UnequipItem(GetBaseObject(), False, True)");
                sb.AppendLine("  EndIf");
            }
            if (equipLocal) sb.AppendLine($"  {Var("OnPCEquip")} = 1");
            if (uses.Contains("onpcequip")) sb.AppendLine("  __pcEquipped = True");
            sb.AppendLine("  MwTick()");
            sb.AppendLine("EndEvent");
            sb.AppendLine();
        }
        if (uses.Contains("onpchitme") || uses.Contains("hitonme"))
        {
            sb.AppendLine("Event OnHit(ObjectReference akAggressor, Form akSource, Projectile akProjectile, Bool abPowerAttack, Bool abSneakAttack, Bool abBashAttack, Bool abHitBlocked)");
            if (uses.Contains("hitonme"))
            {
                // HitOnMe weapon: the weapon (or spell) that hit last, and whether the hit landed (HitAttemptOnMe: any).
                sb.AppendLine("  __hitBy = akSource");
                sb.AppendLine("  __hitLanded = !abHitBlocked");
            }
            if (uses.Contains("onpchitme"))
            {
                sb.AppendLine("  If akAggressor == PlayerRef");
                sb.AppendLine("    __pcHit = True");
                sb.AppendLine("    MwTick()");
                sb.AppendLine("  EndIf");
            }
            sb.AppendLine("EndEvent");
            sb.AppendLine();
        }
        if (sleep) WriteSleepEvents(sb);
    }

    static void WriteSleepEvents(StringBuilder sb, bool global = false)
    {
        // GetPCSleep: Morrowind's scripts act while the player rests (an ambush wakes them); Skyrim's run when the
        // sleep starts, and whatever they place is there on waking.
        sb.AppendLine("Event OnSleepStart(Float afSleepStartTime, Float afDesiredSleepEndTime)");
        sb.AppendLine("  __pcSleep = True");
        sb.AppendLine("  __sleepStart = afSleepStartTime");
        sb.AppendLine("  __sleepEnd = afDesiredSleepEndTime");
        if (global)
        {
            sb.AppendLine("  If TMMwScript.InMorrowind() && IsRunning()");
            sb.AppendLine("    MwTick()");
            sb.AppendLine("  EndIf");
        }
        else sb.AppendLine("  MwTick()");
        sb.AppendLine("EndEvent");
        sb.AppendLine();
        sb.AppendLine("Event OnSleepStop(Bool abInterrupted)");
        sb.AppendLine("  __pcSleep = False");
        sb.AppendLine("EndEvent");
        sb.AppendLine();
    }

    void WriteHelpers(StringBuilder sb)
    {
        void Lines(params string[] lines) { foreach (var l in lines) sb.AppendLine(l); }
        if (uses.Contains("dt")) Lines("Float __dt", "Float __last = -1.0");
        if (uses.Contains("cellchanged")) Lines("Cell __lastCell", "Bool __cellSeen", "Bool __cellChanged");
        if (uses.Contains("onactivate")) Lines("Bool __activated", "Bool __blocking", "Bool __blockSet");
        if (uses.Contains("onactivate") || uses.Contains("actionref")) Lines("ObjectReference __actionRef");
        if (uses.Contains("ondeath")) Lines("Bool __died");
        if (uses.Contains("onmurder")) Lines("Bool __murdered");
        if (uses.Contains("onknockout")) Lines("Bool __knockedOut");
        if (uses.Contains("onpcadd")) Lines("Bool __pcAdded");
        if (uses.Contains("onpcdrop")) Lines("Bool __pcDropped");
        if (uses.Contains("onpcequip")) Lines("Bool __pcEquipped");
        if (uses.Contains("onpchitme")) Lines("Bool __pcHit");
        if (uses.Contains("pcsleep")) Lines("Bool __pcSleep", "Float __sleepStart", "Float __sleepEnd");
        if (uses.Contains("hitonme"))
            Lines("Form __hitBy", "Bool __hitLanded", "",
                "; HitOnMe / HitAttemptOnMe: hit by that weapon (any attempt, blocked too) since the script last asked.",
                "Bool Function MwHitOnMe(Form akWeapon, Bool abAttempt)",
                "  If !__hitBy || __hitBy != akWeapon || !abAttempt && !__hitLanded", "    Return False", "  EndIf",
                "  __hitBy = None", "  Return True", "EndFunction");
        if (uses.Contains("basestats"))
            Lines("", "; The Morrowind record's attributes and skills of the carrier (filled per record), -1 where not known.",
                "Int Function MwBaseStat(Int aiStat)",
                "  If MwBaseStats && aiStat < MwBaseStats.Length", "    Return MwBaseStats[aiStat]", "  EndIf",
                "  Return -1", "EndFunction");
        if (kind == Kind.Object && (ctx.IsItem || uses.Contains("onpcadd") || uses.Contains("onpcdrop") || locals.ContainsKey("OnPCAdd") || locals.ContainsKey("OnPCDrop"))) Lines("Bool __carried");
        if (uses.Contains("startpos")) Lines("Bool __startSet", "Float __sx", "Float __sy", "Float __sz", "Float __sax", "Float __say", "Float __saz");
        foreach (var s in loopSounds.Order(StringComparer.Ordinal)) Lines($"Int __loop_{s}");
        if (uses.Contains("say")) Lines("Float __sayUntil");
        if (uses.Contains("sound")) Lines("Float[] __soundUntil", "Form[] __soundOf");
        if (uses.Contains("button"))
        {
            Lines("Int __button = -1", "",
                "; GetButtonPressed: the button chosen in the last message box, once (-1 until then).",
                "Int Function MwTakeButton()", "  Int b = __button", "  __button = -1", "  Return b", "EndFunction");
        }
        // Events' flags: true once after the event, as Morrowind's functions are.
        if (uses.Contains("onactivate"))
            Lines("", "; OnActivate: true once after an activation; checking it holds the activation back (until Activate).",
                "Bool Function MwOnActivate()", "  If !__blocking", "    __blocking = True", "    TMMwScript.Block(Self, True)", "  EndIf",
                "  Bool was = __activated", "  __activated = False", "  Return was", "EndFunction");
        foreach (var (use, flag, fn) in new[] { ("ondeath", "__died", "MwOnDeath"), ("onmurder", "__murdered", "MwOnMurder"),
                     ("onknockout", "__knockedOut", "MwOnKnockout"), ("onpcadd", "__pcAdded", "MwOnPCAdd"), ("onpcdrop", "__pcDropped", "MwOnPCDrop"),
                     ("onpcequip", "__pcEquipped", "MwOnPCEquip"), ("onpchitme", "__pcHit", "MwOnPCHitMe") })
        {
            if (!uses.Contains(use)) continue;
            Lines("", $"Bool Function {fn}()", $"  Bool was = {flag}", $"  {flag} = False", "  Return was", "EndFunction");
        }
        if (uses.Contains("say"))
            Lines("", "; Say: Skyrim doesn't report when a line ends: SayDone after the recording's length.",
                "Function MwSaid(Float afSeconds)", "  __sayUntil = Utility.GetCurrentRealTime() + afSeconds", "EndFunction");
        if (uses.Contains("sound"))
            Lines("", "; GetSoundPlaying: a sound plays for its file's length after PlaySound.",
                "Function MwPlayed(Form akSound, Float afSeconds)",
                "  If !__soundOf", "    __soundOf = new Form[8]", "    __soundUntil = new Float[8]", "  EndIf",
                "  Int i = __soundOf.Find(akSound)", "  If i < 0", "    i = __soundOf.Find(None)", "  EndIf", "  If i < 0", "    i = 0", "  EndIf",
                "  __soundOf[i] = akSound", "  __soundUntil[i] = Utility.GetCurrentRealTime() + afSeconds", "EndFunction", "",
                "Bool Function MwSoundPlaying(Form akSound)",
                "  If !__soundOf", "    Return False", "  EndIf",
                "  Int i = __soundOf.Find(akSound)", "  Return i >= 0 && Utility.GetCurrentRealTime() < __soundUntil[i]", "EndFunction");
        sb.AppendLine();
    }
}
