using System.Collections.Concurrent;
using System.Diagnostics;
using Meridian.Mediator.Publishing;
using Meridian.Mediator.Streaming;
using Meridian.Mediator.Wrappers;

namespace Meridian.Mediator;

/// <summary>
/// Default mediator implementation. Sends requests through the pipeline and publishes notifications.
/// Uses a static handler cache for high-performance type-to-wrapper lookups.
/// Emits <see cref="Activity"/> spans via <see cref="ActivitySource"/> for OpenTelemetry integration.
/// </summary>
public class Mediator : IMediator
{
    private static readonly ConcurrentDictionary<Type, RequestHandlerBase> _requestHandlers = new();
    private static readonly ConcurrentDictionary<Type, NotificationHandlerWrapperBase> _notificationHandlers = new();
    private static readonly ConcurrentDictionary<Type, StreamRequestHandlerBase> _streamRequestHandlers = new();

    /// <summary>
    /// The <see cref="ActivitySource"/> used for emitting OpenTelemetry-compatible traces.
    /// Subscribe to "Meridian.Mediator" in your telemetry configuration to receive spans.
    /// Zero cost when no listener is attached.
    /// </summary>
    internal static readonly ActivitySource ActivitySourceInstance = new("Meridian.Mediator", "1.0.0");

    private readonly IServiceProvider _serviceProvider;
    private readonly INotificationPublisher _publisher;

    /// <summary>
    /// Initializes a new instance of the <see cref="Mediator"/> class with the default <see cref="ForeachAwaitPublisher"/>.
    /// </summary>
    /// <param name="serviceProvider">The service provider for resolving handlers and behaviors.</param>
    public Mediator(IServiceProvider serviceProvider)
        : this(serviceProvider, new ForeachAwaitPublisher())
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Mediator"/> class with a custom notification publisher.
    /// </summary>
    /// <param name="serviceProvider">The service provider for resolving handlers and behaviors.</param>
    /// <param name="publisher">The notification publisher strategy to use.</param>
    public Mediator(IServiceProvider serviceProvider, INotificationPublisher publisher)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    }

    /// <inheritdoc/>
    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();

        using var activity = ActivitySourceInstance.StartActivity($"Mediator.Send {requestType.Name}");
        activity?.SetTag("meridian.request_type", requestType.FullName);
        activity?.SetTag("meridian.response_type", typeof(TResponse).FullName);

        var handler = (RequestHandlerWrapper<TResponse>)_requestHandlers.GetOrAdd(requestType,
            static t => CreateRequestHandler(t, typeof(RequestHandlerWrapperImpl<,>)));

        return handler.Handle(request, _serviceProvider, cancellationToken);
    }

    /// <inheritdoc/>
    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();

        using var activity = ActivitySourceInstance.StartActivity($"Mediator.Send {requestType.Name}");
        activity?.SetTag("meridian.request_type", requestType.FullName);

        var handler = (RequestHandlerWrapper<Unit>)_requestHandlers.GetOrAdd(requestType,
            static t => CreateRequestHandler(t, typeof(RequestHandlerWrapperImpl<,>)));

        return handler.Handle(request, _serviceProvider, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<object?> Send(object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();

        using var activity = ActivitySourceInstance.StartActivity($"Mediator.Send {requestType.Name}");
        activity?.SetTag("meridian.request_type", requestType.FullName);

        var handler = _requestHandlers.GetOrAdd(requestType,
            static t => CreateRequestHandler(t, typeof(RequestHandlerWrapperImpl<,>)));

        return handler.Handle(request, _serviceProvider, cancellationToken);
    }

    /// <inheritdoc/>
    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);

        using var activity = ActivitySourceInstance.StartActivity($"Mediator.Publish {typeof(TNotification).Name}");
        activity?.SetTag("meridian.notification_type", typeof(TNotification).FullName);

        return PublishNotification(notification, cancellationToken);
    }

    /// <inheritdoc/>
    public Task Publish(object notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (notification is not INotification notif)
        {
            throw new ArgumentException($"Object of type {notification.GetType()} does not implement {nameof(INotification)}.", nameof(notification));
        }

        using var activity = ActivitySourceInstance.StartActivity($"Mediator.Publish {notification.GetType().Name}");
        activity?.SetTag("meridian.notification_type", notification.GetType().FullName);

        return PublishNotification(notif, cancellationToken);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();

        using var activity = ActivitySourceInstance.StartActivity($"Mediator.CreateStream {requestType.Name}");
        activity?.SetTag("meridian.stream_request_type", requestType.FullName);
        activity?.SetTag("meridian.response_type", typeof(TResponse).FullName);

        var handler = (StreamRequestHandlerWrapper<TResponse>)_streamRequestHandlers.GetOrAdd(requestType,
            static t => CreateStreamHandler(t));

        return handler.Handle(request, _serviceProvider, cancellationToken);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();

        using var activity = ActivitySourceInstance.StartActivity($"Mediator.CreateStream {requestType.Name}");
        activity?.SetTag("meridian.stream_request_type", requestType.FullName);

        var handler = _streamRequestHandlers.GetOrAdd(requestType, static t => CreateStreamHandler(t));

        return handler.Handle(request, _serviceProvider, cancellationToken);
    }

    private Task PublishNotification(INotification notification, CancellationToken cancellationToken)
    {
        var notificationType = notification.GetType();
        var handler = _notificationHandlers.GetOrAdd(notificationType,
            static t =>
            {
                var wrapperType = typeof(NotificationHandlerWrapperImpl<>).MakeGenericType(t);
                return (NotificationHandlerWrapperBase)Activator.CreateInstance(wrapperType)!;
            });

        return handler.Handle(notification, _serviceProvider, _publisher, cancellationToken);
    }

    private static RequestHandlerBase CreateRequestHandler(Type requestType, Type wrapperGenericType)
    {
        var responseType = GetResponseType(requestType);
        var wrapperType = wrapperGenericType.MakeGenericType(requestType, responseType);
        return (RequestHandlerBase)Activator.CreateInstance(wrapperType)!;
    }

    private static StreamRequestHandlerBase CreateStreamHandler(Type requestType)
    {
        var responseType = GetStreamResponseType(requestType);
        var wrapperType = typeof(StreamRequestHandlerWrapperImpl<,>).MakeGenericType(requestType, responseType);
        return (StreamRequestHandlerBase)Activator.CreateInstance(wrapperType)!;
    }

    private static Type GetResponseType(Type requestType)
    {
        // Walk the interface hierarchy to find IRequest<TResponse>
        foreach (var interfaceType in requestType.GetInterfaces())
        {
            if (interfaceType.IsGenericType && interfaceType.GetGenericTypeDefinition() == typeof(IRequest<>))
            {
                return interfaceType.GetGenericArguments()[0];
            }
        }

        throw new InvalidOperationException(
            $"Type {requestType} does not implement {typeof(IRequest<>).Name}.");
    }

    private static Type GetStreamResponseType(Type requestType)
    {
        foreach (var interfaceType in requestType.GetInterfaces())
        {
            if (interfaceType.IsGenericType && interfaceType.GetGenericTypeDefinition() == typeof(IStreamRequest<>))
            {
                return interfaceType.GetGenericArguments()[0];
            }
        }

        throw new InvalidOperationException(
            $"Type {requestType} does not implement {typeof(IStreamRequest<>).Name}.");
    }
}
