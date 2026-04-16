using System.Linq.Expressions;
using System.Reflection;
using Meridian.Mapping.Execution;

namespace Meridian.Mapping.Queryable;

/// <summary>
/// Builds LINQ expression trees from <see cref="TypeMap"/> configurations.
/// These expressions can be used with IQueryable providers (EF Core, LINQ to SQL)
/// to translate mapping configuration into SQL projections.
/// </summary>
public static class ExpressionBuilder
{
    /// <summary>
    /// Builds a projection expression from source type to destination type
    /// using the configured <see cref="TypeMap"/>.
    /// </summary>
    /// <typeparam name="TSource">The source type (entity).</typeparam>
    /// <typeparam name="TDestination">The destination type (DTO).</typeparam>
    /// <param name="configurationProvider">The configuration provider containing type maps.</param>
    /// <param name="membersToExpand">Optional members to explicitly expand (for nested projections).</param>
    /// <returns>An expression tree representing the projection.</returns>
    public static Expression<Func<TSource, TDestination>> BuildProjection<TSource, TDestination>(
        IConfigurationProvider configurationProvider,
        params Expression<Func<TDestination, object>>[]? membersToExpand)
    {
        var sourceParam = Expression.Parameter(typeof(TSource), "src");
        var bindings = BuildMemberBindings(
            configurationProvider, typeof(TSource), typeof(TDestination), sourceParam, membersToExpand);

        var newExpr = Expression.New(typeof(TDestination));
        var memberInit = Expression.MemberInit(newExpr, bindings);

        return Expression.Lambda<Func<TSource, TDestination>>(memberInit, sourceParam);
    }

    /// <summary>
    /// Builds a projection expression using runtime types.
    /// </summary>
    /// <param name="configurationProvider">The configuration provider.</param>
    /// <param name="sourceType">The source type.</param>
    /// <param name="destinationType">The destination type.</param>
    /// <returns>A lambda expression for the projection.</returns>
    public static LambdaExpression BuildProjection(
        IConfigurationProvider configurationProvider,
        Type sourceType,
        Type destinationType)
    {
        var sourceParam = Expression.Parameter(sourceType, "src");
        var bindings = BuildMemberBindings(
            configurationProvider, sourceType, destinationType, sourceParam, null);

        var newExpr = Expression.New(destinationType);
        var memberInit = Expression.MemberInit(newExpr, bindings);

        var funcType = typeof(Func<,>).MakeGenericType(sourceType, destinationType);
        return Expression.Lambda(funcType, memberInit, sourceParam);
    }

    private static List<MemberBinding> BuildMemberBindings(
        IConfigurationProvider configurationProvider,
        Type sourceType,
        Type destType,
        Expression sourceExpression,
        Expression[]? membersToExpand)
    {
        var typeMap = configurationProvider.FindTypeMap(sourceType, destType);
        if (typeMap == null)
        {
            throw new InvalidOperationException(
                $"Missing mapping configuration for {sourceType.FullName} -> {destType.FullName}. " +
                $"Create a mapping using CreateMap<{sourceType.Name}, {destType.Name}>().");
        }

        var bindings = new List<MemberBinding>();
        var expandedMembers = GetExpandedMemberNames(membersToExpand);

        foreach (var propertyMap in typeMap.PropertyMaps)
        {
            if (propertyMap.Ignored)
                continue;

            if (propertyMap.ExplicitExpansion && !IsExpanded(expandedMembers, propertyMap.DestinationProperty.Name))
                continue;

            EnsureProjectionSupported(propertyMap, $"{destType.Name}.{propertyMap.DestinationProperty.Name}");

            // Skip constant values for projection
            if (propertyMap.HasConstantValue)
            {
                var constantExpr = Expression.Constant(propertyMap.ConstantValue, propertyMap.DestinationProperty.PropertyType);
                bindings.Add(Expression.Bind(propertyMap.DestinationProperty, constantExpr));
                continue;
            }

            Expression? valueExpression = null;

            if (propertyMap.CustomMapExpression != null)
            {
                // Embed the custom expression directly, replacing its parameter with our source
                valueExpression = ReplaceParameter(
                    propertyMap.CustomMapExpression.Body,
                    propertyMap.CustomMapExpression.Parameters[0],
                    sourceExpression);
            }
            else if (propertyMap.SourcePropertyChain != null && propertyMap.SourcePropertyChain.Length > 0)
            {
                // Build nested member access: src.Address.Street
                valueExpression = BuildChainAccess(sourceExpression, propertyMap.SourcePropertyChain);
            }
            else if (propertyMap.SourceProperty != null)
            {
                // Simple property access: src.Name
                valueExpression = Expression.Property(sourceExpression, propertyMap.SourceProperty);
            }

            if (valueExpression == null)
                continue;

            if (propertyMap.HasNullSubstitute)
            {
                valueExpression = ApplyNullSubstitute(valueExpression, propertyMap);
            }

            var destPropType = propertyMap.DestinationProperty.PropertyType;
            var valuePropType = valueExpression.Type;

            // Check if this is a complex type that needs sub-projection
            if (ShouldProjectNested(configurationProvider, valuePropType, destPropType))
            {
                valueExpression = BuildNestedProjection(
                    configurationProvider, valueExpression, valuePropType, destPropType);
            }
            // Check if it's a collection of complex types
            else if (IsCollectionProjection(configurationProvider, valuePropType, destPropType, out var srcElemType, out var destElemType))
            {
                valueExpression = BuildCollectionProjection(
                    configurationProvider, valueExpression, valuePropType, destPropType, srcElemType!, destElemType!);
            }
            else if (valueExpression.Type != destPropType)
            {
                // Add type conversion if needed
                valueExpression = Expression.Convert(valueExpression, destPropType);
            }

            bindings.Add(Expression.Bind(propertyMap.DestinationProperty, valueExpression));
        }

        return bindings;
    }

    private static HashSet<string>? GetExpandedMemberNames(Expression[]? membersToExpand)
    {
        if (membersToExpand == null || membersToExpand.Length == 0)
            return null;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var expr in membersToExpand)
        {
            if (expr is LambdaExpression lambda)
            {
                var body = lambda.Body;
                if (body is UnaryExpression unary &&
                    (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
                {
                    body = unary.Operand;
                }

                if (body is MemberExpression member)
                {
                    names.Add(member.Member.Name);
                }
            }
        }

        return names.Count > 0 ? names : null;
    }

    private static bool IsExpanded(HashSet<string>? expandedMembers, string memberName)
    {
        return expandedMembers != null && expandedMembers.Contains(memberName);
    }

    private static bool ShouldProjectNested(
        IConfigurationProvider configurationProvider,
        Type sourcePropertyType,
        Type destPropertyType)
    {
        if (sourcePropertyType.IsPrimitive || sourcePropertyType == typeof(string) ||
            sourcePropertyType == typeof(decimal) || sourcePropertyType == typeof(DateTime) ||
            sourcePropertyType == typeof(DateTimeOffset) || sourcePropertyType == typeof(Guid) ||
            sourcePropertyType.IsEnum)
            return false;

        if (Nullable.GetUnderlyingType(sourcePropertyType) != null)
            return false;

        // Check if a type map exists for the nested type
        var typeMap = configurationProvider.FindTypeMap(sourcePropertyType, destPropertyType);
        return typeMap != null;
    }

    private static void EnsureProjectionSupported(PropertyMap propertyMap, string memberPath)
    {
        if (propertyMap.ValueResolverType != null)
            throw new InvalidOperationException($"Projection for '{memberPath}' is not supported because it uses a value resolver.");

        if (propertyMap.MemberValueResolverType != null)
            throw new InvalidOperationException($"Projection for '{memberPath}' is not supported because it uses a member value resolver.");

        if (propertyMap.MemberConverterInstance != null || propertyMap.MemberConverterFunc != null)
            throw new InvalidOperationException($"Projection for '{memberPath}' is not supported because it uses a member converter.");

        if (propertyMap.CustomMapFunc != null && propertyMap.CustomMapExpression == null)
            throw new InvalidOperationException($"Projection for '{memberPath}' is not supported because it uses a runtime mapping function.");

        if (propertyMap.PreCondition != null || propertyMap.Condition != null || propertyMap.Condition3Arg != null)
            throw new InvalidOperationException($"Projection for '{memberPath}' is not supported because it uses runtime conditions.");

        if (propertyMap.DestinationPropertyChain != null)
            throw new InvalidOperationException($"Projection for '{memberPath}' is not supported because it maps a destination path.");
    }

    private static Expression ApplyNullSubstitute(Expression valueExpression, PropertyMap propertyMap)
    {
        if (valueExpression.Type.IsValueType && Nullable.GetUnderlyingType(valueExpression.Type) == null)
            return valueExpression;

        var substituteType = propertyMap.NullSubstitute?.GetType() ?? propertyMap.DestinationProperty.PropertyType;
        Expression substitute = Expression.Constant(propertyMap.NullSubstitute, substituteType);

        if (substitute.Type != valueExpression.Type)
        {
            substitute = Expression.Convert(substitute, valueExpression.Type);
        }

        return Expression.Coalesce(valueExpression, substitute);
    }

    private static Expression BuildNestedProjection(
        IConfigurationProvider configurationProvider,
        Expression sourceExpression,
        Type sourceType,
        Type destType)
    {
        var nestedBindings = BuildMemberBindings(configurationProvider, sourceType, destType, sourceExpression, null);
        var newExpr = Expression.New(destType);
        var memberInit = Expression.MemberInit(newExpr, nestedBindings);

        // Handle null source: src.Address == null ? null : new AddressDto { ... }
        if (!sourceType.IsValueType)
        {
            var nullCheck = Expression.Equal(sourceExpression, Expression.Constant(null, sourceType));
            return Expression.Condition(nullCheck, Expression.Constant(null, destType), memberInit);
        }

        return memberInit;
    }

    private static bool IsCollectionProjection(
        IConfigurationProvider configurationProvider,
        Type sourceType,
        Type destType,
        out Type? srcElementType,
        out Type? destElementType)
    {
        srcElementType = null;
        destElementType = null;

        if (sourceType == typeof(string) || destType == typeof(string))
            return false;

        srcElementType = GetElementType(sourceType);
        destElementType = GetElementType(destType);

        if (srcElementType == null || destElementType == null)
            return false;

        // Check if a type map exists for the element types
        if (srcElementType == destElementType || srcElementType.IsPrimitive || srcElementType == typeof(string))
            return false;

        return configurationProvider.FindTypeMap(srcElementType, destElementType) != null;
    }

    private static Expression BuildCollectionProjection(
        IConfigurationProvider configurationProvider,
        Expression sourceExpression,
        Type sourceCollectionType,
        Type destCollectionType,
        Type srcElementType,
        Type destElementType)
    {
        // Build: src.Items.Select(x => new ItemDto { ... })
        var itemParam = Expression.Parameter(srcElementType, "item");
        var nestedBindings = BuildMemberBindings(configurationProvider, srcElementType, destElementType, itemParam, null);
        var newExpr = Expression.New(destElementType);
        var memberInit = Expression.MemberInit(newExpr, nestedBindings);
        var selectorLambda = Expression.Lambda(memberInit, itemParam);

        // Call Enumerable.Select
        var selectMethod = typeof(Enumerable).GetMethods()
            .First(m => m.Name == "Select" && m.GetParameters().Length == 2
                && m.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2)
            .MakeGenericMethod(srcElementType, destElementType);

        Expression selectCall = Expression.Call(null, selectMethod, sourceExpression, selectorLambda);

        // Add .ToList() or .ToArray() based on destination type
        if (destCollectionType.IsArray)
        {
            var toArrayMethod = typeof(Enumerable).GetMethod("ToArray")!
                .MakeGenericMethod(destElementType);
            selectCall = Expression.Call(null, toArrayMethod, selectCall);
        }
        else
        {
            var toListMethod = typeof(Enumerable).GetMethod("ToList")!
                .MakeGenericMethod(destElementType);
            selectCall = Expression.Call(null, toListMethod, selectCall);
        }

        // Handle null source collection
        if (!sourceCollectionType.IsValueType)
        {
            var nullCheck = Expression.Equal(sourceExpression, Expression.Constant(null, sourceCollectionType));
            var nullResult = Expression.Constant(null, destCollectionType);
            return Expression.Condition(nullCheck, nullResult, selectCall);
        }

        return selectCall;
    }

    private static Expression BuildChainAccess(Expression source, PropertyInfo[] chain)
    {
        Expression current = source;
        for (int i = 0; i < chain.Length; i++)
        {
            current = Expression.Property(current, chain[i]);
        }
        return current;
    }

    private static Expression ReplaceParameter(Expression body, ParameterExpression oldParam, Expression newExpr)
    {
        return new ParameterReplacer(oldParam, newExpr).Visit(body);
    }

    private static Type? GetElementType(Type type)
    {
        if (type.IsArray)
            return type.GetElementType();

        if (type.IsGenericType)
        {
            var genericDef = type.GetGenericTypeDefinition();
            if (genericDef == typeof(IEnumerable<>) ||
                genericDef == typeof(ICollection<>) ||
                genericDef == typeof(IList<>) ||
                genericDef == typeof(List<>) ||
                genericDef == typeof(IReadOnlyList<>) ||
                genericDef == typeof(IReadOnlyCollection<>))
            {
                return type.GetGenericArguments()[0];
            }
        }

        var enumInterface = type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));

        return enumInterface?.GetGenericArguments()[0];
    }

    private class ParameterReplacer : ExpressionVisitor
    {
        private readonly ParameterExpression _oldParam;
        private readonly Expression _newExpr;

        public ParameterReplacer(ParameterExpression oldParam, Expression newExpr)
        {
            _oldParam = oldParam;
            _newExpr = newExpr;
        }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            return node == _oldParam ? _newExpr : base.VisitParameter(node);
        }
    }
}
