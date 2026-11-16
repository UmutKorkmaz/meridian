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

        Assert.Equal(20, started);
        Assert.Equal(0, active);
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
        Assert.Equal(0, active);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(-1)]
    public async Task TaskWhenAllPublisher_All_Handlers_Canceled_Preserves_Cancellation(int concurrency)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new OperationCanceledException("handler cancellation", cancellation.Token);
        var started = 0;
        var executors = Enumerable.Range(0, 2)
            .Select(_ => new NotificationHandlerExecutor(new object(), async (_, token) =>
            {
                if (Interlocked.Increment(ref started) == 2)
                    entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    throw original;
                }
            })).ToArray();

        var publication = new TaskWhenAllPublisher(concurrency)
            .Publish(executors, new TestNotification(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publication);
        Assert.Same(original, exception);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.True(publication.IsCanceled);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(-1)]
    public async Task TaskWhenAllPublisher_Multiple_Faults_Preserve_Every_Failure_Identity(int concurrency)
    {
        Exception[] failures = { new InvalidOperationException("first"), new ArgumentException("second"),
            new FormatException("third") };
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var completed = 0;
        var executors = failures.Select(failure => new NotificationHandlerExecutor(new object(), async (_, token) =>
        {
            if (Interlocked.Increment(ref entered) == 3)
                allEntered.TrySetResult();
            await gate.Task.WaitAsync(token);
            Interlocked.Increment(ref completed);
            throw failure;
        })).ToArray();

        var publication = new TaskWhenAllPublisher(concurrency).Publish(executors, new TestNotification(), CancellationToken.None);
        await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.TrySetResult();
        var observed = await Assert.ThrowsAsync<AggregateException>(() => publication);

        Assert.Equal(3, completed);
        Assert.Equal(3, observed.InnerExceptions.Count);
        foreach (var failure in failures)
            Assert.Contains(observed.InnerExceptions, exception => ReferenceEquals(failure, exception));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(-1)]
    public async Task TaskWhenAllPublisher_Single_Fault_With_Cancellation_Preserves_Original(int concurrency)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var failure = new InvalidOperationException("original");
        var executors = new[]
        {
            new NotificationHandlerExecutor(new object(), (_, _) => Task.FromCanceled(cancellation.Token)),
            new NotificationHandlerExecutor(new object(), (_, _) => Task.FromException(failure))
        };
        // Handler-local cancellation and a real fault are both awaited; the fault wins as in Task.WhenAll.
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TaskWhenAllPublisher(concurrency).Publish(executors, new TestNotification(), CancellationToken.None));
        Assert.Same(failure, observed);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(-1)]
    public async Task TaskWhenAllPublisherPreservesSoleFaultedCancellationAfterCanceledTask(int concurrency)
    {
        using var canceledHandler = new CancellationTokenSource();
        using var faultedHandler = new CancellationTokenSource();
        canceledHandler.Cancel();
        faultedHandler.Cancel();
        var original = new OperationCanceledException("faulted callback", faultedHandler.Token);
        var faultedTask = Task.FromException(original);
        Assert.True(faultedTask.IsFaulted);
        Assert.Same(original, Assert.Single(faultedTask.Exception!.InnerExceptions));
        var executors = new[]
        {
            new NotificationHandlerExecutor(new object(), (_, _) => Task.FromCanceled(canceledHandler.Token)),
            new NotificationHandlerExecutor(new object(), (_, _) => faultedTask)
        };

        var publication = new TaskWhenAllPublisher(concurrency)
            .Publish(executors, new TestNotification(), CancellationToken.None);
        var observed = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publication);
        Assert.Same(original, observed);
        Assert.Equal(faultedHandler.Token, observed.CancellationToken);
        Assert.True(publication.IsCanceled);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(-1)]
    public async Task TaskWhenAllPublisherContinuesAfterSynchronousThrow(int concurrency)
    {
        var firstHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var synchronousFailure = new InvalidOperationException("synchronous failure");
        var asynchronousFailure = new ArgumentException("returned-task failure");
        var invoked = 0;
        var executors = new[]
        {
            new NotificationHandlerExecutor(new object(), (_, _) =>
            {
                Interlocked.Increment(ref invoked);
                return firstHandler.Task;
            }),
            new NotificationHandlerExecutor(new object(), (_, _) =>
            {
                Interlocked.Increment(ref invoked);
                throw synchronousFailure;
            }),
            new NotificationHandlerExecutor(new object(), (_, _) =>
            {
                Interlocked.Increment(ref invoked);
                return Task.FromException(asynchronousFailure);
            })
        };

        var publication = new TaskWhenAllPublisher(concurrency)
            .Publish(executors, new TestNotification(), CancellationToken.None);
        try
        {
            Assert.Equal(3, invoked);
            Assert.False(publication.IsCompleted);
            firstHandler.TrySetResult();

            var observed = await Assert.ThrowsAsync<AggregateException>(() => publication);
            Assert.Equal(2, observed.InnerExceptions.Count);
            Assert.Contains(observed.InnerExceptions, failure => ReferenceEquals(synchronousFailure, failure));
            Assert.Contains(observed.InnerExceptions, failure => ReferenceEquals(asynchronousFailure, failure));
        }
        finally
        {
            firstHandler.TrySetResult();
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(-1)]
    public async Task TaskWhenAllPublisherSynchronousCancellationPreservesIdentity(int concurrency)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var original = new OperationCanceledException("synchronous cancellation", cancellation.Token);
        var invoked = 0;
        var executors = Enumerable.Range(0, 2).Select(_ =>
            new NotificationHandlerExecutor(new object(), (_, _) =>
            {
                Interlocked.Increment(ref invoked);
                throw original;
            })).ToArray();

        var publication = new TaskWhenAllPublisher(concurrency)
            .Publish(executors, new TestNotification(), CancellationToken.None);
        var observed = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publication);
        Assert.Equal(2, invoked);
        Assert.Same(original, observed);
        Assert.Equal(cancellation.Token, observed.CancellationToken);
        Assert.True(publication.IsCanceled);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(-1)]
    public async Task TaskWhenAllPublisherPreservesEveryFaultFromReturnedTask(int concurrency)
    {
        var firstHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFailure = new InvalidOperationException("first child");
        var secondFailure = new ArgumentException("second child");
        var otherFailure = new FormatException("other handler");
        var multipleFaults = Task.WhenAll(Task.FromException(firstFailure), Task.FromException(secondFailure));
        Assert.Equal(2, multipleFaults.Exception!.InnerExceptions.Count);
        var invoked = 0;
        var executors = new[]
        {
            new NotificationHandlerExecutor(new object(), (_, _) =>
            {
                Interlocked.Increment(ref invoked);
                return firstHandler.Task;
            }),
            new NotificationHandlerExecutor(new object(), (_, _) =>
            {
                Interlocked.Increment(ref invoked);
                return multipleFaults;
            }),
            new NotificationHandlerExecutor(new object(), (_, _) =>
            {
                Interlocked.Increment(ref invoked);
                return Task.FromException(otherFailure);
            })
        };
        var publication = new TaskWhenAllPublisher(concurrency)
            .Publish(executors, new TestNotification(), CancellationToken.None);
        try
        {
            Assert.Equal(3, invoked);
            Assert.False(publication.IsCompleted);
            firstHandler.TrySetResult();
            var observed = await Assert.ThrowsAsync<AggregateException>(() => publication);
            Assert.Equal(3, observed.InnerExceptions.Count);
            Assert.Contains(observed.InnerExceptions, failure => ReferenceEquals(firstFailure, failure));
            Assert.Contains(observed.InnerExceptions, failure => ReferenceEquals(secondFailure, failure));
            Assert.Contains(observed.InnerExceptions, failure => ReferenceEquals(otherFailure, failure));
        }
        finally
        {
            firstHandler.TrySetResult();
        }
    }

    [Theory]
    [InlineData(16)]
    [InlineData(-1)]
    public async Task TaskWhenAllPublisherPreservesExplicitAggregateIdentity(int concurrency)
    {
        var original = new AggregateException(new InvalidOperationException("nested"), new ArgumentException("nested"));
        var otherFailure = new FormatException("other handler");
        var executors = new[]
        {
            new NotificationHandlerExecutor(new object(), (_, _) => throw original),
            new NotificationHandlerExecutor(new object(), (_, _) => Task.FromException(otherFailure))
        };
        var observed = await Assert.ThrowsAsync<AggregateException>(() =>
            new TaskWhenAllPublisher(concurrency).Publish(executors, new TestNotification(), CancellationToken.None));
        Assert.Equal(2, observed.InnerExceptions.Count);
        Assert.Contains(observed.InnerExceptions, failure => ReferenceEquals(original, failure));
        Assert.Contains(observed.InnerExceptions, failure => ReferenceEquals(otherFailure, failure));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(-1)]
    public async Task TaskWhenAllPublisherRetainsFaultedCancellationExceptionAndOmitsCanceledTask(int concurrency)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancellationFailure = new OperationCanceledException("faulted callback", cancellation.Token);
        var otherFailure = new InvalidOperationException("other fault");
        var faultedTask = Task.FromException(cancellationFailure);
        var canceledTask = Task.FromCanceled(cancellation.Token);
        Assert.True(faultedTask.IsFaulted);
        Assert.True(canceledTask.IsCanceled);
        var executors = new[]
        {
            new NotificationHandlerExecutor(new object(), (_, _) => faultedTask),
            new NotificationHandlerExecutor(new object(), (_, _) => canceledTask),
            new NotificationHandlerExecutor(new object(), (_, _) => Task.FromException(otherFailure))
        };
        var observed = await Assert.ThrowsAsync<AggregateException>(() =>
            new TaskWhenAllPublisher(concurrency).Publish(executors, new TestNotification(), CancellationToken.None));
        Assert.Equal(2, observed.InnerExceptions.Count);
        Assert.Contains(observed.InnerExceptions, failure => ReferenceEquals(cancellationFailure, failure));
        Assert.Contains(observed.InnerExceptions, failure => ReferenceEquals(otherFailure, failure));
    }

    [Fact]
    public async Task TaskWhenAllPublisherCancellationPreservesPermitBoundAndStopsQueuedWork()
    {
        using var cancellation = new CancellationTokenSource();
        var firstHandlers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = 0;
        var started = 0;
        var executors = Enumerable.Range(0, 20).Select(_ =>
            new NotificationHandlerExecutor(new object(), (_, token) =>
            {
                Interlocked.Increment(ref invoked);
                Assert.Equal(cancellation.Token, token);
                token.ThrowIfCancellationRequested();
                Interlocked.Increment(ref started);
                return firstHandlers.Task;
            })).ToArray();
        var publication = new TaskWhenAllPublisher().Publish(executors, new TestNotification(), cancellation.Token);
        try
        {
            Assert.Equal(16, invoked);
            Assert.Equal(16, started);
            Assert.False(publication.IsCompleted);
            cancellation.Cancel();
            Assert.False(publication.IsCompleted);
            firstHandlers.TrySetResult();
            var observed = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publication);
            Assert.Equal(cancellation.Token, observed.CancellationToken);
            Assert.True(publication.IsCanceled);
            Assert.Equal(16, started);
            Assert.InRange(invoked, 16, 20);
        }
        finally
        {
            firstHandlers.TrySetResult();
        }
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
