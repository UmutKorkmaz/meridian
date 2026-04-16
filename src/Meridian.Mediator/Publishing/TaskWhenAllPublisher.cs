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
        List<Task> tasks;
        using var limiter = _maxDegreeOfParallelism == -1 ? null : new SemaphoreSlim(_maxDegreeOfParallelism);
        tasks = limiter == null
            ? handlerExecutors.Select(handler => handler.HandlerCallback(notification, cancellationToken)).ToList()
            : handlerExecutors.Select(handler => RunBounded(handler, notification, cancellationToken, limiter)).ToList();

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
        var exceptions = new List<Exception>();

        List<Task> tasks;
        using var limiter = _maxDegreeOfParallelism == -1 ? null : new SemaphoreSlim(_maxDegreeOfParallelism);
        tasks = limiter == null
            ? handlerExecutors.Select(handler => RunResilient(handler, notification, cancellationToken, exceptions)).ToList()
            : handlerExecutors.Select(handler => RunBoundedResilient(handler, notification, cancellationToken, limiter, exceptions)).ToList();

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
