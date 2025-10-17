using Meridian.Mapping.Execution;

namespace Meridian.Mapping.Converters;

/// <summary>
/// Converts a source member value to a destination member value.
/// A simpler alternative to <see cref="IValueResolver{TSource, TDestination, TDestMember}"/>
/// when you only need the source member value (not the full source object).
/// </summary>
/// <typeparam name="TSourceMember">The source member type.</typeparam>
/// <typeparam name="TDestMember">The destination member type.</typeparam>
public interface IValueConverter<in TSourceMember, TDestMember>
{
    /// <summary>
    /// Converts the source member value to the destination member value.
    /// </summary>
    /// <param name="sourceMember">The source member value.</param>
    /// <param name="context">The resolution context.</param>
    /// <returns>The converted destination member value.</returns>
    TDestMember Convert(TSourceMember sourceMember, ResolutionContext context);
}
