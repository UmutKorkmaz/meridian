namespace Meridian.Mediator.FluentValidation;

/// <summary>Adapts typed FluentValidation validators to Meridian's asynchronous validation contract.</summary>
/// <typeparam name="T">The type being validated.</typeparam>
public sealed class FluentValidationAdapter<T> : Behaviors.IValidator<T>
{
    private readonly IEnumerable<global::FluentValidation.IValidator<T>> _validators;

    /// <summary>Creates an adapter over validators resolved from the current service scope.</summary>
    /// <param name="validators">The typed FluentValidation validators.</param>
    public FluentValidationAdapter(IEnumerable<global::FluentValidation.IValidator<T>> validators)
    {
        ArgumentNullException.ThrowIfNull(validators);
        _validators = validators;
    }

    /// <inheritdoc />
    public async Task<Behaviors.ValidationResult> ValidateAsync(T instance, CancellationToken cancellationToken)
    {
        var errors = new List<Behaviors.ValidationError>();
        foreach (var validator in _validators)
        {
            var result = await validator.ValidateAsync(instance, cancellationToken).ConfigureAwait(false);
            foreach (var error in result.Errors)
            {
                errors.Add(new Behaviors.ValidationError(error.PropertyName, error.ErrorMessage));
            }
        }

        return new Behaviors.ValidationResult { Errors = errors };
    }
}
