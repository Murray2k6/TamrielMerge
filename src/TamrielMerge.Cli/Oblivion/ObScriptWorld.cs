using System.Reflection;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// The names Oblivion scripts refer to, from a load order of plugins: each EditorID's Papyrus type (what the converted
/// record is in Skyrim) and the script attached to it (for quest.variable and ref.variable), plus every script's type
/// and whether it runs on people or creatures.
/// </summary>
public sealed class ObScriptWorld : ObPapyrus.IWorld
{
    readonly Dictionary<string, ObPapyrus.FormInfo> forms = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<FormKey, string> scriptIds = [];
    readonly Dictionary<FormKey, FormKey> scriptOf = [];   // record → its script
    readonly Dictionary<FormKey, (string EditorID, string Type, FormKey? Base)> pending = [];
    readonly HashSet<FormKey> actorScripts = [];

    /// <summary>Every script by its FormKey, last plugin winning.</summary>
    public Dictionary<FormKey, Ob.IScriptGetter> Scripts { get; } = [];
    public Dictionary<FormKey, string> ScriptPlugin { get; } = [];
    public HashSet<FormKey> QuestScripts { get; } = [];

    public ObScriptWorld(IEnumerable<Ob.IOblivionModGetter> loadOrder)
    {
        foreach (var mod in loadOrder)
        {
            foreach (var sc in mod.Scripts)
            {
                Scripts[sc.FormKey] = sc;
                ScriptPlugin[sc.FormKey] = mod.ModKey.FileName;
                if (sc.EditorID is { } id) scriptIds[sc.FormKey] = id;
            }
            foreach (var r in mod.EnumerateMajorRecords())
            {
                if (r is Ob.IScriptGetter) continue;
                if (ScriptLink(r) is { } script) scriptOf[r.FormKey] = script;
                if (r is Ob.INpcGetter or Ob.ICreatureGetter && ScriptLink(r) is { } s2) actorScripts.Add(s2);
                if (r is Ob.IQuestGetter q && q.Script.FormKeyNullable is { } qs)
                {
                    QuestScripts.Add(qs);
                }
                if (r.EditorID is not { Length: > 0 } edid) continue;
                FormKey? baseKey = r switch
                {
                    Ob.IPlacedObjectGetter p => p.Base.FormKeyNullable,
                    Ob.IPlacedNpcGetter p => p.Base.FormKeyNullable,
                    Ob.IPlacedCreatureGetter p => p.Base.FormKeyNullable,
                    _ => null,
                };
                pending[r.FormKey] = (edid, PapyrusType(r), baseKey);
            }
        }
        // Each quest as the last plugin has it.
        var quests = new Dictionary<FormKey, Ob.IQuestGetter>();
        foreach (var mod in loadOrder) foreach (var q in mod.Quests) quests[q.FormKey] = q;
        foreach (var q in quests.Values)
                if (q.EditorID is { } qid && q.Script.FormKeyNullable is { } qs && scriptIds.TryGetValue(qs, out var sid))
                {
                    if (!questsByScript.TryGetValue(sid, out var list)) questsByScript[sid] = list = [];
                    if (!list.Contains(qid, StringComparer.OrdinalIgnoreCase)) list.Add(qid);
                }
        foreach (var (key, (edid, type, baseKey)) in pending)
        {
            var scriptKey = scriptOf.TryGetValue(baseKey ?? key, out var s) ? s : (FormKey?)null;
            string? scriptName = scriptKey is { } sk && scriptIds.TryGetValue(sk, out var sid) ? ((ObPapyrus.IWorld)this).ScriptName(sid) : null;
            forms[edid] = new ObPapyrus.FormInfo(edid, type, scriptName);
            keys[edid] = key;
        }
        pending.Clear();
    }

    readonly Dictionary<string, FormKey> keys = new(StringComparer.OrdinalIgnoreCase);

    public ObPapyrus.FormInfo? Find(string editorId) => forms.GetValueOrDefault(editorId);
    /// <summary>The record (or placed reference) with this EditorID.</summary>
    public FormKey? KeyOf(string editorId) => keys.TryGetValue(editorId, out var k) ? k : null;

    Dictionary<string, Dictionary<string, string>>? variables;
    static readonly System.Text.RegularExpressions.Regex declaration = new(@"^\s*(short|long|int|float|ref|reference|string_var|array_var)\s+(\w+)",
        System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public IReadOnlyDictionary<string, string>? Variables(string papyrusScriptName)
    {
        if (variables is null)
        {
            variables = new(StringComparer.OrdinalIgnoreCase);
            foreach (var sc in Scripts.Values)
            {
                if (sc.EditorID is not { } id || sc.Fields.SourceCode is not { } src) continue;
                var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (System.Text.RegularExpressions.Match m in declaration.Matches(src)) vars.TryAdd(m.Groups[2].Value, m.Groups[1].Value);
                variables[((ObPapyrus.IWorld)this).ScriptName(id)] = vars;
            }
        }
        return variables.GetValueOrDefault(papyrusScriptName);
    }
    public bool OnActor(FormKey script) => actorScripts.Contains(script);

    readonly Dictionary<string, List<string>> questsByScript = new(StringComparer.OrdinalIgnoreCase);

    public string? QuestOf(string scriptEditorId) =>
        questsByScript.TryGetValue(scriptEditorId, out var quests) && quests.Count == 1 ? quests[0] : null;

    static readonly Dictionary<Type, PropertyInfo?> scriptProperty = [];

    /// <summary>The script a record carries (activators, containers, doors, items, NPCs, creatures, quests …).</summary>
    public static FormKey? ScriptLink(IMajorRecordGetter r)
    {
        var type = r.GetType();
        PropertyInfo? prop;
        lock (scriptProperty)
            if (!scriptProperty.TryGetValue(type, out prop))
                // Binary overlays may implement it only through their getter interface.
                scriptProperty[type] = prop = new[] { type }.Concat(type.GetInterfaces())
                    .Select(t => t.GetProperty("Script"))
                    .FirstOrDefault(p => p is not null && typeof(IFormLinkGetter).IsAssignableFrom(p.PropertyType));
        return prop?.GetValue(r) is IFormLinkGetter link && !link.IsNull ? link.FormKey : null;
    }

    /// <summary>The Papyrus type of an Oblivion record once converted.</summary>
    static string PapyrusType(IMajorRecordGetter r) => r switch
    {
        Ob.IQuestGetter => "Quest",
        Ob.IGlobalGetter => "GlobalVariable",
        Ob.IPlacedNpcGetter or Ob.IPlacedCreatureGetter => "Actor",
        Ob.IPlacedObjectGetter => "ObjectReference",
        Ob.INpcGetter or Ob.ICreatureGetter => "ActorBase",
        Ob.ISpellGetter => "Spell",
        Ob.IFactionGetter => "Faction",
        Ob.ISoundGetter => "Sound",
        Ob.ICellGetter => "Cell",
        Ob.IDialogTopicGetter => "Topic",
        Ob.IAIPackageGetter => "Package",
        Ob.IEffectShaderGetter => "EffectShader",
        Ob.IRaceGetter => "Race",
        Ob.IClassGetter => "Class",
        Ob.ICombatStyleGetter => "CombatStyle",
        Ob.IWorldspaceGetter => "WorldSpace",
        Ob.IWeatherGetter => "Weather",
        Ob.IMagicEffectGetter => "MagicEffect",
        Ob.IEnchantmentGetter => "Enchantment",
        Ob.IKeyGetter => "Key",
        Ob.IBookGetter => "Book",
        Ob.IWeaponGetter => "Weapon",
        Ob.IArmorGetter or Ob.IClothingGetter => "Armor",
        Ob.IPotionGetter => "Potion",
        Ob.IIngredientGetter => "Ingredient",
        Ob.IAmmunitionGetter => "Ammo",
        Ob.ISoulGemGetter => "SoulGem",
        Ob.IMiscellaneousGetter or Ob.ISigilStoneGetter => "MiscObject",
        Ob.ILeveledItemGetter => "LeveledItem",
        Ob.ILeveledCreatureGetter => "LeveledActor",
        Ob.ILeveledSpellGetter => "LeveledSpell",
        Ob.IActivatorGetter => "Activator",
        Ob.IContainerGetter => "Container",
        Ob.IDoorGetter => "Door",
        Ob.ILightGetter => "Light",
        Ob.IFurnitureGetter => "Furniture",
        Ob.IStaticGetter => "Static",
        _ => "Form",
    };
}
