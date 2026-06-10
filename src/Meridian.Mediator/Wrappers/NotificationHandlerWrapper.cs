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

        IEnumerable<NotificationHandlerExecutor> executors;

        if (handlers is INotificationHandler<TNotification>[] handlerArray)
        {
            var execs = new NotificationHandlerExecutor[handlerArray.Length];
            for (int i = 0; i < handlerArray.Length; i++)
            {
                var h = handlerArray[i];
                execs[i] = new NotificationHandlerExecutor(h, (notif, ct) => h.Handle((TNotification)notif, ct));
            }
            executors = execs;
        }
        else if (handlers is ICollection<INotificationHandler<TNotification>> handlerCol)
        {
            var execs = new NotificationHandlerExecutor[handlerCol.Count];
            int i = 0;
            foreach (var h in handlerCol)
            {
                execs[i++] = new NotificationHandlerExecutor(h, (notif, ct) => h.Handle((TNotification)notif, ct));
            }
            executors = execs;
        }
        else
        {
            var execList = new List<NotificationHandlerExecutor>();
            foreach (var h in handlers)
            {
                execList.Add(new NotificationHandlerExecutor(h, (notif, ct) => h.Handle((TNotification)notif, ct)));
            }
            executors = execList;
        }

        return publisher.Publish(executors, notification, cancellationToken);
    }
}
