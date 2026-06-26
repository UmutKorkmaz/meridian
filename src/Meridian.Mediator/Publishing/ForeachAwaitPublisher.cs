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
        if (handlerExecutors is ICollection<NotificationHandlerExecutor> { Count: 0 } ||
            handlerExecutors is IReadOnlyCollection<NotificationHandlerExecutor> { Count: 0 })
        {
            return;
        }

        // ⚡ Bolt: Use for loop to avoid IEnumerator allocation when possible
        if (handlerExecutors is IReadOnlyList<NotificationHandlerExecutor> roList)
        {
            for (int i = 0; i < roList.Count; i++)
            {
                await roList[i].HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (handlerExecutors is IList<NotificationHandlerExecutor> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                await list[i].HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            foreach (var handler in handlerExecutors)
            {
                await handler.HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
