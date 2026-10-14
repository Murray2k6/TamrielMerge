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
        loadOrder = loadOrder.ToList();
        loadOrderKeys = loadOrder.Select(m => m.ModKey).ToList();
        foreach (var mod in loadOrder)
            foreach (var e in mod.Enchantments)
                if (e.Data?.Type.ToString() == "Scroll") scrollEnchantments.Add(e.FormKey);
        // The forms compiled scripts refer to (SCRO), for names a script uses that no record has as its EditorID.
        var referenced = new HashSet<FormKey>();
        foreach (var mod in loadOrder)
            foreach (var sc in mod.Scripts)
                foreach (var r in sc.Fields.References.OfType<Ob.IScriptObjectReferenceGetter>())
                    if (!r.Reference.IsNull) referenced.Add(r.Reference.FormKey);
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
                if (ScriptLink(r) is { } script)
                {
                    scriptOf[r.FormKey] = script;
                    Carriers.TryAdd(script, []);
                    Carriers[script].Add((r.GetType().Name.Replace("BinaryOverlay", ""), r.EditorID ?? r.FormKey.ToString()));
                }
                // Scripted effects (SEFF) of spells, enchantments, potions and ingredients run a magic effect script.
                foreach (var se in ScriptEffects(r))
                {
                    Carriers.TryAdd(se, []);
                    Carriers[se].Add(("Effect of " + r.GetType().Name.Replace("BinaryOverlay", ""), r.EditorID ?? r.FormKey.ToString()));
                }
                if (r is Ob.INpcGetter or Ob.ICreatureGetter && ScriptLink(r) is { } s2) actorScripts.Add(s2);
                if (r is Ob.IQuestGetter q && q.Script.FormKeyNullable is { } qs)
                {
                    QuestScripts.Add(qs);
                }
                FormKey? baseKey = r switch
                {
                    Ob.IPlacedObjectGetter p => p.Base.FormKeyNullable,
                    Ob.IPlacedNpcGetter p => p.Base.FormKeyNullable,
                    Ob.IPlacedCreatureGetter p => p.Base.FormKeyNullable,
                    _ => null,
                };
                // How many references each base has (a base with one stands for it: BaseID.variable).
                if (baseKey is { } bk)
                {
                    var (n, first) = refsOfBase.GetValueOrDefault(bk);
                    refsOfBase[bk] = (n + 1, n == 0 ? r.FormKey : first);
                    if (n == 0) baseOfRef[r.FormKey] = (bk, PapyrusType(r), r.EditorID);
                }
                if (r.EditorID is not { Length: > 0 } edid)
                {
                    if (referenced.Contains(r.FormKey)) unnamed[r.FormKey] = (PapyrusType(r), baseKey);
                    continue;
                }
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
            forms[edid] = new ObPapyrus.FormInfo(edid, type, ScriptNameOf(baseKey ?? key));
            keys[edid] = key;
        }
        pending.Clear();
        foreach (var mod in loadOrder)
            foreach (var b in mod.Birthsigns)
                if (b.EditorID is { } bid && !Birthsigns.Contains(bid, StringComparer.OrdinalIgnoreCase)) Birthsigns.Add(bid);
        foreach (var mod in loadOrder)
            foreach (var m in mod.MagicEffects)
                if (m.EditorID is { Length: 4 } code) MagicEffects[code] = m;
        RetypeNumericRefs();
    }

    string? ScriptNameOf(FormKey record) =>
        scriptOf.TryGetValue(record, out var s) && scriptIds.TryGetValue(s, out var sid) ? ((ObPapyrus.IWorld)this).ScriptName(sid) : null;

    readonly List<ModKey> loadOrderKeys;
    readonly Dictionary<FormKey, (int Count, FormKey First)> refsOfBase = [];
    readonly Dictionary<FormKey, (FormKey Base, string Type, string? EditorID)> baseOfRef = [];
    readonly Dictionary<FormKey, (string Type, FormKey? Base)> unnamed = [];

    /// <summary>The birthsigns, in the order TMObState numbers them.</summary>
    public List<string> Birthsigns { get; } = [];
    public int BirthsignIndex(string editorId) => Birthsigns.FindIndex(b => b.Equals(editorId, StringComparison.OrdinalIgnoreCase));
    /// <summary>Oblivion's magic effects by their four-letter code.</summary>
    public Dictionary<string, Ob.IMagicEffectGetter> MagicEffects { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A form known by a made-up name (@ref:FormKey) when it has no EditorID of its own.</summary>
    ObPapyrus.FormInfo Named(FormKey key, string type, FormKey? baseKey)
    {
        string id = "@ref:" + key;
        if (!forms.ContainsKey(id))
        {
            forms[id] = new ObPapyrus.FormInfo(id, type, ScriptNameOf(baseKey ?? key));
            keys[id] = key;
        }
        return forms[id];
    }

    /// <summary>The single reference of a base object, which Oblivion scripts can name by the base's EditorID.</summary>
    public ObPapyrus.FormInfo? UniqueRef(string baseEditorId)
    {
        if (KeyOf(baseEditorId) is not { } bk || refsOfBase.GetValueOrDefault(bk) is not (1, var r) || !baseOfRef.TryGetValue(r, out var info)) return null;
        return info.EditorID is { Length: > 0 } e && forms.TryGetValue(e, out var f) ? f : Named(r, info.Type, info.Base);
    }

    readonly Dictionary<string, Dictionary<string, FormKey>?> scriptNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A name that isn't an EditorID, through the forms the compiled script refers to: the references of the compiled
    /// script that none of its other names account for, taken in order against its unknown names (in the order they
    /// first appear), when there are as many of each.
    /// </summary>
    public ObPapyrus.FormInfo? FindInScript(string scriptEditorId, string name)
    {
        if (!scriptNames.TryGetValue(scriptEditorId, out var map))
        {
            map = null;
            var sc = Scripts.Values.FirstOrDefault(x => string.Equals(x.EditorID, scriptEditorId, StringComparison.OrdinalIgnoreCase));
            if (sc?.Fields.SourceCode is { } src)
            {
                var parsed = ObScript.Parse(src);
                var vars = parsed.Variables.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                // The names in the code (not in comments or strings, not variables of other scripts after a dot).
                string code = string.Join("\n", src.Split('\n').Select(l => System.Text.RegularExpressions.Regex.Replace(l, "\"[^\"]*\"", " ").Split(';')[0]));
                var blockTypes = parsed.Blocks.Select(b => b.Type).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var words = System.Text.RegularExpressions.Regex.Matches(code, "(?<![.A-Za-z0-9_])[A-Za-z_][A-Za-z0-9_]*")
                    .Select(m => m.Value).Where(w => !blockTypes.Contains(w) && !w.Equals(parsed.Name, StringComparison.OrdinalIgnoreCase) && !w.Equals(scriptEditorId, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var known = words.Where(w => forms.ContainsKey(w)).Select(w => keys[w]).ToHashSet();
                var unknown = words.Where(w => !forms.ContainsKey(w) && !vars.Contains(w) && !NotNames.Contains(w) && Functions?[w] is null && !w.All(char.IsDigit)).ToList();
                var free = sc.Fields.References.OfType<Ob.IScriptObjectReferenceGetter>().Where(r => !r.Reference.IsNull).Select(r => r.Reference.FormKey)
                    .Where(k => !known.Contains(k) && k.ID != 0x14 && k.ID != 0x7).Distinct().ToList();
                if (unknown.Count == free.Count && free.Count > 0)
                {
                    map = new(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < free.Count; i++) map[unknown[i]] = free[i];
                }
            }
            scriptNames[scriptEditorId] = map;
        }
        if (map is null || !map.TryGetValue(name, out var key)) return null;
        if (unnamed.TryGetValue(key, out var u)) return Named(key, u.Type, u.Base);
        if (baseOfRef.TryGetValue(key, out var b)) return b.EditorID is { Length: > 0 } e && forms.TryGetValue(e, out var f) ? f : Named(key, b.Type, b.Base);
        if (forms.Values.FirstOrDefault(f => keys.TryGetValue(f.EditorID, out var k) && k == key) is { } byKey) return byKey;
        // A record no script property can hold (a script itself): Oblivion reads it as its FormID.
        int index = loadOrderKeys.IndexOf(key.ModKey);
        return new ObPapyrus.FormInfo(((uint)Math.Max(0, index) << 24 | key.ID).ToString(System.Globalization.CultureInfo.InvariantCulture), "FormID", null);
    }

    /// <summary>The function table, for telling functions from names (set by whoever translates).</summary>
    public ObFunctions? Functions { get; set; }
    static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "scn", "scriptname", "begin", "end", "if", "elseif", "else", "endif", "set", "to", "let", "while", "loop", "foreach", "return",
        "short", "long", "int", "float", "ref", "reference", "string_var", "array_var", "player", "playerref", "x", "y", "z", "eval",
        "break", "continue", "call",
    };

    /// <summary>
    /// Ref variables that only ever hold numbers (set to a number other than 0, compared with one, never used as a
    /// reference): Oblivion lets a ref variable hold a number, and the scripts that do mean a number.
    /// </summary>
    void RetypeNumericRefs()
    {
        var numeric = new HashSet<(string Script, string Var)>();
        var asRef = new HashSet<(string Script, string Var)>();
        foreach (var sc in Scripts.Values)
        {
            if (sc.EditorID is not { } id || sc.Fields.SourceCode is not { } src) continue;
            ObScript.Script parsed;
            try { parsed = ObScript.Parse(src); } catch { continue; }
            string me = ((ObPapyrus.IWorld)this).ScriptName(id);
            var locals = parsed.Variables.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            (string, string)? Owner(ObScript.Expr e) => e switch
            {
                ObScript.Name { Target: null } n when locals.Contains(n.Id) => (me, n.Id),
                ObScript.Name { Target: ObScript.Name { Target: null } o } n when Find(o.Id) is { ScriptName: { } os } => (os, n.Id),
                _ => null,
            };
            static bool NonZero(ObScript.Expr e) => e is ObScript.Num { Value: not 0 } || e is ObScript.Unary { Op: "-", Operand: ObScript.Num { Value: not 0 } };
            void Walk(ObScript.Expr? e)
            {
                switch (e)
                {
                    case ObScript.Binary b:
                        if (b.Op is "==" or "!=" or "<" or "<=" or ">" or ">=")
                        {
                            if (Owner(b.Left) is { } l && NonZero(b.Right)) numeric.Add(l);
                            if (Owner(b.Right) is { } r && NonZero(b.Left)) numeric.Add(r);
                        }
                        Walk(b.Left); Walk(b.Right);
                        break;
                    case ObScript.Call c:
                        if (c.Target is ObScript.Name t && Owner(t) is { } tv) asRef.Add(tv);
                        foreach (var a in c.Args) Walk(a);
                        break;
                    case ObScript.Name { Target: ObScript.Name t2 } when Owner(t2) is { } tv2:
                        asRef.Add(tv2);
                        break;
                    case ObScript.Unary u: Walk(u.Operand); break;
                }
            }
            foreach (var st in parsed.AllStatements())
                switch (st)
                {
                    case ObScript.Set set:
                        if (Owner(set.Target) is { } v)
                        {
                            if (NonZero(set.Value)) numeric.Add(v);
                            else if (set.Value is ObScript.Call or ObScript.Name) asRef.Add(v);
                        }
                        Walk(set.Value);
                        break;
                    case ObScript.If i: foreach (var br in i.Branches) Walk(br.Condition); break;
                    case ObScript.While w: Walk(w.Condition); break;
                    case ObScript.CallStmt cs: Walk(cs.Call); break;
                }
        }
        Variables("");
        foreach (var (script, v) in numeric.Where(n => !asRef.Contains(n)))
            if (variables!.TryGetValue(script, out var vars) && vars.TryGetValue(v, out var t) && t.ToLowerInvariant() is "ref" or "reference")
                vars[v] = "float";
    }

    readonly Dictionary<string, FormKey> keys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What carries each script: (record type, EditorID) of the records naming it, and of the spells,
    /// enchantments, potions and ingredients whose scripted effects run it.</summary>
    public Dictionary<FormKey, List<(string Type, string EditorID)>> Carriers { get; } = [];

    /// <summary>The scripts a record's scripted effects (SEFF) run.</summary>
    public static IEnumerable<FormKey> ScriptEffects(IMajorRecordGetter r)
    {
        IEnumerable<Ob.IEffectGetter> effects = r switch
        {
            Ob.ISpellGetter s => s.Effects,
            Ob.IEnchantmentGetter e => e.Effects,
            Ob.IPotionGetter p => p.Effects,
            Ob.IIngredientGetter i => i.Effects,
            _ => [],
        };
        foreach (var e in effects)
            if (e.ScriptEffect?.Data?.Script.FormKeyNullable is { } k && !k.IsNull) yield return k;
    }

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

    readonly HashSet<FormKey> scrollEnchantments = [];

    /// <summary>The Papyrus type of an Oblivion record once converted.</summary>
    string PapyrusType(IMajorRecordGetter r) => r switch
    {
        // What the converter makes of them: scrolls are Skyrim scrolls, leveled creatures actors of their own,
        // potions and soul gems with object scripts misc items (which carry scripts), apparatus misc clutter.
        Ob.IBookGetter b when b.Enchantment.FormKeyNullable is { } be && scrollEnchantments.Contains(be) => "Scroll",
        Ob.ILeveledCreatureGetter => "ActorBase",
        Ob.IPotionGetter or Ob.ISoulGemGetter when ScriptLink(r) is not null => "MiscObject",
        Ob.IAlchemicalApparatusGetter => "MiscObject",
        // Base objects whose Skyrim kind depends on what they carry (activator, flora, static): any form.
        Ob.IActivatorGetter or Ob.IFurnitureGetter or Ob.IStaticGetter or Ob.IFloraGetter or Ob.IGrassGetter or Ob.ITreeGetter => "Form",
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
        Ob.ILeveledSpellGetter => "LeveledSpell",
        Ob.IContainerGetter => "Container",
        Ob.IDoorGetter => "Door",
        Ob.ILightGetter => "Light",
        _ => "Form",
    };
}
