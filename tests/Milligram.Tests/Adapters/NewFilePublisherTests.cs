using Milligram.Adapters.Files;

namespace Milligram.Tests.Adapters;

public class NewFilePublisherTests
{
    [Fact]
    public void AnExistingFileKeepsItsContentAndTheCompletedInput()
    {
        using var project = new TempProject(("finished.tmp", "new"), ("state.json", "old"));
        var finished = Path.Combine(project.Root, "finished.tmp");
        var destination = Path.Combine(project.Root, "state.json");

        Assert.False(new NewFilePublisher().TryPublish(finished, destination));

        Assert.Equal("old", File.ReadAllText(destination));
        Assert.Equal("new", File.ReadAllText(finished));
    }

    [Fact]
    public void AMissingCompletedFileIsAnErrorAndNeverPublishesADestination()
    {
        using var project = new TempProject();
        var destination = Path.Combine(project.Root, "state.json");

        Assert.ThrowsAny<IOException>(() => new NewFilePublisher().TryPublish(Path.Combine(project.Root, "missing.tmp"), destination));

        Assert.False(File.Exists(destination));
    }
}
