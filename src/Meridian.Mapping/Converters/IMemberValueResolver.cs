using Meridian.Mapping.Execution;

namespace Meridian.Mapping.Converters;

/// <summary>
/// Resolves a destination member value using both source member and destination member access.
/// Provides finer-grained control than <see cref="IValueResolver{TSource, TDestination, TDestMember}"/>
/// by also receiving the source member value.
/// </summary>
/// <typeparam name="TSource">The source type.</typeparam>
/// <typeparam name="TDestination">The destination type.</typeparam>
/// <typeparam name="TSourceMember">The source member type.</typeparam>
/// <typeparam name="TDestMember">The destination member type.</typeparam>
public interface IMemberValueResolver<in TSource, in TDestination, in TSourceMember, TDestMember>
{
    /// <summary>
    /// Resolves the destination member value from source and destination member values.
    /// </summary>
    /// <param name="source">The source object.</param>
    /// <param name="destination">The destination object.</param>
    /// <param name="sourceMember">The source member value.</param>
    /// <param name="destMember">The current destination member value.</param>
    /// <param name="context">The resolution context.</param>
    /// <returns>The resolved destination member value.</returns>
    TDestMember Resolve(TSource source, TDestination destination, TSourceMember sourceMember, TDestMember destMember, ResolutionContext context);
}
