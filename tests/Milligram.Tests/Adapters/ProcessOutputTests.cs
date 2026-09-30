using System.Text;
using Milligram.Adapters.Processes;

namespace Milligram.Tests.Adapters;

public class ProcessOutputTests
{
    [Fact]
    public async Task ABufferedCallbackCannotDelayStartingTheOtherReader()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingReader();
        var constructing = Task.Run(() => new ProcessOutput(new StringReader("line"), pending,
            _ => { entered.TrySetResult(); release.Wait(); }, _ => { }, lines: true));
        ProcessOutput? output = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            output = await constructing.WaitAsync(TimeSpan.FromSeconds(2));
            await pending.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            output ??= await constructing.WaitAsync(TimeSpan.FromSeconds(5));
            await output.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothStreamsDrainIncludingTheirFinalUnterminatedText(bool lines)
    {
        var text = "first\r\n\n" + new string('x', 10000) + " 🐱 last";
        var stdout = new List<string>();
        var stderr = new List<string>();
        await using var output = new ProcessOutput(new StringReader(text), new StringReader("error\r\nlast error"), stdout.Add, stderr.Add, lines);
        Assert.True(await output.DrainAsync(TimeSpan.FromSeconds(5), CancellationToken.None));
        if (lines)
        {
            Assert.Equal(["first", "", new string('x', 10000) + " 🐱 last"], stdout);
            Assert.Equal(["error", "last error"], stderr);
        }
        else
        {
            Assert.Equal(text, string.Concat(stdout));
            Assert.Equal("error\r\nlast error", string.Concat(stderr));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ADescendantHoldingEitherPipeCannotPreventReadersFromBeingJoined(bool stderr, bool lines)
    {
        var pending = new PendingReader();
        var ended = new TrackedReader("done");
        var output = new ProcessOutput(stderr ? ended : pending, stderr ? pending : ended, _ => { }, _ => { }, lines);
        try
        {
            await pending.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(await output.DrainAsync(TimeSpan.FromMilliseconds(5), CancellationToken.None));
            Assert.False(pending.Finished.Task.IsCompleted);
        }
        finally { await output.DisposeAsync(); }
        Assert.True(output.Completion.IsCompletedSuccessfully);
        Assert.True(pending.Finished.Task.IsCompletedSuccessfully);
        Assert.True(pending.Disposed);
        Assert.True(ended.Disposed);
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsAnOutputTimeout()
    {
        var pending = new PendingReader();
        await using var output = new ProcessOutput(pending, new StringReader(""), _ => { }, _ => { }, lines: true);
        using var cancellation = new CancellationTokenSource();
        await pending.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => output.DrainAsync(TimeSpan.FromSeconds(5), cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AReaderOrCallbackFailureCancelsItsSiblingAndRemainsObservable(bool callback)
    {
        var pending = new PendingReader();
        var failing = new ControlledReader();
        var failure = new IOException("output failed");
        await using var output = new ProcessOutput(pending, failing, _ => { }, _ => throw failure, lines: true);
        await Task.WhenAll(pending.Started.Task, failing.Started.Task).WaitAsync(TimeSpan.FromSeconds(5));
        if (callback) failing.Line.SetResult("trigger callback");
        else failing.Line.SetException(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => output.Completion.WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.True(pending.Finished.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ACallbackTimeoutIsAnErrorRatherThanTheDrainDeadline()
    {
        var failure = new TimeoutException("callback timed out");
        await using var output = new ProcessOutput(new StringReader("line"), new StringReader(""), _ => throw failure, _ => { }, lines: true);
        Assert.Same(failure, await Assert.ThrowsAsync<TimeoutException>(() => output.DrainAsync(TimeSpan.FromSeconds(5), CancellationToken.None)));
    }

    [Fact]
    public async Task DisposalLeavesNoReaderThatCanDeliverAnotherCallback()
    {
        var reader = new ControlledReader();
        var received = new StringBuilder();
        var output = new ProcessOutput(reader, new StringReader(""), line => received.Append(line), _ => { }, lines: true);
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await output.DisposeAsync();
        reader.Line.SetResult("too late");
        Assert.True(output.Completion.IsCompletedSuccessfully);
        Assert.Empty(received.ToString());
    }

    private sealed class TrackedReader(string text) : StringReader(text)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class ControlledReader : TextReader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Line { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return await Line.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class PendingReader : TextReader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) { await Wait(cancellationToken); return null; }
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default) { await Wait(cancellationToken); return 0; }
        private async Task Wait(CancellationToken cancellation)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellation); }
            finally { Finished.TrySetResult(); }
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
