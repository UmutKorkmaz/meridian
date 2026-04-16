using System.Collections;
using System.Linq.Expressions;

namespace Meridian.Mapping.Configuration;

/// <summary>
/// Non-generic interface that <see cref="MappingExpression{TSource, TDestination}"/> implements
/// to expose its configuration to <see cref="MapperConfiguration.CompileTypeMap"/> without reflection.
/// This eliminates all BindingFlags.NonPublic | BindingFlags.Instance GetProperty/GetValue calls
/// during type map compilation, providing significantly faster startup performance.
/// </summary>
internal interface ICompiledMappingExpression
{
    Delegate? GetCustomConverter();
    Type? GetTypeConverterType();
    object? GetTypeConverterInstance();
    Delegate? GetConstructUsingFunc();
    int? GetMaxDepthValue();
    MemberList GetValidationMemberList();
    IList GetIncludedBases();
    IList GetIncludedDerived();
    bool GetIncludeAllDerivedEnabled();
    bool GetHasForAllMembers();
    Delegate? GetForAllMembersAction();
    bool GetHasForAllOtherMembers();
    Delegate? GetForAllOtherMembersAction();
    IList GetBeforeMapActions();
    IList GetAfterMapActions();
    IList GetBeforeMapActionTypes();
    IList GetAfterMapActionTypes();
    bool GetPreserveReferencesEnabled();
    IList GetForPathConfigs();
    IList GetIncludedMemberExpressions();
    IDictionary GetMemberConfigs();
    IDictionary GetSourceMemberConfigs();
    IDictionary GetCtorParamConfigs();
}

/// <summary>
/// Non-generic interface for member configuration expressions, eliminating reflection
/// in <see cref="MapperConfiguration.ApplyMemberConfig"/>.
/// </summary>
internal interface ICompiledMemberConfig
{
    bool GetIsIgnored();
    bool GetHasConstantValue();
    object? GetConstantValue();
    Delegate? GetMemberConverterFunc();
    object? GetMemberConverterInstance();
    LambdaExpression? GetMemberConverterSourceExpression();
    LambdaExpression? GetMapFromExpression();
    Delegate? GetMapFromFunc();
    Type? GetValueResolverType();
    Type? GetMemberValueResolverType();
    LambdaExpression? GetMemberValueResolverSourceExpression();
    string? GetMapFromSourceName();
    Delegate? GetConditionFunc();
    Delegate? GetCondition3ArgFunc();
    Delegate? GetPreConditionFunc();
    bool GetHasNullSubstitute();
    object? GetNullSubstituteValue();
    bool GetExplicitExpansion();
    bool? GetUseDestinationValue();
    bool? GetAllowNull();
}

/// <summary>
/// Non-generic interface for constructor parameter configuration expressions.
/// </summary>
internal interface ICompiledCtorParamConfig
{
    LambdaExpression? GetMapFromExpression();
}
