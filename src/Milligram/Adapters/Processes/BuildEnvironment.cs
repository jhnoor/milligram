namespace Milligram.Adapters.Processes;

/// <summary>Keep the scanner's in-process SDK registration out of launched tools.</summary>
internal sealed class BuildEnvironment(IReadOnlyDictionary<string, string?> original)
{
    private static readonly string[] Names = ["MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath"];

    public static BuildEnvironment Capture() => new(Names.ToDictionary(name => name,
        Environment.GetEnvironmentVariable, StringComparer.Ordinal));

    public void Restore(IDictionary<string, string?> child)
    {
        foreach (var name in Names)
        {
            if (original.GetValueOrDefault(name) is { } value) child[name] = value;
            else child.Remove(name);
        }
    }
}
