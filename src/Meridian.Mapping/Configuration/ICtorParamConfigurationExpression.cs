using System.Linq.Expressions;

namespace Meridian.Mapping.Configuration;

/// <summary>
/// Fluent API for configuring how a constructor parameter is resolved during mapping.
/// </summary>
/// <typeparam name="TSource">The source type.</typeparam>
public interface ICtorParamConfigurationExpression<TSource>
{
    /// <summary>
    /// Maps the constructor parameter from a source expression.
    /// </summary>
    /// <typeparam name="TResult">The type of the expression result.</typeparam>
    /// <param name="mapExpression">Expression selecting the source value for this constructor parameter.</param>
    void MapFrom<TResult>(Expression<Func<TSource, TResult>> mapExpression);
}
