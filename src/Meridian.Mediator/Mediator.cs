using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Meridian.Mediator.Diagnostics;
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
    private static readonly string ActivitySourceVersion =
        typeof(Mediator).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    internal static readonly ActivitySource ActivitySourceInstance = new("Meridian.Mediator", ActivitySourceVersion);

    private readonly Generated.GeneratedMediatorComposition _generated;
    private readonly IServiceProvider _serviceProvider;
    private readonly INotificationPublisher _publisher;
    private readonly MediatorTelemetryOptions _telemetryOptions;
    private readonly MediatorMetrics _metrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="Mediator"/> class with the default <see cref="ForeachAwaitPublisher"/>.
    /// </summary>
    /// <param name="serviceProvider">The service provider for resolving handlers and behaviors.</param>
    public Mediator(IServiceProvider serviceProvider)
        : this(serviceProvider, new ForeachAwaitPublisher(), MediatorTelemetryOptions.Default)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Mediator"/> class with a custom notification publisher.
    /// </summary>
    /// <param name="serviceProvider">The service provider for resolving handlers and behaviors.</param>
    /// <param name="publisher">The notification publisher strategy to use.</param>
    public Mediator(IServiceProvider serviceProvider, INotificationPublisher publisher)
        : this(serviceProvider, publisher, MediatorTelemetryOptions.Default)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Mediator"/> class with
    /// a custom notification publisher and telemetry options.
    /// </summary>
    /// <param name="serviceProvider">The service provider for resolving handlers and behaviors.</param>
    /// <param name="publisher">The notification publisher strategy to use.</param>
    /// <param name="telemetryOptions">Telemetry emission options for failure tags.</param>
    public Mediator(
        IServiceProvider serviceProvider,
        INotificationPublisher publisher,
        MediatorTelemetryOptions telemetryOptions)
        : this(serviceProvider, publisher, telemetryOptions, Generated.GeneratedMediatorComposition.Empty)
    {
    }

    /// <summary>Creates a mediator using an immutable provider-owned generated registry composition.</summary>
    public Mediator(IServiceProvider serviceProvider, INotificationPublisher publisher,
        MediatorTelemetryOptions telemetryOptions, Generated.GeneratedMediatorComposition generated)
        : this(serviceProvider, publisher, telemetryOptions, generated, null)
    {
    }

    /// <summary>
    /// Creates a mediator that reports to a provider-owned <see cref="MediatorMetrics"/> component.
    /// A <c>null</c> component leaves metrics disabled, exactly like the constructors without this parameter.
    /// </summary>
    public Mediator(IServiceProvider serviceProvider, INotificationPublisher publisher,
        MediatorTelemetryOptions telemetryOptions, Generated.GeneratedMediatorComposition generated,
        MediatorMetrics? metrics)
    {
        _metrics = metrics ?? MediatorMetrics.Disabled;
        _generated = generated ?? throw new ArgumentNullException(nameof(generated));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _telemetryOptions = telemetryOptions ?? throw new ArgumentNullException(nameof(telemetryOptions));
    }

    /// <inheritdoc/>
    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        // The unobserved path is kept inline (see SendCore for the metered twin): an extra call frame measurably
        // slowed this hot path, so the body is deliberately not shared.
        if (_metrics.RequestEnabled)
        {
            return SendMetered(request, cancellationToken);
        }

        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var handler = (RequestHandlerWrapper<TResponse>)FindRequestHandler(requestType);

        if (!ActivitySourceInstance.HasListeners())
        {
            return handler.Handle(request, _serviceProvider, cancellationToken);
        }

        return SendWithActivity(request, requestType, handler, cancellationToken);
    }

    private Task<TResponse> SendMetered<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken)
    {
        var operation = _metrics.BeginRequest(request?.GetType());
        Task<TResponse> task;
        try
        {
            task = SendCore(request!, cancellationToken);
        }
        catch (Exception ex)
        {
            _metrics.EndRequest(operation, MediatorMetrics.Classify(ex, cancellationToken));
            throw;
        }

        return _metrics.ObserveRequest(task, operation, cancellationToken);
    }

    // Twin of the inline body in Send, used only by the metered path.
    private Task<TResponse> SendCore<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var handler = (RequestHandlerWrapper<TResponse>)FindRequestHandler(requestType);

        if (!ActivitySourceInstance.HasListeners())
        {
            return handler.Handle(request, _serviceProvider, cancellationToken);
        }

        return SendWithActivity(request, requestType, handler, cancellationToken);
    }

    // The activity closures live in these helpers, not in the public entry points: a lambda that captures a
    // parameter makes the compiler allocate its closure on method entry, i.e. on every call, listener or not.
    private Task<TResponse> SendWithActivity<TResponse>(
        IRequest<TResponse> request, Type requestType, RequestHandlerWrapper<TResponse> handler, CancellationToken cancellationToken)
    {
        var activity = StartRequestActivity(requestType, typeof(TResponse));
        return ExecuteWithActivityAsync(
            activity,
            () => handler.Handle(request, _serviceProvider, cancellationToken),
            cancellationToken);
    }

    private Task<Unit> SendVoidWithActivity<TRequest>(
        TRequest request, Type requestType, RequestHandlerWrapper<Unit> handler, CancellationToken cancellationToken)
        where TRequest : IRequest
    {
        var activity = StartRequestActivity(requestType, null);
        return ExecuteWithActivityAsync(
            activity,
            () => handler.Handle(request, _serviceProvider, cancellationToken),
            cancellationToken);
    }

    private Task<object?> SendObjectWithActivity(
        object request, Type requestType, RequestHandlerBase handler, CancellationToken cancellationToken)
    {
        var activity = StartRequestActivity(requestType, null);
        return ExecuteWithActivityAsync(
            activity,
            () => handler.Handle(request, _serviceProvider, cancellationToken),
            cancellationToken);
    }

    private Task PublishWithActivity(INotification notification, CancellationToken cancellationToken)
    {
        var activity = StartNotificationActivity(notification.GetType());
        return ExecuteWithActivityAsync(
            activity,
            () => PublishNotification(notification, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest
    {
        if (_metrics.RequestEnabled)
        {
            return SendMetered(request, cancellationToken);
        }

        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var handler = (RequestHandlerWrapper<Unit>)FindRequestHandler(requestType);

        if (!ActivitySourceInstance.HasListeners())
        {
            return handler.Handle(request, _serviceProvider, cancellationToken);
        }

        return SendVoidWithActivity(request, requestType, handler, cancellationToken);
    }

    private Task SendMetered<TRequest>(TRequest request, CancellationToken cancellationToken)
        where TRequest : IRequest
    {
        var operation = _metrics.BeginRequest(request?.GetType());
        Task task;
        try
        {
            task = SendCore(request!, cancellationToken);
        }
        catch (Exception ex)
        {
            _metrics.EndRequest(operation, MediatorMetrics.Classify(ex, cancellationToken));
            throw;
        }

        return _metrics.ObserveRequest(task, operation, cancellationToken);
    }

    private Task<Unit> SendCore<TRequest>(TRequest request, CancellationToken cancellationToken)
        where TRequest : IRequest
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var handler = (RequestHandlerWrapper<Unit>)FindRequestHandler(requestType);

        if (!ActivitySourceInstance.HasListeners())
        {
            return handler.Handle(request, _serviceProvider, cancellationToken);
        }

        return SendVoidWithActivity(request, requestType, handler, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<object?> Send(object request, CancellationToken cancellationToken = default)
    {
        if (_metrics.RequestEnabled)
        {
            return SendObjectMetered(request, cancellationToken);
        }

        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var handler = FindRequestHandler(requestType);

        if (!ActivitySourceInstance.HasListeners())
        {
            return handler.Handle(request, _serviceProvider, cancellationToken);
        }

        return SendObjectWithActivity(request, requestType, handler, cancellationToken);
    }

    private Task<object?> SendObjectMetered(object request, CancellationToken cancellationToken)
    {
        var operation = _metrics.BeginRequest(request?.GetType());
        Task<object?> task;
        try
        {
            task = SendObjectCore(request!, cancellationToken);
        }
        catch (Exception ex)
        {
            _metrics.EndRequest(operation, MediatorMetrics.Classify(ex, cancellationToken));
            throw;
        }

        return _metrics.ObserveRequest(task, operation, cancellationToken);
    }

    private Task<object?> SendObjectCore(object request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var handler = FindRequestHandler(requestType);

        if (!ActivitySourceInstance.HasListeners())
        {
            return handler.Handle(request, _serviceProvider, cancellationToken);
        }

        return SendObjectWithActivity(request, requestType, handler, cancellationToken);
    }

    /// <inheritdoc/>
    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        if (_metrics.NotificationEnabled)
        {
            return PublishMetered(notification, cancellationToken);
        }

        ArgumentNullException.ThrowIfNull(notification);

        if (!ActivitySourceInstance.HasListeners())
        {
            return PublishNotification(notification, cancellationToken);
        }

        return PublishWithActivity(notification, cancellationToken);
    }

    private Task PublishMetered<TNotification>(TNotification notification, CancellationToken cancellationToken)
        where TNotification : INotification
    {
        var operation = _metrics.BeginNotification(notification?.GetType());
        Task task;
        try
        {
            task = PublishCore(notification!, cancellationToken);
        }
        catch (Exception ex)
        {
            _metrics.EndNotification(operation, MediatorMetrics.Classify(ex, cancellationToken));
            throw;
        }

        return _metrics.ObserveNotification(task, operation, cancellationToken);
    }

    private Task PublishCore<TNotification>(TNotification notification, CancellationToken cancellationToken)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (!ActivitySourceInstance.HasListeners())
        {
            return PublishNotification(notification, cancellationToken);
        }

        return PublishWithActivity(notification, cancellationToken);
    }

    /// <inheritdoc/>
    public Task Publish(object notification, CancellationToken cancellationToken = default)
    {
        if (_metrics.NotificationEnabled)
        {
            return PublishObjectMetered(notification, cancellationToken);
        }

        ArgumentNullException.ThrowIfNull(notification);

        if (notification is not INotification notif)
        {
            throw new ArgumentException($"Object of type {notification.GetType()} does not implement {nameof(INotification)}.", nameof(notification));
        }

        if (!ActivitySourceInstance.HasListeners())
        {
            return PublishNotification(notif, cancellationToken);
        }

        return PublishWithActivity(notif, cancellationToken);
    }

    private Task PublishObjectMetered(object notification, CancellationToken cancellationToken)
    {
        var operation = _metrics.BeginNotification(notification?.GetType());
        Task task;
        try
        {
            task = PublishObjectCore(notification!, cancellationToken);
        }
        catch (Exception ex)
        {
            _metrics.EndNotification(operation, MediatorMetrics.Classify(ex, cancellationToken));
            throw;
        }

        return _metrics.ObserveNotification(task, operation, cancellationToken);
    }

    private Task PublishObjectCore(object notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (notification is not INotification notif)
        {
            throw new ArgumentException($"Object of type {notification.GetType()} does not implement {nameof(INotification)}.", nameof(notification));
        }

        if (!ActivitySourceInstance.HasListeners())
        {
            return PublishNotification(notif, cancellationToken);
        }

        return PublishWithActivity(notif, cancellationToken);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
        _metrics.StreamEnabled ? CreateStreamMetered(request, cancellationToken) : CreateStreamCore(request, false, cancellationToken);

    private IAsyncEnumerable<TResponse> CreateStreamMetered<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken)
    {
        // Only a failed synchronous dispatch is reported here; each acquired enumerator reports its own enumeration.
        var dispatch = _metrics.BeginStream(request?.GetType());
        try
        {
            return CreateStreamCore(request!, true, cancellationToken);
        }
        catch (Exception ex)
        {
            _metrics.EndStream(dispatch, MediatorMetrics.Classify(ex, cancellationToken), StreamPhase.Dispatch);
            throw;
        }
    }

    private IAsyncEnumerable<TResponse> CreateStreamCore<TResponse>(
        IStreamRequest<TResponse> request, bool metered, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var handler = (StreamRequestHandlerWrapper<TResponse>)FindStreamHandler(requestType);

        if (ActivitySourceInstance.HasListeners())
        {
            return ObservedStream<TResponse>.Deferred(
                _metrics, _telemetryOptions, request, requestType, handler, _serviceProvider, cancellationToken);
        }

        var stream = handler.Handle(request, _serviceProvider, cancellationToken);
        return metered
            ? ObservedStream<TResponse>.Eager(_metrics, _telemetryOptions, requestType, stream, cancellationToken)
            : stream;
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
        _metrics.StreamEnabled ? CreateStreamObjectMetered(request, cancellationToken) : CreateStreamObjectCore(request, false, cancellationToken);

    private IAsyncEnumerable<object?> CreateStreamObjectMetered(object request, CancellationToken cancellationToken)
    {
        var dispatch = _metrics.BeginStream(request?.GetType());
        try
        {
            return CreateStreamObjectCore(request!, true, cancellationToken);
        }
        catch (Exception ex)
        {
            _metrics.EndStream(dispatch, MediatorMetrics.Classify(ex, cancellationToken), StreamPhase.Dispatch);
            throw;
        }
    }

    private IAsyncEnumerable<object?> CreateStreamObjectCore(object request, bool metered, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var handler = FindStreamHandler(requestType);

        if (ActivitySourceInstance.HasListeners())
        {
            return new DeferredObjectStream(
                _metrics, _telemetryOptions, request, requestType, handler, _serviceProvider, cancellationToken);
        }

        var stream = handler.Handle(request, _serviceProvider, cancellationToken);
        return metered
            ? ObservedStream<object?>.Eager(_metrics, _telemetryOptions, requestType, stream, cancellationToken)
            : stream;
    }

    private Task PublishNotification(INotification notification, CancellationToken cancellationToken)
    {
        var handler = FindNotificationHandler(notification.GetType());

        return handler.Handle(notification, _serviceProvider, _publisher, cancellationToken);
    }

    private NotificationHandlerWrapperBase FindNotificationHandler(Type notificationType)
    {
        var generated = _generated.FindNotificationHandler(notificationType);
        if (generated != null) return generated;
        if (_generated.RequireGeneratedDispatch)
            throw new InvalidOperationException($"No generated notification dispatch for {notificationType}. Declare its closed handler and register the matching generated registry.");
        // Native AOT cannot instantiate the legacy wrapper at run time. The constant guard lets the compiler drop
        // the reflective branch; dynamic-code runtimes keep the unchanged non-strict fallback below.
        if (!RuntimeFeature.IsDynamicCodeSupported)
            throw new InvalidOperationException($"No generated notification dispatch for {notificationType}. This runtime cannot create handler wrappers at run time (Native AOT): declare its closed handler in a generated registry and register it with AddGeneratedRegistry.");
        return LegacyNotificationHandler(notificationType);
    }

    [RequiresUnreferencedCode("Legacy notification dispatch creates generic handler wrappers by reflection. Use a generated registry.")]
    [RequiresDynamicCode("Legacy notification dispatch creates generic handler wrappers by reflection. Use a generated registry.")]
    private static NotificationHandlerWrapperBase LegacyNotificationHandler(Type notificationType) =>
        _notificationHandlers.GetOrAdd(notificationType,
            static t =>
            {
                var wrapperType = typeof(NotificationHandlerWrapperImpl<>).MakeGenericType(t);
                return (NotificationHandlerWrapperBase)Activator.CreateInstance(wrapperType)!;
            });

    private StreamRequestHandlerBase FindStreamHandler(Type requestType)
    {
        var generated = _generated.FindStreamHandler(requestType);
        if (generated != null) return generated;
        if (_generated.RequireGeneratedDispatch)
            throw new InvalidOperationException($"No generated stream dispatch for {requestType}. Declare its closed handler and register the matching generated registry.");
        if (!RuntimeFeature.IsDynamicCodeSupported)
            throw new InvalidOperationException($"No generated stream dispatch for {requestType}. This runtime cannot create handler wrappers at run time (Native AOT): declare its closed handler in a generated registry and register it with AddGeneratedRegistry.");
        return LegacyStreamHandler(requestType);
    }

    [RequiresUnreferencedCode("Legacy stream dispatch creates generic handler wrappers by reflection. Use a generated registry.")]
    [RequiresDynamicCode("Legacy stream dispatch creates generic handler wrappers by reflection. Use a generated registry.")]
    private static StreamRequestHandlerBase LegacyStreamHandler(Type requestType) =>
        _streamRequestHandlers.GetOrAdd(requestType, static t => CreateStreamHandler(t));

    private RequestHandlerBase FindRequestHandler(Type requestType)
    {
        var generated = _generated.FindRequestHandler(requestType);
        if (generated != null) return generated;
        if (_generated.RequireGeneratedDispatch)
            throw new InvalidOperationException($"No generated request dispatch for {requestType}. Declare its closed handler and register the matching generated registry.");
        if (!RuntimeFeature.IsDynamicCodeSupported)
            throw new InvalidOperationException($"No generated request dispatch for {requestType}. This runtime cannot create handler wrappers at run time (Native AOT): declare its closed handler in a generated registry and register it with AddGeneratedRegistry.");
        return LegacyRequestHandler(requestType);
    }

    [RequiresUnreferencedCode("Legacy request dispatch creates generic handler wrappers by reflection. Use a generated registry.")]
    [RequiresDynamicCode("Legacy request dispatch creates generic handler wrappers by reflection. Use a generated registry.")]
    private static RequestHandlerBase LegacyRequestHandler(Type requestType) =>
        _requestHandlers.GetOrAdd(requestType, static t => CreateRequestHandler(t, typeof(RequestHandlerWrapperImpl<,>)));

    private static RequestHandlerBase CreateRequestHandler(Type requestType, Type wrapperGenericType)
    {
        var responseType = GetResponseType(requestType);
        Type wrapperType;

        if (responseType == typeof(Unit) && typeof(IRequest).IsAssignableFrom(requestType))
        {
            wrapperType = typeof(UnitRequestHandlerWrapperImpl<>).MakeGenericType(requestType);
        }
        else
        {
            wrapperType = wrapperGenericType.MakeGenericType(requestType, responseType);
        }

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

    private static Activity? StartRequestActivity(Type requestType, Type? responseType)
    {
        if (!ActivitySourceInstance.HasListeners())
        {
            return null;
        }

        var activity = ActivitySourceInstance.StartActivity($"Mediator.Send {requestType.Name}");
        activity?.SetTag("meridian.request_type", requestType.FullName);
        if (responseType is not null)
        {
            activity?.SetTag("meridian.response_type", responseType.FullName);
        }

        return activity;
    }

    private static Activity? StartNotificationActivity(Type notificationType)
    {
        if (!ActivitySourceInstance.HasListeners())
        {
            return null;
        }

        var activity = ActivitySourceInstance.StartActivity($"Mediator.Publish {notificationType.Name}");
        activity?.SetTag("meridian.notification_type", notificationType.FullName);
        return activity;
    }

    internal static Activity? StartStreamActivity(Type requestType, Type? responseType)
    {
        if (!ActivitySourceInstance.HasListeners())
        {
            return null;
        }

        var activity = ActivitySourceInstance.StartActivity($"Mediator.CreateStream {requestType.Name}");
        activity?.SetTag("meridian.stream_request_type", requestType.FullName);
        if (responseType is not null)
        {
            activity?.SetTag("meridian.response_type", responseType.FullName);
        }

        return activity;
    }

    private async Task ExecuteWithActivityAsync(
        Activity? activity,
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        using (activity)
        {
            try
            {
                await operation().ConfigureAwait(false);
                activity?.SetStatus(ActivityStatusCode.Ok);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                MarkActivityFailure(activity, ex);
                throw;
            }
        }
    }

    private async Task<T> ExecuteWithActivityAsync<T>(
        Activity? activity,
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using (activity)
        {
            try
            {
                var result = await operation().ConfigureAwait(false);
                activity?.SetStatus(ActivityStatusCode.Ok);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                MarkActivityFailure(activity, ex);
                throw;
            }
        }
    }

    private void MarkActivityFailure(Activity? activity, Exception ex) =>
        MarkActivityFailure(_telemetryOptions, activity, ex);

    internal static void MarkActivityFailure(MediatorTelemetryOptions telemetryOptions, Activity? activity, Exception ex)
    {
        if (telemetryOptions.RecordExceptionMessage)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        }
        else
        {
            activity?.SetStatus(ActivityStatusCode.Error);
        }

        activity?.SetTag("exception.type", ex.GetType().FullName);
        if (telemetryOptions.RecordExceptionMessage)
        {
            activity?.SetTag("exception.message", ex.Message);
        }

        if (telemetryOptions.RecordExceptionStackTrace)
        {
            var stackTrace = telemetryOptions.RecordExceptionMessage
                ? ex.ToString()
                : $"{ex.GetType().FullName}{Environment.NewLine}{ex.StackTrace}";
            activity?.SetTag("exception.stacktrace", stackTrace);
        }
    }
}
