using System.Linq.Expressions;
using Meridian.Mapping.Converters;

namespace Meridian.Mapping.Configuration;

/// <summary>
/// Fluent API for configuring how an individual destination member is mapped.
/// Accessed via <see cref="IMappingExpression{TSource, TDestination}.ForMember(Expression{Func{TDestination, object?}}, Action{IMemberConfigurationExpression{TSource, TDestination}})"/>.
/// </summary>
/// <typeparam name="TSource">The source type.</typeparam>
/// <typeparam name="TDestination">The destination type.</typeparam>
public interface IMemberConfigurationExpression<TSource, TDestination>
{
    /// <summary>
    /// Maps the destination member from a source expression.
    /// </summary>
    /// <typeparam name="TResult">The type of the source expression result.</typeparam>
    /// <param name="mapExpression">Expression selecting the source value.</param>
    void MapFrom<TResult>(Expression<Func<TSource, TResult>> mapExpression);

    /// <summary>
    /// Maps the destination member from a function receiving the full source object.
    /// </summary>
    /// <typeparam name="TResult">The return type.</typeparam>
    /// <param name="mapFunction">Function computing the destination member value.</param>
    void MapFrom<TResult>(Func<TSource, TDestination, TResult> mapFunction);

    /// <summary>
    /// Resolves the destination member value using a value resolver type (can be DI-resolved).
    /// </summary>
    /// <typeparam name="TValueResolver">The value resolver type.</typeparam>
    void MapFrom<TValueResolver>() where TValueResolver : class;

    /// <summary>
    /// Maps the destination member from a source property by name.
    /// </summary>
    /// <param name="sourcePropertyName">The name of the source property.</param>
    void MapFrom(string sourcePropertyName);

    /// <summary>
    /// Ignores this destination member during mapping.
    /// The member will not be set and will retain its default value.
    /// </summary>
    void Ignore();

    /// <summary>
    /// Applies a runtime condition. The member is only mapped when the condition is true.
    /// The condition is evaluated after the source value is resolved.
    /// </summary>
    /// <param name="condition">A predicate evaluated against the source object.</param>
    void Condition(Func<TSource, bool> condition);

    /// <summary>
    /// Applies a runtime condition with access to source, destination, and the resolved source member value.
    /// The condition is evaluated after the source value is resolved.
    /// </summary>
    /// <param name="condition">A predicate receiving (source, destination, sourceMemberValue).</param>
    void Condition(Func<TSource, TDestination, object?, bool> condition);

    /// <summary>
    /// Applies a pre-condition. If false, the member mapping is skipped entirely
    /// (the source value is never resolved). Evaluated before <see cref="Condition(Func{TSource, bool})"/>.
    /// </summary>
    /// <param name="condition">A predicate evaluated against the source object.</param>
    void PreCondition(Func<TSource, bool> condition);

    /// <summary>
    /// Substitutes a value when the resolved source value is null.
    /// </summary>
    /// <param name="substitution">The value to use when source is null.</param>
    void NullSubstitute(object substitution);

    /// <summary>
    /// Uses a constant value for this destination member, ignoring the source entirely.
    /// </summary>
    /// <param name="value">The constant value to assign.</param>
    void UseValue(object value);

    /// <summary>
    /// Applies a value converter to the source member before assigning to the destination.
    /// </summary>
    /// <typeparam name="TSourceMember">The source member type.</typeparam>
    /// <typeparam name="TDestMember">The destination member type.</typeparam>
    /// <param name="converter">An <see cref="IValueConverter{TSourceMember, TDestMember}"/> instance.</param>
    /// <param name="sourceMember">Expression selecting the source member to convert.</param>
    void ConvertUsing<TSourceMember, TDestMember>(
        IValueConverter<TSourceMember, TDestMember> converter,
        Expression<Func<TSource, TSourceMember>> sourceMember);

    /// <summary>
    /// Applies a conversion function to the source member before assigning to the destination.
    /// </summary>
    /// <typeparam name="TSourceMember">The source member type.</typeparam>
    /// <typeparam name="TDestMember">The destination member type.</typeparam>
    /// <param name="converter">A conversion function.</param>
    /// <param name="sourceMember">Expression selecting the source member to convert.</param>
    void ConvertUsing<TSourceMember, TDestMember>(
        Func<TSourceMember, TDestMember> converter,
        Expression<Func<TSource, TSourceMember>> sourceMember);

    /// <summary>
    /// Resolves the destination member using a member value resolver that receives
    /// both the source member value and the current destination member value.
    /// </summary>
    /// <typeparam name="TValueResolver">
    /// A type implementing <see cref="IMemberValueResolver{TSource, TDestination, TSourceMember, TDestMember}"/>.
    /// </typeparam>
    /// <typeparam name="TSourceMember">The source member type.</typeparam>
    /// <param name="sourceMember">Expression selecting the source member whose value is passed to the resolver.</param>
    void MapFrom<TValueResolver, TSourceMember>(
        Expression<Func<TSource, TSourceMember>> sourceMember) where TValueResolver : class;
}
