namespace Milligram.Application;

public enum JobState { Idle, Queued, Running, Succeeded, Failed }

public sealed record JobStatus(
    string Name,
    JobState State,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Message,
    IReadOnlyList<string> Log,
    IReadOnlyList<string> Queued)
{
    public static readonly JobStatus Idle = new("", JobState.Idle, null, null, null, [], []);
}

/// <summary>Runs long jobs (scans, tests, mutation) one at a time, in the order they came, and reports progress to viewers.</summary>
public sealed class JobQueue(IViewerEvents events)
{
    private const int LogLimit = 400;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(400);

    private readonly Lock gate = new();
    private readonly List<string> queued = [];
    private readonly List<string> log = [];
    private JobStatus status = JobStatus.Idle;
    private DateTimeOffset lastProgress;
    private Task tail = Task.CompletedTask;

    public JobStatus Status { get { lock (gate) return status with { Log = log.ToList(), Queued = queued.ToList() }; } }

    /// <summary>
    /// Each job starts when the one before it ends, however that ended. Continuations are scheduled rather than run
    /// inline, so no job runs under the lock or on the caller's thread.
    /// </summary>
    public Task Enqueue(string name, Func<Action<string>, CancellationToken, Task<string>> work, CancellationToken cancellation = default)
    {
        Task job;
        lock (gate)
        {
            queued.Add(name);
            job = tail = tail.ContinueWith(_ => RunAsync(name, work, cancellation),
                CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
        }
        Publish();
        return job;
    }

    private async Task RunAsync(string name, Func<Action<string>, CancellationToken, Task<string>> work, CancellationToken cancellation)
    {
        try
        {
            lock (gate)
            {
                queued.Remove(name);
                log.Clear();
                status = new JobStatus(name, JobState.Running, DateTimeOffset.UtcNow, null, null, [], []);
            }
            Publish();
            cancellation.ThrowIfCancellationRequested();
            var message = await work(Append, cancellation);
            Finish(JobState.Succeeded, message);
        }
        catch (Exception e)
        {
            Append($"error: {e.Message}");
            Finish(JobState.Failed, e.Message);
        }
    }

    private void Append(string line)
    {
        bool publish;
        lock (gate)
        {
            log.Add(line);
            if (log.Count > LogLimit) log.RemoveRange(0, log.Count - LogLimit);
            publish = DateTimeOffset.UtcNow - lastProgress > ProgressInterval;
            if (publish) lastProgress = DateTimeOffset.UtcNow;
        }
        if (publish) Publish();
    }

    private void Finish(JobState state, string? message)
    {
        lock (gate) status = status with { State = state, FinishedAt = DateTimeOffset.UtcNow, Message = message };
        Publish();
    }

    private void Publish() => events.Publish("job", Status);
}
