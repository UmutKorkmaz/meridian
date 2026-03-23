using System.Linq.Expressions;
using System.Reflection;

namespace Meridian.Mapping.Execution;

/// <summary>
/// Creates destination object instances. Supports parameterless constructors,
/// parameterized constructors with automatic parameter matching, and custom
/// constructor parameter mappings.
/// </summary>
public static class ObjectCreator
{
    /// <summary>
    /// Creates an instance of the destination type, attempting parameterless constructor first.
    /// </summary>
    /// <param name="type">The type to instantiate.</param>
    /// <returns>A new instance of the type.</returns>
    public static object CreateInstance(Type type)
    {
        if (type.IsValueType)
            return Activator.CreateInstance(type)!;

        var ctor = type.GetConstructor(
            BindingFlags.Public | BindingFlags.Instance,
            null, Type.EmptyTypes, null);

        if (ctor != null)
            return ctor.Invoke(null);

        // Try the first public constructor
        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (ctors.Length == 0)
            throw new InvalidOperationException(
                $"No public constructor found for type '{type.FullName}'. " +
                "Use ConstructUsing to specify how to create instances.");

        // Use the constructor with the fewest parameters as fallback
        var simplest = ctors.OrderBy(c => c.GetParameters().Length).First();
        var parameters = simplest.GetParameters()
            .Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
            .ToArray();

        return simplest.Invoke(parameters);
    }

    /// <summary>
    /// Creates an instance using constructor parameter matching from source object.
    /// </summary>
    /// <param name="destinationType">The destination type to create.</param>
    /// <param name="source">The source object to extract constructor arguments from.</param>
    /// <param name="sourceType">The source type.</param>
    /// <param name="ctorParamMappings">Optional explicit constructor parameter mappings.</param>
    /// <returns>A new destination instance.</returns>
    public static object CreateWithConstructorMapping(
        Type destinationType,
        object source,
        Type sourceType,
        Dictionary<string, LambdaExpression>? ctorParamMappings)
    {
        var ctors = destinationType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (ctors.Length == 0)
            throw new InvalidOperationException($"No public constructor found for type '{destinationType.FullName}'.");

        // Try to find a matching constructor
        var sourceProps = sourceType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead)
            .ToDictionary(p => p.Name, p => p, StringComparer.OrdinalIgnoreCase);

        // Sort constructors by parameter count descending to prefer more specific constructors
        foreach (var ctor in ctors.OrderByDescending(c => c.GetParameters().Length))
        {
            var parameters = ctor.GetParameters();
            var args = new object?[parameters.Length];
            var allResolved = true;

            for (int i = 0; i < parameters.Length; i++)
            {
                var param = parameters[i];

                // Check explicit mapping first
                if (ctorParamMappings != null &&
                    ctorParamMappings.TryGetValue(param.Name!, out var expr))
                {
                    var compiled = expr.Compile();
                    args[i] = compiled.DynamicInvoke(source);
                    continue;
                }

                // Try matching by name from source properties
                if (sourceProps.TryGetValue(param.Name!, out var sourceProp) &&
                    IsAssignable(param.ParameterType, sourceProp.PropertyType))
                {
                    args[i] = sourceProp.GetValue(source);
                    continue;
                }

                // Use default value if available
                if (param.HasDefaultValue)
                {
                    args[i] = param.DefaultValue;
                    continue;
                }

                // Use type default
                args[i] = param.ParameterType.IsValueType ? Activator.CreateInstance(param.ParameterType) : null;
            }

            if (allResolved)
                return ctor.Invoke(args);
        }

        return CreateInstance(destinationType);
    }

    /// <summary>
    /// Compiles a parameterless factory delegate for a type with a parameterless constructor.
    /// </summary>
    /// <param name="type">The type to create a factory for.</param>
    /// <returns>A compiled factory delegate, or null if no parameterless constructor exists.</returns>
    public static Func<object>? CompileFactory(Type type)
    {
        if (type.IsValueType)
        {
            return () => Activator.CreateInstance(type)!;
        }

        var ctor = type.GetConstructor(
            BindingFlags.Public | BindingFlags.Instance,
            null, Type.EmptyTypes, null);

        if (ctor == null)
            return null;

        var newExpr = Expression.New(ctor);
        var boxed = Expression.Convert(newExpr, typeof(object));
        return Expression.Lambda<Func<object>>(boxed).Compile();
    }

    private static bool IsAssignable(Type target, Type source)
    {
        if (target.IsAssignableFrom(source))
            return true;

        // Handle nullable
        var targetUnderlying = Nullable.GetUnderlyingType(target);
        if (targetUnderlying != null && targetUnderlying.IsAssignableFrom(source))
            return true;

        // Handle implicit numeric conversions
        if (IsNumeric(target) && IsNumeric(source))
            return true;

        return false;
    }

    private static bool IsNumeric(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type == typeof(byte) || type == typeof(sbyte) ||
               type == typeof(short) || type == typeof(ushort) ||
               type == typeof(int) || type == typeof(uint) ||
               type == typeof(long) || type == typeof(ulong) ||
               type == typeof(float) || type == typeof(double) ||
               type == typeof(decimal);
    }
}
