using System.Linq.Expressions;
using System.Reflection;

namespace Meridian.Mapping.Execution;

/// <summary>
/// Emits a single compiled <c>Func&lt;object, IMapper, object&gt;</c> per
/// <see cref="TypeMap"/> that inlines the entire mapping — destination
/// construction, every property assignment, and recursive nested-type
/// dispatch — into one delegate call. This is the reflection-based
/// equivalent of what Phase 5's source generator will emit at compile time.
/// </summary>
/// <remarks>
/// <para>
/// The compiler is deliberately conservative: any <see cref="TypeMap"/>
/// using resolvers, converters, conditions, constant values, null
/// substitutions, custom ctors, custom funcs, before/after-map actions,
/// base-type inheritance, polymorphic dispatch, or <c>PreserveReferences</c>
/// returns a <c>null</c> compiled delegate and falls back to the
/// interpreter path in <see cref="MappingEngine"/>. Correctness is the
/// hard requirement here — the fast path exists only for the common
/// <c>.ForMember(d, o =&gt; o.MapFrom(s =&gt; ...))</c> shape that covers
/// the vast majority of real-world mappers.
/// </para>
/// <para>
/// The generated expression tree uses <see cref="Expression.Property"/>
/// for direct property access and <see cref="Expression.Convert"/> for
/// primitive conversions. Nested type mappings are routed back through
/// <see cref="IMapper.Map(object, Type, Type)"/> — safe, but one virtual
/// call per nested property. When the nested TypeMap has its own
/// fast path, the second call hits <see cref="MappingEngine.MapWithTypeMap"/>'s
/// fast-path branch and stays on the hot path.
/// </para>
/// </remarks>
internal static class FastPathCompiler
{
    private static readonly MethodInfo _engineMapMethod =
        typeof(MappingEngine).GetMethod(
            nameof(MappingEngine.Map),
            BindingFlags.Public | BindingFlags.Instance,
            null,
            [typeof(object), typeof(Type), typeof(Type), typeof(ResolutionContext)],
            null)
        ?? throw new InvalidOperationException("MappingEngine.Map(object, Type, Type, ResolutionContext) not found.");

    private static readonly MethodInfo _contextIncrementDepthMethod =
        typeof(ResolutionContext).GetMethod(nameof(ResolutionContext.IncrementDepth))!;

    /// <summary>
    /// Attempts to compile a fast-path delegate for <paramref name="typeMap"/>.
    /// Returns <c>null</c> when the type map uses features the fast path
    /// does not handle — the caller must fall back to the interpreter.
    /// </summary>
    /// <param name="typeMap">The type map to compile.</param>
    /// <param name="allowNullDestinationValues">
    /// The configuration's <see cref="IConfigurationProvider.AllowNullDestinationValues"/>
    /// flag. When <c>false</c> the interpreter skips assigning <c>null</c> to
    /// reference-type destination members, which the fast path does not
    /// model — compilation is refused in that case.
    /// </param>
    /// <param name="hasValueTransformers">
    /// Whether the mapper has any global value transformers registered.
    /// Value transformers run per-property in the interpreter; the fast
    /// path does not model them and refuses compilation.
    /// </param>
    public static Func<object, MappingEngine, ResolutionContext, object>? TryCompile(
        TypeMap typeMap,
        bool allowNullDestinationValues,
        bool hasValueTransformers)
    {
        if (!IsSimpleTypeMap(typeMap, allowNullDestinationValues, hasValueTransformers))
            return null;

        var parameterlessCtor = typeMap.DestinationType.GetConstructor(
            BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (parameterlessCtor is null)
            return null;

        var srcParam = Expression.Parameter(typeof(object), "srcObj");
        var engineParam = Expression.Parameter(typeof(MappingEngine), "engine");
        var ctxParam = Expression.Parameter(typeof(ResolutionContext), "ctx");
        var srcTyped = Expression.Variable(typeMap.SourceType, "src");
        var dst = Expression.Variable(typeMap.DestinationType, "dst");

        var body = new List<Expression>
        {
            Expression.Assign(srcTyped, Expression.Convert(srcParam, typeMap.SourceType)),
            Expression.Assign(dst, Expression.New(parameterlessCtor)),
        };

        foreach (var pm in typeMap.PropertyMaps)
        {
            if (pm.Ignored) continue;

            var assign = TryBuildAssignment(pm, srcTyped, dst, engineParam, ctxParam);
            if (assign is null) return null;  // unreachable given IsSimpleTypeMap, but defensive
            body.Add(assign);
        }

        body.Add(Expression.Convert(dst, typeof(object)));

        var block = Expression.Block(typeof(object), [srcTyped, dst], body);

        try
        {
            return Expression.Lambda<Func<object, MappingEngine, ResolutionContext, object>>(
                block, srcParam, engineParam, ctxParam).Compile();
        }
        catch
        {
            // If Expression.Lambda rejects something we failed to anticipate,
            // the interpreter is a safe fallback — correctness over speed.
            return null;
        }
    }

    /// <summary>
    /// Diagnostic variant of <see cref="IsSimpleTypeMap"/>: returns the first
    /// reason the type map was rejected, or <c>null</c> if it passes.
    /// Used by tests to understand why a fast path did not compile.
    /// </summary>
    internal static string? DescribeRejection(
        TypeMap t, bool allowNullDestinationValues, bool hasValueTransformers)
    {
        if (!allowNullDestinationValues) return "AllowNullDestinationValues=false";
        if (hasValueTransformers) return "ValueTransformers present";
        if (t.CustomConverter is not null) return "CustomConverter";
        if (t.TypeConverterType is not null) return "TypeConverterType";
        if (t.CompiledTypeConverter is not null) return "CompiledTypeConverter";
        if (t.ConstructUsing is not null) return "ConstructUsing";
        // CompiledObjectFactory is intentionally NOT checked: it's the
        // interpreter's pre-compiled `new TDest()` helper and the fast path
        // emits its own Expression.New, so its presence on a TypeMap is
        // orthogonal to fast-path eligibility.
        if (t.CtorParamMappings is { Count: > 0 }) return "CtorParamMappings";
        if (t.BeforeMapActions is { Count: > 0 }) return "BeforeMapActions";
        if (t.AfterMapActions is { Count: > 0 }) return "AfterMapActions";
        if (t.BaseTypeMaps is { Count: > 0 }) return "BaseTypeMaps";
        if (t.DerivedTypeMaps is { Count: > 0 }) return "DerivedTypeMaps";
        if (t.IncludedMemberGetters is { Count: > 0 }) return "IncludedMemberGetters";
        if (t.PreserveReferences) return "PreserveReferences";

        var parameterlessCtor = t.DestinationType.GetConstructor(
            BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (parameterlessCtor is null)
            return $"destination type {t.DestinationType.Name} has no parameterless public ctor";

        for (int i = 0; i < t.PropertyMaps.Count; i++)
        {
            var pm = t.PropertyMaps[i];
            if (pm.Ignored) continue;
            var prop = pm.DestinationProperty.Name;
            if (pm.PreCondition is not null) return $"{prop}: PreCondition";
            if (pm.Condition is not null) return $"{prop}: Condition";
            if (pm.Condition3Arg is not null) return $"{prop}: Condition3Arg";
            if (pm.HasConstantValue) return $"{prop}: HasConstantValue";
            if (pm.HasNullSubstitute) return $"{prop}: HasNullSubstitute";
            if (pm.UseDestinationValue != null) return $"{prop}: UseDestinationValue";
            if (pm.AllowNull != null) return $"{prop}: AllowNull";
            if (pm.ValueResolverType is not null) return $"{prop}: ValueResolverType";
            if (pm.MemberValueResolverType is not null) return $"{prop}: MemberValueResolverType";
            if (pm.MemberConverterInstance is not null) return $"{prop}: MemberConverterInstance";
            if (pm.MemberConverterFunc is not null) return $"{prop}: MemberConverterFunc";
            if (pm.CustomMapFunc is not null && pm.CustomMapExpression is null)
                return $"{prop}: 2-arg CustomMapFunc without expression";
            if (pm.DestinationPropertyChain is not null) return $"{prop}: DestinationPropertyChain";

            var hasSource = pm.CustomMapExpression is not null
                            || pm.SourceProperty is not null
                            || (pm.SourcePropertyChain is { Length: > 0 });
            if (!hasSource) return $"{prop}: no source (no MapFrom, SourceProperty, or chain)";

            if (!pm.DestinationProperty.CanWrite) return $"{prop}: dest property not writable";
            if (pm.DestinationProperty.GetIndexParameters().Length > 0) return $"{prop}: indexer";
            if (IsCollection(pm.DestinationProperty.PropertyType))
                return $"{prop}: collection-typed destination";
        }

        return null;
    }

    private static bool IsSimpleTypeMap(TypeMap t, bool allowNullDestinationValues, bool hasValueTransformers)
    {
        if (!allowNullDestinationValues) return false;
        if (hasValueTransformers) return false;

        if (t.CustomConverter is not null) return false;
        if (t.TypeConverterType is not null) return false;
        if (t.CompiledTypeConverter is not null) return false;
        if (t.ConstructUsing is not null) return false;
        // CompiledObjectFactory intentionally not a blocker — see DescribeRejection.
        if (t.CtorParamMappings is { Count: > 0 }) return false;
        if (t.BeforeMapActions is { Count: > 0 }) return false;
        if (t.AfterMapActions is { Count: > 0 }) return false;
        if (t.BaseTypeMaps is { Count: > 0 }) return false;
        if (t.DerivedTypeMaps is { Count: > 0 }) return false;
        if (t.IncludedMemberGetters is { Count: > 0 }) return false;
        if (t.PreserveReferences) return false;

        foreach (var pm in t.PropertyMaps)
        {
            if (pm.Ignored) continue;
            if (pm.PreCondition is not null) return false;
            if (pm.Condition is not null) return false;
            if (pm.Condition3Arg is not null) return false;
            if (pm.HasConstantValue) return false;
            if (pm.HasNullSubstitute) return false;
            if (pm.UseDestinationValue != null) return false;
            if (pm.AllowNull != null) return false;
            if (pm.ValueResolverType is not null) return false;
            if (pm.MemberValueResolverType is not null) return false;
            if (pm.MemberConverterInstance is not null) return false;
            if (pm.MemberConverterFunc is not null) return false;

            // CustomMapFunc is populated alongside CustomMapExpression for
            // standard .MapFrom(src => ...) — we can still inline by using
            // the expression. Only refuse when the user supplied a 2-arg
            // custom func without an expression form (destination-aware
            // MapFrom), since that requires both arguments at runtime.
            if (pm.CustomMapFunc is not null && pm.CustomMapExpression is null) return false;

            if (pm.DestinationPropertyChain is not null) return false;  // ForPath

            var hasSource = pm.CustomMapExpression is not null
                            || pm.SourceProperty is not null
                            || (pm.SourcePropertyChain is { Length: > 0 });
            if (!hasSource) return false;

            if (!pm.DestinationProperty.CanWrite) return false;
            if (pm.DestinationProperty.GetIndexParameters().Length > 0) return false;

            // Collections would need a length cap in the hot path — defer to interpreter.
            if (IsCollection(pm.DestinationProperty.PropertyType)) return false;
        }

        return true;
    }

    private static bool IsCollection(Type t)
    {
        if (t == typeof(string)) return false;
        if (t.IsArray) return true;
        if (!t.IsGenericType) return false;

        var def = t.GetGenericTypeDefinition();
        return def == typeof(IEnumerable<>)
            || def == typeof(ICollection<>)
            || def == typeof(IList<>)
            || def == typeof(List<>)
            || def == typeof(IReadOnlyList<>)
            || def == typeof(IReadOnlyCollection<>)
            || def == typeof(HashSet<>)
            || def == typeof(ISet<>)
            || def == typeof(Dictionary<,>)
            || def == typeof(IDictionary<,>)
            || def == typeof(IReadOnlyDictionary<,>);
    }

    private static Expression? TryBuildAssignment(
        PropertyMap pm,
        ParameterExpression srcTyped,
        ParameterExpression dst,
        ParameterExpression engineParam,
        ParameterExpression ctxParam)
    {
        var sourceExpr = BuildSourceExpression(pm, srcTyped);
        if (sourceExpr is null) return null;

        var destProp = pm.DestinationProperty;
        var destType = destProp.PropertyType;
        var destAccess = Expression.Property(dst, destProp);

        Expression valueExpr;

        if (destType.IsAssignableFrom(sourceExpr.Type))
        {
            // Happy path: same type or source-is-subclass-of-dest.
            valueExpr = sourceExpr;
        }
        else if (IsSimpleConvertible(sourceExpr.Type, destType))
        {
            // Primitive numeric conversion, enum<->number, nullable<T> fold.
            valueExpr = Expression.Convert(sourceExpr, destType);
        }
        else
        {
            // Nested object type — dispatch back through MappingEngine.Map
            // with a depth-incremented context so DefaultMaxDepth and
            // PreserveReferences enforcement survive the nested call.
            var boxed = Expression.Convert(sourceExpr, typeof(object));
            var incrementedCtx = Expression.Call(ctxParam, _contextIncrementDepthMethod);
            var mapCall = Expression.Call(
                engineParam,
                _engineMapMethod,
                boxed,
                Expression.Constant(sourceExpr.Type, typeof(Type)),
                Expression.Constant(destType, typeof(Type)),
                incrementedCtx);

            var converted = destType == typeof(object)
                ? (Expression)mapCall
                : Expression.Convert(mapCall, destType);

            if (!sourceExpr.Type.IsValueType)
            {
                // sourceExpr is a reference type — guard against null to avoid
                // MappingEngine.Map treating null source as a no-op cast.
                var nullConst = Expression.Constant(null, sourceExpr.Type);
                var isNull = Expression.Equal(sourceExpr, nullConst);
                var defaultDest = destType.IsValueType
                    ? (Expression)Expression.Default(destType)
                    : Expression.Constant(null, destType);

                valueExpr = Expression.Condition(isNull, defaultDest, converted);
            }
            else
            {
                valueExpr = converted;
            }
        }

        // Belt and suspenders: every branch above is supposed to land at
        // destType, but Expression.Assign is strict about exact type match
        // and the reflection-level IsAssignableFrom/IsSimpleConvertible
        // interaction with nullable value types has subtle cases (e.g.
        // int -> int?). Force a final convert when needed so we get a
        // clean assign rather than a cryptic ArgumentException at config time.
        if (valueExpr.Type != destType)
        {
            valueExpr = Expression.Convert(valueExpr, destType);
        }

        return Expression.Assign(destAccess, valueExpr);
    }

    private static Expression? BuildSourceExpression(PropertyMap pm, ParameterExpression srcTyped)
    {
        if (pm.CustomMapExpression is { } lambda)
        {
            // Rewrite the lambda's source parameter to reference our typed
            // local, then splice the body directly into the fast-path lambda.
            return ParameterReplacer.Replace(lambda.Body, lambda.Parameters[0], srcTyped);
        }

        if (pm.SourcePropertyChain is { Length: > 0 } chain)
        {
            // Null-safe chained property access: if any intermediate is null,
            // the final value is default. Matches the interpreter's
            // CompileChainGetter behaviour.
            var lastValueType = chain[^1].PropertyType;
            var defaultValue = lastValueType.IsValueType
                ? (Expression)Expression.Default(lastValueType)
                : Expression.Constant(null, lastValueType);

            Expression current = srcTyped;
            Expression? guard = null;

            for (var i = 0; i < chain.Length - 1; i++)
            {
                current = Expression.Property(current, chain[i]);
                if (!chain[i].PropertyType.IsValueType)
                {
                    var isNull = Expression.Equal(current, Expression.Constant(null, chain[i].PropertyType));
                    guard = guard is null ? (Expression)isNull : Expression.OrElse(guard, isNull);
                }
            }

            current = Expression.Property(current, chain[^1]);
            return guard is null
                ? current
                : Expression.Condition(guard, defaultValue, current);
        }

        if (pm.SourceProperty is { } sp)
        {
            return Expression.Property(srcTyped, sp);
        }

        return null;
    }

    /// <summary>
    /// Matches the interpreter's Convert.ChangeType-style conversion path
    /// for primitive types, enums, and nullables — the cases where
    /// <see cref="Expression.Convert"/> produces correct IL.
    /// </summary>
    private static bool IsSimpleConvertible(Type source, Type dest)
    {
        source = Nullable.GetUnderlyingType(source) ?? source;
        dest = Nullable.GetUnderlyingType(dest) ?? dest;

        if (source.IsPrimitive && dest.IsPrimitive) return true;
        if (source.IsEnum && dest.IsEnum) return true;
        if (source.IsEnum && dest.IsPrimitive) return true;
        if (source.IsPrimitive && dest.IsEnum) return true;
        if (source == typeof(decimal) && dest.IsPrimitive) return true;
        if (source.IsPrimitive && dest == typeof(decimal)) return true;
        if (source == dest) return true;

        return false;
    }

    private sealed class ParameterReplacer : ExpressionVisitor
    {
        private readonly ParameterExpression _target;
        private readonly Expression _replacement;

        private ParameterReplacer(ParameterExpression target, Expression replacement)
        {
            _target = target;
            _replacement = replacement;
        }

        public static Expression Replace(Expression body, ParameterExpression target, Expression replacement)
            => new ParameterReplacer(target, replacement).Visit(body);

        protected override Expression VisitParameter(ParameterExpression node)
            => node == _target ? _replacement : base.VisitParameter(node);
    }
}
