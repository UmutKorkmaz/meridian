using System.Linq.Expressions;
using System.Reflection;
using Meridian.Mapping.Configuration;

namespace Meridian.Mapping.Execution;

/// <summary>
/// Represents the compiled mapping plan for a specific source-to-destination type pair.
/// Contains all <see cref="PropertyMap"/> entries and any custom converter configuration.
/// Built during configuration and cached for reuse.
/// All delegate properties are stored as strongly-typed Func/Action wrappers (object-based)
/// to avoid <see cref="Delegate.DynamicInvoke"/> overhead at runtime.
/// </summary>
public class TypeMap
{
    /// <summary>
    /// Gets the source type.
    /// </summary>
    public Type SourceType { get; }

    /// <summary>
    /// Gets the destination type.
    /// </summary>
    public Type DestinationType { get; }

    /// <summary>
    /// Gets the property mappings for this type pair.
    /// </summary>
    public List<PropertyMap> PropertyMaps { get; } = new();

    /// <summary>
    /// Gets or sets the custom converter function (ConvertUsing with Func).
    /// Compiled into an object-based wrapper at configuration time.
    /// </summary>
    public Func<object, object?>? CustomConverter { get; set; }

    /// <summary>
    /// Gets or sets the pre-compiled type converter invocation delegate.
    /// Eliminates GetMethod("Convert") reflection at mapping time.
    /// </summary>
    public Func<object, object?, ResolutionContext, object?>? CompiledTypeConverter { get; set; }

    /// <summary>
    /// Gets or sets the custom type converter type (DI-resolved).
    /// </summary>
    public Type? TypeConverterType { get; set; }

    /// <summary>
    /// Gets or sets the custom construction function.
    /// Compiled into an object-based wrapper at configuration time.
    /// </summary>
    public Func<object, object>? ConstructUsing { get; set; }

    /// <summary>
    /// Gets or sets the constructor parameter configurations.
    /// </summary>
    public Dictionary<string, LambdaExpression>? CtorParamMappings { get; set; }

    /// <summary>
    /// Gets or sets the maximum recursion depth for this type map.
    /// </summary>
    public int? MaxDepth { get; set; }

    /// <summary>
    /// Gets or sets the member list used for validation.
    /// </summary>
    public MemberList ValidationMemberList { get; set; } = MemberList.Destination;

    /// <summary>
    /// Gets the compiled object factory for creating destination instances.
    /// </summary>
    public Func<object>? CompiledObjectFactory { get; set; }

    /// <summary>
    /// Gets or sets the list of actions to execute before property mapping.
    /// Compiled into object-based wrappers — no DynamicInvoke needed.
    /// </summary>
    public List<Action<object, object, ResolutionContext>> BeforeMapActions { get; set; } = new();

    /// <summary>
    /// Gets or sets the list of actions to execute after property mapping.
    /// Compiled into object-based wrappers — no DynamicInvoke needed.
    /// </summary>
    public List<Action<object, object, ResolutionContext>> AfterMapActions { get; set; } = new();

    /// <summary>
    /// Gets or sets whether circular reference tracking is enabled for this type map.
    /// When true, the mapping engine will cache mapped objects and return the cached
    /// instance if the same source object is encountered again.
    /// </summary>
    public bool PreserveReferences { get; set; }

    /// <summary>
    /// Gets the list of derived type maps for polymorphic dispatch.
    /// When the runtime source type is more derived, the engine picks the most specific map.
    /// </summary>
    public List<TypeMap> DerivedTypeMaps { get; set; } = new();

    /// <summary>
    /// Gets or sets the list of base type maps this map inherits from.
    /// Base property maps and actions are applied during execution for Include/IncludeBase behavior.
    /// </summary>
    public List<TypeMap> BaseTypeMaps { get; set; } = new();

    /// <summary>
    /// Gets or sets the included member getter expressions for IncludeMembers.
    /// Each getter retrieves a nested source object whose properties are used for auto-mapping.
    /// </summary>
    public List<Func<object, object?>>? IncludedMemberGetters { get; set; }

    /// <summary>
    /// Gets or sets the source members excluded from source-member validation.
    /// </summary>
    public HashSet<string> IgnoredSourceMembers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional compiled fast-path delegate that produces a mapped destination
    /// in a single call, bypassing the per-property interpreter in
    /// <see cref="MappingEngine"/>. Populated by <see cref="FastPathCompiler"/>
    /// only when the type map uses the simple <c>ForMember + MapFrom</c>
    /// subset (97% of real usage). <see cref="MappingEngine.MapWithTypeMap"/>
    /// prefers this when non-null.
    /// </summary>
    /// <remarks>
    /// Signature: <c>(object source, MappingEngine engine, ResolutionContext context) =&gt; (object)destination</c>.
    /// The engine + context are threaded through so nested property mappings
    /// recurse with a depth-incremented context — preserving <c>DefaultMaxDepth</c>
    /// enforcement and <c>PreserveReferences</c> cache identity across nested calls.
    /// </remarks>
    public Func<object, MappingEngine, ResolutionContext, object>? CompiledFastPath { get; set; }

    /// <summary>
    /// Initializes a new <see cref="TypeMap"/>.
    /// </summary>
    /// <param name="sourceType">The source type.</param>
    /// <param name="destinationType">The destination type.</param>
    public TypeMap(Type sourceType, Type destinationType)
    {
        SourceType = sourceType ?? throw new ArgumentNullException(nameof(sourceType));
        DestinationType = destinationType ?? throw new ArgumentNullException(nameof(destinationType));
    }

    /// <summary>
    /// Gets the list of unmapped destination members for validation.
    /// </summary>
    /// <returns>Names of destination members that are neither mapped nor ignored.</returns>
    public IEnumerable<string> GetUnmappedDestinationMembers()
    {
        var destProperties = DestinationType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0);

        var mappedOrIgnored = PropertyMaps
            .Select(pm => pm.DestinationProperty.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return destProperties
            .Where(p => !mappedOrIgnored.Contains(p.Name))
            .Select(p => p.Name);
    }

    /// <summary>
    /// Gets the list of unmapped source members for validation.
    /// </summary>
    /// <returns>Names of source members that are not used in any mapping.</returns>
    public IEnumerable<string> GetUnmappedSourceMembers()
    {
        var sourceProperties = SourceType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead);

        var usedSourceMembers = PropertyMaps
            .Where(pm => pm.SourceProperty != null)
            .Select(pm => pm.SourceProperty!.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return sourceProperties
            .Where(p => !usedSourceMembers.Contains(p.Name) && !IgnoredSourceMembers.Contains(p.Name))
            .Select(p => p.Name);
    }
}
