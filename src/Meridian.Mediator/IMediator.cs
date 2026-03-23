namespace Meridian.Mediator;

/// <summary>
/// Defines a mediator to encapsulate request/response, publishing, and streaming interaction patterns.
/// Combines <see cref="ISender"/>, <see cref="IPublisher"/>, and <see cref="IStreamSender"/>.
/// </summary>
public interface IMediator : ISender, IPublisher, IStreamSender { }
