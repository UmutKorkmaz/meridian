namespace Meridian.Mediator.Streaming;

/// <summary>
/// Marker interface for a stream request that returns an <see cref="IAsyncEnumerable{T}"/> of <typeparamref name="TResponse"/>.
/// </summary>
/// <typeparam name="TResponse">The type of each item in the response stream.</typeparam>
public interface IStreamRequest<out TResponse> { }
