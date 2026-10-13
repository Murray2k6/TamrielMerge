using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Activator = Mutagen.Bethesda.Skyrim.Activator;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's locks and traps, worked the Morrowind way (TMMwLock, TMMwSecurity): lockpicks and probes are tools
/// held in the hand (daggers, so they can be equipped) with Morrowind's quality and uses; a reference's lock level
/// (FLTV), key (KNAM) and trap (TNAM) go on its TMMwLock script, and doors and containers also keep a Skyrim lock so
/// keys and AI see it. A trap casts its own spell (converted, MwMagic) or, when none of its effects carry over,
/// takes the spell's damage from the player's health.
/// </summary>
public sealed partial class MwPluginBuilder
{
    readonly List<(Weapon Tool, float Quality, int Uses)> lockpicks = [], probes = [];
    Quest? securityQuest;
    readonly Dictionary<string, (ISpellGetter? Spell, int Damage)> traps = new(StringComparer.OrdinalIgnoreCase);
    public int LockCount { get; private set; }
    public int TrapCount { get; private set; }

    Quest SecurityQuest => securityQuest ??= NewSecurityQuest();

    Quest NewSecurityQuest()
    {
        var q = new Quest(mod)
        {
            EditorID = "MWSecurity",
            Name = "Morrowind Security",
            Flags = Quest.Flag.StartGameEnabled | Quest.Flag.RunOnce,
            Priority = 10,
            Type = Quest.TypeEnum.Misc,
        };
        mod.Quests.Add(q);
        return q;
    }

    /// <summary>A lockpick (LKDT) or probe (PBDT): weight, value, quality, uses — a dagger to hold.</summary>
    IMajorRecord Tool(Tes3Record rec, string edid, string? name, string? model, bool pick)
    {
        var d = rec.Get(pick ? "LKDT" : "PBDT")!.Value;
        float weight = d.Float(0), quality = d.Float(8);
        int value = d.Int(4), uses = d.Int(12);
        var w = mod.Weapons.DuplicateInAsNewRecord(Vanilla<IWeaponGetter>("IronDagger"));
        w.EditorID = edid;
        w.Name = name;
        w.ObjectEffect.Clear();
        w.BasicStats = new WeaponBasicStats { Value = (uint)Math.Max(0, value), Weight = weight, Damage = 1 };
        if (ItemMesh(model, weight) is { } mesh)
        {
            string path = SkyrimModelPath(mesh.Path);
            w.Model = new Model { File = path };
            w.FirstPersonModel = FirstPersonStatic(path).ToNullableLink<IStaticGetter>();
            w.ObjectBounds = Bounds(mesh);
        }
        (pick ? lockpicks : probes).Add((w, quality, Math.Max(1, uses)));
        return w;
    }

    static LockLevel SkyrimLock(int level) => level switch
    {
        <= 20 => LockLevel.Novice,
        <= 40 => LockLevel.Apprentice,
        <= 60 => LockLevel.Adept,
        <= 80 => LockLevel.Expert,
        _ => LockLevel.Master,
    };

    /// <summary>Puts a reference's Morrowind lock and trap on the placed door, container or swinging door.</summary>
    void ApplyLock(Tes3Ref r, PlacedObject placed, IMajorRecord b)
    {
        bool swing = b is Activator a && swingDoors.ContainsValue(a);
        if (b is not (Door or Container) && !swing) return;
        bool locked = r.LockLevel > 0, trapped = r.Trap is { Length: > 0 };
        if (!locked && !trapped) return;
        var key = r.Key is { Length: > 0 } ? Item(r.Key) as Key : null;
        var entry = new ScriptEntry { Name = "TMMwLock" };
        entry.Properties.Add(new ScriptObjectProperty { Name = "Security", Object = SecurityQuest.ToLink<ISkyrimMajorRecordGetter>() });
        if (locked)
        {
            entry.Properties.Add(new ScriptIntProperty { Name = "LockLevel", Data = r.LockLevel });
            if (swing) entry.Properties.Add(new ScriptBoolProperty { Name = "Locked", Data = true });
            else placed.Lock = new LockData { Level = SkyrimLock(r.LockLevel), Key = key is null ? new FormLink<IKeyGetter>() : key.ToLink<IKeyGetter>() };
            LockCount++;
        }
        if (swing) entry.Properties.Add(new ScriptBoolProperty { Name = "Swing", Data = true });
        if (key is not null) entry.Properties.Add(new ScriptObjectProperty { Name = "DoorKey", Object = key.ToLink<ISkyrimMajorRecordGetter>() });
        if (trapped && Trap(r.Trap!) is var (spell, damage))
        {
            entry.Properties.Add(new ScriptBoolProperty { Name = "Trapped", Data = true });
            if (spell is not null) entry.Properties.Add(new ScriptObjectProperty { Name = "TrapSpell", Object = spell.ToLink<ISkyrimMajorRecordGetter>() });
            entry.Properties.Add(new ScriptIntProperty { Name = "TrapDamage", Data = damage });
            TrapCount++;
        }
        placed.VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = [entry] };
    }

    /// <summary>
    /// A trap spell (SPEL, effects ENAM: id, skill, attribute, range, area, duration, magnitude min/max) as the Skyrim
    /// spell of its first elemental or paralysis effect, and its damage (average magnitude x duration of the health
    /// damaging effects) for when there is none.
    /// </summary>
    (ISpellGetter? Spell, int Damage) Trap(string id)
    {
        if (traps.TryGetValue(id, out var hit)) return hit;
        float damage = 0;
        if (world.Objects.TryGetValue(id, out var rec) && rec.Type == "SPEL")
            foreach (var e in rec.All("ENAM"))
            {
                int effect = BitConverter.ToInt16(e.Data, 0);
                float magnitude = (e.Int(16) + e.Int(20)) / 2f;
                if (effect is 14 or 15 or 16 or 23 or 27) damage += magnitude * Math.Max(1, e.Int(12));
            }
        // The trap's own spell, converted (MwMagic), cast from the door or container at the player.
        return traps[id] = (MwSpell(id), (int)MathF.Round(Math.Max(damage, 5)));
    }

    /// <summary>Every lockpick and probe (so the tool lists are complete) and the security quest's tool tables.</summary>
    void FinishLocks()
    {
        if (securityQuest is null) return;
        foreach (var rec in world.Objects.Values.Where(o => o.Type is "LOCK" or "PROB").ToList()) Item(rec.Id);
        FormList List(string edid, IEnumerable<Weapon> tools)
        {
            var l = new FormList(mod) { EditorID = edid };
            foreach (var t in tools) l.Items.Add(t.ToLink<ISkyrimMajorRecordGetter>());
            mod.FormLists.Add(l);
            return l;
        }
        var entry = new ScriptEntry { Name = "TMMwSecurity" };
        entry.Properties.Add(new ScriptObjectProperty { Name = "Picks", Object = List("MWLockpicks", lockpicks.Select(p => p.Tool)).ToLink<ISkyrimMajorRecordGetter>() });
        entry.Properties.Add(new ScriptFloatListProperty { Name = "PickQuality", Data = [.. lockpicks.Select(p => p.Quality)] });
        entry.Properties.Add(new ScriptIntListProperty { Name = "PickUses", Data = [.. lockpicks.Select(p => p.Uses)] });
        entry.Properties.Add(new ScriptObjectProperty { Name = "Probes", Object = List("MWProbes", probes.Select(p => p.Tool)).ToLink<ISkyrimMajorRecordGetter>() });
        entry.Properties.Add(new ScriptFloatListProperty { Name = "ProbeQuality", Data = [.. probes.Select(p => p.Quality)] });
        entry.Properties.Add(new ScriptIntListProperty { Name = "ProbeUses", Data = [.. probes.Select(p => p.Uses)] });
        securityQuest.VirtualMachineAdapter = new QuestAdapter { Scripts = [entry] };
    }
}
