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

    /// <summary>
    /// Initializes a new instance of the <see cref="AuthorizationBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="authorizationHandlers">The authorization handlers for the request type.</param>
    public AuthorizationBehavior(IEnumerable<IAuthorizationHandler<TRequest>> authorizationHandlers)
    {
        _authorizationHandlers = authorizationHandlers ?? throw new ArgumentNullException(nameof(authorizationHandlers));
    }

    /// <inheritdoc/>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        foreach (var handler in _authorizationHandlers)
        {
            var result = await handler.AuthorizeAsync(request, cancellationToken);
            if (!result.IsAuthorized)
            {
                throw new UnauthorizedException(result.FailureReason ?? "Unauthorized");
            }
        }

        return await next();
    }
}
