using Meridian.Mapping.Execution;

namespace Meridian.Mapping.Converters;

/// <summary>
/// Resolves a destination member value from the source and destination objects.
/// Implement this interface for custom per-member resolution logic, optionally
/// resolved from the DI container.
/// </summary>
/// <typeparam name="TSource">The source type.</typeparam>
/// <typeparam name="TDestination">The destination type.</typeparam>
/// <typeparam name="TDestMember">The destination member type.</typeparam>
public interface IValueResolver<in TSource, in TDestination, TDestMember>
{
    /// <summary>
    /// Resolves the destination member value.
    /// </summary>
    /// <param name="source">The source object.</param>
    /// <param name="destination">The destination object being mapped to.</param>
    /// <param name="destMember">The current value of the destination member.</param>
    /// <param name="context">The resolution context.</param>
    /// <returns>The resolved destination member value.</returns>
    TDestMember Resolve(TSource source, TDestination destination, TDestMember destMember, ResolutionContext context);
}
