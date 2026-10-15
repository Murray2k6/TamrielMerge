namespace TamrielMerge.Oblivion;

public sealed partial class ObMeshConverter
{
    Result ConvertOldEffect(string model, string source, byte[] bytes, string modOutDir, bool moving, float? itemMass, string why) =>
        throw new InvalidDataException($"{source}: {why}");
}
