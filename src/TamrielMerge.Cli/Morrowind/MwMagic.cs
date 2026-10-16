using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Skyrim;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's magic through <see cref="TesMagic"/>: spells, abilities, curses, blights, diseases and powers (SPEL:
/// SPDT type, cost, flags; ENAM effects), enchantments (ENCH: ENDT type, cost, charge) on weapons, armour and
/// clothing, scrolls (enchanted books), and actors' spell lists (NPCS). Summons and bound weapons use the creatures
/// and items Morrowind's settings name (sMagic...ID). Intervention goes to Morrowind's DivineMarker and TempleMarker
/// spots; an interior finds its place in the world through the doors (a WorldSpot marker linked to the outside door).
/// </summary>
public sealed partial class MwPluginBuilder
{
    TesMagic? magic;
    readonly Dictionary<string, Spell?> spells = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ObjectEffect?> enchantments = new(StringComparer.OrdinalIgnoreCase);

    TesMagic Magic
    {
        get
        {
            if (magic is not null) return magic;
            Vanilla<IWeaponGetter>("IronDagger");   // opens Skyrim.esm
            return magic = new TesMagic(mod, skyrimEsm!, "MW") { Look = Look };
        }
    }

    public int SpellCount => magic?.SpellCount ?? 0;
    public int EnchantmentCount => magic?.EnchantmentCount ?? 0;
    public IEnumerable<string> SkippedEffects => magic?.Skipped.Select(kv => $"{kv.Key} x{kv.Value}") ?? [];

    /// <summary>The Skyrim actor or weapon a summon or bound effect gives, from Morrowind's settings.</summary>
    FormKey? Associated(string effect)
    {
        // Bound armour is Skyrim's (TesMagic); only summons and bound weapons name a Morrowind creature or item.
        if (effect is "BoundCuirass" or "BoundHelm" or "BoundBoots" or "BoundShield" or "BoundGloves") return null;
        string setting = effect switch
        {
            "SummonBonewalker" => "sMagicLeastBonewalkerID",
            "SummonWolf" => "sMagicCreature01ID",
            "SummonBear" => "sMagicCreature02ID",
            "SummonBonewolf" => "sMagicCreature03ID",
            "SummonCreature04" => "sMagicCreature04ID",
            "SummonCreature05" => "sMagicCreature05ID",
            _ when effect.StartsWith("Summon") => $"sMagic{effect[6..]}ID",
            _ => $"sMagic{effect}ID",
        };
        string? id = world.Settings.TryGetValue(setting, out var gmst) ? gmst.Str("STRV") : null;
        if (id is null && effect == "BoundMace") id = "bound_mace";
        if (id is null) return null;
        if (effect.StartsWith("Summon")) return Actor(id) is Npc npc ? npc.FormKey : null;
        if (Item(id) is not Weapon weapon) return null;
        // A bound weapon vanishes when dropped or when its owner dies.
        if (weapon.Data is { } wd) wd.Flags |= WeaponData.Flag.BoundWeapon;
        return weapon.FormKey;
    }

    IEnumerable<TesEffect> EffectsOf(Tes3Record rec) =>
        rec.All("ENAM").Select(e => TesMagic.FromMorrowind(e.Data, Associated)).OfType<TesEffect>();

    /// <summary>A Morrowind spell (SPDT: type 0 spell, 1 ability, 2 blight, 3 disease, 4 curse, 5 power; cost).</summary>
    Spell? MwSpell(string id)
    {
        if (spells.TryGetValue(id, out var hit)) return hit;
        spells[id] = null;
        if (!world.Objects.TryGetValue(id, out var rec) || rec.Type != "SPEL" || rec.Get("SPDT") is not { } d) return null;
        var cast = d.Int(0) switch { 1 or 4 => TesCast.Ability, 2 or 3 => TesCast.Disease, 5 => TesCast.Power, _ => TesCast.Spell };
        return spells[id] = Magic.Spell("MWSpell" + Sanitize(id), rec.Str("FNAM"), cast, EffectsOf(rec), d.Int(4));
    }

    /// <summary>An enchantment (ENDT: type 0 cast once, 1 when strikes, 2 when used, 3 constant; cost; charge).</summary>
    (ObjectEffect? Effect, int Type, int Charge) MwEnchantment(string id)
    {
        if (!world.Objects.TryGetValue(id, out var rec) || rec.Type != "ENCH" || rec.Get("ENDT") is not { } d) return (null, -1, 0);
        int type = d.Int(0), charge = d.Int(8);
        if (!enchantments.TryGetValue(id, out var ench))
        {
            var cast = type switch { 1 => TesCast.WhenStrikes, 2 => TesCast.WhenUsed, 3 => TesCast.Constant, _ => TesCast.CastOnce };
            enchantments[id] = ench = cast == TesCast.CastOnce ? null : Magic.Enchantment("MWEnch" + Sanitize(id), null, cast, EffectsOf(rec), d.Int(4));
        }
        return (ench, type, charge);
    }

    /// <summary>Puts a weapon's, armour's or clothing's enchantment (ENAM) on its Skyrim record.</summary>
    void Enchant(Tes3Record rec, IMajorRecord? item)
    {
        if (rec.Str("ENAM") is not { Length: > 0 } enchId || item is null) return;
        var (ench, type, charge) = MwEnchantment(enchId);
        if (ench is null) return;
        switch (item)
        {
            case Weapon w:
                w.ObjectEffect.SetTo(ench);
                if (type == 1) w.EnchantmentAmount = (ushort)Math.Clamp(charge, 1, ushort.MaxValue);
                break;
            case Armor a when type is 2 or 3:
                a.ObjectEffect.SetTo(ench);
                break;
        }
    }

    /// <summary>A scroll: a book carrying a cast-once enchantment.</summary>
    IMajorRecord? ScrollItem(Tes3Record rec, string edid, string? name, string? model)
    {
        if (rec.Str("ENAM") is not { Length: > 0 } enchId || !world.Objects.TryGetValue(enchId, out var ench) || ench.Type != "ENCH"
            || ench.Get("ENDT") is not { } d || d.Int(0) != 0) return null;
        var data = rec.Get("BKDT")!.Value;
        float weight = data.Float(0);
        var mesh = ItemMesh(model, weight);
        return Magic.Scroll(edid, name, EffectsOf(ench), mesh is null ? null : SkyrimModelPath(mesh.Path), weight, (uint)Math.Max(0, data.Int(4)));
    }

    /// <summary>An actor's spells, abilities, powers and diseases (NPCS).</summary>
    void AddSpells(Npc npc, Tes3Record rec)
    {
        foreach (var f in rec.All("NPCS"))
            if (MwSpell(f.Text) is { } spell)
            {
                npc.ActorEffect ??= [];
                npc.ActorEffect.Add(spell.ToLink<ISpellRecordGetter>());
            }
    }

    // ---- Morrowind's look ----

    static readonly string[] Schools = ["Alteration", "Conjuration", "Destruction", "Illusion", "Mysticism", "Restoration"];
    MwVfx? vfx;
    readonly Dictionary<string, ArtObject?> arts = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, SoundDescriptor?> sounds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Summons and bound items as Morrowind shows them (its magic effect record, MGEF): its cast visual (CVFX) and, for a
    /// summon, VFX_Summon_Start on the creature; its cast and hit sounds (CSND, HSND), or the school's ("Conjuration
    /// Cast", "Conjuration Hit") when the record leaves them blank, as Morrowind does. Self effects show the cast visual
    /// on the caster when they land; a summon shows it in the hand while casting.
    /// </summary>
    TesMagic.EffectLook? Look(TesEffect e)
    {
        bool summon = e.Name.StartsWith("Summon"), bound = e.Name.StartsWith("Bound");
        if (!summon && !bound) return null;
        int index = Array.IndexOf(TesMagic.MwEffectNames, e.Name);
        if (index < 0 || !world.MagicEffects.TryGetValue(index, out var mgef)) return null;
        int school = mgef.Get("MEDT") is { } medt ? Math.Clamp(medt.Int(0), 0, 5) : 1;
        string? castVfx = mgef.Str("CVFX") is { Length: > 0 } c ? c : "VFX_DefaultCast";
        var castArt = Art(castVfx, summon ? ArtObject.TypeEnum.MagicCasting : ArtObject.TypeEnum.MagicHitEffect);
        var hitArt = summon ? Art("VFX_Summon_Start", ArtObject.TypeEnum.MagicHitEffect) : castArt;
        var list = new List<(string, FormKey)>();
        if (Sound(mgef.Str("CSND") is { Length: > 0 } cs ? cs : $"{Schools[school]} Cast") is { } castSound) list.Add(("Release", castSound.FormKey));
        if (Sound(mgef.Str("HSND") is { Length: > 0 } hs ? hs : $"{Schools[school]} Hit") is { } hitSound) list.Add(("OnHit", hitSound.FormKey));
        return new TesMagic.EffectLook(summon ? castArt?.FormKey : null, hitArt?.FormKey, list);
    }

    /// <summary>A Morrowind visual (a VFX_* static) as a Skyrim art object of its converted effect mesh.</summary>
    ArtObject? Art(string staticId, ArtObject.TypeEnum type)
    {
        string key = $"{staticId}|{type}";
        if (arts.TryGetValue(key, out var hit)) return hit;
        arts[key] = null;
        if (world.Model(staticId) is not { Length: > 0 } model) { Problems.Add($"vfx {staticId}: no model"); return null; }
        vfx ??= new MwVfx(data, textures);
        if (vfx.Convert(model, modOutDir) is not { } rel) { Problems.AddRange(vfx.Problems); vfx.Problems.Clear(); return null; }
        var art = new ArtObject(mod)
        {
            EditorID = "MWArt" + Sanitize(staticId) + (type == ArtObject.TypeEnum.MagicCasting ? "Cast" : ""),
            ObjectBounds = new ObjectBounds(),
            Model = new Model { File = rel },
            Type = type,
        };
        mod.ArtObjects.Add(art);
        return arts[key] = art;
    }

    /// <summary>A Morrowind sound (SOUN: FNAM under Data Files\Sound) as a Skyrim sound descriptor playing its file,
    /// set up like Skyrim's conjuration release sound, or like the vanilla descriptor <paramref name="template"/> (a
    /// weather loop, thunder) when given; those get the template's name part as a suffix.</summary>
    SoundDescriptor? Sound(string soundId, string template = "MAGConjureFireSD")
    {
        string key = template == "MAGConjureFireSD" ? soundId : soundId + "|" + template;
        if (sounds.TryGetValue(key, out var hit)) return hit;
        sounds[key] = null;
        if (!world.Objects.TryGetValue(soundId, out var rec) || rec.Type != "SOUN" || rec.Str("FNAM") is not { Length: > 0 } file) return null;
        var bytes = data.Read(@"sound\" + file);
        if (bytes is null) { Problems.Add($"sound {soundId}: {file} not found"); return null; }
        string rel = Path.Combine("fx", "TamrielMerge", "MW", file.Replace('/', '\\'));
        string target = Path.Combine(modOutDir, "Sound", rel);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, bytes);
        var like = SkyrimEsm.SoundDescriptors.First(s => s.EditorID == template);
        var sound = mod.SoundDescriptors.DuplicateInAsNewRecord(like);
        sound.EditorID = "MWSound" + Sanitize(soundId) + (key == soundId ? "" : Sanitize(template.Replace("AMBWeather", "")));
        sound.SoundFiles.Clear();
        sound.SoundFiles.Add(new Mutagen.Bethesda.Plugins.Assets.AssetLink<Mutagen.Bethesda.Skyrim.Assets.SkyrimSoundAssetType>(rel));
        return sounds[key] = sound;
    }

    // ---- Intervention ----

    readonly List<(PlacedObject Marker, bool Divine, string Cell)> interventionMarkers = [];

    /// <summary>Morrowind's intervention spots (DivineMarker: Imperial Cult; TempleMarker: Tribunal Temple) as
    /// persistent markers in the given cell; true when the reference was one.</summary>
    bool InterventionMarker(Tes3Ref r, Cell persistentCell, string cellName)
    {
        bool divine = r.BaseId.Equals("DivineMarker", StringComparison.OrdinalIgnoreCase);
        if (!divine && !r.BaseId.Equals("TempleMarker", StringComparison.OrdinalIgnoreCase)) return false;
        var marker = new PlacedObject(mod)
        {
            Base = new FormLinkNullable<IPlaceableObjectGetter>(XMarkerHeading),
            Placement = new Placement { Position = new P3Float(r.Position.X, r.Position.Y, r.Position.Z), Rotation = new P3Float(0, 0, r.Rotation.Z) },
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
        };
        persistentCell.Persistent.Add(marker);
        (divine ? Magic.DivineMarkers : Magic.AlmsiviMarkers).Items.Add(marker.ToLink<ISkyrimMajorRecordGetter>());
        interventionMarkers.Add((marker, divine, cellName));
        return true;
    }

    /// <summary>
    /// After the doors are linked: each interior's WorldSpot marker, linked to the outside door that leads (through
    /// other interiors if need be) to it, so Intervention cast inside knows where in the world the caster is; the
    /// Mark/Recall anchor placed in the starting cell.
    /// </summary>
    bool magicFinished;

    void FinishMagic()
    {
        // Once: it places records (the anchor, a WorldSpot in each interior).
        if (magic is null || magicFinished) return;
        magicFinished = true;
        if (magic.RecallAnchor is { } anchor && interiorsByName.Values.FirstOrDefault() is { } anyCell)
        {
            var home = Start is { } st && interiorsByName.TryGetValue(st.Cell, out var startCell) ? startCell : anyCell;
            home.Persistent.Add(anchor);
        }
        // Breadth-first from the outside: an interior's way out is the first outside door found leading into it.
        var outside = new Dictionary<string, PlacedObject>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        foreach (var (r, placed, cell) in doors)
            if (cell == Exterior && r.DoorDestinationCell is { } into && !outside.ContainsKey(into)) { outside[into] = placed; queue.Enqueue(into); }
        var byCell = doors.GroupBy(d => d.Cell, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            string here = queue.Dequeue();
            foreach (var (r, _, _) in byCell.GetValueOrDefault(here) ?? [])
                if (r.DoorDestinationCell is { } next && !outside.ContainsKey(next)) { outside[next] = outside[here]; queue.Enqueue(next); }
        }
        foreach (var (name, cell) in interiorsByName)
        {
            if (!outside.TryGetValue(name, out var door)) continue;
            cell.Persistent.Add(new PlacedObject(mod)
            {
                Base = new FormLinkNullable<IPlaceableObjectGetter>(magic.WorldSpot.FormKey),
                Placement = new Placement { Position = new P3Float(0, 0, 0), Rotation = new P3Float(0, 0, 0) },
                SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
                LinkedReferences = [new LinkedReferences { Reference = door.ToLink<IPlacedGetter>() }],
            });
        }
    }

    /// <summary>Converts every spell, enchantment and scroll (magic-test: no cells or items needed).</summary>
    public void ConvertAllMagic()
    {
        // Enchantments and scrolls directly, without building the items (their meshes and canon look-ups are the
        // costly part and not what this checks).
        foreach (var rec in world.Objects.Values.Where(o => o.Type == "SPEL").ToList()) MwSpell(rec.Id);
        foreach (var rec in world.Objects.Values.Where(o => o.Type == "ENCH").ToList())
            if (MwEnchantment(rec.Id) is { Type: 0 }) Magic.Scroll("MWScroll" + Sanitize(rec.Id), rec.Id, EffectsOf(rec), null, 0, 0);
    }
}