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
        // ⚡ Bolt: Fast path for zero-allocation enumeration
        // When collection is a list or array, using a for-loop avoids IEnumerator allocation
        if (handlerExecutors is IList<NotificationHandlerExecutor> list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                await list[i].HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        if (handlerExecutors is IReadOnlyList<NotificationHandlerExecutor> readOnlyList)
        {
            for (var i = 0; i < readOnlyList.Count; i++)
            {
                await readOnlyList[i].HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        foreach (var handler in handlerExecutors)
        {
            await handler.HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
        }
    }
}
