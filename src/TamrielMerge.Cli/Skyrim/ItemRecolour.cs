using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Skyrim's steel weapons and armor recoloured to a metal Skyrim has none of (DfItems: silver, mithril, adamantium):
/// each model copied under meshes\TamrielMerge\DF\Items\{metal}\ with its diffuse textures multiplied by the metal's
/// colour (their detail kept) under textures\TamrielMerge\DF\Items\{metal}\; the item and its worn pieces (ARMA) and
/// first-person model point at the copies. A texture that can't be decoded keeps steel's.
/// </summary>
public sealed class ItemRecolour(string skyrimData, string outDir, Func<FormKey> nextKey)
{
    Dictionary<string, IArchiveFile>? files;
    readonly Dictionary<string, string> doneMeshes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> doneTextures = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<FormKey, FormKey> doneAddons = new();
    readonly Dictionary<FormKey, FormKey> doneStatics = new();

    byte[]? Read(string rel)
    {
        string loose = Path.Combine(skyrimData, rel);
        if (File.Exists(loose)) return File.ReadAllBytes(loose);
        if (files is null)
        {
            files = new(StringComparer.OrdinalIgnoreCase);
            foreach (var bsa in Directory.GetFiles(skyrimData, "*.bsa")
                         .Where(p => Path.GetFileName(p) is var n && (n.StartsWith("Skyrim - Meshes") || n.StartsWith("Skyrim - Textures") || n is "Update.bsa" or "Dawnguard.bsa" or "Dragonborn.bsa" or "HearthFires.bsa")))
                foreach (var f in Archive.CreateReader(GameRelease.SkyrimSE, bsa).Files)
                    files[f.Path] = f;   // later archives (DLC, Update) win
        }
        return files.TryGetValue(rel, out var file) ? file.GetBytes() : null;
    }

    /// <summary>The recoloured copy of a model (Data-relative, without "meshes\"), or null if it can't be read.</summary>
    string? Model(string model, string metal, (float R, float G, float B) tint)
    {
        string key = metal + "|" + model;
        if (doneMeshes.TryGetValue(key, out var hit)) return hit;
        string src = model.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) ? model : "meshes\\" + model;
        var bytes = Read(src);
        if (bytes is null) return null;
        string rel = Path.Combine("TamrielMerge", "DF", "Items", metal, model.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase) ? model[7..] : model);
        string full = Path.Combine(outDir, "meshes", rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string tmp = Path.Combine(Path.GetTempPath(), $"tm_rc_{Guid.NewGuid():N}.nif");
        File.WriteAllBytes(tmp, bytes);
        try
        {
            using var nif = new nifly.NifFile();
            if (nif.Load(tmp) != 0) return null;
            foreach (var shape in nif.GetShapes())
                if (nif.GetTexturePathByIndex(shape, 0) is { Length: > 0 } diffuse && Texture(diffuse, metal, tint) is { } tinted)
                    nif.SetTextureSlot(shape, tinted, 0);
            nif.Save(full);
        }
        finally { File.Delete(tmp); }
        return doneMeshes[key] = rel;
    }

    /// <summary>The recoloured copy of a diffuse texture (Data-relative), or null if it can't be decoded.</summary>
    string? Texture(string texture, string metal, (float R, float G, float B) tint)
    {
        string src = texture.StartsWith("textures\\", StringComparison.OrdinalIgnoreCase) ? texture : "textures\\" + texture;
        string key = metal + "|" + src;
        if (doneTextures.TryGetValue(key, out var hit)) return hit;
        if (Read(src) is not { } bytes || Util.DdsImage.Decode(bytes) is not { } img) return doneTextures[key] = null!;
        var px = img.Px;
        bool alpha = false;
        for (int i = 0; i < px.Length; i++)
        {
            uint c = px[i];
            uint a = c >> 24;
            if (a != 255) alpha = true;
            uint r = (uint)Math.Clamp(((c >> 16) & 0xFF) * tint.R, 0, 255), g = (uint)Math.Clamp(((c >> 8) & 0xFF) * tint.G, 0, 255), b = (uint)Math.Clamp((c & 0xFF) * tint.B, 0, 255);
            px[i] = a << 24 | r << 16 | g << 8 | b;
        }
        string rel = Path.Combine("textures", "TamrielMerge", "DF", "Items", metal, src[9..]);
        string full = Path.Combine(outDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        Morrowind.MwTextures.WriteBcDds(full, px, img.W, img.H, alpha);
        return doneTextures[key] = rel;
    }

    /// <summary>A weapon (a copy of <paramref name="template"/>) recoloured: its model and its first-person model.</summary>
    public void Weapon(Weapon w, IWeaponGetter template, string metal, (float R, float G, float B) tint, SkyrimMod mod)
    {
        if (template.Model?.File.DataRelativePath.Path is { Length: > 0 } m && Model(m, metal, tint) is { } rel)
            w.Model = new Model { File = rel };
        if (!template.FirstPersonModel.IsNull)
        {
            var key = template.FirstPersonModel.FormKey;
            if (!doneStatics.TryGetValue(key, out var made))
            {
                var link = template.FirstPersonModel;
                made = key;
                // The first-person static lives in Skyrim.esm; copy it with the recoloured model.
                foreach (var src in new[] { key })
                {
                    var stat = new Static(nextKey(), SkyrimRelease.SkyrimSE) { EditorID = $"DF1st{metal}{key.ID:X6}" };
                    stat.Model = w.Model?.DeepCopy();
                    mod.Statics.Add(stat);
                    made = stat.FormKey;
                }
                doneStatics[key] = made;
            }
            w.FirstPersonModel.SetTo(made);
        }
    }

    /// <summary>A piece of armor (a copy of <paramref name="template"/>) recoloured: its ground models and its worn
    /// pieces (each ARMA copied with its male, female and first-person models recoloured).</summary>
    public void Armor(Armor arm, IArmorGetter template, string metal, (float R, float G, float B) tint, SkyrimMod mod, ILinkCache cache)
    {
        if (arm.WorldModel is { } world)
        {
            if (world.Male?.Model?.File.DataRelativePath.Path is { Length: > 0 } male && Model(male, metal, tint) is { } r1) world.Male.Model = new Model { File = r1 };
            if (world.Female?.Model?.File.DataRelativePath.Path is { Length: > 0 } female && Model(female, metal, tint) is { } r2) world.Female.Model = new Model { File = r2 };
        }
        for (int i = 0; i < arm.Armature.Count; i++)
        {
            var key = arm.Armature[i].FormKey;
            if (!doneAddons.TryGetValue(key, out var made))
            {
                if (!cache.TryResolve<IArmorAddonGetter>(key, out var addon)) continue;
                var copy = (ArmorAddon)addon.Duplicate(nextKey());
                mod.ArmorAddons.Add(copy);
                copy.EditorID = $"DFAddon{metal}{addon.EditorID}";
                if (copy.WorldModel is { } wm)
                {
                    if (wm.Male?.File.DataRelativePath.Path is { Length: > 0 } m && Model(m, metal, tint) is { } a) wm.Male = new Model { File = a };
                    if (wm.Female?.File.DataRelativePath.Path is { Length: > 0 } f && Model(f, metal, tint) is { } b) wm.Female = new Model { File = b };
                }
                if (copy.FirstPersonModel is { } fp)
                {
                    if (fp.Male?.File.DataRelativePath.Path is { Length: > 0 } m && Model(m, metal, tint) is { } a) fp.Male = new Model { File = a };
                    if (fp.Female?.File.DataRelativePath.Path is { Length: > 0 } f && Model(f, metal, tint) is { } b) fp.Female = new Model { File = b };
                }
                made = doneAddons[key] = copy.FormKey;
            }
            arm.Armature[i] = made.ToLink<IArmorAddonGetter>();
        }
    }
}
