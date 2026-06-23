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
            if (handlerArray.Length == 0)
            {
                return publisher.Publish(Array.Empty<NotificationHandlerExecutor>(), notification, cancellationToken);
            }

            var executorArray = new NotificationHandlerExecutor[handlerArray.Length];
            for (int i = 0; i < handlerArray.Length; i++)
            {
                var handler = handlerArray[i];
                executorArray[i] = new NotificationHandlerExecutor(
                    handler,
                    (notif, ct) => handler.Handle((TNotification)notif, ct));
            }

            executors = executorArray;
        }
        else if (handlers is ICollection<INotificationHandler<TNotification>> collection)
        {
            if (collection.Count == 0)
            {
                return publisher.Publish(Array.Empty<NotificationHandlerExecutor>(), notification, cancellationToken);
            }

            var executorArray = new NotificationHandlerExecutor[collection.Count];
            var i = 0;
            foreach (var handler in collection)
            {
                executorArray[i++] = new NotificationHandlerExecutor(
                    handler,
                    (notif, ct) => handler.Handle((TNotification)notif, ct));
            }

            executors = executorArray;
        }
        else if (handlers is IReadOnlyCollection<INotificationHandler<TNotification>> roCollection)
        {
            if (roCollection.Count == 0)
            {
                return publisher.Publish(Array.Empty<NotificationHandlerExecutor>(), notification, cancellationToken);
            }

            var executorArray = new NotificationHandlerExecutor[roCollection.Count];
            var i = 0;
            foreach (var handler in handlers)
            {
                executorArray[i++] = new NotificationHandlerExecutor(
                    handler,
                    (notif, ct) => handler.Handle((TNotification)notif, ct));
            }

            executors = executorArray;
        }
        else
        {
            var executorList = new List<NotificationHandlerExecutor>();
            foreach (var handler in handlers)
            {
                executorList.Add(new NotificationHandlerExecutor(
                    handler,
                    (notif, ct) => handler.Handle((TNotification)notif, ct)));
            }

            executors = executorList;
        }

        return publisher.Publish(executors, notification, cancellationToken);
    }
}
