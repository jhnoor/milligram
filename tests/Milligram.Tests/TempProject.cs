namespace Milligram.Tests;

/// <summary>A throwaway project directory with the given files.</summary>
internal sealed class TempProject : IDisposable
{
    public TempProject(params (string Path, string Text)[] files)
    {
        Root = Path.Combine(Path.GetTempPath(), "milligram-tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
        foreach (var (path, text) in files) Write(path, text);
    }

    public string Root { get; }

    public string Write(string path, string text)
    {
        var full = Path.Combine(Root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>The repository root, found by walking up to Milligram.slnx.</summary>
    public static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Milligram.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Cannot find Milligram.slnx above the test binaries.");
    }
}
