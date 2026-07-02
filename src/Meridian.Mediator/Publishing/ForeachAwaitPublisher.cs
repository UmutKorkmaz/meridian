namespace Meridian.Mediator.Publishing;

/// <summary>
/// Default notification publisher strategy that invokes each handler sequentially using foreach/await.
/// Handlers are executed one at a time in registration order.
/// </summary>
public class ForeachAwaitPublisher : INotificationPublisher
{
    /// <inheritdoc/>
    public async Task Publish(IEnumerable<NotificationHandlerExecutor> handlerExecutors, INotification notification, CancellationToken cancellationToken)
    {
        // ⚡ Bolt: Fast path for zero handlers
        if (handlerExecutors is ICollection<NotificationHandlerExecutor> { Count: 0 })
        {
            return;
        }

        foreach (var handler in handlerExecutors)
        {
            await handler.HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
        }
    }
}
