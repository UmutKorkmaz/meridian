using Meridian.Mediator.Wrappers;

namespace Meridian.Mediator.Generated;

/// <summary>An immutable set of closed dispatch wrappers for one provider. Never stores resolved services.</summary>
public sealed class GeneratedMediatorComposition
{
    private readonly IReadOnlyDictionary<Type, RequestHandlerBase> _requests;
    private readonly IReadOnlyDictionary<Type, NotificationHandlerWrapperBase> _notifications;
    private readonly IReadOnlyDictionary<Type, StreamRequestHandlerBase> _streams;
    internal static GeneratedMediatorComposition Empty { get; } = new(Array.Empty<IGeneratedMediatorRegistry>(), false);

    /// <summary>Validates and snapshots the supplied registries. Repeated identical registries are coalesced.</summary>
    public GeneratedMediatorComposition(IEnumerable<IGeneratedMediatorRegistry> registries, bool requireGeneratedDispatch)
    {
        ArgumentNullException.ThrowIfNull(registries);
        RequireGeneratedDispatch = requireGeneratedDispatch;
        var requests = new Dictionary<Type, RequestHandlerBase>();
        var notifications = new Dictionary<Type, NotificationHandlerWrapperBase>();
        var streams = new Dictionary<Type, StreamRequestHandlerBase>();
        var identities = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var registry in registries)
        {
            if (identities.TryGetValue(registry.RegistryId, out var type))
            {
                if (type != registry.GetType()) throw new InvalidOperationException("Generated registry ID collision: " + registry.RegistryId);
                continue;
            }
            identities.Add(registry.RegistryId, registry.GetType());
            foreach (var request in registry.RequestTypes)
            {
                var wrapper = registry.FindRequestHandler(request);
                if (wrapper == null)
                {
                    if (requireGeneratedDispatch) throw new InvalidOperationException($"Registry {registry.RegistryId} declares {request} without a generated request wrapper required for strict dispatch.");
                    continue;
                }
                if (!requests.TryAdd(request, wrapper)) throw new InvalidOperationException($"Generated registries declare duplicate request slot {request}.");
            }
            foreach (var notification in registry.NotificationTypes)
            {
                var wrapper = registry.FindNotificationHandler(notification);
                if (wrapper == null)
                {
                    if (requireGeneratedDispatch) throw new InvalidOperationException($"Registry {registry.RegistryId} declares {notification} without a generated notification wrapper required for strict dispatch.");
                    continue;
                }
                notifications.TryAdd(notification, wrapper);
            }
            foreach (var stream in registry.StreamTypes)
            {
                var wrapper = registry.FindStreamHandler(stream);
                if (wrapper == null)
                {
                    if (requireGeneratedDispatch) throw new InvalidOperationException($"Registry {registry.RegistryId} declares {stream} without a generated stream wrapper required for strict dispatch.");
                    continue;
                }
                if (!streams.TryAdd(stream, wrapper)) throw new InvalidOperationException($"Generated registries declare duplicate stream slot {stream}.");
            }
        }
        _requests = requests;
        _notifications = notifications;
        _streams = streams;
    }

    /// <summary>Whether an unknown request must fail instead of using legacy reflection dispatch.</summary>
    public bool RequireGeneratedDispatch { get; }
    internal RequestHandlerBase? FindRequestHandler(Type requestType) => _requests.GetValueOrDefault(requestType);
    internal NotificationHandlerWrapperBase? FindNotificationHandler(Type notificationType) => _notifications.GetValueOrDefault(notificationType);
    internal StreamRequestHandlerBase? FindStreamHandler(Type requestType) => _streams.GetValueOrDefault(requestType);
}
