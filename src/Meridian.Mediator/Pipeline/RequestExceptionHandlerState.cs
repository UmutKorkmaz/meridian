namespace Meridian.Mediator.Pipeline;

/// <summary>
/// Represents the state of exception handling for a request.
/// Exception handlers can mark the state as handled and provide a response.
/// </summary>
/// <typeparam name="TResponse">Response type.</typeparam>
public class RequestExceptionHandlerState<TResponse>
{
    /// <summary>
    /// Gets a value indicating whether the exception has been handled.
    /// </summary>
    public bool Handled { get; private set; }

    /// <summary>
    /// Gets the response set by the exception handler.
    /// </summary>
    public TResponse? Response { get; private set; }

    /// <summary>
    /// Marks the exception as handled and provides a response to return.
    /// </summary>
    /// <param name="response">The response to return instead of re-throwing the exception.</param>
    public void SetHandled(TResponse response)
    {
        Handled = true;
        Response = response;
    }
}
