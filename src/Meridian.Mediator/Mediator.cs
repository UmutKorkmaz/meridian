using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
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

    private readonly IServiceProvider _serviceProvider;
    private readonly INotificationPublisher _publisher;
    private readonly MediatorTelemetryOptions _telemetryOptions;

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
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _telemetryOptions = telemetryOptions ?? throw new ArgumentNullException(nameof(telemetryOptions));
    }

    /// <inheritdoc/>
    public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var handler = (RequestHandlerWrapper<TResponse>)_requestHandlers.GetOrAdd(requestType,
            static t => CreateRequestHandler(t, typeof(RequestHandlerWrapperImpl<,>)));

        return ExecuteWithActivityAsync(
            StartRequestActivity(requestType, typeof(TResponse)),
            () => handler.Handle(request, _serviceProvider, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : IRequest
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var handler = (RequestHandlerWrapper<Unit>)_requestHandlers.GetOrAdd(requestType,
            static t => CreateRequestHandler(t, typeof(RequestHandlerWrapperImpl<,>)));

        return ExecuteWithActivityAsync(
            StartRequestActivity(requestType, typeof(Unit)),
            () => handler.Handle(request, _serviceProvider, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task<object?> Send(object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var handler = _requestHandlers.GetOrAdd(requestType,
            static t => CreateRequestHandler(t, typeof(RequestHandlerWrapperImpl<,>)));

        return ExecuteWithActivityAsync(
            StartRequestActivity(requestType, null),
            () => handler.Handle(request, _serviceProvider, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(notification);
        return ExecuteWithActivityAsync(
            StartNotificationActivity(notification.GetType()),
            () => PublishNotification(notification, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task Publish(object notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        if (notification is not INotification notif)
        {
            throw new ArgumentException($"Object of type {notification.GetType()} does not implement {nameof(INotification)}.", nameof(notification));
        }
        return ExecuteWithActivityAsync(
            StartNotificationActivity(notification.GetType()),
            () => PublishNotification(notif, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var handler = (StreamRequestHandlerWrapper<TResponse>)_streamRequestHandlers.GetOrAdd(requestType,
            static t => CreateStreamHandler(t));

        return ExecuteStreamWithActivity(
            request,
            requestType,
            typeof(TResponse),
            handler,
            cancellationToken);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requestType = request.GetType();
        var handler = _streamRequestHandlers.GetOrAdd(requestType, static t => CreateStreamHandler(t));

        return ExecuteStreamWithActivity(
            request,
            requestType,
            null,
            handler,
            cancellationToken);
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

    private static Activity? StartRequestActivity(Type requestType, Type? responseType)
    {
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
        var activity = ActivitySourceInstance.StartActivity($"Mediator.Publish {notificationType.Name}");
        activity?.SetTag("meridian.notification_type", notificationType.FullName);
        return activity;
    }

    private static Activity? StartStreamActivity(Type requestType, Type? responseType)
    {
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

    private async IAsyncEnumerable<TResponse> ExecuteStreamWithActivity<TResponse>(
        IStreamRequest<TResponse> request,
        Type requestType,
        Type? responseType,
        StreamRequestHandlerWrapper<TResponse> handler,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var activity = StartStreamActivity(requestType, responseType);
        IAsyncEnumerator<TResponse> enumerator;

        try
        {
            enumerator = ExecuteWithCurrentActivity(
                activity,
                () => handler.Handle(request, _serviceProvider, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken));
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

        await using (enumerator.ConfigureAwait(false))
        {
            while (true)
            {
                TResponse current;

                try
                {
                    if (!await MoveNextAsync(enumerator, activity).ConfigureAwait(false))
                    {
                        activity?.SetStatus(ActivityStatusCode.Ok);
                        yield break;
                    }

                    current = enumerator.Current;
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

                yield return current;
            }
        }
    }

    private async IAsyncEnumerable<object?> ExecuteStreamWithActivity(
        object request,
        Type requestType,
        Type? responseType,
        StreamRequestHandlerBase handler,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var activity = StartStreamActivity(requestType, responseType);
        IAsyncEnumerator<object?> enumerator;

        try
        {
            enumerator = ExecuteWithCurrentActivity(
                activity,
                () => handler.Handle(request, _serviceProvider, cancellationToken)
                    .GetAsyncEnumerator(cancellationToken));
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

        await using (enumerator.ConfigureAwait(false))
        {
            while (true)
            {
                object? current;

                try
                {
                    if (!await MoveNextAsync(enumerator, activity).ConfigureAwait(false))
                    {
                        activity?.SetStatus(ActivityStatusCode.Ok);
                        yield break;
                    }

                    current = enumerator.Current;
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

                yield return current;
            }
        }
    }

    private void MarkActivityFailure(Activity? activity, Exception ex)
    {
        if (_telemetryOptions.RecordExceptionMessage)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        }
        else
        {
            activity?.SetStatus(ActivityStatusCode.Error);
        }

        activity?.SetTag("exception.type", ex.GetType().FullName);
        if (_telemetryOptions.RecordExceptionMessage)
        {
            activity?.SetTag("exception.message", ex.Message);
        }

        if (_telemetryOptions.RecordExceptionStackTrace)
        {
            activity?.SetTag("exception.stacktrace", ex.ToString());
        }
    }

    private static T ExecuteWithCurrentActivity<T>(Activity? activity, Func<T> action)
    {
        var previous = Activity.Current;
        Activity.Current = activity;

        try
        {
            return action();
        }
        finally
        {
            Activity.Current = previous;
        }
    }

    private static async ValueTask<bool> MoveNextAsync<T>(
        IAsyncEnumerator<T> enumerator,
        Activity? activity)
    {
        var previous = Activity.Current;
        Activity.Current = activity;

        try
        {
            return await enumerator.MoveNextAsync().ConfigureAwait(false);
        }
        finally
        {
            Activity.Current = previous;
        }
    }
}
