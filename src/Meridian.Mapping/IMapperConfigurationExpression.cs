using System.Reflection;
using Meridian.Mapping.Configuration;

namespace Meridian.Mapping;

/// <summary>
/// Configuration expression used during mapper setup.
/// Allows adding profiles, creating maps, and configuring global options.
/// </summary>
public interface IMapperConfigurationExpression
{
    /// <summary>
    /// Creates a mapping between <typeparamref name="TSource"/> and <typeparamref name="TDestination"/>.
    /// </summary>
    /// <typeparam name="TSource">The source type.</typeparam>
    /// <typeparam name="TDestination">The destination type.</typeparam>
    /// <returns>A mapping expression for further configuration.</returns>
    IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>();

    /// <summary>
    /// Creates a projection between <typeparamref name="TSource"/> and
    /// <typeparamref name="TDestination"/>. Projection maps compile the same as
    /// regular maps today, but the separate entry point makes migration from
    /// AutoMapper-style APIs simpler.
    /// </summary>
    IMappingExpression<TSource, TDestination> CreateProjection<TSource, TDestination>();

    /// <summary>
    /// Adds a mapping profile instance.
    /// </summary>
    /// <param name="profile">The profile instance.</param>
    void AddProfile(Profile profile);

    /// <summary>
    /// Adds a mapping profile by type.
    /// </summary>
    /// <typeparam name="TProfile">The profile type.</typeparam>
    void AddProfile<TProfile>() where TProfile : Profile, new();

    /// <summary>
    /// Scans the given assemblies for <see cref="Profile"/> subclasses and adds them.
    /// </summary>
    /// <param name="assemblies">The assemblies to scan.</param>
    void AddProfiles(params Assembly[] assemblies);

    /// <summary>
    /// Scans the given assemblies for mappings and profiles.
    /// </summary>
    void AddMaps(params Assembly[] assemblies);

    /// <summary>
    /// Scans the assemblies containing the given marker types for mappings and profiles.
    /// </summary>
    void AddMaps(params Type[] markerTypes);

    /// <summary>
    /// Loads the given assemblies by simple or full name and scans them for mappings and profiles.
    /// </summary>
    void AddMaps(params string[] assemblyNames);

    /// <summary>
    /// Gets or sets whether null source collections map to null instead of empty collections.
    /// Default is false (null collections become empty).
    /// </summary>
    bool AllowNullCollections { get; set; }

    /// <summary>
    /// Gets or sets whether null destination values are allowed.
    /// Default is true.
    /// </summary>
    bool AllowNullDestinationValues { get; set; }

    /// <summary>
    /// Gets or sets the default maximum recursion depth applied to every type map
    /// that does not set its own <see cref="IMappingExpression{TSource,TDestination}.MaxDepth"/>.
    /// Default is <c>64</c>, matching <c>System.Text.Json</c>'s <c>JsonSerializerOptions.MaxDepth</c>
    /// and Newtonsoft.Json v13+. Lowering this further improves DoS resistance; raising it
    /// increases the risk of <see cref="StackOverflowException"/> on attacker-crafted input.
    /// </summary>
    /// <remarks>
    /// When a mapping reaches this depth, the result at that level is
    /// <c>default(TDestination)</c> (i.e. <c>null</c> for reference types). This mirrors
    /// AutoMapper's opt-in <c>MaxDepth</c> behavior — the only difference in Meridian is
    /// that the cap is applied by default rather than requiring explicit configuration.
    /// </remarks>
    int DefaultMaxDepth { get; set; }

    /// <summary>
    /// Gets or sets the default maximum number of items allowed in a source collection
    /// being mapped, applied to every type map that does not set its own per-map cap.
    /// Default is <c>10_000</c>. Exceeding the limit throws
    /// <see cref="MeridianMappingLimitException"/> before any destination allocation
    /// occurs, bounding worst-case memory use on attacker-controlled input.
    /// </summary>
    /// <remarks>
    /// Inspired by ASP.NET Core's <c>MvcOptions.MaxModelBindingCollectionSize</c>
    /// (default 1024). Set to <see cref="int.MaxValue"/> to disable — not recommended
    /// for collections that may contain user-supplied data.
    /// </remarks>
    int DefaultMaxCollectionItems { get; set; }

    /// <summary>
    /// Creates a mapping between two types specified at runtime, supporting open generic types.
    /// For example: <c>CreateMap(typeof(Response&lt;&gt;), typeof(ResponseDto&lt;&gt;))</c>.
    /// </summary>
    /// <param name="sourceType">The source type (may be an open generic type definition).</param>
    /// <param name="destinationType">The destination type (may be an open generic type definition).</param>
    void CreateMap(Type sourceType, Type destinationType);

    /// <summary>
    /// Gets the collection of global value transformers. Transformers are applied to
    /// mapped values by type after the value has been resolved.
    /// </summary>
    /// <example>
    /// <code>
    /// cfg.ValueTransformers.Add&lt;string&gt;(s =&gt; s.Trim());
    /// </code>
    /// </example>
    ValueTransformerCollection ValueTransformers { get; }
}
