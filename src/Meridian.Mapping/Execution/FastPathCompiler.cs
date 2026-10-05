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
/// The generated expression tree uses <see cref="Expression.Property(Expression, System.Reflection.PropertyInfo)"/>
/// for direct property access. Non-assignable values are routed through
/// <see cref="MappingEngine.ConvertValue"/>, which is the same conversion
/// routine used by the interpreter. Nested type mappings therefore retain
/// depth-aware dispatch while all conversion semantics have one owner.
/// </para>
/// <para>
/// Eligible nested maps (see <see cref="TryCompile"/>) are emitted inline into the
/// parent's delegate instead of dispatching through <see cref="MappingEngine.ConvertValue"/>.
/// Inlining is a compile-time snapshot: replacing a nested <see cref="TypeMap.CompiledFastPath"/>
/// afterwards does not affect an already-compiled parent, so tests that need the interpreter must
/// clear every type map's delegate, parents included.
/// </para>
/// </remarks>
internal static class FastPathCompiler
{
    /// <summary>Maximum nesting depth of nested maps inlined into one compiled delegate.</summary>
    internal const int MaxInlineDepth = 4;

    /// <summary>Upper bound on nested maps inlined into one root delegate, bounding expression-tree growth for wide graphs.</summary>
    internal const int MaxInlinedMapsPerRoot = 32;

    private static readonly MethodInfo _engineConvertValueMethod =
        typeof(MappingEngine).GetMethod(
            nameof(MappingEngine.ConvertValue),
            BindingFlags.NonPublic | BindingFlags.Instance,
            null,
            [typeof(object), typeof(Type), typeof(ResolutionContext), typeof(string), typeof(Type)],
            null)
        ?? throw new InvalidOperationException("MappingEngine.ConvertValue(object, Type, ResolutionContext, string, Type) not found.");

    private static readonly MethodInfo _engineConvertAssignableValueMethod =
        typeof(MappingEngine).GetMethod(nameof(MappingEngine.ConvertAssignableValue), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo _contextIncrementDepthMethod =
        typeof(ResolutionContext).GetMethod(nameof(ResolutionContext.IncrementDepth))!;

    private static readonly MethodInfo _contextAtRelativeDepthMethod =
        typeof(ResolutionContext).GetMethod(nameof(ResolutionContext.AtRelativeDepth), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo _engineIsDepthExceededMethod =
        typeof(MappingEngine).GetMethod(nameof(MappingEngine.IsDepthExceeded), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo _objectGetTypeMethod =
        typeof(object).GetMethod(nameof(GetType))!;

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
    /// <param name="hasGeneratedMappings">Whether generated mapping resolvers are registered.</param>
    /// <param name="findTypeMap">
    /// Exact-pair lookup in the finished set of type maps. When supplied, nested maps that are themselves
    /// eligible are inlined (see remarks); when <c>null</c> every nested map keeps the engine's generic dispatch.
    /// </param>
    /// <remarks>
    /// <para>
    /// A nested pair <c>(S, D)</c> is inlined only when every condition the engine's generic dispatch
    /// (<see cref="MappingEngine.ConvertValue"/>) would evaluate before reaching an exact-pair type map holds at
    /// compile time: both types are non-collection, non-string reference types, <c>(S, D)</c> has an exact type
    /// map that passes the same eligibility test as a root map (so no resolvers, actions, converters,
    /// <c>PreserveReferences</c>, polymorphism or constructor mapping), and the pair is not already being
    /// inlined (cycles keep engine dispatch). A non-sealed <c>S</c> additionally guards on the runtime type
    /// being exactly <c>S</c> because the engine looks nested maps up by runtime type; any other value takes
    /// the generic path unchanged. Each inlined map keeps its own depth check
    /// (<see cref="MappingEngine.IsDepthExceeded"/>) at its relative depth, and its conversions use a context
    /// created at the depth the engine's chain of <see cref="ResolutionContext.IncrementDepth"/> calls would
    /// have produced, so per-map and inherited limits, items, state and services behave identically.
    /// </para>
    /// </remarks>
    public static Func<object, MappingEngine, ResolutionContext, object>? TryCompile(
        TypeMap typeMap,
        bool allowNullDestinationValues,
        bool hasValueTransformers,
        bool hasGeneratedMappings = false,
        Func<Type, Type, TypeMap?>? findTypeMap = null)
    {
        if (findTypeMap is not null)
        {
            try
            {
                var inlined = TryBuildExpression(typeMap, allowNullDestinationValues, hasValueTransformers, hasGeneratedMappings, findTypeMap)?.Compile();
                if (inlined is not null)
                    return inlined;
            }
            catch
            {
                // An unanticipated inlining failure must not cost the root its (non-inlined) fast path.
            }
        }

        try
        {
            return TryBuildExpression(typeMap, allowNullDestinationValues, hasValueTransformers, hasGeneratedMappings, findTypeMap: null)?.Compile();
        }
        catch
        {
            // If Expression.Lambda rejects something we failed to anticipate,
            // the interpreter is a safe fallback — correctness over speed.
            return null;
        }
    }

    /// <summary>
    /// Builds, without compiling, the expression tree <see cref="TryCompile"/> would compile.
    /// Also the seam tests use to inspect which nested maps were inlined.
    /// </summary>
    internal static Expression<Func<object, MappingEngine, ResolutionContext, object>>? TryBuildExpression(
        TypeMap typeMap,
        bool allowNullDestinationValues,
        bool hasValueTransformers,
        bool hasGeneratedMappings = false,
        Func<Type, Type, TypeMap?>? findTypeMap = null)
    {
        if (!IsSimpleTypeMap(typeMap, allowNullDestinationValues, hasValueTransformers))
            return null;

        var srcParam = Expression.Parameter(typeof(object), "srcObj");
        var engineParam = Expression.Parameter(typeof(MappingEngine), "engine");
        var ctxParam = Expression.Parameter(typeof(ResolutionContext), "ctx");
        var srcTyped = Expression.Variable(typeMap.SourceType, "src");

        var state = new BuildState(
            findTypeMap, allowNullDestinationValues, hasValueTransformers, hasGeneratedMappings, typeMap, engineParam, ctxParam);
        var mapped = BuildMapBody(typeMap, srcTyped, level: 0, state);
        if (mapped is null)
            return null;

        var block = Expression.Block(
            typeof(object),
            [srcTyped],
            Expression.Assign(srcTyped, Expression.Convert(srcParam, typeMap.SourceType)),
            Expression.Convert(mapped, typeof(object)));

        try
        {
            return Expression.Lambda<Func<object, MappingEngine, ResolutionContext, object>>(
                block, srcParam, engineParam, ctxParam);
        }
        catch
        {
            // Same defensive fallback as compilation: the interpreter is always correct.
            return null;
        }
    }

    /// <summary>Per-root build state: configuration flags, the shared engine/context parameters and the inlining bookkeeping.</summary>
    private sealed class BuildState(
        Func<Type, Type, TypeMap?>? findTypeMap,
        bool allowNullDestinationValues,
        bool hasValueTransformers,
        bool hasGeneratedMappings,
        TypeMap root,
        ParameterExpression engine,
        ParameterExpression ctx)
    {
        public Func<Type, Type, TypeMap?>? FindTypeMap { get; } = findTypeMap;
        public bool AllowNullDestinationValues { get; } = allowNullDestinationValues;
        public bool HasValueTransformers { get; } = hasValueTransformers;
        public bool HasGeneratedMappings { get; } = hasGeneratedMappings;
        public ParameterExpression Engine { get; } = engine;
        public ParameterExpression Context { get; } = ctx;

        /// <summary>Maps currently being emitted, root first; used to keep cycles on the engine's dispatch.</summary>
        public List<TypeMap> Stack { get; } = [root];

        public int InlinedMaps { get; set; }
    }

    /// <summary>
    /// Emits <c>new TDest()</c> plus every member assignment for <paramref name="map"/>, typed as its
    /// destination. <paramref name="level"/> is how many inlined maps deep this body sits (0 for the root).
    /// </summary>
    private static Expression? BuildMapBody(TypeMap map, ParameterExpression srcTyped, int level, BuildState state)
    {
        var parameterlessCtor = map.DestinationType.GetConstructor(
            BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (parameterlessCtor is null)
            return null;

        var dst = Expression.Variable(map.DestinationType, "dst");
        var body = new List<Expression> { Expression.Assign(dst, Expression.New(parameterlessCtor)) };

        foreach (var pm in map.PropertyMaps)
        {
            if (pm.Ignored) continue;

            var assign = TryBuildAssignment(pm, srcTyped, dst, level, state);
            if (assign is null) return null;  // unreachable given IsSimpleTypeMap, but defensive
            body.Add(assign);
        }

        body.Add(dst);
        return Expression.Block(map.DestinationType, [dst], body);
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
        int level,
        BuildState state)
    {
        var sourceExpr = BuildSourceExpression(pm, srcTyped);
        if (sourceExpr is null) return null;

        var destProp = pm.DestinationProperty;
        var destType = destProp.PropertyType;
        var destAccess = Expression.Property(dst, destProp);

        Expression valueExpr;

        if (destType.IsAssignableFrom(sourceExpr.Type))
        {
            // With registered generated mappings, the shared dispatcher checks
            // explicit/generated pairs before preserving an unregistered alias.
            // Pass the source once; the dispatcher handles null without a second getter read.
            valueExpr = state.HasGeneratedMappings
                ? Expression.Convert(Expression.Call(state.Engine, _engineConvertAssignableValueMethod,
                    Expression.Convert(sourceExpr, typeof(object)), Expression.Constant(sourceExpr.Type, typeof(Type)),
                    Expression.Constant(destType, typeof(Type)),
                    ChildContext(level, state)), destType)
                : sourceExpr;
        }
        else
        {
            // Route non-assignable values and explicitly opted-in generated
            // pairs through the interpreter's one conversion routine, or inline
            // an eligible nested map. The member is read exactly once, like the
            // interpreter's CompiledGetter, and the single value feeds both the
            // null check and the conversion.
            var member = Expression.Variable(sourceExpr.Type, "member");
            var converted = TryInlineNestedMap(member, destType, destProp.Name, level, state)
                ?? BuildEngineConversion(member, destType, destProp.Name, level, state);

            Expression conditional;
            if (!sourceExpr.Type.IsValueType || Nullable.GetUnderlyingType(sourceExpr.Type) is not null)
            {
                // Reference and nullable-value sources can produce a null boxed
                // value. Match the interpreter by assigning the destination's
                // default without asking the conversion routine to inspect null.
                var nullConst = Expression.Constant(null, sourceExpr.Type);
                var isNull = Expression.Equal(member, nullConst);
                var defaultDest = destType.IsValueType
                    ? (Expression)Expression.Default(destType)
                    : Expression.Constant(null, destType);

                conditional = Expression.Condition(isNull, defaultDest, converted);
            }
            else
            {
                conditional = converted;
            }

            valueExpr = Expression.Block(conditional.Type, [member], Expression.Assign(member, sourceExpr), conditional);
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

    /// <summary>
    /// The context the interpreter hands to member conversions of a map <paramref name="level"/> inlined
    /// levels below the root: one level deeper than that map's own context. The root keeps the public
    /// <see cref="ResolutionContext.IncrementDepth"/> call; deeper levels create the final context directly.
    /// </summary>
    private static Expression ChildContext(int level, BuildState state) => level == 0
        ? Expression.Call(state.Context, _contextIncrementDepthMethod)
        : Expression.Call(state.Context, _contextAtRelativeDepthMethod, Expression.Constant(level + 1));

    /// <summary>The engine's generic conversion for a non-null <paramref name="member"/>, typed as the destination.</summary>
    private static Expression BuildEngineConversion(
        ParameterExpression member, Type destType, string destinationMember, int level, BuildState state)
    {
        var conversionCall = Expression.Call(
            state.Engine,
            _engineConvertValueMethod,
            Expression.Convert(member, typeof(object)),
            Expression.Constant(destType, typeof(Type)),
            ChildContext(level, state),
            Expression.Constant(destinationMember, typeof(string)),
            Expression.Constant(member.Type, typeof(Type)));

        return destType == typeof(object)
            ? conversionCall
            : Expression.Convert(conversionCall, destType);
    }

    /// <summary>
    /// Inlines the exact-pair type map for <paramref name="member"/> -> <paramref name="destType"/> when it is
    /// provably what <see cref="MappingEngine.ConvertValue"/> would run; returns <c>null</c> to keep the generic dispatch.
    /// </summary>
    private static Expression? TryInlineNestedMap(
        ParameterExpression member, Type destType, string destinationMember, int level, BuildState state)
    {
        var sourceType = member.Type;
        if (state.FindTypeMap is null
            || level >= MaxInlineDepth
            || state.InlinedMaps >= MaxInlinedMapsPerRoot)
            return null;

        // ConvertValue only reaches an exact-pair type map for non-string, non-collection reference
        // pairs (assignability and enum handling come first). Anything else keeps its dispatch.
        if (!sourceType.IsClass || !destType.IsClass
            || sourceType == typeof(string) || destType == typeof(string)
            || MappingEngine.IsCollectionMapping(sourceType, destType, out _, out _))
            return null;

        var nested = state.FindTypeMap(sourceType, destType);
        if (nested is null
            || nested.SourceType != sourceType
            || nested.DestinationType != destType
            || state.Stack.Contains(nested)
            || !IsSimpleTypeMap(nested, state.AllowNullDestinationValues, state.HasValueTransformers))
            return null;

        state.Stack.Add(nested);
        state.InlinedMaps++;
        var body = BuildMapBody(nested, member, level + 1, state);
        state.Stack.RemoveAt(state.Stack.Count - 1);
        if (body is null)
            return null;

        // The nested map is one level below this map: its own depth boundary (per-map override and inherited
        // limits included) is evaluated exactly where the engine would, before any member is touched.
        var depthExceeded = Expression.Call(
            state.Engine,
            _engineIsDepthExceededMethod,
            state.Context,
            Expression.Constant(level + 1),
            Expression.Constant(nested.MaxDepth, typeof(int?)));
        Expression mapped = Expression.Condition(depthExceeded, Expression.Constant(null, destType), body);

        if (sourceType.IsSealed)
            return mapped;

        // The engine resolves the nested map by the value's runtime type; only a value of exactly the
        // declared type is known to resolve to the inlined map.
        var exactType = Expression.Equal(
            Expression.Call(member, _objectGetTypeMethod),
            Expression.Constant(sourceType, typeof(Type)));
        return Expression.Condition(exactType, mapped, BuildEngineConversion(member, destType, destinationMember, level, state));
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
