using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Redguard;

/// <summary>
/// Redguard's items (ITEM.INI: type[x], name[x] (RTX tag), description[x], use_script[x], game_object_file[x],
/// inventory_object_file[x], hand_object_file[x], player_max[x], player_total[x], ...) as Skyrim records. A script names
/// an item by its index (AddItem(67, 1), HaveItem(9), ActiveItem() = 8), so every item gets a record, in index order in
/// the FormList RGItems the converted scripts read:
/// <list type="bullet">
/// <item>swords (type 1) are weapons on vanilla templates (Cyrus's sabre a steel sword), keeping the worn look;</item>
/// <item>gold is Skyrim's gold, the torch Skyrim's torch;</item>
/// <item>keys are keys; books, journals, maps and letters are books whose text is the item's description;</item>
/// <item>items Redguard lets the player use (a use_script other than the generic "I can't use this by itself": potions,
/// Hist sap, spider's milk, ectoplasm, orc's blood, the shovel, the flag, the flask, water, bandages) are potions whose
/// one effect (TMRgUseItem) starts the item's converted use script, as Redguard ran it in a shell when used;</item>
/// <item>everything else is a misc item.</item>
/// </list>
/// Models are Redguard's own (the game model, else the inventory model shrunk to hand size), with a loose item's box
/// collision; names are the RTX names in title case.
/// </summary>
public sealed partial class RgPluginBuilder
{
    public sealed record ItemDef(int Id, Dictionary<string, string> Fields)
    {
        public string Get(string key) => Fields.GetValueOrDefault(key, "").Trim();
        public int Int(string key) => int.TryParse(Get(key), out var v) ? v : 0;
        public int Type => Int("type");
        public string UseScript => Get("use_script").ToUpperInvariant();
        public string GameModel => Get("game_object_file");
        public string InventoryModel => Get("inventory_object_file");
    }

    /// <summary>Redguard's items by index, from ITEM.INI.</summary>
    public static List<ItemDef> LoadItemDefs(string rgDir)
    {
        var byId = new SortedDictionary<int, Dictionary<string, string>>();
        string path = Path.Combine(rgDir, "ITEM.INI");
        if (!File.Exists(path)) return [];
        foreach (var raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            int semi = line.IndexOf(';');
            if (semi >= 0) line = line[..semi].Trim();
            var m = Regex.Match(line, @"^([A-Za-z_]+)\[(\d+)\]\s*=\s*(.*)$");
            if (!m.Success) continue;
            int id = int.Parse(m.Groups[2].Value);
            if (!byId.TryGetValue(id, out var f)) byId[id] = f = new(StringComparer.OrdinalIgnoreCase);
            f[m.Groups[1].Value] = m.Groups[3].Value.Trim();
        }
        return byId.Select(kv => new ItemDef(kv.Key, kv.Value)).ToList();
    }

    /// <summary>Items whose name tag the game shares with another item (copied entries in ITEM.INI): the name they
    /// stand for, from their inventory model.</summary>
    static readonly Dictionary<string, string> NamesByModel = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Isignt.3D"] = "Tavern Sign", ["Isignd.3D"] = "Dwarven Sign", ["Iwanted.3D"] = "Wanted Poster", ["Iplan.3D"] = "Plans",
    };

    /// <summary>Vanilla weapon templates for Redguard's swords, by their hand model.</summary>
    static readonly Dictionary<string, string> WeaponTemplates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CYSWD1"] = "SteelSword", ["GSWORD1A"] = "IronSword", ["GSWORD"] = "IronSword", ["ATORSWRD"] = "EbonySword",
        ["FIRESWOR"] = "SteelSword", ["OGAX0000"] = "IronBattleaxe", ["DRMSWRD1"] = "DwarvenSword",
    };

    public List<ItemDef> ItemDefs { get; private set; } = [];
    /// <summary>Each item's record (Skyrim's gold and torch for those two), by index.</summary>
    readonly Dictionary<int, FormKey> itemRecords = [];
    /// <summary>The use effect of each usable item, by index: its shell activator is filled in once scripts exist.</summary>
    readonly Dictionary<int, MagicEffect> useEffects = [];
    FormList? itemList;
    public int ItemCount => itemRecords.Count;

    static readonly FormKey Gold001 = new(ModKey.FromFileName("Skyrim.esm"), 0x00000F), Torch01 = new(ModKey.FromFileName("Skyrim.esm"), 0x01D4EC),
        ItmPotionUse = new(ModKey.FromFileName("Skyrim.esm"), 0x0B6435);

    string ItemName(ItemDef d)
    {
        if (NamesByModel.TryGetValue(Path.GetFileName(d.InventoryModel), out var fixedName)) return fixedName;
        string tag = d.Get("name");
        string text = RtxEntry(tag) is { } e ? e.Text : $"Item {d.Id}";
        return TitleCase(text);
    }

    string ItemDescription(ItemDef d)
    {
        string tag = d.Get("description");
        return RtxEntry(tag) is { } e ? Pretty(e.Text) : "";
    }

    /// <summary>An RTX entry by tag, in the case ITEM.INI writes it or not ("?xPI" is the RTX's "?xpi").</summary>
    RgRtx.Entry? RtxEntry(string tag)
    {
        if (tag.Length != 4 || rtx is null) return null;
        if (rtx.Entries.TryGetValue(tag, out var e)) return e;
        return rtx.Entries.Values.FirstOrDefault(x => x.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));
    }

    static string TitleCase(string text)
    {
        string lower = text.Trim().ToLowerInvariant();
        string[] small = ["of", "the", "a", "an", "and", "to", "in", "with", "from"];
        var words = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (i > 0 && small.Contains(words[i])) continue;
            // M'Kai, N'Gasta, A'tor: a capital after a one-letter prefix and an apostrophe.
            var parts = words[i].Split('\'');
            words[i] = string.Join("'", parts.Select((p, k) => p.Length == 0 ? p : k == 0 || parts[0].Length == 1 ? char.ToUpperInvariant(p[0]) + p[1..] : p));
        }
        return string.Join(' ', words);
    }

    /// <summary>A Redguard model file (3dart\x.3d, any case) for an item, scaled so its longest side is at most
    /// maxSize (inventory models are shown big).</summary>
    RgMeshConverter.Result? ItemMesh(string file, string name, float maxSize, float mass)
    {
        if (file.Length == 0 || scenes.Count == 0) return null;
        // Item models are in the scenes' archives (ISLAND.ROB first), or loose in 3dart.
        string model3d = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
        var scene = scenes.GetValueOrDefault("ISLAND") ?? scenes.Values.First();
        if (Model(scene, model3d) is not { } model || model.Vertices.Count == 0) { Problems.Add($"item model {file} not found"); return null; }
        var lo = model.Vertices.Aggregate(System.Numerics.Vector3.Min); var hi = model.Vertices.Aggregate(System.Numerics.Vector3.Max);
        var size = hi - lo;
        float longest = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        float scale = longest > maxSize ? maxSize / longest : 1;
        // The model is shared with the scenes: scaled for this conversion only.
        var original = model.Vertices.ToList();
        if (scale != 1) for (int i = 0; i < model.Vertices.Count; i++) model.Vertices[i] *= scale;
        try { return meshes.Convert(model, name, "ISLAND", modOutDir, itemMass: mass); }
        catch (Exception e) { Problems.Add($"item model {file}: {e.Message}"); return null; }
        finally { for (int i = 0; i < original.Count; i++) model.Vertices[i] = original[i]; }
    }

    static ObjectBounds BoundsOf(RgMeshConverter.Result? mesh)
    {
        if (mesh is null) return new ObjectBounds();
        short C(float v) => (short)Math.Clamp(MathF.Round(v), short.MinValue, short.MaxValue);
        return new ObjectBounds { First = new P3Int16(C(mesh.Min.X), C(mesh.Min.Y), C(mesh.Min.Z)), Second = new P3Int16(C(mesh.Max.X), C(mesh.Max.Y), C(mesh.Max.Z)) };
    }

    /// <summary>Makes every item and the RGItems list. Needs the RTX (for names), so it runs once dialogue loading has
    /// read it.</summary>
    void BuildItems()
    {
        ItemDefs = LoadItemDefs(rgDir);
        if (ItemDefs.Count == 0) { Problems.Add("ITEM.INI not found: no items"); return; }
        itemList = new FormList(mod) { EditorID = "RGItems" };
        mod.FormLists.Add(itemList);
        for (int id = 0; id <= ItemDefs.Max(d => d.Id); id++)
        {
            var d = ItemDefs.FirstOrDefault(x => x.Id == id) ?? new ItemDef(id, []);
            FormKey key;
            try { key = MakeItem(d); }
            catch (Exception e) { Problems.Add($"item {id}: {e.Message}"); key = Gold001; }
            itemRecords[id] = key;
            itemList.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(key));
        }
    }

    FormKey MakeItem(ItemDef d)
    {
        string name = ItemName(d), description = ItemDescription(d), edid = $"RGItem{d.Id:D2}{Sanitize(name)}";
        string hand = Path.GetFileNameWithoutExtension(d.Get("hand_object_file"));
        if (d.Id == 2) return Gold001;
        if (Path.GetFileNameWithoutExtension(d.InventoryModel).Equals("Itorch", StringComparison.OrdinalIgnoreCase)) return Torch01;
        if (d.Type == 1)
        {
            var template = vanilla.Get<IWeaponGetter>(WeaponTemplates.GetValueOrDefault(hand, "IronSword")) ?? vanilla.Get<IWeaponGetter>("IronSword")!;
            var w = mod.Weapons.DuplicateInAsNewRecord(template);
            w.EditorID = edid;
            w.Name = name;
            if (description.Length > 0) w.Description = description;
            return w.FormKey;
        }
        string upper = name.ToUpperInvariant();
        bool usable = d.UseScript.Length > 0 && d.UseScript != "INOUSE";
        var mesh = ItemMesh(d.GameModel.Length > 0 ? d.GameModel : d.InventoryModel, $"rgitem{d.Id:D2}", 40, usable ? 0.5f : 1f);
        // Without Redguard's model, Skyrim's look of the kind: a potion, a key, a book, a note, a satchel.
        string fallback = usable ? @"Clutter\Potions\PotionFortifySkill01.nif" : upper.Contains("KEY") ? @"Clutter\Key01.nif"
            : Regex.IsMatch(upper, @"\b(BOOK|JOURNAL|LORE|HISTORY)\b") ? @"Clutter\Books\Book02LowPoly02.nif"
            : Regex.IsMatch(upper, @"\b(MAP|LETTER|PLANS|POSTER|PIECE|INSIGNIA|FIRMAMENT|FLORA|ARTIFACTS)\b") ? @"Clutter\Books\Note01.nif" : @"Clutter\Containers\Satchel.nif";
        Model? model = new Model { File = mesh is null ? fallback : mesh.Path[7..] };
        if (usable)
        {
            // The use effect: TMRgUseItem on the player starts the item's use script (its shell is set once scripts
            // are made).
            var mgef = new MagicEffect(mod)
            {
                EditorID = $"RGUse{d.Id:D2}{Sanitize(name)}",
                Name = name,
                Flags = MagicEffect.Flag.HideInUI | MagicEffect.Flag.NoDuration | MagicEffect.Flag.Painless,
                Archetype = new MagicEffectArchetype { Type = MagicEffectArchetype.TypeEnum.Script },
                CastType = CastType.FireAndForget,
                TargetType = TargetType.Self,
                MenuDisplayObject = new FormLinkNullable<IStaticGetter>(),
            };
            mod.MagicEffects.Add(mgef);
            useEffects[d.Id] = mgef;
            var potion = new Ingestible(mod)
            {
                EditorID = edid,
                Name = name,
                Description = description,
                Weight = 0.5f,
                Value = 0,
                ObjectBounds = BoundsOf(mesh),
                Model = model,
                ConsumeSound = new FormLink<ISoundDescriptorGetter>(ItmPotionUse),
                Flags = Ingestible.Flag.NoAutoCalc | Ingestible.Flag.Medicine,
                Effects = [new Effect { BaseEffect = mgef.ToNullableLink(), Data = new EffectData { Magnitude = 0, Duration = 0, Area = 0 } }],
            };
            mod.Ingestibles.Add(potion);
            return potion.FormKey;
        }
        if (upper.Contains("KEY"))
        {
            var key = new Key(mod) { EditorID = edid, Name = name, Value = 0, Weight = 0.1f, ObjectBounds = BoundsOf(mesh), Model = model };
            mod.Keys.Add(key);
            return key.FormKey;
        }
        if (Regex.IsMatch(upper, @"\b(BOOK|JOURNAL|MAP|LETTER|PLANS|POSTER|SPELL|PARCHMENT|NOTE)\b") || d.InventoryModel.Contains("Ibook", StringComparison.OrdinalIgnoreCase))
        {
            var book = new Book(mod)
            {
                EditorID = edid,
                Name = name,
                BookText = description.Length > 0 ? $"<p align=\"center\">{name}</p><br><br>{description}" : name,
                Description = description,
                Value = 0,
                Weight = 0.5f,
                Type = upper.Contains("BOOK") || upper.Contains("JOURNAL") ? Book.BookType.BookOrTome : Book.BookType.NoteOrScroll,
                Teaches = new BookTeachesNothing(),
                ObjectBounds = BoundsOf(mesh),
                Model = model,
            };
            mod.Books.Add(book);
            return book.FormKey;
        }
        var misc = new MiscItem(mod) { EditorID = edid, Name = name, Value = 0, Weight = 0.5f, ObjectBounds = BoundsOf(mesh), Model = model };
        mod.MiscItems.Add(misc);
        return misc.FormKey;
    }

    /// <summary>The record standing for Redguard item n (null for an index ITEM.INI doesn't have).</summary>
    FormKey? ItemRecord(int n) => itemRecords.TryGetValue(n, out var k) ? k : null;
}
