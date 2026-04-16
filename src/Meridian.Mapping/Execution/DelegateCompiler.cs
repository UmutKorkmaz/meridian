using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace Meridian.Mapping.Execution;

/// <summary>
/// Builds typed <c>Func&lt;&gt;</c> wrappers around user-provided delegates
/// whose signatures are known structurally but not statically to Meridian
/// (e.g. a <c>Func&lt;TSource, bool&gt;</c> predicate where TSource varies per
/// TypeMap). Wrappers emit a direct call to the delegate's <c>Invoke</c>
/// method via expression trees. They do NOT use
/// <see cref="Delegate.DynamicInvoke"/>.
/// </summary>
/// <remarks>
/// <para>
/// We intentionally use <c>Expression.Call(Expression.Constant(del), "Invoke", ...)</c>
/// rather than <c>Expression.Invoke</c>. The latter fails for closures
/// produced by <c>LambdaExpression.Compile()</c> because compiled lambdas
/// have an invisible leading <c>Closure</c> parameter, which mismatches
/// <c>Expression.Invoke</c>'s parameter binding. Calling <c>Invoke</c>
/// directly on the typed delegate sidesteps that entirely.
/// </para>
/// <para>
/// Each wrapper is cached keyed by the source delegate's reference, so
/// repeated invocations (batch operations that re-enter the mapper) pay
/// the Expression-compile cost exactly once.
/// </para>
/// </remarks>
internal static class DelegateCompiler
{
    private static readonly ConcurrentDictionary<Delegate, Func<object, bool>> _predicates = new();
    private static readonly ConcurrentDictionary<Delegate, Func<object, object?, object?, bool>> _predicates3 = new();
    private static readonly ConcurrentDictionary<Delegate, Func<object, object?>> _funcs1 = new();
    private static readonly ConcurrentDictionary<Delegate, Func<object, object?, object?>> _funcs2 = new();

    /// <summary>
    /// Wraps <c>Func&lt;TSource, bool&gt;</c> as <c>Func&lt;object, bool&gt;</c>.
    /// </summary>
    public static Func<object, bool> WrapPredicate(Delegate del)
    {
        ArgumentNullException.ThrowIfNull(del);
        return _predicates.GetOrAdd(del, static d =>
        {
            var paramType = d.Method.GetParameters()[0].ParameterType;
            var objParam = Expression.Parameter(typeof(object), "src");
            var castSource = Expression.Convert(objParam, paramType);

            var invokeMethod = d.GetType().GetMethod("Invoke")!;
            var call = Expression.Call(Expression.Constant(d), invokeMethod, castSource);

            return Expression.Lambda<Func<object, bool>>(call, objParam).Compile();
        });
    }

    /// <summary>
    /// Wraps <c>Func&lt;T1, T2, T3, bool&gt;</c> as <c>Func&lt;object, object?, object?, bool&gt;</c>.
    /// Used by 3-arg <c>.Condition((src, dest, resolved) =&gt; ...)</c>.
    /// </summary>
    public static Func<object, object?, object?, bool> WrapPredicate3(Delegate del)
    {
        ArgumentNullException.ThrowIfNull(del);
        return _predicates3.GetOrAdd(del, static d =>
        {
            var p = d.Method.GetParameters();
            var a = Expression.Parameter(typeof(object), "a");
            var b = Expression.Parameter(typeof(object), "b");
            var c = Expression.Parameter(typeof(object), "c");
            var castA = Expression.Convert(a, p[0].ParameterType);
            var castB = Expression.Convert(b, p[1].ParameterType);
            var castC = Expression.Convert(c, p[2].ParameterType);

            var invokeMethod = d.GetType().GetMethod("Invoke")!;
            var call = Expression.Call(Expression.Constant(d), invokeMethod, castA, castB, castC);

            return Expression.Lambda<Func<object, object?, object?, bool>>(call, a, b, c).Compile();
        });
    }

    /// <summary>
    /// Wraps <c>Func&lt;T1, TResult&gt;</c> as <c>Func&lt;object, object?&gt;</c>.
    /// Used by member converters, value resolvers, and compiled ctor-param expressions.
    /// </summary>
    public static Func<object, object?> WrapFunc1(Delegate del)
    {
        ArgumentNullException.ThrowIfNull(del);
        return _funcs1.GetOrAdd(del, static d =>
        {
            var p = d.Method.GetParameters();
            // Compiled LambdaExpression delegates have an invisible leading
            // Closure parameter in d.Method.GetParameters(), but the delegate
            // type itself (Func<TSource, TResult>) reports only the user
            // parameters. Use the delegate TYPE's Invoke method to get clean
            // parameter types.
            var invokeMethod = d.GetType().GetMethod("Invoke")!;
            var invokeParams = invokeMethod.GetParameters();

            var a = Expression.Parameter(typeof(object), "a");
            var castA = Expression.Convert(a, invokeParams[0].ParameterType);
            var call = Expression.Call(Expression.Constant(d), invokeMethod, castA);
            var boxed = Expression.Convert(call, typeof(object));

            return Expression.Lambda<Func<object, object?>>(boxed, a).Compile();
        });
    }

    /// <summary>
    /// Wraps <c>Func&lt;T1, T2, TResult&gt;</c> as <c>Func&lt;object, object?, object?&gt;</c>.
    /// Used by 2-arg <c>.MapFrom((src, dest) =&gt; ...)</c>.
    /// </summary>
    public static Func<object, object?, object?> WrapFunc2(Delegate del)
    {
        ArgumentNullException.ThrowIfNull(del);
        return _funcs2.GetOrAdd(del, static d =>
        {
            var invokeMethod = d.GetType().GetMethod("Invoke")!;
            var invokeParams = invokeMethod.GetParameters();

            var a = Expression.Parameter(typeof(object), "a");
            var b = Expression.Parameter(typeof(object), "b");
            var castA = Expression.Convert(a, invokeParams[0].ParameterType);
            var castB = Expression.Convert(b, invokeParams[1].ParameterType);
            var call = Expression.Call(Expression.Constant(d), invokeMethod, castA, castB);
            var boxed = Expression.Convert(call, typeof(object));

            return Expression.Lambda<Func<object, object?, object?>>(boxed, a, b).Compile();
        });
    }
}
