using System.Linq.Expressions;
using Meridian.Mapping.Converters;

namespace Meridian.Mapping.Configuration;

/// <summary>
/// Implementation of per-member configuration. Stores the mapping strategy
/// (expression, ignore, condition, etc.) chosen by the user for a single
/// destination member.
/// </summary>
/// <typeparam name="TSource">The source type.</typeparam>
/// <typeparam name="TDestination">The destination type.</typeparam>
public class MemberConfigurationExpression<TSource, TDestination> : IMemberConfigurationExpression<TSource, TDestination>, ICompiledMemberConfig
{
    internal LambdaExpression? MapFromExpression { get; private set; }
    internal Delegate? MapFromFunc { get; private set; }
    internal Type? ValueResolverType { get; private set; }
    internal Type? MemberValueResolverType { get; private set; }
    internal LambdaExpression? MemberValueResolverSourceExpression { get; private set; }
    internal string? MapFromSourceName { get; private set; }
    internal bool IsIgnored { get; private set; }
    internal Func<TSource, bool>? ConditionFunc { get; private set; }
    internal Delegate? Condition3ArgFunc { get; private set; }
    internal Func<TSource, bool>? PreConditionFunc { get; private set; }
    internal object? NullSubstituteValue { get; private set; }
    internal bool HasNullSubstitute { get; private set; }
    internal object? ConstantValue { get; private set; }
    internal bool HasConstantValue { get; private set; }
    internal Delegate? MemberConverterFunc { get; private set; }
    internal LambdaExpression? MemberConverterSourceExpression { get; private set; }
    internal object? MemberConverterInstance { get; private set; }
    internal bool ExplicitExpansionEnabled { get; private set; }
    internal bool? UseDestinationValueSetting { get; private set; }
    internal bool? AllowNullSetting { get; private set; }

    /// <inheritdoc />
    public void MapFrom<TResult>(Expression<Func<TSource, TResult>> mapExpression)
    {
        MapFromExpression = mapExpression ?? throw new ArgumentNullException(nameof(mapExpression));
    }

    /// <inheritdoc />
    public void MapFrom<TResult>(Func<TSource, TDestination, TResult> mapFunction)
    {
        MapFromFunc = mapFunction ?? throw new ArgumentNullException(nameof(mapFunction));
    }

    /// <inheritdoc />
    public void MapFrom<TValueResolver>() where TValueResolver : class
    {
        ValueResolverType = typeof(TValueResolver);
    }

    /// <inheritdoc />
    public void MapFrom(string sourcePropertyName)
    {
        MapFromSourceName = sourcePropertyName ?? throw new ArgumentNullException(nameof(sourcePropertyName));
    }

    /// <inheritdoc />
    public void Ignore()
    {
        IsIgnored = true;
    }

    /// <inheritdoc />
    public void Condition(Func<TSource, bool> condition)
    {
        ConditionFunc = condition ?? throw new ArgumentNullException(nameof(condition));
    }

    /// <inheritdoc />
    public void Condition(Func<TSource, TDestination, object?, bool> condition)
    {
        Condition3ArgFunc = condition ?? throw new ArgumentNullException(nameof(condition));
    }

    /// <inheritdoc />
    public void PreCondition(Func<TSource, bool> condition)
    {
        PreConditionFunc = condition ?? throw new ArgumentNullException(nameof(condition));
    }

    /// <inheritdoc />
    public void NullSubstitute(object substitution)
    {
        NullSubstituteValue = substitution;
        HasNullSubstitute = true;
    }

    /// <inheritdoc />
    public void ExplicitExpansion()
    {
        ExplicitExpansionEnabled = true;
    }

    /// <inheritdoc />
    public void UseDestinationValue()
    {
        UseDestinationValueSetting = true;
    }

    /// <inheritdoc />
    public void DoNotUseDestinationValue()
    {
        UseDestinationValueSetting = false;
    }

    /// <inheritdoc />
    public void AllowNull()
    {
        AllowNullSetting = true;
    }

    /// <inheritdoc />
    public void DoNotAllowNull()
    {
        AllowNullSetting = false;
    }

    /// <inheritdoc />
    public void UseValue(object value)
    {
        ConstantValue = value;
        HasConstantValue = true;
    }

    /// <inheritdoc />
    public void ConvertUsing<TSourceMember, TDestMember>(
        IValueConverter<TSourceMember, TDestMember> converter,
        Expression<Func<TSource, TSourceMember>> sourceMember)
    {
        ArgumentNullException.ThrowIfNull(converter);
        ArgumentNullException.ThrowIfNull(sourceMember);
        MemberConverterInstance = converter;
        MemberConverterSourceExpression = sourceMember;
    }

    /// <inheritdoc />
    public void ConvertUsing<TSourceMember, TDestMember>(
        Func<TSourceMember, TDestMember> converter,
        Expression<Func<TSource, TSourceMember>> sourceMember)
    {
        ArgumentNullException.ThrowIfNull(converter);
        ArgumentNullException.ThrowIfNull(sourceMember);
        MemberConverterFunc = converter;
        MemberConverterSourceExpression = sourceMember;
    }

    /// <inheritdoc />
    public void MapFrom<TValueResolver, TSourceMember>(
        Expression<Func<TSource, TSourceMember>> sourceMember) where TValueResolver : class
    {
        ArgumentNullException.ThrowIfNull(sourceMember);
        MemberValueResolverType = typeof(TValueResolver);
        MemberValueResolverSourceExpression = sourceMember;
    }

    // --- ICompiledMemberConfig implementation ---
    bool ICompiledMemberConfig.GetIsIgnored() => IsIgnored;
    bool ICompiledMemberConfig.GetHasConstantValue() => HasConstantValue;
    object? ICompiledMemberConfig.GetConstantValue() => ConstantValue;
    Delegate? ICompiledMemberConfig.GetMemberConverterFunc() => MemberConverterFunc;
    object? ICompiledMemberConfig.GetMemberConverterInstance() => MemberConverterInstance;
    LambdaExpression? ICompiledMemberConfig.GetMemberConverterSourceExpression() => MemberConverterSourceExpression;
    LambdaExpression? ICompiledMemberConfig.GetMapFromExpression() => MapFromExpression;
    Delegate? ICompiledMemberConfig.GetMapFromFunc() => MapFromFunc;
    Type? ICompiledMemberConfig.GetValueResolverType() => ValueResolverType;
    Type? ICompiledMemberConfig.GetMemberValueResolverType() => MemberValueResolverType;
    LambdaExpression? ICompiledMemberConfig.GetMemberValueResolverSourceExpression() => MemberValueResolverSourceExpression;
    string? ICompiledMemberConfig.GetMapFromSourceName() => MapFromSourceName;
    Delegate? ICompiledMemberConfig.GetConditionFunc() => ConditionFunc;
    Delegate? ICompiledMemberConfig.GetCondition3ArgFunc() => Condition3ArgFunc;
    Delegate? ICompiledMemberConfig.GetPreConditionFunc() => PreConditionFunc;
    bool ICompiledMemberConfig.GetHasNullSubstitute() => HasNullSubstitute;
    object? ICompiledMemberConfig.GetNullSubstituteValue() => NullSubstituteValue;
    bool ICompiledMemberConfig.GetExplicitExpansion() => ExplicitExpansionEnabled;
    bool? ICompiledMemberConfig.GetUseDestinationValue() => UseDestinationValueSetting;
    bool? ICompiledMemberConfig.GetAllowNull() => AllowNullSetting;
}
