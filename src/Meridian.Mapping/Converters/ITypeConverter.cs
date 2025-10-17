using Meridian.Mapping.Execution;

namespace Meridian.Mapping.Converters;

/// <summary>
/// Converts an entire source object to a destination object.
/// Implement this interface for global type conversion logic that completely
/// replaces the default member-by-member mapping behavior.
/// </summary>
/// <typeparam name="TSource">The source type to convert from.</typeparam>
/// <typeparam name="TDestination">The destination type to convert to.</typeparam>
public interface ITypeConverter<in TSource, TDestination>
{
    /// <summary>
    /// Performs conversion from source to destination.
    /// </summary>
    /// <param name="source">The source object.</param>
    /// <param name="destination">The existing destination object (may be default).</param>
    /// <param name="context">The resolution context providing access to the mapper and depth tracking.</param>
    /// <returns>The converted destination object.</returns>
    TDestination Convert(TSource source, TDestination destination, ResolutionContext context);
}
