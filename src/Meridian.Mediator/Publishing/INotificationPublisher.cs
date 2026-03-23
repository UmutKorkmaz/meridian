namespace Meridian.Mediator.Publishing;

/// <summary>
/// Defines a strategy for publishing notifications to multiple handlers.
/// </summary>
public interface INotificationPublisher
{
    /// <summary>
    /// Publishes a notification to the given handler executors.
    /// </summary>
    /// <param name="handlerExecutors">The handler executors to invoke.</param>
    /// <param name="notification">The notification being published.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the publish operation.</returns>
    Task Publish(IEnumerable<NotificationHandlerExecutor> handlerExecutors, INotification notification, CancellationToken cancellationToken);
}
