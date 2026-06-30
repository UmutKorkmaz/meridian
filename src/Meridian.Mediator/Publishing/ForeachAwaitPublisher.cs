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
        // ⚡ Bolt: Type-check for IList/IReadOnlyList to avoid IEnumerator allocations in hot path
        if (handlerExecutors is IList<NotificationHandlerExecutor> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                await list[i].HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (handlerExecutors is IReadOnlyList<NotificationHandlerExecutor> readOnlyList)
        {
            for (int i = 0; i < readOnlyList.Count; i++)
            {
                await readOnlyList[i].HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
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
