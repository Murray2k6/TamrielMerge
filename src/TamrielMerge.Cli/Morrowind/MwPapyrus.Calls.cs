using System.Globalization;
using System.Text.RegularExpressions;
using static TamrielMerge.Morrowind.MwScript;

namespace TamrielMerge.Morrowind;

/// <summary>Morrowind's functions as Papyrus.</summary>
public sealed partial class MwPapyrus
{
    /// <summary>The reference a call acts on: its explicit ref->, else the script's own (Self; a global script's MwSelf;
    /// a result script's speaker).</summary>
    Code Subject(Call c)
    {
        if (c.Target is { } t) return RefCode(t);
        return kind switch
        {
            Kind.Global => new Code("MwSelf", T.Ref),
            Kind.Result => new Code("akSpeakerRef", T.Ref),
            _ => new Code("Self", ctx.OnActor ? T.Actor : T.Ref),
        };
    }

    static string AsRef(Code c) => c.Type is T.Ref or T.Actor ? c.Text : $"({Paren(c.Text)} as ObjectReference)";
    // A script's Self can't be cast across to a sibling type directly (an ObjectReference script to Actor): through its parent.
    static string AsActor(Code c) => c.Type == T.Actor ? c.Text : c.Text == "Self" ? "((Self as ObjectReference) as Actor)" : $"({Paren(c.Text)} as Actor)";

    /// <summary>A bare word or string argument as written (an ID, a cell name, an axis).</summary>
    static string Word(Call c, int i) => i < c.Args.Count ? c.Args[i] switch { Name n when n.Owner is null => n.Id, Str s => s.Value, Num n => n.Text, _ => "" } : "";
    Code Arg(Call c, int i) => i < c.Args.Count ? Expr(c.Args[i]) : new Code("0", T.Int);
    string ArgNum(Call c, int i, string def = "0") => i < c.Args.Count ? Num(Expr(c.Args[i])) : def;
    string ArgFloat(Call c, int i, string def = "0.0") => i < c.Args.Count ? Convert(Expr(c.Args[i]), T.Float) : def;
    string ArgInt(Call c, int i, string def = "0") => i < c.Args.Count ? Convert(Expr(c.Args[i]), T.Int) : def;

    /// <summary>An argument naming a form (an item, an actor base, a spell...): its property; a variable holding a number isn't one.</summary>
    string Form(Call c, int i, string type = "Form")
    {
        string id = Word(c, i);
        if (id.Length == 0) { Fail("form argument that isn't an ID", "None"); return "None"; }
        string? t = world.TypeOf(id);
        if (t is null) { Fail("unknown ID", "None"); return "None"; }
        return type switch
        {
            "Spell" => Prop("spell:" + id, "Spell", "sp_" + Clean(id)),
            "Faction" => Prop("faction:" + id, "Faction", "fac_" + Clean(id)),
            _ => BaseProperty(id),
        };
    }

    /// <summary>An argument naming a reference (an ID, or player).</summary>
    Code RefArg(Call c, int i)
    {
        string id = Word(c, i);
        if (id.Length == 0) return Fail("reference argument that isn't an ID", "None");
        if (!id.Equals("player", StringComparison.OrdinalIgnoreCase) && world.TypeOf(id) is null) return Fail("unknown ID", "None");
        return RefCode(id);
    }

    static string Axis(Call c) => Word(c, 0).ToUpperInvariant() is "X" or "Y" or "Z" ? Word(c, 0).ToUpperInvariant() : "Z";

    string Faction(Call c, int i)
    {
        string id = Word(c, i);
        if (id.Length == 0)
        {
            // Without a faction: the speaker's (in a result script) or the calling actor's first faction.
            if (kind == Kind.Result && ctx.SpeakerFaction is { } sf) return Prop("faction:" + sf, "Faction", "fac_" + Clean(sf));
            if (kind == Kind.Result || kind == Kind.Object && ctx.OnActor) return $"TMMwScript.FactionOf({AsActor(Subject(c))})";
            Fail("faction function without a faction outside dialogue", "None");
            return "None";
        }
        if (world.FactionId(id) is not { } fid) { Fail("unknown faction", "None"); return "None"; }
        return Prop("faction:" + fid, "Faction", "fac_" + Clean(fid));
    }

    /// <summary>A journal's quest, or null for a journal the game doesn't have.</summary>
    string? Journal(Call c, int i)
    {
        string id = Word(c, i);
        if (!world.IsJournal(id)) return null;
        return Prop("journal:" + id, "Quest", "q_" + Clean(id));
    }

    Code CallCode(Call c, bool statement)
    {
        string fn = c.Function.ToLowerInvariant();
        Code R(string text, T type) => new(text, type);
        string self() => AsRef(Subject(c));
        string actorSelf() => AsActor(Subject(c));
        // Nothing to do in Skyrim, said so in the script (and counted as translated).
        Code Nothing(string why) => R(statement ? $"; {c.Function}: {why}" : "0", statement ? T.Void : T.Int);

        // ---- attributes, skills and the like: Get/Set/Mod<stat> ----
        if (Stat(fn) is { } stat) return StatCode(c, stat, actorSelf);

        switch (fn)
        {
            // ---- events (true once after the event) ----
            case "onactivate":
                if (kind != Kind.Object) return R("False", T.Bool);
                uses.Add("onactivate");
                return R("MwOnActivate()", T.Bool);
            case "ondeath" or "onmurder" or "onknockout" when c.Target is not null:
                // Another actor's death, asked from elsewhere: whether it is dead (or down).
                return R(fn == "onknockout" ? $"{actorSelf()}.IsBleedingOut()" : $"{actorSelf()}.IsDead()", T.Bool);
            case "ondeath" or "onmurder" or "onknockout":
                if (kind != Kind.Object || !ctx.OnActor) return R("False", T.Bool);   // only people and creatures die
                uses.Add(fn);
                return R(fn switch { "ondeath" => "MwOnDeath()", "onmurder" => "MwOnMurder()", _ => "MwOnKnockout()" }, T.Bool);
            case "onpcadd" or "onpcdrop" or "onpcequip" or "onpchitme":
                if (kind != Kind.Object) return R("False", T.Bool);
                uses.Add(fn);
                PlayerRef();
                return R(fn switch { "onpcadd" => "MwOnPCAdd()", "onpcdrop" => "MwOnPCDrop()", "onpcequip" => "MwOnPCEquip()", _ => "MwOnPCHitMe()" }, T.Bool);
            case "onpcsoulgemuse" or "onrepair" or "onpcrepair":
                // Skyrim has no event for using a soul gem on an item or repairing it.
                return R("False", T.Bool);
            case "menumode": return R("Utility.IsInMenuMode()", T.Bool);
            case "getsecondspassed": uses.Add("dt"); return R("__dt", T.Float);
            case "cellchanged":
                if (kind == Kind.Result) return R("False", T.Bool);
                uses.Add("cellchanged"); PlayerRef();
                return R("__cellChanged", T.Bool);
            case "getpcsleep":
                if (kind == Kind.Result) return R("False", T.Bool);
                uses.Add("pcsleep");
                return R("__pcSleep", T.Bool);
            case "wakeuppc":
                // The player's rest ends (the sleep menu closed, as Skyrim's own interruption does), some hours into it as
                // Morrowind's hourly checks would wake them.
                if (kind == Kind.Result) return R("TMMwScript.WakeUpPC(0.0, 0.0)", T.Void);
                uses.Add("pcsleep");
                return R("TMMwScript.WakeUpPC(__sleepStart, __sleepEnd)", T.Void);

            // ---- enabling, locks, activation ----
            case "enable": return R($"{self()}.Enable()", T.Void);
            case "disable": return R($"{self()}.Disable()", T.Void);
            case "getdisabled": return R($"{self()}.IsDisabled()", T.Bool);
            case "setdelete": return R($"TMMwScript.SetDelete({self()}, {ArgInt(c, 0)})", T.Void);
            case "lock": return R($"TMMwScript.LockMw({self()}, {ArgInt(c, 0, "50")})", T.Void);
            case "unlock": return R($"TMMwScript.UnlockMw({self()})", T.Void);
            case "getlocked": return R($"TMMwScript.IsLockedMw({self()})", T.Bool);
            case "activate":
            {
                // The default activation Morrowind's OnActivate held back: by whoever activated it, else the player.
                string who = checksActivate ? $"TMMwScript.Activator(__actionRef, {PlayerRef()})" : PlayerRef();
                if (c.Args.Count > 0) who = AsRef(RefArg(c, 0));
                if (checksActivate && c.Target is null)
                {
                    // Activate lets the activation go until the script checks OnActivate again (its next run).
                    uses.Add("onactivate");
                    return R($"TMMwScript.DefaultActivate(Self, {who})\n__blocking = False\nTMMwScript.Block(Self, False)\nRegisterForSingleUpdate(0.1)", T.Void);
                }
                return R($"TMMwScript.DefaultActivate({self()}, {who})", T.Void);
            }

            // ---- items ----
            case "additem" or "removeitem":
            {
                string id = Word(c, 0);
                string form = Form(c, 0);
                string count = ArgInt(c, 1, "1");
                int coins = world.CoinsIn(id);
                if (coins > 1) count = $"{Paren(count)} * {coins}";
                return R(fn == "additem" ? $"{self()}.AddItem({form}, {count})" : $"{self()}.RemoveItem({form}, {count})", T.Void);
            }
            case "getitemcount":
            {
                string id = Word(c, 0);
                string form = Form(c, 0);
                int coins = world.CoinsIn(id);
                return coins > 1 ? R($"{self()}.GetItemCount({form}) / {coins}", T.Int) : R($"{self()}.GetItemCount({form})", T.Int);
            }
            case "drop": return R($"{self()}.DropObject({Form(c, 0)}, {ArgInt(c, 1, "1")})", T.Void);
            case "equip": return R($"{actorSelf()}.EquipItem({Form(c, 0)})", T.Void);
            case "hasitemequipped": return R($"{actorSelf()}.IsEquipped({Form(c, 0)})", T.Bool);
            case "getweapontype": return R($"TMMwScript.WeaponType({actorSelf()})", T.Int);
            case "getarmortype": return R($"TMMwScript.ArmorType({actorSelf()}, {ArgInt(c, 0)})", T.Int);
            case "hassoulgem": return R($"TMMwScript.SoulGems({actorSelf()}, {Form(c, 0)})", T.Int);
            case "removesoulgem": return R($"TMMwScript.RemoveSoulGem({actorSelf()}, {Form(c, 0)}, {ArgInt(c, 1, "1")})", T.Void);
            case "dropsoulgem": return R($"TMMwScript.RemoveSoulGem({actorSelf()}, {Form(c, 0)}, 1)", T.Void);
            case "addsoulgem":
                return R($"{self()}.AddItem({Prop("soulgem:" + Word(c, 1), "Form", "sg_" + Clean(Word(c, 1)))}, 1)", T.Void);

            // ---- spells and effects ----
            case "addspell": return R($"{actorSelf()}.AddSpell({Form(c, 0, "Spell")}, False)", T.Void);
            case "removespell": return R($"{actorSelf()}.RemoveSpell({Form(c, 0, "Spell")})", T.Void);
            case "getspell": return R($"{actorSelf()}.HasSpell({Form(c, 0, "Spell")})", T.Bool);
            case "cast": return R($"{Form(c, 0, "Spell")}.Cast({self()}, {AsRef(RefArg(c, 1))})", T.Void);
            case "explodespell": return R($"{Form(c, 0, "Spell")}.Cast({self()}, {self()})", T.Void);
            case "getspelleffects": return R($"TMMwScript.HasSpellEffects({actorSelf()}, {Form(c, 0, "Spell")})", T.Bool);
            case "removespelleffects": return R($"{actorSelf()}.DispelSpell({Form(c, 0, "Spell")})", T.Void);
            case "geteffect": return EffectCode(c, actorSelf, remove: false);
            case "removeeffects": return EffectCode(c, actorSelf, remove: true);
            case "getcommondisease": return R($"TMMwScript.HasAny({actorSelf()}, {Prop("diseases:common", "FormList", "MwCommonDiseases")})", T.Bool);
            case "getblightdisease": return R($"TMMwScript.HasAny({actorSelf()}, {Prop("diseases:blight", "FormList", "MwBlightDiseases")})", T.Bool);
            case "iswerewolf": return R($"TMMwScript.IsWerewolf({actorSelf()})", T.Bool);
            case "becomewerewolf": return R($"TMMwScript.BecomeWerewolf({actorSelf()})", T.Void);
            case "undowerewolf": return R($"TMMwScript.UndoWerewolf({actorSelf()})", T.Void);
            case "setwerewolfacrobatics": return Nothing("Skyrim's beast form has its own movement");
            case "getwerewolfkills": return R("Game.QueryStat(\"Werewolf Transformations\")", T.Int);

            // ---- actors ----
            case "startcombat": return R($"{actorSelf()}.StartCombat({AsActor(RefArg(c, 0))})", T.Void);
            case "stopcombat": return R($"{actorSelf()}.StopCombat()", T.Void);
            case "forcegreeting":
                // Activated by the player, a person starts the conversation (as Morrowind's greeting does).
                return R($"TMMwScript.ForceGreeting({actorSelf()}, {PlayerRef()})", T.Void);
            case "resurrect": return R($"{actorSelf()}.Resurrect()", T.Void);
            case "getdisposition": return R($"TMMwScript.GetDisposition({actorSelf()})", T.Int);
            case "setdisposition": return R($"TMMwScript.SetDisposition({actorSelf()}, {ArgFloat(c, 0)})", T.Void);
            case "moddisposition": return R($"TMMwScript.ModDisposition({actorSelf()}, {ArgFloat(c, 0)})", T.Void);
            case "gethello" or "getfight" or "getflee" or "getalarm": return R($"TMMwScript.GetAi({actorSelf()}, \"{fn[3..]}\")", T.Int);
            case "sethello" or "setfight" or "setflee" or "setalarm": return R($"TMMwScript.SetAi({actorSelf()}, \"{fn[3..]}\", {ArgFloat(c, 0)})", T.Void);
            case "modhello" or "modfight" or "modflee" or "modalarm": return R($"TMMwScript.SetAi({actorSelf()}, \"{fn[3..]}\", TMMwScript.GetAi({actorSelf()}, \"{fn[3..]}\") + {ArgFloat(c, 0)})", T.Void);
            case "getlevel": return R($"{actorSelf()}.GetLevel()", T.Int);
            case "setlevel": return R($"TMMwScript.SetLevel({actorSelf()}, {ArgInt(c, 0)})", T.Void);
            case "getdeadcount": return R($"TMMwScript.DeadCount({Form(c, 0)})", T.Int);
            case "getdetected": return R($"{AsActor(RefArg(c, 0))}.IsDetectedBy({actorSelf()})", T.Bool);
            case "getlos" or "getlineofsight": return R($"{actorSelf()}.HasLOS({AsRef(RefArg(c, 0))})", T.Bool);
            case "gettarget": return R($"{actorSelf()}.GetCombatTarget() == {AsActor(RefArg(c, 0))}", T.Bool);
            case "getattacked": return R($"{actorSelf()}.IsInCombat()", T.Bool);
            case "getweapondrawn": return R($"{actorSelf()}.IsWeaponDrawn()", T.Bool);
            case "getspellreadied": return R($"TMMwScript.SpellReadied({actorSelf()})", T.Bool);
            case "getrace": return R($"TMMwScript.IsRace({actorSelf()}, {Prop("race:" + Word(c, 0), "Race", "race_" + Clean(Word(c, 0)))})", T.Bool);
            case "samefaction": return R($"TMMwScript.SameFaction({actorSelf()}, {PlayerRef()})", T.Bool);
            case "raiserank" or "lowerrank":
                return R($"TMMwScript.ModRank({actorSelf()}, TMMwScript.FactionOf({actorSelf()}), {(fn == "raiserank" ? 1 : -1)})", T.Void);
            case "forcesneak": return R($"{actorSelf()}.StartSneaking()", T.Void);
            case "clearforcesneak": return R($"TMMwScript.StopSneaking({actorSelf()})", T.Void);
            case "forcerun" or "clearforcerun" or "forcejump" or "clearforcejump" or "forcemovejump" or "clearforcemovejump":
                return Nothing("Skyrim's AI packages decide running and jumping");
            case "getforcesneak": return R($"{actorSelf()}.IsSneaking()", T.Bool);
            case "getforcerun": return R($"{actorSelf()}.IsRunning()", T.Bool);
            case "getforcejump" or "getforcemovejump": return R("False", T.Bool);
            case "fall": return Nothing("Skyrim's actors fall by themselves");
            case "face": return R($"TMMwScript.Face({self()}, {ArgFloat(c, 0)}, {ArgFloat(c, 1)})", T.Void);
            case "hitonme" or "hitattemptonme":
                // Hit (or attacked: Skyrim reports hits, blocked ones too) by that weapon since the script last asked.
                if (kind != Kind.Object || c.Target is not null) return R("False", T.Bool);
                uses.Add("hitonme");
                return R($"MwHitOnMe({Form(c, 0)}, {(fn == "hitonme" ? "False" : "True")})", T.Bool);
            case "repairedonme": return R("False", T.Bool);

            // ---- AI packages ----
            // AIWander range duration time idles...: the duration in hours (0: until told otherwise).
            case "aiwander": return R($"{AI()}.Wander({actorSelf()}, {ArgFloat(c, 0)}, {ArgFloat(c, 1)})", T.Void);
            case "aitravel": return R($"{AI()}.Travel({actorSelf()}, {ArgFloat(c, 0)}, {ArgFloat(c, 1)}, {ArgFloat(c, 2)})", T.Void);
            case "aifollow" or "aiescort":
                return R($"{AI()}.{(fn == "aifollow" ? "Follow" : "Escort")}({actorSelf()}, {AsActor(RefArg(c, 0))}, {ArgFloat(c, 1)}, {ArgFloat(c, 2)}, {ArgFloat(c, 3)}, {ArgFloat(c, 4)})", T.Void);
            case "aifollowcell" or "aiescortcell":
                // The cell only says where the destination is; the coordinates are within it.
                return R($"{AI()}.{(fn == "aifollowcell" ? "Follow" : "Escort")}({actorSelf()}, {AsActor(RefArg(c, 0))}, {ArgFloat(c, 2)}, {ArgFloat(c, 3)}, {ArgFloat(c, 4)}, {ArgFloat(c, 5)})", T.Void);
            case "aiactivate": return R($"{AI()}.ActivateRef({actorSelf()}, {AsRef(RefArg(c, 0))})", T.Void);
            case "getaipackagedone": return R($"{AI()}.Done({actorSelf()})", T.Bool);
            case "getcurrentaipackage": return R($"{AI()}.Current({actorSelf()})", T.Int);

            // ---- placement and movement ----
            case "getdistance": return R($"TMMwScript.Distance({self()}, {AsRef(RefArg(c, 0))})", T.Float);
            case "getpos": return R($"{self()}.GetPosition{Axis(c)}()", T.Float);
            case "getangle": return R($"{self()}.GetAngle{Axis(c)}()", T.Float);
            case "setpos": return R($"TMMwScript.SetPos({self()}, \"{Axis(c)}\", {ArgFloat(c, 1)})", T.Void);
            case "setangle": return R($"TMMwScript.SetAngle({self()}, \"{Axis(c)}\", {ArgFloat(c, 1)})", T.Void);
            case "getstartingpos" or "getstartingangle":
                if (kind != Kind.Object || c.Target is not null) return R($"{self()}.Get{(fn == "getstartingpos" ? "Position" : "Angle")}{Axis(c)}()", T.Float);
                uses.Add("startpos");
                return R((fn == "getstartingpos" ? "__s" : "__sa") + Axis(c).ToLowerInvariant(), T.Float);
            case "setatstart":
                if (kind != Kind.Object || c.Target is not null) return Nothing("only a script's own object knows where it started");
                uses.Add("startpos");
                return R("TranslateTo(__sx, __sy, __sz, __sax, __say, __saz, 100000.0, 100000.0)", T.Void);
            case "move" or "moveworld":
                uses.Add("dt");
                return R($"TMMwScript.Move({self()}, \"{Axis(c)}\", {ArgFloat(c, 1)}, __dt, {(fn == "moveworld" ? "True" : "False")})", T.Void);
            case "rotate" or "rotateworld":
                uses.Add("dt");
                return R($"TMMwScript.Rotate({self()}, \"{Axis(c)}\", {ArgFloat(c, 1)}, __dt)", T.Void);
            case "getscale": return R($"{self()}.GetScale()", T.Float);
            case "setscale": return R($"{self()}.SetScale({ArgFloat(c, 0)})", T.Void);
            case "modscale": return R($"{self()}.SetScale({self()}.GetScale() + {ArgFloat(c, 0)})", T.Void);
            case "position":
                return R($"TMMwScript.PositionAt({self()}, {Anchor("")}, {ArgFloat(c, 0)}, {ArgFloat(c, 1)}, {ArgFloat(c, 2)}, {ArgFloat(c, 3)})", T.Void);
            case "positioncell":
            {
                // x y z angle "cell" (some scripts name the cell first).
                var args = c.Args.ToList();
                if (args.Count > 0 && args[0] is Str) args = [.. args.Skip(1), args[0]];
                var cc = c with { Args = args };
                string cell = Word(cc, 4);
                if (!KnownCell(cell)) return Nothing($"there is no cell \"{cell}\" in the game");
                return R($"TMMwScript.PositionAt({self()}, {Anchor(cell)}, {ArgFloat(cc, 0)}, {ArgFloat(cc, 1)}, {ArgFloat(cc, 2)}, {ArgFloat(cc, 3)})", T.Void);
            }
            case "placeitemcell":
                if (!KnownCell(Word(c, 1))) return Nothing($"there is no cell \"{Word(c, 1)}\" in the game");
                return R($"TMMwScript.PlaceItem({Anchor(Word(c, 1))}, {Form(c, 0)}, {ArgFloat(c, 2)}, {ArgFloat(c, 3)}, {ArgFloat(c, 4)}, {ArgFloat(c, 5)})", T.Void);
            case "placeitem":
                return R($"TMMwScript.PlaceItem({PlacementAnchor(c)}, {Form(c, 0)}, {ArgFloat(c, 1)}, {ArgFloat(c, 2)}, {ArgFloat(c, 3)}, {ArgFloat(c, 4)})", T.Void);
            case "placeatpc":
                return R($"TMMwScript.PlaceNear({PlayerRef()}, {Form(c, 0)}, {ArgInt(c, 1, "1")}, {ArgFloat(c, 2)}, {ArgInt(c, 3)})", T.Void);
            case "placeatme":
                return R($"TMMwScript.PlaceNear({self()}, {Form(c, 0)}, {ArgInt(c, 1, "1")}, {ArgFloat(c, 2)}, {ArgInt(c, 3)})", T.Void);
            case "getstandingpc": return R($"TMMwScript.StandingOn({self()}, {PlayerRef()})", T.Bool);
            case "getstandingactor": return R($"TMMwScript.StandingOn({self()}, {PlayerRef()})", T.Bool);
            case "getcollidingpc": return R($"TMMwScript.Touching({self()}, {PlayerRef()})", T.Bool);
            case "getcollidingactor": return R($"TMMwScript.Touching({self()}, {PlayerRef()})", T.Bool);
            case "hurtstandingactor" or "hurtcollidingactor":
                uses.Add("dt");
                return R($"TMMwScript.HurtTouching({self()}, {PlayerRef()}, {ArgFloat(c, 0)} * __dt, {(fn == "hurtstandingactor" ? "True" : "False")})", T.Void);
            case "getpccell":
                // A name no cell has (a misspelling in the original) is never the player's cell.
                if (!KnownCell(Word(c, 0)) || Word(c, 0).Length == 0) return R("False", T.Bool);
                return R($"TMMwScript.InCells({PlayerRef()}, {CellList(Word(c, 0))})", T.Bool);
            case "getinterior": return R($"{self()}.IsInInterior()", T.Bool);
            case "getwaterlevel": return R($"TMMwScript.WaterLevel({self()})", T.Float);
            case "setwaterlevel" or "modwaterlevel": return Nothing("Skyrim's water height is fixed per cell");
            case "centeroncell" or "coc":
                if (!KnownCell(Word(c, 0))) return Nothing($"there is no cell \"{Word(c, 0)}\" in the game");
                return R($"TMMwScript.PositionAt({PlayerRef()}, {Anchor(Word(c, 0))}, 0.0, 0.0, 0.0, 0.0)", T.Void);
            case "centeronexterior" or "coe":
                return R($"TMMwScript.PositionAt({PlayerRef()}, {Anchor("")}, ({ArgFloat(c, 0)} + 0.5) * 8192.0, ({ArgFloat(c, 1)} + 0.5) * 8192.0, 0.0, 0.0)", T.Void);

            // ---- quests, journal, scripts ----
            case "journal" or "setjournalindex":
            {
                // A journal the game doesn't have (left over from development): Morrowind does nothing either.
                if (Journal(c, 0) is not { } q) return Nothing($"there is no journal {Word(c, 0)} in the game");
                // The index is the journal's global (which can go back, as Morrowind's can); Journal also adds the entry
                // (the quest's stage, or its objective when the journal returns to an entry it had).
                bool entry = fn == "journal";
                if (entry && c.Args.Count > 1 && c.Args[1] is Num jn) journalIndexes.Add((Word(c, 0), (int)jn.Value));
                return R($"TMMwScript.Journal({q}, {JournalIndex(Word(c, 0))}, {ArgInt(c, 1)}, {(entry ? "True" : "False")})", T.Void);
            }
            case "getjournalindex":
                return Journal(c, 0) is not null ? R($"{JournalIndex(Word(c, 0))}.GetValueInt()", T.Int) : R("0", T.Int);   // no such journal: index 0, as in Morrowind
            case "addtopic": return Nothing("Skyrim's converted topics need no unlocking");
            case "clearinfoactor": return Nothing("Skyrim keeps no list of topics heard");
            case "startscript":
            {
                string id = Word(c, 0);
                if (!world.IsScript(id)) return Fail("StartScript of an unknown script", "None");
                string q = ScriptQuest(id);
                string target = c.Target is not null ? self() : "None";
                return R($"({q} as {world.GlobalScriptName(id)}).MwStartScript({target})", T.Void);
            }
            case "stopscript":
            {
                string id = Word(c, 0);
                if (!world.IsScript(id)) return Fail("StopScript of an unknown script", "None");
                return R($"{ScriptQuest(id)}.Stop()", T.Void);
            }
            case "scriptrunning":
            {
                string id = Word(c, 0);
                if (!world.IsScript(id)) return Fail("ScriptRunning of an unknown script", "False");
                return R($"{ScriptQuest(id)}.IsRunning()", T.Bool);
            }
            case "goodbye":
                goodbye = true;   // the response's Goodbye flag
                return Nothing("the response ends the conversation (its Goodbye flag)");
            case "choice":
                return Nothing("the choices are the response's linked topics");

            // ---- factions and reputation ----
            case "getpcrank": return R($"{PlayerRef()}.GetFactionRank({Faction(c, 0)})", T.Int);
            case "pcjoinfaction": return R($"{PlayerRef()}.SetFactionRank({Faction(c, 0)}, 0)", T.Void);
            case "pcraiserank": return R($"TMMwScript.ModRank({PlayerRef()}, {Faction(c, 0)}, 1)", T.Void);
            case "pclowerrank": return R($"TMMwScript.ModRank({PlayerRef()}, {Faction(c, 0)}, -1)", T.Void);
            case "pcexpell": return R($"{Faction(c, 0)}.SetPlayerExpelled(True)", T.Void);
            case "pcclearexpelled": return R($"{Faction(c, 0)}.SetPlayerExpelled(False)", T.Void);
            case "pcexpelled": return R($"{Faction(c, 0)}.IsPlayerExpelled()", T.Bool);
            case "getpcfacrep": return R($"{FactionRep(c, 0)}.GetValue()", T.Float);
            case "modpcfacrep": return R($"{FactionRep(c, 1)}.Mod({ArgFloat(c, 0)})", T.Void);
            case "setpcfacrep": return R($"{FactionRep(c, 1)}.SetValue({ArgFloat(c, 0)})", T.Void);
            case "getreputation": return R($"{Generated("gen:MWReputation", "GlobalVariable", "MwReputation")}.GetValue()", T.Float);
            case "modreputation": return R($"{Generated("gen:MWReputation", "GlobalVariable", "MwReputation")}.Mod({ArgFloat(c, 0)})", T.Void);
            case "setreputation": return R($"{Generated("gen:MWReputation", "GlobalVariable", "MwReputation")}.SetValue({ArgFloat(c, 0)})", T.Void);
            case "getfactionreaction":
                return R($"{Prop("faction:" + Word(c, 0), "Faction", "fac_" + Clean(Word(c, 0)))}.GetReaction({Prop("faction:" + Word(c, 1), "Faction", "fac_" + Clean(Word(c, 1)))})", T.Int);
            case "setfactionreaction" or "modfactionreaction":
            {
                string a = Prop("faction:" + Word(c, 0), "Faction", "fac_" + Clean(Word(c, 0)));
                string b = Prop("faction:" + Word(c, 1), "Faction", "fac_" + Clean(Word(c, 1)));
                // Skyrim's reactions are neutral, enemy, ally or friend (0-3): Morrowind's numbers by their sign.
                return R($"TMMwScript.SetReaction({a}, {b}, {(fn == "modfactionreaction" ? $"TMMwScript.ReactionValue({a}, {b}) + " : "")}{ArgFloat(c, 2)})", T.Void);
            }

            // ---- crime ----
            case "getpccrimelevel": return R($"{Crime()}.GetCrimeGold()", T.Int);
            case "setpccrimelevel": return R($"TMMwScript.SetCrime({Crime()}, {ArgInt(c, 0)})", T.Void);
            case "modpccrimelevel": return R($"TMMwScript.SetCrime({Crime()}, {Crime()}.GetCrimeGold() + {ArgInt(c, 0)})", T.Void);
            case "payfine" or "payfinethief": return R($"TMMwScript.PayFine({Crime()}, {PlayerRef()}, {(fn == "payfine" ? "True" : "False")})", T.Void);
            case "gotojail": return R($"TMMwScript.GoToJail({Crime()}, {PlayerRef()})", T.Void);
            case "getpcinjail" or "getpctraveling": return R("False", T.Bool);

            // ---- the player and the interface ----
            case "disableplayercontrols": return R("Game.DisablePlayerControls(True, False, False, False, True, False, True)", T.Void);
            case "enableplayercontrols": return R("Game.EnablePlayerControls(True, False, False, False, True, False, True)", T.Void);
            case "disableplayerfighting" or "disableplayermagic": return R("Game.DisablePlayerControls(False, True, False, False, False, False, False)", T.Void);
            case "enableplayerfighting" or "enableplayermagic": return R("Game.EnablePlayerControls(False, True, False, False, False, False, False)", T.Void);
            case "disableplayerviewswitch": return R("Game.DisablePlayerControls(False, False, True, False, False, False, False)", T.Void);
            case "enableplayerviewswitch": return R("Game.EnablePlayerControls(False, False, True, False, False, False, False)", T.Void);
            case "disableplayerlooking": return R("Game.DisablePlayerControls(False, False, False, True, False, False, False)", T.Void);
            case "enableplayerlooking": return R("Game.EnablePlayerControls(False, False, False, True, False, False, False)", T.Void);
            case "disableplayerjumping" or "enableplayerjumping": return Nothing("Skyrim's controls have no separate jump switch");
            case "disablevanitymode" or "enablevanitymode": return Nothing("Skyrim's idle camera can't be switched by script");
            case "disableteleporting": return R("Game.EnableFastTravel(False)", T.Void);
            case "enableteleporting": return R("Game.EnableFastTravel(True)", T.Void);
            case "disablelevitation" or "enablelevitation": return Nothing("Skyrim has no levitation");
            case "getplayercontrolsdisabled": return R("!Game.IsMovementControlsEnabled()", T.Bool);
            case "getplayerfightingdisabled" or "getplayermagicdisabled": return R("!Game.IsFightingControlsEnabled()", T.Bool);
            case "getplayerlookingdisabled": return R("!Game.IsLookingControlsEnabled()", T.Bool);
            case "getplayerviewswitch": return R("!Game.IsCamSwitchControlsEnabled()", T.Bool);
            case "getplayerjumpingdisabled" or "getvanitymodedisabled": return R("False", T.Bool);
            case "enableracemenu" or "enablenamemenu" or "enableclassmenu" or "enablebirthmenu" or "enablestatreviewmenu":
                return Nothing("the player made their character at the start of the journey (TMStoryOrder)");
            case "enableinventorymenu" or "enablemagicmenu" or "enablemapmenu" or "enablestatsmenu" or "enablelevelupmenu" or "enablerest":
                return Nothing("Skyrim's menus are always available");
            case "showrestmenu": return R("TMMwScript.ShowRestMenu()", T.Void);
            case "showmap":
                return R($"TMMwScript.ShowMap({Prop("mapmarkers:" + Word(c, 0), "FormList", "mm_" + Clean(Word(c, 0)))})", T.Void);
            case "fadeout": return R($"TMMwScript.FadeOut({ArgFloat(c, 0)})", T.Void);
            case "fadein": return R($"TMMwScript.FadeIn({ArgFloat(c, 0)})", T.Void);
            case "fadeto": return R($"TMMwScript.FadeTo({ArgFloat(c, 0)}, {ArgFloat(c, 1)})", T.Void);
            case "pcforce1stperson": return R("Game.ForceFirstPerson()", T.Void);
            case "pcforce3rdperson": return R("Game.ForceThirdPerson()", T.Void);
            case "pcget3rdperson": return R("TMMwScript.InThirdPerson()", T.Bool);
            case "getpcrunning": return R($"{PlayerRef()}.IsRunning()", T.Bool);
            case "getpcsneaking": return R($"{PlayerRef()}.IsSneaking()", T.Bool);
            case "getpcjumping": return R($"{PlayerRef()}.GetAnimationVariableBool(\"bInJumpState\")", T.Bool);
            case "getpcvisionbonus": return R("0", T.Int);
            case "xbox": return R("0", T.Int);   // never the Xbox
            case "menutest" or "togglemenus" or "tm" or "toggleai" or "tai" or "togglecollision" or "tcl" or "togglegodmode" or "tgm" or "fixme" or "outputobjcounts" or "resetactors":
                return Nothing("a console command; nothing a quest needs");
            case "dontsaveobject": return Nothing("Skyrim saves what it needs");
            case "skipanim": return Nothing("Skyrim's animations are driven by its behaviours");

            // ---- messages and speech ----
            case "messagebox": return MessageBoxCode(c);
            case "getbuttonpressed":
                if (kind == Kind.Result) return R("-1", T.Int);
                uses.Add("button");
                return R("MwTakeButton()", T.Int);
            case "say": return SayCode(c, statement);
            case "saydone":
                if (kind == Kind.Result) return R("True", T.Bool);
                uses.Add("say");
                return R("Utility.GetCurrentRealTime() >= __sayUntil", T.Bool);

            // ---- sound, music, visuals ----
            case "playsound" or "playsoundvp":
                return SoundCode(c, $"{PlayerRef()}", fn == "playsoundvp" ? 1 : -1);
            case "playsound3d" or "playsound3dvp":
                return SoundCode(c, self(), fn == "playsound3dvp" ? 1 : -1);
            case "playloopsound3d" or "playloopsound3dvp":
            {
                string id = Word(c, 0);
                string loop = Prop("loopsound:" + id, "Sound", "ls_" + Clean(id));
                if (kind == Kind.Result) return R($"{loop}.Play({self()})", T.Void);
                loopSounds.Add(Clean(id));
                // Started once; Morrowind's scripts call it every frame to keep it going.
                return R($"If !__loop_{Clean(id)}\n  __loop_{Clean(id)} = {loop}.Play({self()})\nEndIf", T.Void);
            }
            case "stopsound":
            {
                string id = Word(c, 0);
                if (kind != Kind.Result && loopSounds.Contains(Clean(id)) || PropertyExists("loopsound:" + id))
                {
                    loopSounds.Add(Clean(id));
                    return R($"If __loop_{Clean(id)}\n  Sound.StopInstance(__loop_{Clean(id)})\n  __loop_{Clean(id)} = 0\nEndIf", T.Void);
                }
                return Nothing("the sound plays once and ends by itself");
            }
            case "getsoundplaying":
            {
                string id = Word(c, 0);
                if (kind == Kind.Result) return R("False", T.Bool);
                if (world.TypeOf(id) != "SOUN") return Fail("unknown sound", "False");
                uses.Add("sound");
                if (loopSounds.Contains(Clean(id))) return R($"__loop_{Clean(id)} != 0", T.Bool);
                return R($"MwSoundPlaying({Prop("sound:" + id, "Sound", "snd_" + Clean(id))})", T.Bool);
            }
            case "streammusic":
            {
                // The track, as a music type of its own played over the music of the place (and done when it ends).
                if (world.MusicFile(Word(c, 0)) is not { } music) return Nothing($"there is no music file {Word(c, 0)} in the game");
                return R($"TMMwScript.StreamMusic({Prop("music:" + music, "MusicType", "mu_" + Clean(Path.GetFileNameWithoutExtension(music)))})", T.Void);
            }
            case "playbink":
            {
                string file = Word(c, 0);
                binks.Add(file);
                return R($"Game.PlayBink(\"{BinkName(file)}\")", T.Void);
            }
            case "playgroup" or "loopgroup":
                return R($"TMMwScript.PlayGroup({self()}, \"{Word(c, 0)}\")", T.Void);
            case "changeweather":
                return R($"TMMwScript.ChangeWeather({Prop("weather:" + Word(c, 0) + ":" + Word(c, 1), "Weather", "w_" + Clean(Word(c, 0)) + "_" + Clean(Word(c, 1)))})", T.Void);
            case "modregion":
                return Nothing("Skyrim's regions keep their weather chances (ChangeWeather sets the weather)");
            case "getcurrentweather": return R("TMMwScript.CurrentWeather()", T.Int);
            case "getwindspeed": return R("Weather.GetCurrentWeather().GetWindDirection() / 360.0", T.Float);
            case "getmasserphase" or "getsecundaphase": return R("((Utility.GetCurrentGameTime() as Int) / 3) % 8", T.Int);
            case "turnmoonred" or "turnmoonwhite": return Nothing("Skyrim's moons can't be recoloured by script");
            case "getcurrenttime": return R($"{GlobalProperty("GameHour")}.GetValue()", T.Float);

            // ---- arithmetic ----
            case "random":
                return R($"Utility.RandomInt(0, {(c.Args.Count > 0 ? $"{Paren(ArgInt(c, 0))} - 1" : "99")})", T.Int);
            case "getsquareroot": return R($"Math.Sqrt({ArgFloat(c, 0)})", T.Float);
            // Not a Morrowind function, but one script asks "GetStat Level" for the level.
            case "getstat": return R($"{actorSelf()}.GetLevel()", T.Int);
        }
        // ref->local, where the script means its own local (Player->fakeiswere).
        if (c.Args.Count == 0 && Slot(new Name(c.Function)) is { } own && kind != Kind.Result) return new Code(own.Read, own.Type);
        if (MwFunctions.Find(c.Function) is null) return Fail($"{c.Function} (unknown function)", "None");
        return Fail(c.Function, "None");
    }

    bool PropertyExists(string fill) => propertyByFill.ContainsKey(fill);

    string AI() => $"({Prop("ai", "Quest", "MwAI")} as TMMwAI)";
    /// <summary>The state Morrowind keeps and Skyrim doesn't (attributes...): TMMwState on the quest MWScriptState.</summary>
    string State() => $"({Prop("state", "Quest", "MwState")} as TMMwState)";
    /// <summary>A journal's index, which (unlike a quest's stage) can go back: a global per journal the dialogue reads.</summary>
    string JournalIndex(string journal) => Prop("jindex:" + journal, "GlobalVariable", "ji_" + Clean(journal));
    string Crime() => Generated("gen:MWCrimeFaction", "Faction", "MwCrime");

    /// <summary>A faction's reputation with the player: a global per faction (Skyrim keeps none).</summary>
    string FactionRep(Call c, int i)
    {
        string id = Word(c, i);
        if (id.Length == 0)
        {
            if (kind == Kind.Result && ctx.SpeakerFaction is { } sf) id = sf;
            else { Fail("faction reputation without a faction", "None"); return "None"; }
        }
        if (world.FactionId(id) is not { } fid) { Fail("unknown faction", "None"); return "None"; }
        return Prop("facrep:" + fid, "GlobalVariable", "rep_" + Clean(fid));
    }

    /// <summary>A marker the converter places in the named cell at its origin ("" for the exterior), for
    /// PositionCell and PlaceItemCell: their coordinates are offsets from it.</summary>
    string Anchor(string cell)
    {
        if (cell.Length > 0 && world.InteriorNamed(cell) is { } interior) return Prop("anchor:" + interior, "ObjectReference", "a_" + Clean(interior));
        return Prop("anchor:", "ObjectReference", "MwExteriorAnchor");
    }

    /// <summary>Whether a cell name means somewhere in the game: an interior, or an exterior name (a town, a region).</summary>
    bool KnownCell(string cell) => cell.Length == 0 || world.InteriorNamed(cell) is not null || world.IsExteriorName(cell);

    /// <summary>PlaceItem places in the calling reference's cell: in a global or result script, the player's.</summary>
    string PlacementAnchor(Call c) => kind == Kind.Object || c.Target is not null ? $"TMMwScript.AnchorFor({self_(c)}, {Anchor("")})" : $"TMMwScript.AnchorFor({PlayerRef()}, {Anchor("")})";
    string self_(Call c) => AsRef(Subject(c));

    string CellList(string name) => Prop("cells:" + name, "FormList", "cells_" + Clean(name));

    public static string BinkName(string file) => "TamrielMerge_MW_" + Path.GetFileName(file.Replace('/', '\\'));

    Code EffectCode(Call c, Func<string> actorSelf, bool remove)
    {
        // Morrowind names effects by their game setting (sEffectLevitate) or number; the converter maps them to
        // Skyrim's magic effects where it has one.
        string id = Word(c, 0);
        string effects = Prop("effects:" + id, "FormList", "me_" + Clean(id));
        return remove ? new Code($"TMMwScript.RemoveEffectsIn({actorSelf()}, {effects})", T.Void) : new Code($"TMMwScript.HasEffectIn({actorSelf()}, {effects})", T.Bool);
    }

    // ---- stats ----

    /// <summary>Morrowind's Get/Set/Mod for attributes, skills and the dynamic stats: (verb, stat).</summary>
    static (string Verb, string Stat)? Stat(string fn)
    {
        foreach (var verb in new[] { "getcurrent", "modcurrent", "get", "set", "mod" })
        {
            if (!fn.StartsWith(verb, StringComparison.Ordinal)) continue;
            string rest = fn[verb.Length..];
            if (rest.EndsWith("getratio")) { if (verb == "get") return ("ratio", rest[..^8]); continue; }
            if (StatNames.ContainsKey(rest)) return (verb, rest);
        }
        return null;
    }

    /// <summary>Morrowind's stats by Skyrim's actor values; "" where Skyrim has none (attributes, some effects).</summary>
    static readonly Dictionary<string, string> StatNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["health"] = "Health", ["magicka"] = "Magicka", ["fatigue"] = "Stamina",
        ["strength"] = "", ["intelligence"] = "", ["willpower"] = "", ["agility"] = "", ["speed"] = "", ["endurance"] = "", ["personality"] = "", ["luck"] = "",
        ["block"] = "Block", ["armorer"] = "Smithing", ["mediumarmor"] = "LightArmor", ["heavyarmor"] = "HeavyArmor", ["bluntweapon"] = "TwoHanded",
        ["longblade"] = "OneHanded", ["axe"] = "TwoHanded", ["spear"] = "TwoHanded", ["athletics"] = "", ["enchant"] = "Enchanting",
        ["destruction"] = "Destruction", ["alteration"] = "Alteration", ["illusion"] = "Illusion", ["conjuration"] = "Conjuration",
        ["mysticism"] = "Alteration", ["restoration"] = "Restoration", ["alchemy"] = "Alchemy", ["unarmored"] = "", ["security"] = "Lockpicking",
        ["sneak"] = "Sneak", ["acrobatics"] = "", ["lightarmor"] = "LightArmor", ["shortblade"] = "OneHanded", ["marksman"] = "Marksman",
        ["mercantile"] = "Speechcraft", ["speechcraft"] = "Speechcraft", ["handtohand"] = "UnarmedDamage",
        ["resistfire"] = "FireResist", ["resistfrost"] = "FrostResist", ["resistshock"] = "ElectricResist", ["resistmagicka"] = "MagicResist",
        ["resistpoison"] = "PoisonResist", ["resistdisease"] = "DiseaseResist", ["resistparalysis"] = "", ["resistnormalweapons"] = "",
        ["resistblight"] = "", ["resistcorprus"] = "",
        ["attackbonus"] = "", ["defendbonus"] = "", ["armorbonus"] = "DamageResist", ["castpenalty"] = "", ["blindness"] = "", ["chameleon"] = "",
        ["invisible"] = "Invisibility", ["paralysis"] = "Paralysis", ["silence"] = "", ["superjump"] = "", ["swimspeed"] = "", ["waterbreathing"] = "WaterBreathing",
        ["waterwalking"] = "", ["flying"] = "",
    };

    /// <summary>The stats TMMwState keeps (Skyrim has no actor value for them): Morrowind's attributes and three skills
    /// (<see cref="MwStats.Names"/>, from the records), then effects' magnitudes (0 until a script sets them).</summary>
    static readonly string[] KeptStats = [.. MwStats.Names, "resistparalysis", "resistnormalweapons", "resistblight", "resistcorprus", "attackbonus", "defendbonus",
        "castpenalty", "blindness", "chameleon", "silence", "superjump", "swimspeed", "waterwalking", "flying"];

    /// <summary>
    /// A kept stat's base value where the record is known: the named person's or creature's, a result script's one
    /// speaker's, an object script's carrier's (a property filled per record); -1 to reckon it in the game (the player:
    /// race, birthsign, how they play; anyone else: race and level).
    /// </summary>
    string BaseStat(Call c, int index)
    {
        if (index >= MwStats.Count) return "0";
        string? id = c.Target ?? (kind == Kind.Result ? ctx.SpeakerId : null);
        if (id is not null)
            return !id.Equals("player", StringComparison.OrdinalIgnoreCase) && world.BaseStats(id) is { } v ? v[index].ToString(CultureInfo.InvariantCulture) : "-1";
        if (kind == Kind.Object && ctx.OnActor)
        {
            uses.Add("basestats");
            Prop("basestats", "Int[]", "MwBaseStats");
            return $"MwBaseStat({index})";
        }
        return "-1";
    }

    Code StatCode(Call c, (string Verb, string Stat) s, Func<string> actorSelf)
    {
        string av = StatNames[s.Stat];
        string a = actorSelf();
        if (av.Length == 0)
        {
            // Stats Skyrim doesn't have (attributes, Athletics, Acrobatics, Unarmored, some effects) are kept per actor by
            // TMMwState: the record's value plus what scripts changed, with what follows from them (Speed the speed...).
            int index = Array.IndexOf(KeptStats, s.Stat.ToLowerInvariant());
            string st = State(), b = BaseStat(c, index);
            return s.Verb switch
            {
                "get" or "getcurrent" => new Code($"{st}.GetStat({a}, {index}, {b})", T.Float),
                "ratio" => new Code("1.0", T.Float),
                "set" => new Code($"{st}.SetStat({a}, {index}, {b}, {ArgFloat(c, 0)})", T.Void),
                _ => new Code($"{st}.ModStat({a}, {index}, {b}, {ArgFloat(c, 0)})", T.Void),
            };
        }
        bool dynamic = av is "Health" or "Magicka" or "Stamina";
        return s.Verb switch
        {
            "get" => new Code(dynamic ? $"{a}.GetActorValue(\"{av}\")" : $"{a}.GetActorValue(\"{av}\")", T.Float),
            "getcurrent" => new Code($"{a}.GetActorValue(\"{av}\")", T.Float),
            "ratio" => new Code($"{a}.GetActorValuePercentage(\"{av}\")", T.Float),
            // Morrowind's SetHealth sets the maximum (and the current with it); ModCurrentHealth damages or heals.
            "set" => new Code($"TMMwScript.SetActorValueMw({a}, \"{av}\", {ArgFloat(c, 0)})", T.Void),
            "modcurrent" => new Code($"TMMwScript.ModCurrent({a}, \"{av}\", {ArgFloat(c, 0)})", T.Void),
            _ => new Code($"{a}.ModActorValue(\"{av}\", {ArgFloat(c, 0)})", T.Void),
        };
    }

    // ---- sound ----

    Code SoundCode(Call c, string at, int volumeArg)
    {
        string id = Word(c, 0);
        if (world.TypeOf(id) != "SOUN") return new Code($"; there is no sound {id} in the game", T.Void);
        string snd = Prop("sound:" + id, "Sound", "snd_" + Clean(id));
        string play = volumeArg > 0
            ? $"Sound.SetInstanceVolume({snd}.Play({at}), {ArgFloat(c, volumeArg)})"
            : $"{snd}.Play({at})";
        if (kind == Kind.Result) return new Code(play, T.Void);
        uses.Add("sound");
        return new Code($"{play}\nMwPlayed({snd}, {F(world.SoundSeconds(id))})", T.Void);
    }

    // ---- messages ----

    /// <summary>Morrowind's ^Action placeholders (key names in message boxes) as words.</summary>
    static string Controls(string text) => Regex.Replace(text, @"\^Action(\w+)", m => m.Groups[1].Value switch
    {
        "Forward" => "Forward", "Back" => "Back", "SlideLeft" => "Strafe Left", "SlideRight" => "Strafe Right", "Activate" => "Activate",
        "Use" => "Attack", "ReadyItem" => "Ready Weapon", "ReadyMagic" => "Ready Magic", "Run" => "Run", "Sneak" => "Sneak", "Jump" => "Jump",
        "Journal" => "Journal", "Inventory" => "Inventory", "Rest" => "Wait", "AlwaysRun" => "Always Run", "AutoMove" => "Auto-Move",
        "TogglePOV" => "Toggle POV", "QuickSave" => "Quick Save", "QuickLoad" => "Quick Load", "MenuMode" => "Menu", var other => other,
    });

    static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n");

    /// <summary>A message's text with its values: Morrowind's printf-style %.0f / %g / %s filled in.</summary>
    string Format(string format, List<Expr> values)
    {
        var parts = new List<string>();
        var text = new System.Text.StringBuilder();
        int argIndex = 0;
        string s = Controls(format);
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch == '%' && i + 1 < s.Length)
            {
                if (s[i + 1] == '%') { text.Append('%'); i++; continue; }
                int j = i + 1;
                while (j < s.Length && (char.IsDigit(s[j]) || s[j] is '.' or '-' or '+')) j++;
                if (j < s.Length && "fgdisFGDIS".Contains(s[j]))
                {
                    char spec = char.ToLowerInvariant(s[j]);
                    string flags = s[(i + 1)..j];
                    if (text.Length > 0) { parts.Add("\"" + Escape(text.ToString()) + "\""); text.Clear(); }
                    if (argIndex < values.Count)
                    {
                        var v = Expr(values[argIndex++]);
                        parts.Add(spec is 'f' && flags.EndsWith(".0") || spec is 'd' or 'i' ? $"({Paren(Num(v))} as Int)"
                            : spec is 'f' && Regex.Match(flags, @"\.(\d)") is { Success: true } m ? $"TMMwScript.Fixed({Convert(v, T.Float)}, {m.Groups[1].Value})"
                            : v.Text);
                    }
                    i = j;
                    continue;
                }
            }
            text.Append(ch);
        }
        if (text.Length > 0 || parts.Count == 0) parts.Add("\"" + Escape(text.ToString()) + "\"");
        // A string first, so Papyrus joins the rest as text.
        if (!parts[0].StartsWith('"')) parts.Insert(0, "\"\"");
        return string.Join(" + ", parts);
    }

    /// <summary>
    /// MessageBox "text %.0f" value... "Button 1" "Button 2": with buttons (and a script that reads them), a Message
    /// record shown with the values (Skyrim formats %.Nf and %g from Show's arguments); Show waits for the choice, which
    /// GetButtonPressed then returns once, as Morrowind's does on the frames after the box closes. Without buttons to
    /// read, a plain message box.
    /// </summary>
    Code MessageBoxCode(Call c)
    {
        if (c.Args.Count == 0 || c.Args[0] is not Str text) return Fail("MessageBox without its text", "None");
        // After the text: values (non-strings) for its %-fields, then the buttons (strings).
        var rest = c.Args.Skip(1).ToList();
        int firstButton = rest.FindIndex(a => a is Str);
        var values = firstButton < 0 ? rest : rest.Take(firstButton).ToList();
        var buttons = firstButton < 0 ? [] : rest.Skip(firstButton).Select(a => a is Str s ? s.Value : a is Name n ? n.Id : "").ToList();
        bool readsButtons = kind != Kind.Result && buttons.Count > 0 && ScriptReadsButtons;
        if (!readsButtons) return new Code($"Debug.MessageBox({Format(text.Value, values)})", T.Void);
        if (values.Count > 9 || text.Value.Contains("%s", StringComparison.OrdinalIgnoreCase))
        {
            // Skyrim's message records format numbers only; a name in the text goes through a plain box first.
            uses.Add("button");
            string idx = $"TMMW_{Clean(scriptId)}_Msg{messages.Count}";
            messages.Add(new MessageForm(idx, "", buttons.Select(Controls).ToList()));
            return new Code($"Debug.MessageBox({Format(text.Value, values)})\n__button = {Generated("message:" + idx, "Message", "m_" + messages.Count)}.Show()", T.Void);
        }
        string id = $"TMMW_{Clean(scriptId)}_Msg{messages.Count}";
        messages.Add(new MessageForm(id, Controls(text.Value), buttons.Select(Controls).ToList()));
        uses.Add("button");
        var args = values.Select(v => Convert(Expr(v), T.Float));
        return new Code($"__button = {Generated("message:" + id, "Message", "m_" + messages.Count)}.Show({string.Join(", ", args)})", T.Void);
    }

    public bool ScriptReadsButtons { get; set; } = true;

    // ---- Say ----

    /// <summary>
    /// Say "file" "text": the speaker says the line with Morrowind's own recording - a topic (MWSay...) whose response
    /// carries the recording (the voice planner packs it for the speaker's voice type) and the subtitle. Said by
    /// something that isn't a person (the player hearing a Daedra, a creature), it plays as a sound with the text shown.
    /// </summary>
    Code SayCode(Call c, bool statement)
    {
        string file = Word(c, 0), text = Word(c, 1);
        if (file.Length == 0) return Fail("Say without a sound file", "None");
        var speakers = c.Target is { } t
            ? (world.TypeOf(t) == "NPC_" ? [t] : new List<string>())
            : kind == Kind.Global ? [] : ctx.Speakers ?? [];
        // SayDone after the recording's own length (read from the file as the scripts are translated).
        string seconds = F(world.VoiceSeconds(file) + 0.25f);
        if (kind != Kind.Result) uses.Add("say");
        string said = kind != Kind.Result ? $"\nMwSaid({seconds})" : "";
        if (speakers.Count == 0)
        {
            string snd = Prop("voice:" + file, "Sound", "vo_" + Clean(Path.GetFileNameWithoutExtension(file)));
            string at = c.Target is null && kind == Kind.Global ? PlayerRef() : AsRef(Subject(c));
            return new Code($"{snd}.Play({at})\nDebug.Notification(\"{Escape(text)}\"){said}", T.Void);
        }
        string id = $"MWSay{Hash(file + "|" + string.Join(",", speakers)):X8}";
        says.Add(new SayLine(id, file, text, speakers));
        return new Code($"{AsRef(Subject(c))}.Say({Generated("say:" + id, "Topic", "say_" + Clean(Path.GetFileNameWithoutExtension(file)))}){said}", T.Void);
    }
}
