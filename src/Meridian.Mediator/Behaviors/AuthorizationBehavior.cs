using Meridian.Mediator.Diagnostics;
using Meridian.Mediator.Pipeline;

namespace Meridian.Mediator.Behaviors;

/// <summary>
/// Marker interface for requests that require authorization.
/// Authorization rules are defined via <see cref="IAuthorizationHandler{TRequest}"/> implementations.
/// </summary>
public interface IAuthorizedRequest
{
}

/// <summary>
/// Authorization handler that checks if the current user is authorized for a specific request type.
/// Implement this interface for each request type that requires authorization.
/// </summary>
/// <typeparam name="TRequest">The request type to authorize.</typeparam>
public interface IAuthorizationHandler<in TRequest>
{
    /// <summary>
    /// Determines whether the current user is authorized to execute the specified request.
    /// </summary>
    /// <param name="request">The request to authorize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An authorization result indicating success or failure.</returns>
    Task<AuthorizationResult> AuthorizeAsync(TRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Represents the result of an authorization check.
/// </summary>
/// <param name="IsAuthorized">Whether the request is authorized.</param>
/// <param name="FailureReason">The reason for authorization failure, if applicable.</param>
public record AuthorizationResult(bool IsAuthorized, string? FailureReason = null)
{
    /// <summary>
    /// Creates a successful authorization result.
    /// </summary>
    /// <returns>An authorized result.</returns>
    public static AuthorizationResult Success() => new(true);

    /// <summary>
    /// Creates a failed authorization result with a reason.
    /// </summary>
    /// <param name="reason">The reason the authorization failed.</param>
    /// <returns>An unauthorized result.</returns>
    public static AuthorizationResult Fail(string reason) => new(false, reason);
}

/// <summary>
/// Exception thrown when a request fails authorization.
/// </summary>
public class UnauthorizedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UnauthorizedException"/> class.
    /// </summary>
    /// <param name="message">The message describing the authorization failure.</param>
    public UnauthorizedException(string message) : base(message) { }
}

/// <summary>
/// Controls how <see cref="AuthorizationBehavior{TRequest, TResponse}"/> handles requests
/// for which no <see cref="IAuthorizationHandler{TRequest}"/> is registered.
/// </summary>
public sealed record AuthorizationBehaviorOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether an authorized request may continue when no
    /// authorization handler is registered. Defaults to <c>false</c>, which rejects the request.
    /// Set to <c>true</c> only to retain the pre-2.1.4 fail-open behavior during migration.
    /// </summary>
    public bool AllowRequestsWithoutAuthorizationHandlers { get; init; }
}

/// <summary>
/// Exception thrown when an authorized request has no registered authorization handler.
/// </summary>
public sealed class MissingAuthorizationHandlerException : UnauthorizedException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MissingAuthorizationHandlerException"/> class.
    /// </summary>
    /// <param name="requestType">The authorized request type with no handler registration.</param>
    public MissingAuthorizationHandlerException(Type requestType)
        : base($"Authorized request type '{requestType.FullName}' has no registered IAuthorizationHandler<{requestType.Name}>.")
    {
    }
}

/// <summary>
/// Pipeline behavior that enforces authorization for <see cref="IAuthorizedRequest"/> requests.
/// Resolves all <see cref="IAuthorizationHandler{TRequest}"/> implementations from DI and checks each one.
/// If any authorization check fails, an <see cref="UnauthorizedException"/> is thrown.
/// </summary>
/// <typeparam name="TRequest">Request type (must implement <see cref="IAuthorizedRequest"/>).</typeparam>
/// <typeparam name="TResponse">Response type.</typeparam>
public class AuthorizationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IAuthorizedRequest
{
    private readonly IEnumerable<IAuthorizationHandler<TRequest>> _authorizationHandlers;
    private readonly AuthorizationBehaviorOptions _options;
    private readonly MediatorMetrics _metrics;

    /// <summary>
    /// Initializes a new instance of the <see cref="AuthorizationBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="authorizationHandlers">The authorization handlers for the request type.</param>
    public AuthorizationBehavior(IEnumerable<IAuthorizationHandler<TRequest>> authorizationHandlers)
        : this(authorizationHandlers, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AuthorizationBehavior{TRequest, TResponse}"/> class
    /// with options controlling missing-handler compatibility behavior.
    /// </summary>
    /// <param name="authorizationHandlers">The authorization handlers for the request type.</param>
    /// <param name="options">Authorization behavior options. A null value uses the secure defaults.</param>
    public AuthorizationBehavior(
        IEnumerable<IAuthorizationHandler<TRequest>> authorizationHandlers,
        AuthorizationBehaviorOptions? options)
        : this(authorizationHandlers, options, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AuthorizationBehavior{TRequest, TResponse}"/> class that
    /// reports rejections to a provider-owned metrics component.
    /// </summary>
    /// <param name="authorizationHandlers">The authorization handlers for the request type.</param>
    /// <param name="options">Authorization behavior options. A null value uses the secure defaults.</param>
    /// <param name="metrics">The metrics component. A null value leaves metrics disabled.</param>
    public AuthorizationBehavior(
        IEnumerable<IAuthorizationHandler<TRequest>> authorizationHandlers,
        AuthorizationBehaviorOptions? options = null,
        MediatorMetrics? metrics = null)
    {
        _authorizationHandlers = authorizationHandlers ?? throw new ArgumentNullException(nameof(authorizationHandlers));
        _options = options ?? new AuthorizationBehaviorOptions();
        _metrics = metrics ?? MediatorMetrics.Disabled;
    }

    /// <inheritdoc/>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (_authorizationHandlers is ICollection<IAuthorizationHandler<TRequest>> collection)
        {
            if (collection.Count == 0 && !_options.AllowRequestsWithoutAuthorizationHandlers)
            {
                _metrics.RecordAuthorizationRejection(typeof(TRequest), AuthorizationRejection.MissingHandler);
                throw new MissingAuthorizationHandlerException(typeof(TRequest));
            }

            foreach (var handler in collection)
            {
                await AuthorizeAsync(handler, request, cancellationToken);
            }

            return await next();
        }

        if (_authorizationHandlers is IReadOnlyCollection<IAuthorizationHandler<TRequest>> readOnlyCollection)
        {
            if (readOnlyCollection.Count == 0 && !_options.AllowRequestsWithoutAuthorizationHandlers)
            {
                _metrics.RecordAuthorizationRejection(typeof(TRequest), AuthorizationRejection.MissingHandler);
                throw new MissingAuthorizationHandlerException(typeof(TRequest));
            }

            foreach (var handler in readOnlyCollection)
            {
                await AuthorizeAsync(handler, request, cancellationToken);
            }

            return await next();
        }

        using var enumerator = _authorizationHandlers.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            if (!_options.AllowRequestsWithoutAuthorizationHandlers)
            {
                _metrics.RecordAuthorizationRejection(typeof(TRequest), AuthorizationRejection.MissingHandler);
                throw new MissingAuthorizationHandlerException(typeof(TRequest));
            }

            return await next();
        }

        do
        {
            await AuthorizeAsync(enumerator.Current, request, cancellationToken);
        }
        while (enumerator.MoveNext());

        return await next();
    }

    private async Task AuthorizeAsync(
        IAuthorizationHandler<TRequest> handler,
        TRequest request,
        CancellationToken cancellationToken)
    {
        var result = await handler.AuthorizeAsync(request, cancellationToken);
        if (!result.IsAuthorized)
        {
            _metrics.RecordAuthorizationRejection(typeof(TRequest), AuthorizationRejection.Denied);
            throw new UnauthorizedException(result.FailureReason ?? "Unauthorized");
        }
    }
}
