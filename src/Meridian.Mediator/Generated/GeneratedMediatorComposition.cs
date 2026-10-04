using Meridian.Mediator.Wrappers;

namespace Meridian.Mediator.Generated;

/// <summary>An immutable set of closed request wrappers for one provider. Never stores resolved services.</summary>
public sealed class GeneratedMediatorComposition
{
    private readonly IReadOnlyDictionary<Type, RequestHandlerBase> _requests;
    internal static GeneratedMediatorComposition Empty { get; } = new(Array.Empty<IGeneratedMediatorRegistry>(), false);

    /// <summary>Validates and snapshots the supplied registries. Repeated identical registries are coalesced.</summary>
    public GeneratedMediatorComposition(IEnumerable<IGeneratedMediatorRegistry> registries, bool requireGeneratedDispatch)
    {
        ArgumentNullException.ThrowIfNull(registries);
        RequireGeneratedDispatch = requireGeneratedDispatch;
        var requests = new Dictionary<Type, RequestHandlerBase>();
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
        }
        _requests = requests;
    }

    /// <summary>Whether an unknown request must fail instead of using legacy reflection dispatch.</summary>
    public bool RequireGeneratedDispatch { get; }
    internal RequestHandlerBase? FindRequestHandler(Type requestType) => _requests.GetValueOrDefault(requestType);
}
