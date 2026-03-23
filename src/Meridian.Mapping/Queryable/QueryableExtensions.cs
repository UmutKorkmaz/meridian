using System.Linq.Expressions;

namespace Meridian.Mapping.Queryable;

/// <summary>
/// Extension methods for projecting <see cref="IQueryable"/> sources using
/// Meridian.Mapping configuration. Translates mapping configuration into
/// expression trees that can be consumed by LINQ providers (EF Core, LINQ to SQL).
/// </summary>
public static class QueryableExtensions
{
    /// <summary>
    /// Projects the source queryable to the destination type using the mapping
    /// configuration from the given <see cref="IConfigurationProvider"/>.
    /// The resulting query expression can be translated to SQL by EF Core
    /// or other LINQ providers.
    /// </summary>
    /// <typeparam name="TDestination">The destination/DTO type to project to.</typeparam>
    /// <param name="source">The source queryable (e.g., a DbSet).</param>
    /// <param name="configurationProvider">The mapper configuration containing type maps.</param>
    /// <param name="membersToExpand">Optional member expressions indicating which
    /// navigation properties to explicitly include in the projection.</param>
    /// <returns>An <see cref="IQueryable{TDestination}"/> representing the projected query.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no mapping configuration exists for the source-to-destination type pair.
    /// </exception>
    public static IQueryable<TDestination> ProjectTo<TDestination>(
        this IQueryable source,
        IConfigurationProvider configurationProvider,
        params Expression<Func<TDestination, object>>[]? membersToExpand)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(configurationProvider);

        var sourceElementType = source.ElementType;
        var destType = typeof(TDestination);

        // Build the projection expression using the generic overload
        var buildMethod = typeof(ExpressionBuilder)
            .GetMethod(nameof(ExpressionBuilder.BuildProjection), 2, new[]
            {
                typeof(IConfigurationProvider),
                typeof(Expression<>).MakeGenericType(typeof(Func<,>).MakeGenericType(
                    Type.MakeGenericMethodParameter(1), typeof(object))).MakeArrayType()
            });

        if (buildMethod != null)
        {
            // Use the generic BuildProjection<TSource, TDestination> via reflection
            // since we only know TSource at runtime
            var genericBuild = typeof(ExpressionBuilder).GetMethods()
                .First(m => m.Name == nameof(ExpressionBuilder.BuildProjection)
                    && m.IsGenericMethodDefinition
                    && m.GetGenericArguments().Length == 2);

            var closedBuild = genericBuild.MakeGenericMethod(sourceElementType, destType);
            var projection = (LambdaExpression)closedBuild.Invoke(null, new object?[] { configurationProvider, membersToExpand })!;

            // Call source.Select(projection) via Queryable.Select
            var selectMethod = typeof(System.Linq.Queryable)
                .GetMethods()
                .First(m => m.Name == "Select" && m.GetParameters().Length == 2)
                .MakeGenericMethod(sourceElementType, destType);

            return (IQueryable<TDestination>)selectMethod.Invoke(null, new object[] { source, projection })!;
        }

        // Fallback: use runtime type projection
        var runtimeProjection = ExpressionBuilder.BuildProjection(configurationProvider, sourceElementType, destType);

        var runtimeSelectMethod = typeof(System.Linq.Queryable)
            .GetMethods()
            .First(m => m.Name == "Select" && m.GetParameters().Length == 2)
            .MakeGenericMethod(sourceElementType, destType);

        return (IQueryable<TDestination>)runtimeSelectMethod.Invoke(null, new object[] { source, runtimeProjection })!;
    }
}
