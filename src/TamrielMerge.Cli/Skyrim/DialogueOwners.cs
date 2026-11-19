using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// A converted game's dialogue said by that game's people only. Morrowind and Oblivion filter most responses by race,
/// class, faction or nothing at all (a greeting for anyone well disposed), and Skyrim offers a response to every
/// speaker its conditions pass, whichever plugin the speaker is from: seen in game on 2026-11-12, Redguard's Tobias
/// greeted Cyrus with another game's "Glad to help, &lt;Alias=Player&gt;. What can I do for you?" and offered its
/// "Persuasion" beside his own topics, and his own greeting ("You look good! It's been too long!") lost to it. Every
/// response that does not name its speaker (no GetIsID on the subject) gets one more condition, first in its list: the
/// speaker is one of the game's people (the faction every converted person of that game is in). A response that names
/// its speaker is left: creatures and people who talk by name need no faction.
/// </summary>
public static class DialogueOwners
{
    public static int Restrict(SkyrimMod mod, Faction people, Action<string>? log = null)
    {
        int restricted = 0, named = 0, already = 0;
        foreach (var topic in mod.DialogTopics)
            foreach (var response in topic.Responses)
            {
                bool namesSpeaker = false, has = false;
                foreach (var c in response.Conditions)
                {
                    if (c.Data.RunOnType != Condition.RunOnType.Subject) continue;
                    if (c.Data is GetIsIDConditionData) namesSpeaker = true;
                    else if (c.Data is GetInFactionConditionData f && f.Faction.Link.FormKey == people.FormKey && c is ConditionFloat { CompareOperator: CompareOperator.EqualTo, ComparisonValue: 1 } && !c.Flags.HasFlag(Condition.Flag.OR)) has = true;
                }
                if (namesSpeaker) { named++; continue; }
                if (has) { already++; continue; }
                var data = new GetInFactionConditionData();
                data.Faction = new FormLinkOrIndex<IFactionGetter>(data, people.FormKey);
                // First in the list and not an OR: Skyrim groups a run of ORs and ANDs the groups, so this stands alone.
                response.Conditions.Insert(0, new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1, Data = data });
                restricted++;
            }
        log?.Invoke($"  dialogue ({mod.ModKey.FileName}): {restricted:N0} responses that named no speaker kept to {people.Name} ({named:N0} name their speaker, {already:N0} already kept)");
        return restricted;
    }
}
