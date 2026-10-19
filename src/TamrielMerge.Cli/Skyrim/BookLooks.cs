using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// What a converted book is read as: one of Skyrim's own reading models (a book's inventory art, the static whose
/// model the book menu opens and turns the pages of), and always the book type. Skyrim's books all have both
/// (2026-10-19, measured: 1,052 of its 1,054 books have inventory art, every one of Skyrim.esm's 821 books, notes and
/// letters too, has type 0; a note is a note by its art, Note01\Note01.nif and the like). The converted Morrowind and
/// Oblivion books had no inventory art and their scrolls and notes type 255 (Note/Scroll), which no vanilla book has; a
/// player's game crashed reading a Morrowind book. The book keeps its own model to lie on a shelf.
/// </summary>
public sealed class BookLooks
{
    readonly List<FormKey> books, notes;

    /// <param name="vanilla">Skyrim's books (Skyrim.esm's).</param>
    public BookLooks(IEnumerable<IBookGetter> vanilla)
    {
        // Ordinary books' and notes' art (teaching nothing), the kinds many books share: a unique one is a quest
        // book's own cover (the Oghma Infinium's, a Black Book's).
        var shared = vanilla
            .Where(b => !b.InventoryArt.IsNull && b.Teaches is null or IBookTeachesNothingGetter && b.Model is not null)
            .GroupBy(b => (Art: b.InventoryArt.FormKey, Note: IsNote(b)))
            .Where(g => g.Count() >= 8)
            .OrderBy(g => g.Key.Art.ID)
            .ToList();
        books = shared.Where(g => !g.Key.Note).Select(g => g.Key.Art).ToList();
        notes = shared.Where(g => g.Key.Note).Select(g => g.Key.Art).ToList();
        if (books.Count == 0 || notes.Count == 0) throw new InvalidOperationException("Skyrim.esm has no shared book or note reading models");
    }

    /// <summary>A vanilla note or letter: its model is one of Skyrim's notes (Clutter\Books\Note01.nif ...).</summary>
    static bool IsNote(IBookGetter b) => Path.GetFileName(b.Model!.File.DataRelativePath.Path).StartsWith("Note", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gives <paramref name="book"/> a vanilla reading model (a note's for a note or scroll, else a book's,
    /// picked steadily by its EditorID) and the book type.</summary>
    public void Apply(Book book, bool note)
    {
        var from = note ? notes : books;
        book.InventoryArt = from[(int)((uint)StableHash(book.EditorID ?? "") % (uint)from.Count)].ToNullableLink<IStaticGetter>();
        book.Type = Book.BookType.BookOrTome;
    }

    static int StableHash(string s) { unchecked { int h = 17; foreach (char c in s) h = h * 31 + c; return h; } }
}
