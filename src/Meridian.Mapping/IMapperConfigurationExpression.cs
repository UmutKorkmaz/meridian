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
