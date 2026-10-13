using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Morrowind's and Oblivion's disposition (0-100, how much an NPC likes the player) in Skyrim, which has none: the
/// spare actor value Variable09 holds it as an offset from 50, so an NPC that never had it set is at Oblivion's usual
/// 50. Dialogue conditions test the actor value (GetActorValue Variable09 against the requirement minus 50), and
/// scripts read and change it (TMMwInfo for dialogue results, the translated scripts' GetDisposition/ModDisposition,
/// TMDisposition for NPCs that start elsewhere than 50, as Morrowind's do).
/// </summary>
public static class Disposition
{
    public const ActorValue Value = ActorValue.Variable09;
    public const string ValueName = "Variable09";
    public const int Neutral = 50;
    public const string StartScript = "TMDisposition";

    /// <summary>An NPC whose disposition starts at <paramref name="start"/> (unless 50, which needs nothing).</summary>
    public static void Start(Npc npc, int start)
    {
        start = Math.Clamp(start, 0, 100);
        if (start == Neutral) return;
        npc.VirtualMachineAdapter ??= new VirtualMachineAdapter();
        npc.VirtualMachineAdapter.Scripts.RemoveAll(s => s.Name == StartScript);
        npc.VirtualMachineAdapter.Scripts.Add(new ScriptEntry { Name = StartScript, Properties = [new ScriptIntProperty { Name = "Start", Data = start }] });
    }

    /// <summary>The speaker's disposition compared with <paramref name="value"/> (0-100).</summary>
    public static ConditionFloat Condition(CompareOperator op, float value, bool or = false) => new()
    {
        Data = new GetActorValueConditionData { ActorValue = Value },
        CompareOperator = op,
        ComparisonValue = value - Neutral,
        Flags = or ? Mutagen.Bethesda.Skyrim.Condition.Flag.OR : 0,
    };
}
