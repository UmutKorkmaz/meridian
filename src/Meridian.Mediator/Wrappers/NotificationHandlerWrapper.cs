using Meridian.Mediator.Publishing;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Wrappers;

/// <summary>
/// Abstract base class for notification handler wrappers.
/// </summary>
public abstract class NotificationHandlerWrapperBase
{
    /// <summary>
    /// Handles a notification by resolving all registered handlers and publishing to them.
    /// </summary>
    /// <param name="notification">The notification.</param>
    /// <param name="serviceProvider">The service provider.</param>
    /// <param name="publisher">The notification publisher strategy.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the publish operation.</returns>
    public abstract Task Handle(INotification notification, IServiceProvider serviceProvider,
        INotificationPublisher publisher, CancellationToken cancellationToken);
}

/// <summary>
/// Concrete notification handler wrapper for a specific notification type.
/// </summary>
/// <typeparam name="TNotification">The notification type.</typeparam>
public class NotificationHandlerWrapperImpl<TNotification> : NotificationHandlerWrapperBase
    where TNotification : INotification
{
    /// <inheritdoc/>
    public override Task Handle(INotification notification, IServiceProvider serviceProvider,
        INotificationPublisher publisher, CancellationToken cancellationToken)
    {
        var handlers = serviceProvider.GetServices<INotificationHandler<TNotification>>();

        // Optimization: Avoid LINQ .Select(...).ToList() allocations in the hot path.
        // MS.DI returns arrays for GetServices(), which implement ICollection<T>.
        List<NotificationHandlerExecutor> executors;
        if (handlers is ICollection<INotificationHandler<TNotification>> collection)
        {
            executors = new List<NotificationHandlerExecutor>(collection.Count);
        }
        else if (handlers is IReadOnlyCollection<INotificationHandler<TNotification>> roCollection)
        {
            executors = new List<NotificationHandlerExecutor>(roCollection.Count);
        }
        else
        {
            executors = new List<NotificationHandlerExecutor>();
        }

        foreach (var handler in handlers)
        {
            executors.Add(new NotificationHandlerExecutor(
                handler,
                (notif, ct) => handler.Handle((TNotification)notif, ct)));
        }

        return publisher.Publish(executors, notification, cancellationToken);
    }
}
