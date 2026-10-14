using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Skyrim;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's magic through <see cref="TesMagic"/>: spells, abilities, diseases, powers and lesser powers, enchantments
/// on weapons (on strike), staffs (cast), apparel (constant), scrolls, and actors' spell lists. Summons and bound
/// weapons take the creature or weapon their magic effect names. Scripted effects (SEFF) aren't carried over.
/// </summary>
public sealed partial class ObPluginBuilder
{
    TesMagic? magic;
    readonly Dictionary<FormKey, Spell?> obSpells = new();
    readonly Dictionary<FormKey, ObjectEffect?> obEnchantments = new();
    Dictionary<string, Ob.IMagicEffectGetter>? magicEffectsByCode;

    TesMagic Magic => magic ??= new TesMagic(mod, SkyrimEsm, "OB");

    public int SpellCount => magic?.SpellCount ?? 0;
    public int EnchantmentCount => magic?.EnchantmentCount ?? 0;
    public IEnumerable<string> SkippedEffects => magic?.Skipped.Select(kv => $"{kv.Key} x{kv.Value}") ?? [];

    /// <summary>The Skyrim actor a summon calls or the weapon a bound effect gives (the magic effect's associated item).</summary>
    FormKey? Associated(string code)
    {
        magicEffectsByCode ??= records.Values.OfType<Ob.IMagicEffectGetter>().Where(m => m.EditorID is not null)
            .GroupBy(m => m.EditorID!).ToDictionary(g => g.Key, g => g.Last());
        // MGEF DATA's associated item (after the base cost) is what Mutagen calls Unused: a FormID in Oblivion.esm,
        // where every magic effect lives.
        if (!magicEffectsByCode.TryGetValue(code, out var mgef) || mgef.Data is not { Unused: var raw and not 0 }) return null;
        var link = new FormKey(mgef.FormKey.ModKey, (uint)raw & 0xFFFFFF);
        if (code.StartsWith('Z')) return records.GetValueOrDefault(link) is Ob.ICreatureGetter or Ob.INpcGetter && Actor(link) is Npc npc ? npc.FormKey : null;
        if (records.GetValueOrDefault(link) is not Ob.IWeaponGetter || Item(link) is not Weapon weapon) return null;
        if (weapon.Data is { } wd) wd.Flags |= WeaponData.Flag.BoundWeapon;
        return weapon.FormKey;
    }

    IEnumerable<TesEffect> EffectsOf(IEnumerable<Ob.IEffectGetter> source)
    {
        foreach (var e in source)
        {
            if (e.Data is not { } d) continue;
            string code = d.MagicEffect.EDID.Type;
            if (ScriptEffectOf(e) is { } scripted) { yield return scripted; continue; }
            if (TesMagic.OblivionName(code) is not { } name) { yield return new TesEffect(code, null, TesRange.Self, 0, 0, 0); continue; }
            var range = d.Type.ToString() switch { "Touch" => TesRange.Touch, "Target" => TesRange.Target, _ => TesRange.Self };
            string sub = d.ActorValue.ToString();
            yield return new TesEffect(name, sub, range, (int)d.Area, (int)d.Duration, d.Magnitude,
                name is "Summon" or "BoundWeapon" ? Associated(code) : null);
        }
    }

    /// <summary>A scripted effect (SEFF) whose script was translated: the effect running it.</summary>
    TesEffect? ScriptEffectOf(Ob.IEffectGetter e)
    {
        if (e.Data is not { } d || d.MagicEffect.EDID.Type != "SEFF" || e.ScriptEffect?.Data?.Script.FormKeyNullable is not { } script
            || !translations.TryGetValue(script, out var o) || o.Kind != "MagicEffect") return null;
        var range = d.Type.ToString() switch { "Touch" => TesRange.Touch, "Target" => TesRange.Target, _ => TesRange.Self };
        bool hostile = e.ScriptEffect.Data.Flags.ToString().Contains("Hostile");
        return new TesEffect("ScriptEffect", o.ScriptName, range, (int)d.Area, (int)d.Duration, d.Magnitude, Label: e.ScriptEffect.Name, Hostile: hostile);
    }

    /// <summary>An Oblivion spell, ability, disease or (lesser) power.</summary>
    Spell? ObSpell(FormKey key)
    {
        if (obSpells.TryGetValue(key, out var hit)) return hit;
        obSpells[key] = null;
        if (records.GetValueOrDefault(key) is not Ob.ISpellGetter s) return null;
        var cast = s.Data?.Type.ToString() switch
        {
            "Ability" => TesCast.Ability, "Disease" => TesCast.Disease, "Power" => TesCast.Power, "LesserPower" => TesCast.LesserPower,
            _ => TesCast.Spell,
        };
        string edid = "OBSpell" + Sanitize(s.EditorID ?? key.ID.ToString("X6"));
        return obSpells[key] = Magic.Spell(edid, s.Name, cast, EffectsOf(s.Effects), (int)(s.Data?.Cost ?? 0));
    }

    /// <summary>An enchantment: on strike (weapons), cast (staffs), constant (apparel); scrolls are made separately.</summary>
    ObjectEffect? ObEnchantment(FormKey key)
    {
        if (obEnchantments.TryGetValue(key, out var hit)) return hit;
        obEnchantments[key] = null;
        if (records.GetValueOrDefault(key) is not Ob.IEnchantmentGetter e || e.Data is not { } d) return null;
        var cast = d.Type.ToString() switch { "Weapon" => TesCast.WhenStrikes, "Staff" => TesCast.Staff, "Apparel" => TesCast.Constant, _ => TesCast.CastOnce };
        if (cast == TesCast.CastOnce) return null;
        return obEnchantments[key] = Magic.Enchantment("OBEnch" + Sanitize(e.EditorID ?? key.ID.ToString("X6")), e.Name, cast, EffectsOf(e.Effects), (int)d.EnchantCost);
    }

    /// <summary>Puts an Oblivion item's enchantment on its Skyrim record.</summary>
    void Enchant(Ob.IOblivionMajorRecordGetter rec, IMajorRecord? item)
    {
        var (link, charge) = rec switch
        {
            Ob.IWeaponGetter w => (w.Enchantment.FormKeyNullable, (int)(w.EnchantmentPoints ?? 0)),
            Ob.IArmorGetter a => (a.Enchantment.FormKeyNullable, 0),
            Ob.IClothingGetter c => (c.Enchantment.FormKeyNullable, 0),
            _ => ((FormKey?)null, 0),
        };
        if (link is not { } key || item is null || ObEnchantment(key) is not { } ench) return;
        switch (item)
        {
            case Weapon w:
                w.ObjectEffect.SetTo(ench);
                w.EnchantmentAmount = (ushort)Math.Clamp(charge > 0 ? charge : 500, 1, ushort.MaxValue);
                break;
            case Armor a when ench.CastType == CastType.ConstantEffect:
                a.ObjectEffect.SetTo(ench);
                break;
        }
    }

    /// <summary>A scroll: a book carrying a scroll enchantment.</summary>
    IMajorRecord? ScrollItem(string edid, Ob.IBookGetter b)
    {
        if (b.Enchantment.FormKeyNullable is not { } key || records.GetValueOrDefault(key) is not Ob.IEnchantmentGetter e || e.Data?.Type.ToString() != "Scroll") return null;
        var mesh = ItemMesh(b.Model?.File, b.Data?.Weight ?? 0);
        return Magic.Scroll(edid, b.Name, EffectsOf(e.Effects), mesh is null ? null : SkyrimModelPath(mesh.Path), b.Data?.Weight ?? 0, (uint)Math.Max(0, b.Data?.Value ?? 0));
    }

    /// <summary>An actor's spells, abilities and powers.</summary>
    void AddSpells(Npc npc, IEnumerable<IFormLinkGetter<Ob.ISpellRecordGetter>> source)
    {
        foreach (var link in source)
            if (ObSpell(link.FormKey) is { } spell)
            {
                npc.ActorEffect ??= [];
                npc.ActorEffect.Add(spell.ToLink<ISpellRecordGetter>());
            }
    }

    /// <summary>Every spell of the active plugins (for spell merchants and the player's start).</summary>
    void FinishMagic()
    {
        foreach (var s in records.Values.OfType<Ob.ISpellGetter>().ToList()) ObSpell(s.FormKey);
    }

    /// <summary>Converts every spell, enchantment and scroll (magic-test: no cells or items needed).</summary>
    public void ConvertAllMagic()
    {
        FinishMagic();
        foreach (var e in records.Values.OfType<Ob.IEnchantmentGetter>().ToList())
            if (e.Data?.Type.ToString() == "Scroll") Magic.Scroll("OBScroll" + Sanitize(e.EditorID ?? e.FormKey.ID.ToString("X6")), e.Name, EffectsOf(e.Effects), null, 0, 0);
            else ObEnchantment(e.FormKey);
    }
}