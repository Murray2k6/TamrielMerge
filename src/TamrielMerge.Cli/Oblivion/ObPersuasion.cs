using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Skyrim;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// What Oblivion's persuasion needs on its people (ObDialogue.Persuasion builds the topics, TMObPersuade changes the
/// disposition): each NPC's fixed reactions to the four actions as keywords, and the globals its exit and bribe lines
/// read. Oblivion's rule (UESP Oblivion:Speechcraft): every NPC loves one of Admire, Boast, Joke and Coerce, likes
/// one, dislikes one and hates one, fixed for it and never changing between conversations; the engine derives the
/// four from the NPC, which its records don't hold, so here they are a hash of its EditorID (stable across builds).
/// </summary>
public sealed partial class ObPluginBuilder
{
    const string PersuadeScript = "TMObPersuade";
    /// <summary>The wheel's four actions, in Oblivion's clockwise order of its wedges (Admire, Joke, Coerce, Boast
    /// from the top) numbered as TMObPersuade numbers them.</summary>
    internal static readonly string[] PersuasionActions = ["Admire", "Boast", "Joke", "Coerce"];
    /// <summary>The four reactions, as TMObPersuade numbers them: loved, liked, disliked, hated (Oblivion's topics
    /// ADMIRELOVE, ADMIRELIKE, ADMIREDISLIKE, ADMIREHATE...).</summary>
    internal static readonly string[] PersuasionReactionNames = ["Love", "Like", "Dislike", "Hate"];

    readonly Dictionary<(int Action, int Reaction), Keyword> persuasionKeywords = new();
    readonly Global?[] persuasionNumbers = new Global?[2];
    readonly object persuasionLock = new();

    /// <summary>The keyword of one reaction to one action (OBPersuadeAdmireLove...), made once.</summary>
    Keyword PersuasionKeyword(int action, int reaction)
    {
        lock (persuasionLock)
        {
            if (persuasionKeywords.TryGetValue((action, reaction), out var k)) return k;
            k = new Keyword(mod) { EditorID = "OBPersuade" + PersuasionActions[action] + PersuasionReactionNames[reaction] };
            mod.Keywords.Add(k);
            return persuasionKeywords[(action, reaction)] = k;
        }
    }

    /// <summary>OBPersuasionLast (GetPersuasionNumber: the last action's change) and OBPersuasionTotal
    /// (GetTotalPersuasionNumber: the persuasion's total), made once; TMObPersuade keeps them.</summary>
    Global PersuasionNumberGlobal(bool total)
    {
        lock (persuasionLock)
        {
            int i = total ? 1 : 0;
            if (persuasionNumbers[i] is { } g) return g;
            g = new GlobalFloat(mod) { EditorID = total ? "OBPersuasionTotal" : "OBPersuasionLast", Data = 0 };
            mod.Globals.Add(g);
            return persuasionNumbers[i] = g;
        }
    }

    /// <summary>
    /// An NPC's reaction to each action (index: action; value: reaction), one of each: the 24 orders of the four
    /// reactions, picked by a stable hash of the NPC's EditorID (its FormKey when it has none), as the permutation's
    /// number in the factorial number system.
    /// </summary>
    internal static int[] PersuasionReactions(string npcId)
    {
        int n = (int)(VanillaActors.StableHash(npcId) % 24);
        var left = new List<int> { 0, 1, 2, 3 };
        var result = new int[4];
        int[] factorial = [6, 2, 1, 1];
        for (int a = 0; a < 4; a++)
        {
            int pick = n / factorial[a];
            n %= factorial[a];
            result[a] = left[pick];
            left.RemoveAt(pick);
        }
        return result;
    }

    /// <summary>Marks a converted person with its four reactions' keywords.</summary>
    void AddPersuasionReactions(Npc npc, Ob.INpcGetter n)
    {
        var reactions = PersuasionReactions(n.EditorID ?? n.FormKey.ToString());
        npc.Keywords ??= [];
        for (int a = 0; a < 4; a++) npc.Keywords.Add(PersuasionKeyword(a, reactions[a]).ToLink<IKeywordGetter>());
    }
}
