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
        // ⚡ Bolt: Fast path for zero handlers - prevents enumerator allocation
        if (handlerExecutors is ICollection<NotificationHandlerExecutor> { Count: 0 } ||
            handlerExecutors is IReadOnlyCollection<NotificationHandlerExecutor> { Count: 0 })
        {
            return;
        }

        // ⚡ Bolt: Fast path for single handler to avoid foreach enumerator allocation
        if (handlerExecutors is IList<NotificationHandlerExecutor> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                await list[i].HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if (handlerExecutors is IReadOnlyList<NotificationHandlerExecutor> roList)
        {
            for (int i = 0; i < roList.Count; i++)
            {
                await roList[i].HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        foreach (var handler in handlerExecutors)
        {
            await handler.HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
        }
    }
}
