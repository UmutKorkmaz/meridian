namespace Meridian.Mediator.Publishing;

/// <summary>
/// Notification publisher strategy that invokes all handlers in parallel using <see cref="Task.WhenAll(IEnumerable{Task})"/>.
/// All handlers start concurrently and the publish operation completes when all handlers finish.
/// If any handlers throw, all exceptions are surfaced in an <see cref="AggregateException"/>.
/// </summary>
public class TaskWhenAllPublisher : INotificationPublisher
{
    private readonly int _maxDegreeOfParallelism;

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskWhenAllPublisher"/> class.
    /// Pass <c>-1</c> to preserve the legacy unbounded fan-out.
    /// </summary>
    public TaskWhenAllPublisher(int maxDegreeOfParallelism = 16)
    {
        if (maxDegreeOfParallelism == 0 || maxDegreeOfParallelism < -1)
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));

        _maxDegreeOfParallelism = maxDegreeOfParallelism;
    }

    /// <inheritdoc/>
    public async Task Publish(IEnumerable<NotificationHandlerExecutor> handlerExecutors, INotification notification, CancellationToken cancellationToken)
    {
        // ⚡ Bolt: Fast path for zero handlers - prevents SemaphoreSlim and List<Task> allocations
        if (handlerExecutors is ICollection<NotificationHandlerExecutor> { Count: 0 } ||
            handlerExecutors is IReadOnlyCollection<NotificationHandlerExecutor> { Count: 0 })
        {
            return;
        }

        List<Task> tasks;
        using var limiter = _maxDegreeOfParallelism == -1 ? null : new SemaphoreSlim(_maxDegreeOfParallelism);

        if (handlerExecutors is ICollection<NotificationHandlerExecutor> collection)
        {
            tasks = new List<Task>(collection.Count);
        }
        else if (handlerExecutors is IReadOnlyCollection<NotificationHandlerExecutor> roCollection)
        {
            tasks = new List<Task>(roCollection.Count);
        }
        else
        {
            tasks = new List<Task>();
        }

        if (limiter == null)
        {
            // ⚡ Bolt: Use indexers for lists to avoid IEnumerator allocations
            if (handlerExecutors is IList<NotificationHandlerExecutor> list)
            {
                for (int i = 0; i < list.Count; i++) tasks.Add(list[i].HandlerCallback(notification, cancellationToken));
            }
            else if (handlerExecutors is IReadOnlyList<NotificationHandlerExecutor> readOnlyList)
            {
                for (int i = 0; i < readOnlyList.Count; i++) tasks.Add(readOnlyList[i].HandlerCallback(notification, cancellationToken));
            }
            else
            {
                foreach (var handler in handlerExecutors) tasks.Add(handler.HandlerCallback(notification, cancellationToken));
            }
        }
        else
        {
            // ⚡ Bolt: Use indexers for lists to avoid IEnumerator allocations
            if (handlerExecutors is IList<NotificationHandlerExecutor> list)
            {
                for (int i = 0; i < list.Count; i++) tasks.Add(RunBounded(list[i], notification, cancellationToken, limiter));
            }
            else if (handlerExecutors is IReadOnlyList<NotificationHandlerExecutor> readOnlyList)
            {
                for (int i = 0; i < readOnlyList.Count; i++) tasks.Add(RunBounded(readOnlyList[i], notification, cancellationToken, limiter));
            }
            else
            {
                foreach (var handler in handlerExecutors) tasks.Add(RunBounded(handler, notification, cancellationToken, limiter));
            }
        }

        if (tasks.Count == 0) return;

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // Task.WhenAll throws only the first exception — collect ALL faulted exceptions.
            var exceptions = tasks
                .Where(t => t.IsFaulted)
                .SelectMany(t => t.Exception!.InnerExceptions)
                .ToList();

            if (exceptions.Count == 1)
                throw; // preserve original stack trace for single exception

            throw new AggregateException(
                $"{exceptions.Count} notification handler(s) failed for {notification.GetType().Name}.",
                exceptions);
        }
    }

    private static async Task RunBounded(
        NotificationHandlerExecutor handler,
        INotification notification,
        CancellationToken cancellationToken,
        SemaphoreSlim limiter)
    {
        await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await handler.HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            limiter.Release();
        }
    }
}

/// <summary>
/// Resilient notification publisher that runs all handlers in parallel and allows
/// successful handlers to complete even when others fail. All exceptions are collected
/// and thrown as an <see cref="AggregateException"/> after all handlers finish.
/// </summary>
public class ResilientTaskWhenAllPublisher : INotificationPublisher
{
    private readonly int _maxDegreeOfParallelism;

    /// <summary>
    /// Initializes a new instance of the <see cref="ResilientTaskWhenAllPublisher"/> class.
    /// Pass <c>-1</c> to preserve the legacy unbounded fan-out.
    /// </summary>
    public ResilientTaskWhenAllPublisher(int maxDegreeOfParallelism = 16)
    {
        if (maxDegreeOfParallelism == 0 || maxDegreeOfParallelism < -1)
            throw new ArgumentOutOfRangeException(nameof(maxDegreeOfParallelism));

        _maxDegreeOfParallelism = maxDegreeOfParallelism;
    }

    /// <inheritdoc/>
    public async Task Publish(IEnumerable<NotificationHandlerExecutor> handlerExecutors, INotification notification, CancellationToken cancellationToken)
    {
        // ⚡ Bolt: Fast path for zero handlers - prevents SemaphoreSlim and List<Task> allocations
        if (handlerExecutors is ICollection<NotificationHandlerExecutor> { Count: 0 } ||
            handlerExecutors is IReadOnlyCollection<NotificationHandlerExecutor> { Count: 0 })
        {
            return;
        }

        var exceptions = new List<Exception>();

        List<Task> tasks;
        using var limiter = _maxDegreeOfParallelism == -1 ? null : new SemaphoreSlim(_maxDegreeOfParallelism);

        if (handlerExecutors is ICollection<NotificationHandlerExecutor> collection)
        {
            tasks = new List<Task>(collection.Count);
        }
        else if (handlerExecutors is IReadOnlyCollection<NotificationHandlerExecutor> roCollection)
        {
            tasks = new List<Task>(roCollection.Count);
        }
        else
        {
            tasks = new List<Task>();
        }

        if (limiter == null)
        {
            // ⚡ Bolt: Use indexers for lists to avoid IEnumerator allocations
            if (handlerExecutors is IList<NotificationHandlerExecutor> list)
            {
                for (int i = 0; i < list.Count; i++) tasks.Add(RunResilient(list[i], notification, cancellationToken, exceptions));
            }
            else if (handlerExecutors is IReadOnlyList<NotificationHandlerExecutor> readOnlyList)
            {
                for (int i = 0; i < readOnlyList.Count; i++) tasks.Add(RunResilient(readOnlyList[i], notification, cancellationToken, exceptions));
            }
            else
            {
                foreach (var handler in handlerExecutors) tasks.Add(RunResilient(handler, notification, cancellationToken, exceptions));
            }
        }
        else
        {
            // ⚡ Bolt: Use indexers for lists to avoid IEnumerator allocations
            if (handlerExecutors is IList<NotificationHandlerExecutor> list)
            {
                for (int i = 0; i < list.Count; i++) tasks.Add(RunBoundedResilient(list[i], notification, cancellationToken, limiter, exceptions));
            }
            else if (handlerExecutors is IReadOnlyList<NotificationHandlerExecutor> readOnlyList)
            {
                for (int i = 0; i < readOnlyList.Count; i++) tasks.Add(RunBoundedResilient(readOnlyList[i], notification, cancellationToken, limiter, exceptions));
            }
            else
            {
                foreach (var handler in handlerExecutors) tasks.Add(RunBoundedResilient(handler, notification, cancellationToken, limiter, exceptions));
            }
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (exceptions.Count > 0)
        {
            throw new AggregateException(
                $"{exceptions.Count} notification handler(s) failed for {notification.GetType().Name}.",
                exceptions);
        }
    }

    private static async Task RunResilient(
        NotificationHandlerExecutor handler,
        INotification notification,
        CancellationToken cancellationToken,
        List<Exception> exceptions)
    {
        try
        {
            await handler.HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (exceptions)
            {
                exceptions.Add(ex);
            }
        }
    }

    private static async Task RunBoundedResilient(
        NotificationHandlerExecutor handler,
        INotification notification,
        CancellationToken cancellationToken,
        SemaphoreSlim limiter,
        List<Exception> exceptions)
    {
        await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunResilient(handler, notification, cancellationToken, exceptions).ConfigureAwait(false);
        }
        finally
        {
            limiter.Release();
        }
    }
}
