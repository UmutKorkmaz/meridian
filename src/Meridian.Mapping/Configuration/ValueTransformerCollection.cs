namespace Meridian.Mapping.Configuration;

/// <summary>
/// Holds global value transformers that are applied to mapped values by type.
/// Transformers are executed after the value has been resolved and before it is
/// assigned to the destination member. Multiple transformers can be registered
/// for the same type and they are applied in registration order.
/// </summary>
public class ValueTransformerCollection
{
    private readonly List<(Type ValueType, Delegate Transformer)> _transformers = new();

    /// <summary>
    /// Adds a value transformer for the specified value type.
    /// The transformer function receives a value and returns a transformed value.
    /// </summary>
    /// <typeparam name="TValue">The type of value to transform.</typeparam>
    /// <param name="transformer">The transformation function.</param>
    public void Add<TValue>(Func<TValue, TValue> transformer)
    {
        ArgumentNullException.ThrowIfNull(transformer);
        _transformers.Add((typeof(TValue), transformer));
    }

    /// <summary>
    /// Applies all matching transformers to the given value.
    /// A transformer matches if the value is assignable to the transformer's value type.
    /// </summary>
    /// <param name="value">The value to transform.</param>
    /// <returns>The transformed value.</returns>
    public object Apply(object value)
    {
        var valueType = value.GetType();

        foreach (var (type, transformer) in _transformers)
        {
            if (type.IsAssignableFrom(valueType))
            {
                value = transformer.DynamicInvoke(value)!;
            }
        }

        return value;
    }

    /// <summary>
    /// Gets whether any transformers have been registered.
    /// </summary>
    public bool HasTransformers => _transformers.Count > 0;
}
