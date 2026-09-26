namespace Milligram.Tests;

/// <summary>A fact about behaviour only Windows has, such as files that can't be replaced while open. Skipped elsewhere.</summary>
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only.";
    }
}
