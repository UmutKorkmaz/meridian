using System.Linq.Expressions;

namespace Meridian.Mapping.Configuration;

/// <summary>
/// Implementation of constructor parameter configuration.
/// Stores the expression used to resolve a constructor parameter from the source.
/// </summary>
/// <typeparam name="TSource">The source type.</typeparam>
public class CtorParamConfigurationExpression<TSource> : ICtorParamConfigurationExpression<TSource>, ICompiledCtorParamConfig
{
    internal LambdaExpression? MapFromExpression { get; private set; }

    /// <inheritdoc />
    public void MapFrom<TResult>(Expression<Func<TSource, TResult>> mapExpression)
    {
        MapFromExpression = mapExpression ?? throw new ArgumentNullException(nameof(mapExpression));
    }

    LambdaExpression? ICompiledCtorParamConfig.GetMapFromExpression() => MapFromExpression;
}
