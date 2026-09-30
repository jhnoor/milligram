using Milligram.Adapters.Web;

namespace Milligram.Tests.Adapters;

public class BrowserOutputWindowTests
{
    [Fact]
    public async Task OnlyAcknowledgedBytesBecomeAvailableToTheNextWrite()
    {
        var window = new BrowserOutputWindow();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await window.ReserveAsync(BrowserOutputWindow.Capacity, deadline.Token);
        var waiting = window.ReserveAsync(2, deadline.Token);
        Assert.False(waiting.IsCompleted);
        window.Acknowledge(1);
        Assert.False(waiting.IsCompleted);
        window.Acknowledge(1);
        await waiting;
        window.Acknowledge(BrowserOutputWindow.Capacity);
        Assert.Throws<BrowserTerminalException>(() => window.Acknowledge(1));
        await window.ReserveAsync(BrowserOutputWindow.Capacity, deadline.Token);
    }

    [Fact]
    public async Task CancellingAWaitingWriteDoesNotSpendAnyCredit()
    {
        var window = new BrowserOutputWindow();
        using var cancellation = new CancellationTokenSource();
        await window.ReserveAsync(BrowserOutputWindow.Capacity, cancellation.Token);
        var waiting = window.ReserveAsync(1, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        window.Acknowledge(BrowserOutputWindow.Capacity);
        Assert.Throws<BrowserTerminalException>(() => window.Acknowledge(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => window.ReserveAsync(1, cancellation.Token));
        Assert.Throws<BrowserTerminalException>(() => window.Acknowledge(1));
        await window.ReserveAsync(1, CancellationToken.None);
        window.Acknowledge(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public async Task InvalidAcknowledgementsCannotCreateCredit(int bytes)
    {
        var window = new BrowserOutputWindow();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await window.ReserveAsync(1, deadline.Token);
        Assert.Throws<BrowserTerminalException>(() => window.Acknowledge(bytes));
        window.Acknowledge(1);
        await window.ReserveAsync(BrowserOutputWindow.Capacity, deadline.Token);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(BrowserOutputWindow.Capacity + 1)]
    public async Task AWriteMustFitWithinOneWindow(int bytes)
    {
        var window = new BrowserOutputWindow();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => window.ReserveAsync(bytes, deadline.Token));
    }
}
