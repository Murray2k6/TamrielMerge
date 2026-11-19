using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall's talk tones (Daggerfall Unity TalkManager.GetReactionToPlayer_0_1_2 and DaggerfallTalkWindow.TalkTone,
/// MIT; user 2026-10-29: the rest of Daggerfall): the player speaks politely (Etiquette), plainly, or bluntly
/// (Streetwise). Each answer the townsfolk give comes in three phrasings by how the tone lands (0 a refusal, 1 plain,
/// 2 warm): reaction = Personality / 5 + the question's modifier (5 for "where is" a building, 0 for work) + the tone's
/// modifier for the speaker's social group (Etiquette {-10, 5, 10, 15, -15}, Streetwise {10, 5, -10, -15, 15} for
/// commoners, merchants, scholars, nobility, underworld) + 5 on a skill roll made or -10 failed, against a roll of 0 to
/// 20: under it 0, under it + 20 1, else 2. The skill is tallied once a talk for each tone used. The tone is a global
/// (<see cref="ToneGlobal"/>) the tone topics set; the five reactions are globals the quest machine fills when a
/// question is asked (TMDfQuestMachine.RollTalk), which the answers' conditions read.
/// </summary>
public static class TalkTone
{
    public const string ToneGlobal = "DFTalkTone";
    public static string ReactGlobal(int group) => $"DFTalkReact{group}";
    public static readonly int[] EtiquetteMods = [-10, 5, 10, 15, -15];
    public static readonly int[] StreetwiseMods = [10, 5, -10, -15, 15];

    /// <summary>TEXT.RSC: the townsfolk's answers to "where is" by social group (5) and the tone's reaction (3): the
    /// first 15 when they don't know, the next 15 when they do (FALL.EXE's table as Daggerfall Unity has it, the
    /// commoners' and merchants' rows swapped as in classic).</summary>
    public static readonly int[] DirectionAnswers =
    [
        7251, 7266, 7281, 7250, 7265, 7280, 7252, 7267, 7282, 7253, 7268, 7283, 7304, 7269, 7284,
        7256, 7271, 7286, 7255, 7270, 7285, 7257, 7272, 7287, 7258, 7273, 7288, 7259, 7274, 7289,
    ];
    /// <summary>"Do you have any work for me?" by the tone's reaction: 8075 refused, 8076 and 8077 directed.</summary>
    public static readonly int[] WorkAnswers = [8075, 8076, 8077];
    /// <summary>The player's greetings by tone (7215-7217) and the townsfolk's replies to each (7218-7220).</summary>
    public static readonly int[] Greetings = [7215, 7216, 7217];
    public static readonly int[] Replies = [7218, 7219, 7220];
    public static readonly string[] Names = ["Polite", "Normal", "Blunt"];

    /// <summary>The tone global and the five reaction globals: a master's (a town part's Daggerfall.esm), else this
    /// plugin's, made when missing (shorts; tone 1 plain, reactions 1).</summary>
    public static (FormKey Tone, FormKey[] Reacts) Globals(SkyrimMod mod, ISkyrimModGetter? master)
    {
        FormKey One(string edid, short value)
        {
            if (master?.Globals.FirstOrDefault(g => g.EditorID == edid) is { } theirs) return theirs.FormKey;
            if (mod.Globals.FirstOrDefault(g => g.EditorID == edid) is { } ours) return ours.FormKey;
            var made = new GlobalShort(mod) { EditorID = edid, Data = value };
            mod.Globals.Add(made);
            return made.FormKey;
        }
        return (One(ToneGlobal, 1), [.. Enumerable.Range(0, 5).Select(g => One(ReactGlobal(g), 1))]);
    }
}
