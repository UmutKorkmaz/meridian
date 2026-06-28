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
        // ⚡ Bolt: Fast path for zero handlers (array covariance check is fastest)
        if (handlerExecutors is object[] { Length: 0 } || handlerExecutors is ICollection<NotificationHandlerExecutor> { Count: 0 })
        {
            return;
        }

        // ⚡ Bolt: Zero-allocation enumeration via IList/IReadOnlyList for-loop
        if (handlerExecutors is IReadOnlyList<NotificationHandlerExecutor> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                await list[i].HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (handlerExecutors is IList<NotificationHandlerExecutor> ilist)
        {
            for (int i = 0; i < ilist.Count; i++)
            {
                await ilist[i].HandlerCallback(notification, cancellationToken).ConfigureAwait(false);
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
