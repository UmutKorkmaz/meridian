using System.Linq.Expressions;
using System.Reflection;

namespace Meridian.Mapping.Execution;

/// <summary>
/// Represents the mapping plan for a single property from source to destination.
/// Contains the compiled getter/setter and any custom mapping logic.
/// </summary>
public class PropertyMap
{
    /// <summary>
    /// Gets the destination property being mapped to.
    /// </summary>
    public PropertyInfo DestinationProperty { get; }

    /// <summary>
    /// Gets the source property being mapped from (null if custom mapping).
    /// </summary>
    public PropertyInfo? SourceProperty { get; set; }

    /// <summary>
    /// Gets or sets the chain of source properties for flattened access (e.g., Address.Street).
    /// </summary>
    public PropertyInfo[]? SourcePropertyChain { get; set; }

    /// <summary>
    /// Gets or sets the chain of destination properties for ForPath (e.g., Address.Street).
    /// </summary>
    public PropertyInfo[]? DestinationPropertyChain { get; set; }

    /// <summary>
    /// Gets or sets the compiled expression for custom MapFrom.
    /// </summary>
    public LambdaExpression? CustomMapExpression { get; set; }

    /// <summary>
    /// Gets or sets a custom mapping function (two-arg: source, dest).
    /// </summary>
    public Delegate? CustomMapFunc { get; set; }

    /// <summary>
    /// Gets or sets the value resolver type (DI-resolved).
    /// </summary>
    public Type? ValueResolverType { get; set; }

    /// <summary>
    /// Gets or sets the member value resolver type (DI-resolved).
    /// Unlike <see cref="ValueResolverType"/>, this resolver also receives
    /// the source member value and current destination member value.
    /// </summary>
    public Type? MemberValueResolverType { get; set; }

    /// <summary>
    /// Gets or sets a compiled getter for the source member expression
    /// when using <see cref="MemberValueResolverType"/>.
    /// </summary>
    public Func<object, object?>? MemberValueResolverSourceGetter { get; set; }

    /// <summary>
    /// Gets or sets whether this member is ignored.
    /// </summary>
    public bool Ignored { get; set; }

    /// <summary>
    /// Gets or sets whether this map was explicitly configured via ForMember/ForPath,
    /// as opposed to convention-based auto-matching.
    /// </summary>
    public bool IsExplicitlyConfigured { get; set; }

    /// <summary>
    /// Gets or sets the condition function (1-arg: source only).
    /// </summary>
    public Delegate? Condition { get; set; }

    /// <summary>
    /// Gets or sets the 3-arg condition function (source, destination, resolvedSourceValue).
    /// Evaluated after value resolution with access to the resolved member value.
    /// </summary>
    public Delegate? Condition3Arg { get; set; }

    /// <summary>
    /// Gets or sets the pre-condition function.
    /// </summary>
    public Delegate? PreCondition { get; set; }

    /// <summary>
    /// Gets or sets the null substitution value.
    /// </summary>
    public object? NullSubstitute { get; set; }

    /// <summary>
    /// Gets or sets whether a null substitution is configured.
    /// </summary>
    public bool HasNullSubstitute { get; set; }

    /// <summary>
    /// Gets or sets the constant value.
    /// </summary>
    public object? ConstantValue { get; set; }

    /// <summary>
    /// Gets or sets whether a constant value is configured.
    /// </summary>
    public bool HasConstantValue { get; set; }

    /// <summary>
    /// Gets the compiled getter delegate for efficient source value retrieval.
    /// </summary>
    public Func<object, object?>? CompiledGetter { get; set; }

    /// <summary>
    /// Gets the compiled setter delegate for efficient destination value assignment.
    /// </summary>
    public Action<object, object?>? CompiledSetter { get; set; }

    /// <summary>
    /// Gets or sets the member-level converter function delegate.
    /// </summary>
    public Delegate? MemberConverterFunc { get; set; }

    /// <summary>
    /// Gets or sets the member-level converter instance (IValueConverter).
    /// </summary>
    public object? MemberConverterInstance { get; set; }

    /// <summary>
    /// Gets or sets the compiled getter for the member converter source expression.
    /// </summary>
    public Func<object, object?>? MemberConverterSourceGetter { get; set; }

    /// <summary>
    /// Initializes a new <see cref="PropertyMap"/>.
    /// </summary>
    /// <param name="destinationProperty">The destination property.</param>
    public PropertyMap(PropertyInfo destinationProperty)
    {
        DestinationProperty = destinationProperty ?? throw new ArgumentNullException(nameof(destinationProperty));
    }

    /// <summary>
    /// Initializes a new <see cref="PropertyMap"/> for ForPath (nested destination).
    /// </summary>
    /// <param name="destinationProperty">The final destination property.</param>
    /// <param name="destinationChain">The chain of properties to navigate (e.g., [Address, Street]).</param>
    public PropertyMap(PropertyInfo destinationProperty, PropertyInfo[] destinationChain)
    {
        DestinationProperty = destinationProperty ?? throw new ArgumentNullException(nameof(destinationProperty));
        DestinationPropertyChain = destinationChain ?? throw new ArgumentNullException(nameof(destinationChain));
    }

    /// <summary>
    /// Compiles getter and setter delegates for optimal runtime performance.
    /// </summary>
    public void Compile()
    {
        // Compile setter: ForPath uses chain setter, normal uses single-prop setter
        if (DestinationPropertyChain != null && DestinationPropertyChain.Length > 1)
        {
            CompiledSetter = CompileForPathSetter(DestinationPropertyChain);
        }
        else
        {
            CompiledSetter = CompileSetter(DestinationProperty);
        }

        if (CustomMapExpression != null)
        {
            CompiledGetter = CompileCustomExpression(CustomMapExpression);
        }
        else if (SourcePropertyChain != null && SourcePropertyChain.Length > 0)
        {
            CompiledGetter = CompileChainGetter(SourcePropertyChain);
        }
        else if (SourceProperty != null)
        {
            CompiledGetter = CompileGetter(SourceProperty);
        }
    }

    private static Func<object, object?> CompileGetter(PropertyInfo prop)
    {
        var param = Expression.Parameter(typeof(object), "obj");
        var cast = Expression.Convert(param, prop.DeclaringType!);
        var access = Expression.Property(cast, prop);
        var boxed = Expression.Convert(access, typeof(object));
        return Expression.Lambda<Func<object, object?>>(boxed, param).Compile();
    }

    private static Func<object, object?> CompileChainGetter(PropertyInfo[] chain)
    {
        var param = Expression.Parameter(typeof(object), "obj");
        Expression current = Expression.Convert(param, chain[0].DeclaringType!);

        // Build null-safe chain: if any intermediate is null, return null
        var returnTarget = Expression.Label(typeof(object), "return");
        var variables = new List<ParameterExpression>();
        var body = new List<Expression>();

        for (int i = 0; i < chain.Length; i++)
        {
            current = Expression.Property(current, chain[i]);

            if (i < chain.Length - 1 && !chain[i].PropertyType.IsValueType)
            {
                // null check intermediate properties
                var temp = Expression.Variable(chain[i].PropertyType, $"v{i}");
                variables.Add(temp);
                body.Add(Expression.Assign(temp, current));
                body.Add(Expression.IfThen(
                    Expression.Equal(temp, Expression.Constant(null, chain[i].PropertyType)),
                    Expression.Return(returnTarget, Expression.Constant(null, typeof(object)))));
                current = temp;

                // Continue the chain from the temp variable for the next property
                if (i + 1 < chain.Length)
                {
                    current = Expression.Property(current, chain[i + 1]);
                    i++; // skip next iteration since we already accessed it

                    if (i < chain.Length - 1 && !chain[i].PropertyType.IsValueType)
                    {
                        var temp2 = Expression.Variable(chain[i].PropertyType, $"v{i}");
                        variables.Add(temp2);
                        body.Add(Expression.Assign(temp2, current));
                        body.Add(Expression.IfThen(
                            Expression.Equal(temp2, Expression.Constant(null, chain[i].PropertyType)),
                            Expression.Return(returnTarget, Expression.Constant(null, typeof(object)))));
                        current = temp2;
                    }
                }
            }
        }

        body.Add(Expression.Return(returnTarget, Expression.Convert(current, typeof(object))));
        body.Add(Expression.Label(returnTarget, Expression.Constant(null, typeof(object))));

        var block = Expression.Block(typeof(object), variables, body);
        return Expression.Lambda<Func<object, object?>>(block, param).Compile();
    }

    private static Func<object, object?> CompileCustomExpression(LambdaExpression mapExpression)
    {
        var sourceParam = mapExpression.Parameters[0];
        var objParam = Expression.Parameter(typeof(object), "obj");
        var castSource = Expression.Convert(objParam, sourceParam.Type);

        var replaced = new ParameterReplacer(sourceParam, castSource).Visit(mapExpression.Body);
        var boxed = Expression.Convert(replaced, typeof(object));

        return Expression.Lambda<Func<object, object?>>(boxed, objParam).Compile();
    }

    private static Action<object, object?> CompileSetter(PropertyInfo prop)
    {
        var objParam = Expression.Parameter(typeof(object), "obj");
        var valParam = Expression.Parameter(typeof(object), "val");
        var castObj = Expression.Convert(objParam, prop.DeclaringType!);
        var castVal = Expression.Convert(valParam, prop.PropertyType);
        var assign = Expression.Assign(Expression.Property(castObj, prop), castVal);
        return Expression.Lambda<Action<object, object?>>(assign, objParam, valParam).Compile();
    }

    /// <summary>
    /// Compiles a setter for a nested destination property path (ForPath).
    /// Automatically creates intermediate objects if they are null.
    /// E.g., for dest.Address.Street, ensures dest.Address is not null before setting Street.
    /// </summary>
    private static Action<object, object?> CompileForPathSetter(PropertyInfo[] chain)
    {
        return (dest, value) =>
        {
            object current = dest;
            // Navigate to the parent of the final property, creating intermediates
            for (int i = 0; i < chain.Length - 1; i++)
            {
                var prop = chain[i];
                var next = prop.GetValue(current);
                if (next == null)
                {
                    // Create intermediate object
                    next = Activator.CreateInstance(prop.PropertyType)
                        ?? throw new InvalidOperationException(
                            $"Cannot create instance of '{prop.PropertyType.Name}' for ForPath. " +
                            "Ensure it has a parameterless constructor.");
                    prop.SetValue(current, next);
                }
                current = next;
            }

            // Set the final property
            var finalProp = chain[^1];
            if (value != null && !finalProp.PropertyType.IsAssignableFrom(value.GetType()))
            {
                if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(
                    Nullable.GetUnderlyingType(finalProp.PropertyType) ?? finalProp.PropertyType))
                {
                    value = Convert.ChangeType(value,
                        Nullable.GetUnderlyingType(finalProp.PropertyType) ?? finalProp.PropertyType);
                }
            }
            finalProp.SetValue(current, value);
        };
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
