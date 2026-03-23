using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Pipeline;

/// <summary>
/// Built-in pipeline behavior that catches exceptions and delegates to
/// <see cref="IRequestExceptionHandler{TRequest, TResponse, TException}"/> and
/// <see cref="IRequestExceptionAction{TRequest, TException}"/> instances.
/// </summary>
/// <typeparam name="TRequest">Request type.</typeparam>
/// <typeparam name="TResponse">Response type.</typeparam>
public class RequestExceptionProcessorBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="RequestExceptionProcessorBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="serviceProvider">The service provider for resolving exception handlers and actions.</param>
    public RequestExceptionProcessorBehavior(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc/>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        try
        {
            return await next().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Try exception handlers (can provide a replacement response)
            var response = await TryHandleException(request, exception, cancellationToken).ConfigureAwait(false);
            if (response.handled)
            {
                return response.result!;
            }

            // Execute exception actions (side effects only, cannot provide response)
            await ExecuteExceptionActions(request, exception, cancellationToken).ConfigureAwait(false);

            throw;
        }
    }

    private async Task<(bool handled, TResponse? result)> TryHandleException(
        TRequest request, Exception exception, CancellationToken cancellationToken)
    {
        var exceptionType = exception.GetType();

        // Walk the exception hierarchy to find matching handlers
        while (exceptionType != null && exceptionType != typeof(object))
        {
            var handlerType = typeof(IRequestExceptionHandler<,,>).MakeGenericType(
                typeof(TRequest), typeof(TResponse), exceptionType);

            var handlers = _serviceProvider.GetServices(handlerType);

            foreach (var handler in handlers)
            {
                if (handler == null) continue;

                var state = new RequestExceptionHandlerState<TResponse>();

                var method = handlerType.GetMethod("Handle")!;
                var task = (Task)method.Invoke(handler, new object?[] { request, exception, state, cancellationToken })!;
                await task.ConfigureAwait(false);

                if (state.Handled)
                {
                    return (true, state.Response);
                }
            }

            exceptionType = exceptionType.BaseType;
        }

        return (false, default);
    }

    private async Task ExecuteExceptionActions(
        TRequest request, Exception exception, CancellationToken cancellationToken)
    {
        var exceptionType = exception.GetType();

        while (exceptionType != null && exceptionType != typeof(object))
        {
            var actionType = typeof(IRequestExceptionAction<,>).MakeGenericType(
                typeof(TRequest), exceptionType);

            var actions = _serviceProvider.GetServices(actionType);

            foreach (var action in actions)
            {
                if (action == null) continue;

                var method = actionType.GetMethod("Execute")!;
                var task = (Task)method.Invoke(action, new object?[] { request, exception, cancellationToken })!;

                try
                {
                    await task.ConfigureAwait(false);
                }
                catch
                {
                    // Exception actions should not prevent the original exception from propagating
                }
            }

            exceptionType = exceptionType.BaseType;
        }
    }
}
