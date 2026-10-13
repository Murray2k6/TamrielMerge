using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Fills each dialogue topic's SNAM (subtype name) from its subtype. Mutagen keeps SNAM as a separate field that
/// defaults to zero, and Skyrim crashes while loading a plugin whose topic has a zero SNAM, so every plugin runs this
/// before it is written. Codes are the ones Skyrim.esm pairs with each subtype.
/// </summary>
public static class TopicSubtypes
{
    static readonly Dictionary<DialogTopic.SubtypeEnum, string> Codes = new()
    {
        [DialogTopic.SubtypeEnum.Custom] = "CUST",
        [DialogTopic.SubtypeEnum.Hello] = "HELO",
        // Morrowind's barks (MwBarks), as Skyrim.esm codes them.
        [DialogTopic.SubtypeEnum.Idle] = "IDLE",
        [DialogTopic.SubtypeEnum.Attack] = "ATCK",
        [DialogTopic.SubtypeEnum.Hit] = "HIT_",
        [DialogTopic.SubtypeEnum.Flee] = "FLEE",
        [DialogTopic.SubtypeEnum.Steal] = "STEA",
        [DialogTopic.SubtypeEnum.Trespass] = "TRES",
        [DialogTopic.SubtypeEnum.NormalToCombat] = "NOTC",
    };

    public static void Fill(ISkyrimMod mod)
    {
        foreach (var topic in mod.DialogTopics)
        {
            if (!Codes.TryGetValue(topic.Subtype, out var code))
                throw new InvalidOperationException($"No SNAM code for dialogue subtype {topic.Subtype} (topic {topic.EditorID}): add it to TopicSubtypes");
            topic.SubtypeName = new RecordType(code);
        }
    }
}
