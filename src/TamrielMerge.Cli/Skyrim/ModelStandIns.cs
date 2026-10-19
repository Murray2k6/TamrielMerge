using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// A model for an item or container that has none in its own game (2026-10-19, measured: Oblivion's script tokens,
/// a test potion, the Osseous Marrow, the magetallow candle and the horse armour plans have no model in Oblivion's
/// data; two of Daggerfall's shop chests none of their own), where every vanilla book, container, potion and nearly
/// every misc item has one: the model of a vanilla record of the same kind, by its EditorID, so it is one Skyrim has.
/// A token is shown as vanilla's are (Olava's token: a sapphire).
/// </summary>
public sealed class ModelStandIns(ISkyrimModGetter skyrim)
{
    readonly Dictionary<string, string> byEditorId = new(StringComparer.OrdinalIgnoreCase);

    string ModelOf(string editorId)
    {
        if (byEditorId.TryGetValue(editorId, out var path)) return path;
        IModeledGetter? r = (IModeledGetter?)skyrim.MiscItems.FirstOrDefault(x => x.EditorID == editorId)
            ?? (IModeledGetter?)skyrim.Ingredients.FirstOrDefault(x => x.EditorID == editorId)
            ?? (IModeledGetter?)skyrim.Ingestibles.FirstOrDefault(x => x.EditorID == editorId)
            ?? (IModeledGetter?)skyrim.Containers.FirstOrDefault(x => x.EditorID == editorId)
            ?? skyrim.Books.FirstOrDefault(x => x.EditorID == editorId);
        path = r?.Model?.File.DataRelativePath.Path ?? throw new InvalidOperationException($"Skyrim.esm has no model for {editorId}");
        // Model paths are written relative to meshes\.
        if (path.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase)) path = path[7..];
        return byEditorId[editorId] = path;
    }

    /// <summary>A note's, for a book or a page.</summary>
    public string Book => ModelOf(NoteEditorId ??= skyrim.Books.First(b => Path.GetFileName(b.Model?.File.DataRelativePath.Path ?? "").Equals("Note01.nif", StringComparison.OrdinalIgnoreCase)).EditorID!);
    string? NoteEditorId;

    /// <summary>A healing potion's.</summary>
    public string Potion => ModelOf("RestoreHealth02");

    /// <summary>A merchant's chest's.</summary>
    public string Container => ModelOf("MerchantWhiterunEorlundChest");

    /// <summary>A misc item's, by what it is called: a candle a candlestick, bone or marrow bone meal, anything else
    /// (a token) a small gem.</summary>
    public string Misc(string? name, string? editorId)
    {
        string what = $"{name} {editorId}";
        if (what.Contains("candle", StringComparison.OrdinalIgnoreCase)) return ModelOf("SilverCandleStick01Off");
        if (what.Contains("marrow", StringComparison.OrdinalIgnoreCase) || what.Contains("bone", StringComparison.OrdinalIgnoreCase)) return ModelOf("BoneMeal");
        return ModelOf("DB06OlavaToken");
    }
}
