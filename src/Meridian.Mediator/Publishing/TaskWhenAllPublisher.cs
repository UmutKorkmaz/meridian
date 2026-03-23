namespace Meridian.Mediator.Publishing;

/// <summary>
/// Notification publisher strategy that invokes all handlers in parallel using <see cref="Task.WhenAll(IEnumerable{Task})"/>.
/// All handlers start concurrently and the publish operation completes when all handlers finish.
/// If any handlers throw, all exceptions are surfaced in an <see cref="AggregateException"/>.
/// </summary>
public class TaskWhenAllPublisher : INotificationPublisher
{
    /// <inheritdoc/>
    public async Task Publish(IEnumerable<NotificationHandlerExecutor> handlerExecutors, INotification notification, CancellationToken cancellationToken)
    {
        var tasks = handlerExecutors
            .Select(handler => handler.HandlerCallback(notification, cancellationToken))
            .ToList();

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
}

/// <summary>
/// Resilient notification publisher that runs all handlers in parallel and allows
/// successful handlers to complete even when others fail. All exceptions are collected
/// and thrown as an <see cref="AggregateException"/> after all handlers finish.
/// </summary>
public class ResilientTaskWhenAllPublisher : INotificationPublisher
{
    /// <inheritdoc/>
    public async Task Publish(IEnumerable<NotificationHandlerExecutor> handlerExecutors, INotification notification, CancellationToken cancellationToken)
    {
        var exceptions = new List<Exception>();

        var tasks = handlerExecutors.Select(async handler =>
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
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (exceptions.Count > 0)
        {
            throw new AggregateException(
                $"{exceptions.Count} notification handler(s) failed for {notification.GetType().Name}.",
                exceptions);
        }
    }
}
