namespace Meridian.Mediator.Publishing;

/// <summary>
/// Wraps a notification handler instance with a callback to invoke it.
/// </summary>
/// <param name="HandlerInstance">The actual handler instance.</param>
/// <param name="HandlerCallback">The callback that invokes the handler with a notification and cancellation token.</param>
public record NotificationHandlerExecutor(
    object HandlerInstance,
    Func<INotification, CancellationToken, Task> HandlerCallback);
