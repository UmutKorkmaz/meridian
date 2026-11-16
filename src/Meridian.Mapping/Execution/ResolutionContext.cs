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

    // Share the operation root, not a snapshot of its lazily allocated dictionary.
    // A child may be the first map that enables PreserveReferences.
    private readonly ResolutionContext _cacheOwner;
    internal bool AllowGeneratedAssignableMappings { get; } = true;
    internal int? InheritedMaxDepth { get; }
    internal int? InheritedMaxCollectionItems { get; }

    /// <summary>Gets the effective global depth boundary, including inherited generated limits.</summary>
    public int MaxDepth => Math.Min(Mapper.ConfigurationProvider.DefaultMaxDepth, InheritedMaxDepth ?? int.MaxValue);

    /// <summary>Gets the effective collection boundary, including inherited generated limits.</summary>
    public int MaxCollectionItems => Math.Min(Mapper.ConfigurationProvider.DefaultMaxCollectionItems, InheritedMaxCollectionItems ?? int.MaxValue);

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
    /// Gets the per-call mapping items.
    /// </summary>
    public IDictionary<string, object?> Items { get; }

    /// <summary>
    /// Gets the per-call mapping state object.
    /// </summary>
    public object? State { get; }

    /// <summary>
    /// Initializes a new <see cref="ResolutionContext"/>.
    /// </summary>
    /// <param name="mapper">The mapper instance.</param>
    /// <param name="depth">The current recursion depth.</param>
    /// <param name="serviceProvider">Optional service provider for DI resolution.</param>
    /// <param name="options">Optional per-call mapping options.</param>
    public ResolutionContext(
        IMapper mapper,
        int depth = 0,
        IServiceProvider? serviceProvider = null,
        IMappingOperationOptions? options = null)
    {
        Mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        _cacheOwner = this;
        Depth = depth;
        ServiceProvider = serviceProvider;
        options ??= new MappingOperationOptions();
        Items = options.Items;
        State = options.State;
    }

    /// <summary>
    /// Initializes a new <see cref="ResolutionContext"/> sharing the same object cache.
    /// </summary>
    private ResolutionContext(
        IMapper mapper,
        int depth,
        IServiceProvider? serviceProvider,
        ResolutionContext cacheOwner,
        IDictionary<string, object?> items,
        object? state,
        int? maxDepth = null,
        int? maxCollectionItems = null,
        bool allowGeneratedAssignableMappings = true)
    {
        Mapper = mapper;
        Depth = depth;
        ServiceProvider = serviceProvider;
        _cacheOwner = cacheOwner;
        Items = items;
        State = state;
        AllowGeneratedAssignableMappings = allowGeneratedAssignableMappings;
        InheritedMaxDepth = maxDepth;
        InheritedMaxCollectionItems = maxCollectionItems;
    }

    /// <summary>
    /// Creates a new context with incremented depth, sharing the same mapped objects cache.
    /// </summary>
    /// <returns>A new resolution context one level deeper.</returns>
    public ResolutionContext IncrementDepth()
    {
        return new ResolutionContext(Mapper, Depth + 1, ServiceProvider, _cacheOwner, Items, State, InheritedMaxDepth, InheritedMaxCollectionItems, AllowGeneratedAssignableMappings);
    }

    /// <summary>
    /// Creates a context <paramref name="relativeDepth"/> levels deeper in one step. Compiled nested maps use it
    /// to produce the same context a chain of <see cref="IncrementDepth"/> calls would, without allocating
    /// the intermediate contexts; the new instance shares the cache owner, items, state, services and limits.
    /// </summary>
    internal ResolutionContext AtRelativeDepth(int relativeDepth) =>
        new(Mapper, Depth + relativeDepth, ServiceProvider, _cacheOwner, Items, State, InheritedMaxDepth, InheritedMaxCollectionItems, AllowGeneratedAssignableMappings);

    internal ResolutionContext ForExistingDestination() => new(Mapper, Depth, ServiceProvider, _cacheOwner, Items, State,
        InheritedMaxDepth, InheritedMaxCollectionItems, allowGeneratedAssignableMappings: false);

    /// <summary>
    /// Maps an explicitly configured nested pair using this operation's services, state and cache.
    /// The relative depth is measured from this context. A custom IMapper must use its own context bridge;
    /// this method throws rather than silently starting a fresh operation.
    /// </summary>
    public bool TryMapConfigured(object source, Type sourceType, Type destinationType, int relativeDepth, out object? destination, int? maxDepth = null, int? maxCollectionItems = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceType);
        ArgumentNullException.ThrowIfNull(destinationType);
        ArgumentOutOfRangeException.ThrowIfNegative(relativeDepth);
        if (Mapper is not global::Meridian.Mapping.Mapper mapper)
            throw new NotSupportedException("Context-preserving configured mapping requires Meridian's Mapper implementation.");
        if (maxDepth is <= 0) throw new ArgumentOutOfRangeException(nameof(maxDepth));
        if (maxCollectionItems is <= 0) throw new ArgumentOutOfRangeException(nameof(maxCollectionItems));
        var nested = new ResolutionContext(Mapper, checked(Depth + relativeDepth), ServiceProvider, _cacheOwner, Items, State,
            Math.Min(MaxDepth, maxDepth ?? int.MaxValue), Math.Min(MaxCollectionItems, maxCollectionItems ?? int.MaxValue), AllowGeneratedAssignableMappings);
        return mapper.TryMapConfigured(source, sourceType, destinationType, nested, out destination);
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
        if (_cacheOwner._mappedObjects == null)
            return false;

        return _cacheOwner._mappedObjects.TryGetValue((source, destType), out mapped);
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
        _cacheOwner._mappedObjects ??= new Dictionary<(object, Type), object>(ReferenceEqualityKeyComparer.Instance);
        _cacheOwner._mappedObjects.TryAdd((source, destType), mapped);
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
