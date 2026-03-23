namespace Meridian.Mediator.Streaming;

/// <summary>
/// Represents an async stream continuation for the next task to execute in the stream pipeline.
/// </summary>
/// <typeparam name="TResponse">The type of each item in the response stream.</typeparam>
/// <returns>An async enumerable of <typeparamref name="TResponse"/>.</returns>
public delegate IAsyncEnumerable<TResponse> StreamHandlerDelegate<out TResponse>();
