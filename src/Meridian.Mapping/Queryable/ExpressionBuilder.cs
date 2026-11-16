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
        var newExpr = BuildConstruction(configurationProvider, typeof(TSource), typeof(TDestination), sourceParam, membersToExpand, out var consumed);
        var bindings = BuildMemberBindings(
            configurationProvider, typeof(TSource), typeof(TDestination), sourceParam, membersToExpand, consumed);
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
        var newExpr = BuildConstruction(configurationProvider, sourceType, destinationType, sourceParam, null, out var consumed);
        var bindings = BuildMemberBindings(
            configurationProvider, sourceType, destinationType, sourceParam, null, consumed);
        var memberInit = Expression.MemberInit(newExpr, bindings);

        var funcType = typeof(Func<,>).MakeGenericType(sourceType, destinationType);
        return Expression.Lambda(funcType, memberInit, sourceParam);
    }

    private static NewExpression BuildConstruction(
        IConfigurationProvider configurationProvider, Type sourceType, Type destinationType, Expression source,
        Expression[]? membersToExpand, out HashSet<string> consumed)
    {
        consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var map = configurationProvider.FindTypeMap(sourceType, destinationType)
            ?? throw new InvalidOperationException($"Missing mapping configuration for {sourceType.Name} -> {destinationType.Name}. Create a mapping using CreateMap<{sourceType.Name}, {destinationType.Name}>().");
        if (map.ConstructUsing != null)
            throw new InvalidOperationException($"Projection for '{destinationType.Name}' cannot use runtime ConstructUsing. Use ForCtorParam expressions instead.");
        if (map.CustomConverter != null || map.TypeConverterType != null || map.CompiledTypeConverter != null)
            throw new InvalidOperationException($"Projection for '{destinationType.Name}' cannot use a runtime type converter. Configure expression-based member mappings instead.");

        // Retain existing mutable/value-type construction unless explicit constructor mappings select a constructor.
        if (map.CtorParamMappings is not { Count: > 0 } &&
            (destinationType.IsValueType || destinationType.GetConstructor(Type.EmptyTypes) != null))
            return Expression.New(destinationType);

        var expanded = GetExpandedMemberNames(membersToExpand);
        var omittedRequired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var incompatibleArguments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(NewExpression Expression, HashSet<string> Consumed)>();
        foreach (var constructor in destinationType.GetConstructors().OrderByDescending(c => c.GetParameters().Length))
        {
            var parameters = constructor.GetParameters();
            var arguments = new List<Expression>();
            var consumedParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in parameters)
            {
                Expression? value = null;
                var applySubstitution = false;
                var destinationMember = map.PropertyMaps.Concat(map.ProjectionOnlyMemberMaps).FirstOrDefault(p =>
                    string.Equals(p.DestinationProperty.Name, parameter.Name, StringComparison.OrdinalIgnoreCase));
                var omitted = destinationMember != null && (destinationMember.Ignored ||
                    (destinationMember.ExplicitExpansion && !IsExpanded(expanded, destinationMember.DestinationProperty.Name)));
                if (omitted)
                {
                    if (!parameter.HasDefaultValue)
                    {
                        omittedRequired.Add(parameter.Name!);
                        break;
                    }
                    value = parameter.DefaultValue == null
                        ? Expression.Default(parameter.ParameterType)
                        : Expression.Constant(parameter.DefaultValue, parameter.ParameterType);
                }
                else
                {
                    if (destinationMember != null)
                        EnsureProjectionSupported(destinationMember, $"{destinationType.Name}.{destinationMember.DestinationProperty.Name}");
                    if (map.CtorParamMappings != null && map.CtorParamMappings.TryGetValue(parameter.Name!, out var configured))
                    {
                        value = ReplaceParameter(configured.Body, configured.Parameters[0], source);
                        consumedParameters.Add(parameter.Name!);
                        applySubstitution = true;
                    }
                    else if (destinationMember is { DestinationProperty.CanWrite: false } &&
                             TryBuildGetterValue(destinationMember, source, destinationType, out value))
                    {
                        consumedParameters.Add(parameter.Name!);
                        applySubstitution = !destinationMember.HasConstantValue;
                    }
                    else
                    {
                        var property = sourceType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                            .FirstOrDefault(p => p.GetMethod is { IsPublic: true } && p.GetIndexParameters().Length == 0 &&
                                string.Equals(p.Name, parameter.Name, StringComparison.OrdinalIgnoreCase) &&
                                (parameter.ParameterType.IsAssignableFrom(p.PropertyType) ||
                                 Nullable.GetUnderlyingType(parameter.ParameterType) == p.PropertyType));
                        if (property != null)
                        {
                            value = Expression.Property(source, property);
                            consumedParameters.Add(parameter.Name!);
                            applySubstitution = true;
                        }
                        else if (parameter.HasDefaultValue)
                            value = parameter.DefaultValue == null
                                ? Expression.Default(parameter.ParameterType)
                                : Expression.Constant(parameter.DefaultValue, parameter.ParameterType);
                    }
                }
                if (value == null) break;
                if (applySubstitution && destinationMember is { DestinationProperty.CanWrite: false, HasNullSubstitute: true })
                    value = ApplyNullSubstitute(value, destinationMember);
                if (value.Type != parameter.ParameterType)
                {
                    try { value = Expression.Convert(value, parameter.ParameterType); }
                    catch (InvalidOperationException)
                    {
                        // An unsupported conversion makes only this overload nonviable.
                        incompatibleArguments.Add(parameter.Name!);
                        break;
                    }
                }
                arguments.Add(value);
            }
            if (arguments.Count == parameters.Length)
                candidates.Add((Expression.New(constructor, arguments), consumedParameters));
        }

        if (candidates.Count == 0 && omittedRequired.Count > 0)
            throw new InvalidOperationException($"Projection for '{destinationType.Name}' has required constructor arguments for omitted members: {string.Join(", ", omittedRequired)}. Request explicit expansion, remove Ignore, or provide an optional constructor default.");
        if (candidates.Count == 0 && incompatibleArguments.Count > 0)
            throw new InvalidOperationException($"No compatible projection constructor on '{destinationType.Name}' can bind arguments: {string.Join(", ", incompatibleArguments)}. Use ForCtorParam expressions with compatible argument types.");
        if (candidates.Count == 0)
            throw new InvalidOperationException($"No public constructor on '{destinationType.Name}' has all required projection arguments mapped. Use ForCtorParam expressions to supply missing arguments.");
        if (candidates.Count > 1 && candidates[0].Expression.Arguments.Count == candidates[1].Expression.Arguments.Count)
            throw new InvalidOperationException($"Projection constructor selection for '{destinationType.Name}' is ambiguous. Use a DTO with a single fully resolved constructor of greatest arity.");
        consumed = candidates[0].Consumed;
        return candidates[0].Expression;
    }

    private static bool TryBuildGetterValue(PropertyMap member, Expression source, Type destinationType, out Expression? value)
    {
        // Match writable member source precedence. Constants bypass null substitution.
        if (member.HasConstantValue)
        {
            var type = member.DestinationProperty.PropertyType;
            var constant = member.ConstantValue;
            if (constant == null ? type.IsValueType && Nullable.GetUnderlyingType(type) == null
                : !type.IsInstanceOfType(constant) && !(Nullable.GetUnderlyingType(type)?.IsInstanceOfType(constant) ?? false))
                throw new InvalidOperationException($"Projection for '{destinationType.Name}.{member.DestinationProperty.Name}' has an incompatible constant value. Use a value compatible with '{type.Name}' or an expression mapping.");
            value = Expression.Constant(constant, type);
        }
        else if (member.CustomMapExpression is { } expression)
            value = ReplaceParameter(expression.Body, expression.Parameters[0], source);
        else if (member.SourcePropertyChain is { Length: > 0 } chain)
            value = BuildChainAccess(source, chain);
        else if (member.SourceProperty is { } property)
        {
            if (property.GetMethod is not { IsPublic: true } || property.GetIndexParameters().Length != 0)
                throw new InvalidOperationException($"Projection for '{destinationType.Name}.{member.DestinationProperty.Name}' requires a public readable source property. Use an expression mapping.");
            value = Expression.Property(source, property);
        }
        else
            value = null;
        return value != null;
    }

    private static List<MemberBinding> BuildMemberBindings(
        IConfigurationProvider configurationProvider,
        Type sourceType,
        Type destType,
        Expression sourceExpression,
        Expression[]? membersToExpand,
        HashSet<string> consumed)
    {
        var typeMap = configurationProvider.FindTypeMap(sourceType, destType);
        if (typeMap == null)
        {
            throw new InvalidOperationException(
                $"Missing mapping configuration for {sourceType.Name} -> {destType.Name}. " +
                $"Create a mapping using CreateMap<{sourceType.Name}, {destType.Name}>().");
        }

        var bindings = new List<MemberBinding>();
        var expandedMembers = GetExpandedMemberNames(membersToExpand);

        foreach (var propertyMap in typeMap.PropertyMaps)
        {
            if (propertyMap.Ignored)
                continue;

            // A record/init member already supplied by a constructor expression must not be
            // overwritten by convention. Explicit member configuration still has final precedence.
            if (!propertyMap.IsExplicitlyConfigured && consumed.Contains(propertyMap.DestinationProperty.Name))
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

            // An expansion marker alone must not overwrite an explicitly bound constructor argument.
            if (valueExpression == null && consumed.Contains(propertyMap.DestinationProperty.Name))
                continue;

            // Expansion-only configuration has no source metadata; preserve same-name convention.
            if (valueExpression == null && propertyMap.ExplicitExpansion)
            {
                var sourceProperty = sourceType.GetProperty(propertyMap.DestinationProperty.Name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (sourceProperty?.GetMethod is { IsPublic: true } && sourceProperty.GetIndexParameters().Length == 0)
                    valueExpression = Expression.Property(sourceExpression, sourceProperty);
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
        var newExpr = BuildConstruction(configurationProvider, sourceType, destType, sourceExpression, null, out var consumed);
        var nestedBindings = BuildMemberBindings(configurationProvider, sourceType, destType, sourceExpression, null, consumed);
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
        var newExpr = BuildConstruction(configurationProvider, srcElementType, destElementType, itemParam, null, out var consumed);
        var nestedBindings = BuildMemberBindings(configurationProvider, srcElementType, destElementType, itemParam, null, consumed);
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
