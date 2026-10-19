using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using nifly;

namespace TamrielMerge.Skyrim;

/// <summary>
/// A worn part that draws nothing, for an armour or skin its game draws nothing for when worn (a belt, Daggerfall's
/// greaves and pauldrons, a skin hiding an actor's body). Every one of Skyrim's 3,835 armour records has a worn part,
/// and 1,112 of its 1,113 worn parts a model (2026-10-19, measured; the one without is a Creation Club horse armour's
/// dummy): an armour with none differs from all of them. The model is an empty node, written into the output.
/// </summary>
public static class WornNothing
{
    /// <summary>The empty model's path under meshes\.</summary>
    public const string ModelPath = @"TamrielMerge\Worn\Nothing.nif";

    /// <summary>Writes the empty model under <paramref name="outDir"/> (once).</summary>
    public static void WriteModel(string outDir)
    {
        string path = Path.Combine(outDir, "meshes", ModelPath);
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var nif = new NifFile();
        nif.Create(NiVersion.getSSE());
        nif.GetRootNode().name = new NiStringRef("Nothing");
        if (nif.Save(path) != 0) throw new IOException($"couldn't write {path}");
    }

    /// <summary>
    /// A worn part for <paramref name="slots"/> wearing nothing, for <paramref name="races"/>' races (copied from it:
    /// a vanilla worn part such as IronCuirassAA, for the playable races), added to <paramref name="mod"/>; the empty model
    /// is written under <paramref name="outDir"/>. <paramref name="key"/>: its form key (a builder numbering its records in a
    /// fixed range of its own), else the next of the plugin's.
    /// </summary>
    public static ArmorAddon Addon(SkyrimMod mod, string outDir, string edid, BipedObjectFlag slots, IArmorAddonGetter races, FormKey? key = null)
    {
        WriteModel(outDir);
        var aa = new ArmorAddon(key ?? mod.GetNextFormKey(), SkyrimRelease.SkyrimSE)
        {
            EditorID = edid,
            BodyTemplate = new BodyTemplate { FirstPersonFlags = slots, ArmorType = ArmorType.Clothing },
            Race = races.Race.FormKeyNullable is { } r ? r.ToNullableLink<IRaceGetter>() : new FormLinkNullable<IRaceGetter>(),
            WorldModel = new GenderedItem<Model?>(new Model { File = ModelPath }, new Model { File = ModelPath }),
            Priority = new GenderedItem<byte>(0, 0),
            WeightSliderEnabled = new GenderedItem<bool>(false, false),
        };
        foreach (var extra in races.AdditionalRaces) aa.AdditionalRaces.Add(extra.FormKey);
        mod.ArmorAddons.Add(aa);
        return aa;
    }

    /// <summary>A worn part for one race only (a skin's).</summary>
    public static ArmorAddon Addon(SkyrimMod mod, string outDir, string edid, BipedObjectFlag slots, FormKey race)
    {
        WriteModel(outDir);
        var aa = new ArmorAddon(mod)
        {
            EditorID = edid,
            BodyTemplate = new BodyTemplate { FirstPersonFlags = slots, ArmorType = ArmorType.Clothing },
            Race = race.ToNullableLink<IRaceGetter>(),
            WorldModel = new GenderedItem<Model?>(new Model { File = ModelPath }, new Model { File = ModelPath }),
            Priority = new GenderedItem<byte>(0, 0),
            WeightSliderEnabled = new GenderedItem<bool>(false, false),
        };
        mod.ArmorAddons.Add(aa);
        return aa;
    }
}
