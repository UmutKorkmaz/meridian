using Meridian.Mediator.Publishing;

namespace Meridian.Mediator.Tests;

public class PublishingConcurrencyTests
{
    [Fact]
    public async Task TaskWhenAllPublisher_Default_FanOut_Is_Bounded()
    {
        var publisher = new TaskWhenAllPublisher();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var active = 0;
        var maxActive = 0;
        var reachedDefaultCap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var executors = Enumerable.Range(0, 20)
            .Select(_ => new NotificationHandlerExecutor(new object(), async (_, cancellationToken) =>
            {
                var currentActive = Interlocked.Increment(ref active);
                UpdateMax(ref maxActive, currentActive);
                if (Interlocked.Increment(ref started) == 16)
                {
                    reachedDefaultCap.TrySetResult();
                }

                try
                {
                    await gate.Task.WaitAsync(cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }))
            .ToList();

        var publishTask = publisher.Publish(executors, new TestNotification(), CancellationToken.None);

        await reachedDefaultCap.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(16, Volatile.Read(ref started));

        gate.TrySetResult();
        await publishTask;

        Assert.True(maxActive <= 16, $"Expected max concurrency <= 16, observed {maxActive}.");
    }

    [Fact]
    public async Task TaskWhenAllPublisher_MinusOne_Restores_Legacy_Unbounded_FanOut()
    {
        var publisher = new TaskWhenAllPublisher(-1);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maxActive = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var executors = Enumerable.Range(0, 20)
            .Select(_ => new NotificationHandlerExecutor(new object(), async (_, cancellationToken) =>
            {
                var currentActive = Interlocked.Increment(ref active);
                UpdateMax(ref maxActive, currentActive);
                if (currentActive == 20)
                {
                    allStarted.TrySetResult();
                }

                try
                {
                    await gate.Task.WaitAsync(cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }))
            .ToList();

        var publishTask = publisher.Publish(executors, new TestNotification(), CancellationToken.None);

        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.TrySetResult();
        await publishTask;

        Assert.Equal(20, maxActive);
    }

    private static void UpdateMax(ref int target, int candidate)
    {
        while (true)
        {
            var observed = Volatile.Read(ref target);
            if (candidate <= observed)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref target, candidate, observed) == observed)
            {
                return;
            }
        }
    }

    private sealed record TestNotification() : INotification;
}
