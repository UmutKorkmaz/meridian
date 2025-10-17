using System.Linq.Expressions;
using Meridian.Mapping.Converters;

namespace Meridian.Mapping.Configuration;

/// <summary>
/// Fluent API for configuring how a source type maps to a destination type.
/// Returned by <see cref="Profile.CreateMap{TSource, TDestination}"/> and provides
/// chainable configuration methods.
/// </summary>
/// <typeparam name="TSource">The source type.</typeparam>
/// <typeparam name="TDestination">The destination type.</typeparam>
public interface IMappingExpression<TSource, TDestination>
{
    /// <summary>
    /// Configures an individual destination member.
    /// </summary>
    /// <param name="destinationMember">Expression selecting the destination member.</param>
    /// <param name="memberOptions">Action configuring the member mapping.</param>
    /// <returns>This expression for chaining.</returns>
    IMappingExpression<TSource, TDestination> ForMember(
        Expression<Func<TDestination, object?>> destinationMember,
        Action<IMemberConfigurationExpression<TSource, TDestination>> memberOptions);

    /// <summary>
    /// Configures an individual destination member by name (string-based).
    /// </summary>
    /// <param name="destinationMemberName">The name of the destination property.</param>
    /// <param name="memberOptions">Action configuring the member mapping.</param>
    /// <returns>This expression for chaining.</returns>
    IMappingExpression<TSource, TDestination> ForMember(
        string destinationMemberName,
        Action<IMemberConfigurationExpression<TSource, TDestination>> memberOptions);

    /// <summary>
    /// Creates a reverse mapping (TDestination to TSource) with sensible defaults.
    /// </summary>
    /// <returns>The reverse mapping expression for further configuration.</returns>
    IMappingExpression<TDestination, TSource> ReverseMap();

    /// <summary>
    /// Uses a custom function to convert the entire source to destination,
    /// bypassing member-by-member mapping.
    /// </summary>
    /// <param name="converter">The conversion function.</param>
    /// <returns>This expression for chaining.</returns>
    IMappingExpression<TSource, TDestination> ConvertUsing(Func<TSource, TDestination> converter);

    /// <summary>
    /// Uses a type converter resolved from the DI container.
    /// </summary>
    /// <typeparam name="TConverter">The converter type implementing <see cref="ITypeConverter{TSource, TDestination}"/>.</typeparam>
    /// <returns>This expression for chaining.</returns>
    IMappingExpression<TSource, TDestination> ConvertUsing<TConverter>() where TConverter : ITypeConverter<TSource, TDestination>;

    /// <summary>
    /// Uses a specific type converter instance.
    /// </summary>
    /// <param name="converter">The converter instance.</param>
    /// <returns>This expression for chaining.</returns>
    IMappingExpression<TSource, TDestination> ConvertUsing(ITypeConverter<TSource, TDestination> converter);

    /// <summary>
    /// Specifies a custom constructor function for creating destination instances.
    /// </summary>
    /// <param name="ctor">Function that creates destination instances from source.</param>
    /// <returns>This expression for chaining.</returns>
    IMappingExpression<TSource, TDestination> ConstructUsing(Func<TSource, TDestination> ctor);

    /// <summary>
    /// Configures how a specific constructor parameter is resolved.
    /// </summary>
    /// <param name="ctorParamName">The constructor parameter name (case-insensitive).</param>
    /// <param name="configAction">Action configuring the parameter mapping.</param>
    /// <returns>This expression for chaining.</returns>
    IMappingExpression<TSource, TDestination> ForCtorParam(
        string ctorParamName,
        Action<ICtorParamConfigurationExpression<TSource>> configAction);

    /// <summary>
    /// Applies the same configuration to all destination members.
    /// </summary>
    /// <param name="memberOptions">Action applied to each destination member.</param>
    /// <returns>This expression for chaining.</returns>
    IMappingExpression<TSource, TDestination> ForAllMembers(
        Action<IMemberConfigurationExpression<TSource, TDestination>> memberOptions);

    /// <summary>
    /// Applies configuration to all destination members that have NOT been
    /// explicitly configured via ForMember. Useful for
    /// ignoring all unmapped members: <c>.ForAllOtherMembers(opt =&gt; opt.Ignore())</c>.
    /// </summary>
    /// <param name="memberOptions">Action applied to each non-configured destination member.</param>
    /// <returns>This expression for chaining.</returns>
    IMappingExpression<TSource, TDestination> ForAllOtherMembers(
        Action<IMemberConfigurationExpression<TSource, TDestination>> memberOptions);

    /// <summary>
    /// Sets the maximum depth for recursive/self-referencing mappings.
    /// </summary>
    /// <param name="depth">The maximum depth (must be &gt; 0).</param>
    /// <returns>This expression for chaining.</returns>
    IMappingExpression<TSource, TDestination> MaxDepth(int depth);

    /// <summary>
    /// Includes configuration from a base type map. Useful for inheritance hierarchies.
    /// </summary>
    /// <typeparam name="TBaseSrc">The base source type.</typeparam>
    /// <typeparam name="TBaseDest">The base destination type.</typeparam>
    /// <returns>This expression for chaining.</returns>
    IMappingExpression<TSource, TDestination> IncludeBase<TBaseSrc, TBaseDest>();

    /// <summary>
    /// Configures the member list used for validation.
    /// </summary>
    /// <param name="memberList">The member list to validate against.</param>
    /// <returns>This expression for chaining.</returns>
    IMappingExpression<TSource, TDestination> ValidateMemberList(MemberList memberList);

    /// <summary>
    /// Registers an action to execute before property mapping occurs.
    /// Multiple calls add additional actions executed in registration order.
    /// </summary>
    /// <param name="beforeFunction">Action receiving the source and destination objects.</param>
    /// <returns>This expression for chaining.</returns>
    IMappingExpression<TSource, TDestination> BeforeMap(Action<TSource, TDestination> beforeFunction);

    /// <summary>
    /// Registers an action to execute after all property mapping is complete.
    /// Multiple calls add additional actions executed in registration order.
    /// </summary>
    /// <param name="afterFunction">Action receiving the source and destination objects.</param>
    /// <returns>This expression for chaining.</returns>
    IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination> afterFunction);

    /// <summary>
    /// Enables circular reference tracking for this type map. When enabled,
    /// if the same source object is encountered again during mapping, the previously
    /// mapped destination is returned instead of creating a new one.
    /// </summary>
    /// <returns>This expression for chaining.</returns>
    IMappingExpression<TSource, TDestination> PreserveReferences();

    /// <summary>
    /// Registers a derived type pair for forward inheritance mapping.
    /// When mapping TSource→TDestination, if a derived source is encountered,
    /// it will use the derived map with inherited base configuration.
    /// </summary>
    /// <typeparam name="TDerivedSrc">The derived source type.</typeparam>
    /// <typeparam name="TDerivedDest">The derived destination type.</typeparam>
    /// <returns>This expression for chaining.</returns>
    IMappingExpression<TSource, TDestination> Include<TDerivedSrc, TDerivedDest>()
        where TDerivedSrc : TSource
        where TDerivedDest : TDestination;

    /// <summary>
    /// Automatically includes all derived type maps registered in the configuration.
    /// At compile time, scans for maps whose source derives from TSource and
    /// destination derives from TDestination, applying inheritance automatically.
    /// </summary>
    /// <returns>This expression for chaining.</returns>
    IMappingExpression<TSource, TDestination> IncludeAllDerived();

    /// <summary>
    /// Maps a nested destination property path (e.g., dest.Address.Street).
    /// Creates intermediate objects if necessary.
    /// </summary>
    /// <typeparam name="TMember">The type of the nested member.</typeparam>
    /// <param name="destinationPath">Expression selecting the nested destination path.</param>
    /// <param name="memberOptions">Action configuring the member mapping.</param>
    /// <returns>This expression for chaining.</returns>
    IMappingExpression<TSource, TDestination> ForPath<TMember>(
        Expression<Func<TDestination, TMember>> destinationPath,
        Action<IMemberConfigurationExpression<TSource, TDestination>> memberOptions);

    /// <summary>
    /// Treats a nested source member's properties as top-level source properties
    /// for auto-mapping purposes.
    /// </summary>
    /// <param name="memberExpressions">Expressions selecting the source members to include.</param>
    /// <returns>This expression for chaining.</returns>
    IMappingExpression<TSource, TDestination> IncludeMembers(
        params Expression<Func<TSource, object>>[] memberExpressions);
}
