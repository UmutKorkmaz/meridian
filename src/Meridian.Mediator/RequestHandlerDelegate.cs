namespace Meridian.Mediator;

/// <summary>
/// Represents an async continuation for the next task to execute in the pipeline.
/// </summary>
/// <typeparam name="TResponse">The type of the response.</typeparam>
/// <returns>Awaitable task returning a <typeparamref name="TResponse"/>.</returns>
public delegate Task<TResponse> RequestHandlerDelegate<TResponse>();
