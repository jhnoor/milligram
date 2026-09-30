namespace Milligram.Tests;

/// <summary>Symbolic-link fixtures need no privileges on Unix; Windows junctions are exercised by the package smoke.</summary>
internal sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Unix symbolic-link fixture; Windows junctions are tested in package smoke.";
    }
}
