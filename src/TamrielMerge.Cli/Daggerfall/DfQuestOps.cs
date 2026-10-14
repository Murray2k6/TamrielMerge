namespace TamrielMerge.Daggerfall;

/// <summary>
/// Daggerfall's quest operations (QBN section 8), one per opcode: what it does, as Daggerfall Unity's text quests name
/// it (the reference for each one's behaviour: Daggerfall Unity, MIT, Assets/Scripts/Game/Questing/Actions), and the
/// meaning of its operands. Matched on all 232 quests by lining up each opcode with the lines of the Daggerfall Unity
/// task its state belongs to (symbol hashes) and UESP's "Quest pseudo-codes".
/// <para>The first operand is always a state: for a <see cref="OpKind.Trigger"/> or <see cref="OpKind.AlwaysOn"/> the
/// state it sets (its task), for an <see cref="OpKind.Action"/> the state it waits for (none: the quest's start-up).</para>
/// </summary>
public static class DfQuestOps
{
    public enum OpKind
    {
        /// <summary>Sets its state when something happens (checked until the state is set; again once it is cleared).</summary>
        Trigger,
        /// <summary>A condition checked every pass: the first for a state sets and clears it, later ones only set it.</summary>
        AlwaysOn,
        /// <summary>Runs once when its state is set (again after the state is cleared and set again).</summary>
        Action,
    }

    /// <summary>Operand roles: s state, i item, n person, p place, c clock, f foe, # number, m message id, - unused.</summary>
    public sealed record OpInfo(int Code, string Name, OpKind Kind, string Args, string Dfu, string? Gap = null)
    {
        /// <summary>Supported by the converter's quest machine (TMDfQuest).</summary>
        public bool Supported => Gap is null;
    }

    public static readonly IReadOnlyDictionary<int, OpInfo> All = new List<OpInfo>
    {
        new(0x00, "PlaceItem", OpKind.Action, "sip", "place item {1} at {2}"),
        new(0x01, "TotingClicked", OpKind.Trigger, "sin", "toting {1} and {2} clicked"),
        new(0x02, "KilledFoe", OpKind.Trigger, "sf#", "killed {2} {1}"),
        new(0x03, "ClickedItem", OpKind.Trigger, "si", "clicked item {1}"),
        new(0x04, "GivePcReward", OpKind.Action, "siiii", "give pc {1+} (quest complete)"),
        new(0x05, "DroppedAt", OpKind.AlwaysOn, "sip", "dropped {1} at {2}"),
        new(0x06, "EndQuest", OpKind.Action, "s", "end quest"),
        new(0x07, "Clear", OpKind.Action, "sssss", "clear {1+}"),
        new(0x08, "StartQuest", OpKind.Action, "s##", "start quest {1} {2}"),
        new(0x09, "CreateFoe", OpKind.Action, "sf###", "create foe {1} every {2} minutes {4} times with {3}% success"),
        new(0x0A, "AddDialog", OpKind.Action, "spni", "add dialog for location {1} person {2} item {3}"),
        new(0x0B, "DialogLink", OpKind.Action, "spni", "dialog link for location {1} person {2} item {3}"),
        new(0x0C, "StartTimer", OpKind.Action, "sc", "start timer {1}"),
        new(0x0D, "StopTimer", OpKind.Action, "sc", "stop timer {1}"),
        new(0x11, "Unknown11", OpKind.Action, "sp##", "(0x11 {1} {2} {3})",
            Gap: "unknown: two uses (A0C10Y05, a house and two numbers); UESP has no meaning for it and Daggerfall Unity's version leaves it out"),
        new(0x13, "Reveal", OpKind.Action, "sp", "reveal {1}"),
        new(0x15, "InjuredFoe", OpKind.Trigger, "sf", "injured {1}"),
        new(0x16, "PlaceFoe", OpKind.Action, "sfp", "place foe {1} at {2}"),
        new(0x17, "Log", OpKind.Action, "s##", "log {1} step {2}"),
        new(0x18, "RemoveLog", OpKind.Action, "s#", "remove log step {1}"),
        new(0x1A, "GetItem", OpKind.Action, "sin", "get item {1} (to {2})"),
        new(0x1B, "RevealMap", OpKind.Action, "sp##", "reveal {1} (region {2} location {3})"),
        new(0x1C, "ClickedNpc", OpKind.Trigger, "sn", "clicked npc {1}"),
        new(0x1D, "Prompt", OpKind.Action, "sss#", "prompt {3} yes {1} no {2}"),
        new(0x1E, "PlaceNpc", OpKind.Action, "snp", "place npc {1} at {2}"),
        new(0x1F, "DailyFrom", OpKind.AlwaysOn, "s##", "daily from {1} to {2}"),
        new(0x22, "PickOneOf", OpKind.Action, "sssss", "pick one of {1+}"),
        new(0x23, "StartTask", OpKind.Action, "sssss", "start task {1+} (cycle)"),
        new(0x24, "TakeItem", OpKind.Action, "si", "take {1} from pc"),
        new(0x25, "ReputeExceedsDo", OpKind.Action, "ssn#", "repute with {2} exceeds {3} do {1}"),
        new(0x26, "RumorMill", OpKind.Action, "s#", "rumor mill {1}"),
        new(0x27, "GiveItemToFoe", OpKind.Action, "sif", "give item {1} to {2}"),
        new(0x2B, "PcAtSet", OpKind.Action, "ssp", "pc at {2} set {1}"),
        new(0x2C, "DestroyNpc", OpKind.Action, "sn", "destroy npc {1}"),
        new(0x2E, "HideNpc", OpKind.Action, "sn", "hide npc {1}"),
        new(0x30, "RestoreNpc", OpKind.Action, "sn", "restore npc {1}"),
        new(0x31, "CureDisease", OpKind.Action, "s#", "cure disease {1}"),
        new(0x32, "PlayVideo", OpKind.Action, "s#", "play video {1}"),
        new(0x33, "Say", OpKind.Action, "s", "say"),
        new(0x34, "WhenAnd", OpKind.AlwaysOn, "sssss", "when {1+ and}"),
        new(0x35, "WhenOr", OpKind.AlwaysOn, "sssss", "when {1+ or}"),
        new(0x36, "MakePermanent", OpKind.Action, "si", "make {1} permanent"),
        new(0x37, "AddFace", OpKind.Action, "sn", "add {1} face"),
        new(0x38, "DropFace", OpKind.Action, "sn", "drop {1} face"),
        new(0x39, "ItemUsedDo", OpKind.Action, "ssi", "{2} used do {1}"),
        new(0x3A, "CureVampirism", OpKind.Action, "s", "cure vampirism"),
        new(0x3B, "CureLycanthropy", OpKind.Action, "s", "cure lycanthropy"),
        new(0x3C, "PlaySound", OpKind.Action, "s#", "play sound {1} once"),
        new(0x3D, "ChangeRepute", OpKind.Action, "sn#", "change repute with {1} by {2}"),
        new(0x3F, "AddFoeFace", OpKind.Action, "sf", "add foe {1} face"),
        new(0x40, "DropFoeFace", OpKind.Action, "sf", "drop foe {1} face"),
        new(0x41, "LegalRepute", OpKind.Action, "s#", "legal repute {1}"),
        new(0x44, "RestrainFoe", OpKind.Action, "sfs", "restrain foe {1}"),
        new(0x45, "CastSpellOnFoe", OpKind.Action, "sf#", "cast spell {2} on {1}"),
        new(0x46, "HaveItemSet", OpKind.Action, "ssii-", "have {2} set {1}"),
        new(0x47, "ClickedPay", OpKind.Trigger, "ss#n", "clicked {3} and at least {2} gold otherwise do {1}"),
        new(0x48, "MakePcDiseased", OpKind.Action, "s#", "make pc ill with {1}"),
        new(0x49, "CastSpellDo", OpKind.Action, "ss#", "cast spell {2} do {1}"),
        new(0x4C, "GivePc", OpKind.Action, "si", "give pc {1} notify"),
        new(0x4D, "LevelCompleted", OpKind.Trigger, "s#", "level {1} completed"),
        new(0x4E, "NpcIsAvailable", OpKind.AlwaysOn, "s#", "when individual {1} is available"),
        new(0x4F, "ReputeIsAtLeast", OpKind.AlwaysOn, "s##", "when repute with faction {1} is at least {2}"),
        new(0x51, "CreateNpc", OpKind.Action, "spn", "create npc {2} at {1}"),
        new(0x52, "MuteNpc", OpKind.Action, "sn", "mute npc {1}"),
        new(0x53, "TransferPc", OpKind.Action, "sp##", "transfer pc inside {1} marker {3}"),
        new(0x54, "PlaySoundEvery", OpKind.Action, "s###", "play sound {1} every {2} minutes {3} times"),
        new(0x55, "AddQuestor", OpKind.Action, "sn", "add {1} as questor"),
        new(0x56, "DropQuestor", OpKind.Action, "sn", "drop {1} as questor"),
        new(0x57, "SendFoe", OpKind.Action, "sf###", "create foe {1} every {2} minutes {4} times with {3}% success (sent)"),
    }.ToDictionary(o => o.Code);

    /// <summary>The operation's info, or an unknown placeholder.</summary>
    public static OpInfo Of(int code) => All.TryGetValue(code, out var o) ? o : new OpInfo(code, $"Op{code:X2}", OpKind.Action, "sssss", $"(0x{code:X2})", Gap: "not in Daggerfall's quests");
}
