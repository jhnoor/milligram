namespace Milligram.Adapters.Processes;

/// <summary>An owned child handle; disposing it detaches, while Stop is reserved for cancelling its startup.</summary>
public interface IDetachedProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    void Stop();
}
