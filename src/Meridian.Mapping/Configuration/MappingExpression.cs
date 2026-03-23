using System.Linq.Expressions;
using System.Reflection;
using Meridian.Mapping.Converters;

namespace Meridian.Mapping.Configuration;

/// <summary>
/// Implementation of the fluent mapping expression API. Collects all configuration
/// for a single source-to-destination type pair, which is later compiled into a
/// <see cref="Execution.TypeMap"/>.
/// </summary>
/// <typeparam name="TSource">The source type.</typeparam>
/// <typeparam name="TDestination">The destination type.</typeparam>
public class MappingExpression<TSource, TDestination> : IMappingExpression<TSource, TDestination>, ICompiledMappingExpression
{
    private readonly Action<Type, Type, object>? _reverseMapRegistrar;

    internal Dictionary<string, MemberConfigurationExpression<TSource, TDestination>> MemberConfigs { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, CtorParamConfigurationExpression<TSource>> CtorParamConfigs { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Func<TSource, TDestination>? CustomConverter { get; private set; }
    internal Type? TypeConverterType { get; private set; }
    internal ITypeConverter<TSource, TDestination>? TypeConverterInstance { get; private set; }
    internal Func<TSource, TDestination>? ConstructUsingFunc { get; private set; }
    internal int? MaxDepthValue { get; private set; }
    internal MemberList ValidationMemberList { get; private set; } = MemberList.Destination;
    internal List<(Type BaseSrcType, Type BaseDestType)> IncludedBases { get; } = new();
    internal List<(Type DerivedSrcType, Type DerivedDestType)> IncludedDerived { get; } = new();
    internal bool IncludeAllDerivedEnabled { get; private set; }
    internal bool HasForAllMembers { get; private set; }
    internal Action<IMemberConfigurationExpression<TSource, TDestination>>? ForAllMembersAction { get; private set; }
    internal bool HasForAllOtherMembers { get; private set; }
    internal Action<IMemberConfigurationExpression<TSource, TDestination>>? ForAllOtherMembersAction { get; private set; }
    internal List<Action<TSource, TDestination>> BeforeMapActions { get; } = new();
    internal List<Action<TSource, TDestination>> AfterMapActions { get; } = new();
    internal bool PreserveReferencesEnabled { get; private set; }
    internal List<ForPathConfig<TSource, TDestination>> ForPathConfigs { get; } = new();
    internal List<LambdaExpression> IncludedMemberExpressions { get; } = new();

    /// <summary>
    /// Initializes a new instance of <see cref="MappingExpression{TSource, TDestination}"/>.
    /// </summary>
    /// <param name="reverseMapRegistrar">Optional callback to register the reverse map with the configuration.</param>
    public MappingExpression(Action<Type, Type, object>? reverseMapRegistrar = null)
    {
        _reverseMapRegistrar = reverseMapRegistrar;
    }

    /// <inheritdoc />
    public IMappingExpression<TSource, TDestination> ForMember(
        Expression<Func<TDestination, object?>> destinationMember,
        Action<IMemberConfigurationExpression<TSource, TDestination>> memberOptions)
    {
        ArgumentNullException.ThrowIfNull(destinationMember);
        ArgumentNullException.ThrowIfNull(memberOptions);

        var memberName = GetMemberName(destinationMember);
        var config = new MemberConfigurationExpression<TSource, TDestination>();
        memberOptions(config);
        MemberConfigs[memberName] = config;
        return this;
    }

    /// <inheritdoc />
    public IMappingExpression<TSource, TDestination> ForMember(
        string destinationMemberName,
        Action<IMemberConfigurationExpression<TSource, TDestination>> memberOptions)
    {
        ArgumentException.ThrowIfNullOrEmpty(destinationMemberName);
        ArgumentNullException.ThrowIfNull(memberOptions);

        // Validate the destination property exists
        var destProp = typeof(TDestination).GetProperty(destinationMemberName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (destProp == null)
        {
            throw new ArgumentException(
                $"Property '{destinationMemberName}' does not exist on type '{typeof(TDestination).Name}'.",
                nameof(destinationMemberName));
        }

        var config = new MemberConfigurationExpression<TSource, TDestination>();
        memberOptions(config);
        MemberConfigs[destProp.Name] = config;
        return this;
    }

    /// <inheritdoc />
    public IMappingExpression<TDestination, TSource> ReverseMap()
    {
        var reverseExpression = new MappingExpression<TDestination, TSource>();

        // Copy simple MapFrom expressions as reverse mappings where possible
        foreach (var (memberName, config) in MemberConfigs)
        {
            if (config.MapFromExpression != null && !config.IsIgnored)
            {
                // Try to extract the source member name from the expression
                var sourceMemberName = TryGetMemberNameFromExpression(config.MapFromExpression);
                if (sourceMemberName != null)
                {
                    // In reverse map: the original source member name becomes the destination member
                    // and the original destination member name becomes the source
                    var sourceType = typeof(TSource);
                    var sourceProp = sourceType.GetProperty(sourceMemberName, BindingFlags.Public | BindingFlags.Instance);
                    if (sourceProp != null)
                    {
                        var reverseConfig = new MemberConfigurationExpression<TDestination, TSource>();

                        // Build the reverse expression: dest => dest.OriginalDestMember maps to src.memberName
                        var destProp = typeof(TDestination).GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance);
                        if (destProp != null)
                        {
                            var param = Expression.Parameter(typeof(TDestination), "src");
                            var access = Expression.Property(param, destProp);
                            var converted = Expression.Convert(access, typeof(object));
                            var lambda = Expression.Lambda(converted, param);
                            reverseConfig.MapFrom<object>(
                                Expression.Lambda<Func<TDestination, object>>(converted, param));
                        }

                        reverseExpression.MemberConfigs[sourceMemberName] = reverseConfig;
                    }
                }
            }
        }

        _reverseMapRegistrar?.Invoke(typeof(TDestination), typeof(TSource), reverseExpression);
        return reverseExpression;
    }

    /// <inheritdoc />
    public IMappingExpression<TSource, TDestination> ConvertUsing(Func<TSource, TDestination> converter)
    {
        CustomConverter = converter ?? throw new ArgumentNullException(nameof(converter));
        return this;
    }

    /// <inheritdoc />
    public IMappingExpression<TSource, TDestination> ConvertUsing<TConverter>() where TConverter : ITypeConverter<TSource, TDestination>
    {
        TypeConverterType = typeof(TConverter);
        return this;
    }

    /// <inheritdoc />
    public IMappingExpression<TSource, TDestination> ConvertUsing(ITypeConverter<TSource, TDestination> converter)
    {
        TypeConverterInstance = converter ?? throw new ArgumentNullException(nameof(converter));
        return this;
    }

    /// <inheritdoc />
    public IMappingExpression<TSource, TDestination> ConstructUsing(Func<TSource, TDestination> ctor)
    {
        ConstructUsingFunc = ctor ?? throw new ArgumentNullException(nameof(ctor));
        return this;
    }

    /// <inheritdoc />
    public IMappingExpression<TSource, TDestination> ForCtorParam(
        string ctorParamName,
        Action<ICtorParamConfigurationExpression<TSource>> configAction)
    {
        ArgumentException.ThrowIfNullOrEmpty(ctorParamName);
        ArgumentNullException.ThrowIfNull(configAction);

        var config = new CtorParamConfigurationExpression<TSource>();
        configAction(config);
        CtorParamConfigs[ctorParamName] = config;
        return this;
    }

    /// <inheritdoc />
    public IMappingExpression<TSource, TDestination> ForAllMembers(
        Action<IMemberConfigurationExpression<TSource, TDestination>> memberOptions)
    {
        HasForAllMembers = true;
        ForAllMembersAction = memberOptions;
        return this;
    }

    /// <inheritdoc />
    public IMappingExpression<TSource, TDestination> ForAllOtherMembers(
        Action<IMemberConfigurationExpression<TSource, TDestination>> memberOptions)
    {
        HasForAllOtherMembers = true;
        ForAllOtherMembersAction = memberOptions;
        return this;
    }

    /// <inheritdoc />
    public IMappingExpression<TSource, TDestination> MaxDepth(int depth)
    {
        if (depth <= 0) throw new ArgumentOutOfRangeException(nameof(depth), "Max depth must be greater than 0.");
        MaxDepthValue = depth;
        return this;
    }

    /// <inheritdoc />
    public IMappingExpression<TSource, TDestination> IncludeBase<TBaseSrc, TBaseDest>()
    {
        IncludedBases.Add((typeof(TBaseSrc), typeof(TBaseDest)));
        return this;
    }

    /// <inheritdoc />
    public IMappingExpression<TSource, TDestination> Include<TDerivedSrc, TDerivedDest>()
        where TDerivedSrc : TSource
        where TDerivedDest : TDestination
    {
        IncludedDerived.Add((typeof(TDerivedSrc), typeof(TDerivedDest)));
        return this;
    }

    /// <inheritdoc />
    public IMappingExpression<TSource, TDestination> IncludeAllDerived()
    {
        IncludeAllDerivedEnabled = true;
        return this;
    }

    /// <inheritdoc />
    public IMappingExpression<TSource, TDestination> ForPath<TMember>(
        Expression<Func<TDestination, TMember>> destinationPath,
        Action<IMemberConfigurationExpression<TSource, TDestination>> memberOptions)
    {
        ArgumentNullException.ThrowIfNull(destinationPath);
        ArgumentNullException.ThrowIfNull(memberOptions);

        var config = new MemberConfigurationExpression<TSource, TDestination>();
        memberOptions(config);

        var pathMembers = GetMemberChain(destinationPath);
        if (pathMembers.Count < 2)
        {
            throw new ArgumentException(
                "ForPath requires a nested member access expression (e.g., d => d.Address.Street).",
                nameof(destinationPath));
        }

        ForPathConfigs.Add(new ForPathConfig<TSource, TDestination>
        {
            DestinationPropertyChain = pathMembers.ToArray(),
            MemberConfig = config
        });

        return this;
    }

    /// <inheritdoc />
    public IMappingExpression<TSource, TDestination> IncludeMembers(
        params Expression<Func<TSource, object>>[] memberExpressions)
    {
        ArgumentNullException.ThrowIfNull(memberExpressions);

        foreach (var expr in memberExpressions)
        {
            IncludedMemberExpressions.Add(expr);
        }

        return this;
    }

    /// <inheritdoc />
    public IMappingExpression<TSource, TDestination> ValidateMemberList(MemberList memberList)
    {
        ValidationMemberList = memberList;
        return this;
    }

    /// <inheritdoc />
    public IMappingExpression<TSource, TDestination> BeforeMap(Action<TSource, TDestination> beforeFunction)
    {
        ArgumentNullException.ThrowIfNull(beforeFunction);
        BeforeMapActions.Add(beforeFunction);
        return this;
    }

    /// <inheritdoc />
    public IMappingExpression<TSource, TDestination> AfterMap(Action<TSource, TDestination> afterFunction)
    {
        ArgumentNullException.ThrowIfNull(afterFunction);
        AfterMapActions.Add(afterFunction);
        return this;
    }

    /// <inheritdoc />
    public IMappingExpression<TSource, TDestination> PreserveReferences()
    {
        PreserveReferencesEnabled = true;
        return this;
    }

    private static string GetMemberName(Expression<Func<TDestination, object?>> expression)
    {
        var body = expression.Body;

        // Unwrap Convert/ConvertChecked (boxing for value types)
        if (body is UnaryExpression unary && (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
        {
            body = unary.Operand;
        }

        if (body is MemberExpression member)
        {
            return member.Member.Name;
        }

        throw new ArgumentException($"Expression '{expression}' does not refer to a property or field.", nameof(expression));
    }

    internal static string? TryGetMemberNameFromExpression(LambdaExpression expression)
    {
        var body = expression.Body;

        if (body is UnaryExpression unary && (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
        {
            body = unary.Operand;
        }

        if (body is MemberExpression member && member.Expression is ParameterExpression)
        {
            return member.Member.Name;
        }

        return null;
    }

    /// <summary>
    /// Extracts a chain of member accesses from a nested expression like d => d.Address.Street.
    /// </summary>
    private static List<MemberInfo> GetMemberChain<TMember>(Expression<Func<TDestination, TMember>> expression)
    {
        var chain = new List<MemberInfo>();
        var body = expression.Body;

        // Unwrap Convert
        if (body is UnaryExpression unary && (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
        {
            body = unary.Operand;
        }

        while (body is MemberExpression memberExpr)
        {
            chain.Insert(0, memberExpr.Member);
            body = memberExpr.Expression!;
        }

        if (body is not ParameterExpression)
        {
            throw new ArgumentException($"Expression '{expression}' is not a valid member access chain.", nameof(expression));
        }

        return chain;
    }

    // --- ICompiledMappingExpression implementation ---
    // These methods expose internal state without reflection, enabling
    // MapperConfiguration.CompileTypeMap to read config ~100x faster.

    Delegate? ICompiledMappingExpression.GetCustomConverter() => CustomConverter;
    Type? ICompiledMappingExpression.GetTypeConverterType() => TypeConverterType;
    object? ICompiledMappingExpression.GetTypeConverterInstance() => TypeConverterInstance;
    Delegate? ICompiledMappingExpression.GetConstructUsingFunc() => ConstructUsingFunc;
    int? ICompiledMappingExpression.GetMaxDepthValue() => MaxDepthValue;
    MemberList ICompiledMappingExpression.GetValidationMemberList() => ValidationMemberList;
    System.Collections.IList ICompiledMappingExpression.GetIncludedBases() => IncludedBases;
    System.Collections.IList ICompiledMappingExpression.GetIncludedDerived() => IncludedDerived;
    bool ICompiledMappingExpression.GetIncludeAllDerivedEnabled() => IncludeAllDerivedEnabled;
    bool ICompiledMappingExpression.GetHasForAllMembers() => HasForAllMembers;
    Delegate? ICompiledMappingExpression.GetForAllMembersAction() => ForAllMembersAction;
    bool ICompiledMappingExpression.GetHasForAllOtherMembers() => HasForAllOtherMembers;
    Delegate? ICompiledMappingExpression.GetForAllOtherMembersAction() => ForAllOtherMembersAction;
    System.Collections.IList ICompiledMappingExpression.GetBeforeMapActions() => BeforeMapActions;
    System.Collections.IList ICompiledMappingExpression.GetAfterMapActions() => AfterMapActions;
    bool ICompiledMappingExpression.GetPreserveReferencesEnabled() => PreserveReferencesEnabled;
    System.Collections.IList ICompiledMappingExpression.GetForPathConfigs() => ForPathConfigs;
    System.Collections.IList ICompiledMappingExpression.GetIncludedMemberExpressions() => IncludedMemberExpressions;
    System.Collections.IDictionary ICompiledMappingExpression.GetMemberConfigs() => MemberConfigs;
    System.Collections.IDictionary ICompiledMappingExpression.GetCtorParamConfigs() => CtorParamConfigs;
}

/// <summary>
/// Configuration for a ForPath mapping (nested destination property chain).
/// </summary>
/// <typeparam name="TSource">The source type.</typeparam>
/// <typeparam name="TDestination">The destination type.</typeparam>
public class ForPathConfig<TSource, TDestination>
{
    /// <summary>
    /// Gets or sets the chain of member accesses (e.g., [Address, Street] for d.Address.Street).
    /// </summary>
    public required MemberInfo[] DestinationPropertyChain { get; set; }

    /// <summary>
    /// Gets or sets the member configuration for this path.
    /// </summary>
    public required MemberConfigurationExpression<TSource, TDestination> MemberConfig { get; set; }
}
