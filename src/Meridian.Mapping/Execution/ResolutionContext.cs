namespace Meridian.Mapping.Execution;

/// <summary>
/// Provides contextual information during a mapping operation.
/// Holds a reference to the current <see cref="IMapper"/> (for nested mapping)
/// and tracks recursion depth. Optionally tracks already-mapped objects to
/// handle circular references when <see cref="TypeMap.PreserveReferences"/> is enabled.
/// </summary>
public class ResolutionContext
{
    /// <summary>
    /// Lazy-allocated dictionary for tracking mapped objects (circular reference detection).
    /// Only allocated when <see cref="CacheMapped"/> is first called, avoiding a dictionary
    /// allocation on every mapping call when PreserveReferences is not in use.
    /// </summary>
    private Dictionary<(object, Type), object>? _mappedObjects;

    /// <summary>
    /// Gets the mapper instance, available for nested/child mappings.
    /// </summary>
    public IMapper Mapper { get; }

    /// <summary>
    /// Gets the current recursion depth.
    /// </summary>
    public int Depth { get; }

    /// <summary>
    /// Gets the service provider for resolving DI-registered services (converters, resolvers).
    /// May be null if no DI container is configured.
    /// </summary>
    public IServiceProvider? ServiceProvider { get; }

    /// <summary>
    /// Initializes a new <see cref="ResolutionContext"/>.
    /// </summary>
    /// <param name="mapper">The mapper instance.</param>
    /// <param name="depth">The current recursion depth.</param>
    /// <param name="serviceProvider">Optional service provider for DI resolution.</param>
    public ResolutionContext(IMapper mapper, int depth = 0, IServiceProvider? serviceProvider = null)
    {
        Mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        Depth = depth;
        ServiceProvider = serviceProvider;
    }

    /// <summary>
    /// Initializes a new <see cref="ResolutionContext"/> sharing the same object cache.
    /// </summary>
    private ResolutionContext(IMapper mapper, int depth, IServiceProvider? serviceProvider,
        Dictionary<(object, Type), object>? mappedObjects)
    {
        Mapper = mapper;
        Depth = depth;
        ServiceProvider = serviceProvider;
        _mappedObjects = mappedObjects;
    }

    /// <summary>
    /// Creates a new context with incremented depth, sharing the same mapped objects cache.
    /// </summary>
    /// <returns>A new resolution context one level deeper.</returns>
    public ResolutionContext IncrementDepth()
    {
        return new ResolutionContext(Mapper, Depth + 1, ServiceProvider, _mappedObjects);
    }

    /// <summary>
    /// Attempts to retrieve a previously mapped destination object for the given source
    /// and destination type. Used to handle circular references.
    /// </summary>
    /// <param name="source">The source object.</param>
    /// <param name="destType">The destination type.</param>
    /// <param name="mapped">The previously mapped destination object, if found.</param>
    /// <returns>True if a cached mapping was found; false otherwise.</returns>
    public bool TryGetMapped(object source, Type destType, out object? mapped)
    {
        mapped = null;
        if (_mappedObjects == null)
            return false;

        return _mappedObjects.TryGetValue((source, destType), out mapped);
    }

    /// <summary>
    /// Caches a mapping from source to destination so circular references can be resolved.
    /// Lazy-allocates the tracking dictionary on first use.
    /// </summary>
    /// <param name="source">The source object.</param>
    /// <param name="destType">The destination type.</param>
    /// <param name="mapped">The mapped destination object.</param>
    public void CacheMapped(object source, Type destType, object mapped)
    {
        _mappedObjects ??= new Dictionary<(object, Type), object>(ReferenceEqualityKeyComparer.Instance);
        _mappedObjects.TryAdd((source, destType), mapped);
    }

    /// <summary>
    /// Custom equality comparer for the mapped objects dictionary key that uses
    /// reference equality for the source object component.
    /// </summary>
    private class ReferenceEqualityKeyComparer : IEqualityComparer<(object, Type)>
    {
        public static readonly ReferenceEqualityKeyComparer Instance = new();

        public bool Equals((object, Type) x, (object, Type) y)
        {
            return ReferenceEquals(x.Item1, y.Item1) && x.Item2 == y.Item2;
        }

        public int GetHashCode((object, Type) obj)
        {
            return HashCode.Combine(
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Item1),
                obj.Item2.GetHashCode());
        }
    }
}
