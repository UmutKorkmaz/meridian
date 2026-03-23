using System.Linq;

namespace Meridian.Mediator.Behaviors;

/// <summary>
/// Bridges FluentValidation validators to Meridian's internal validation abstraction.
/// </summary>
/// <typeparam name="T">The request type to validate.</typeparam>
public sealed class FluentValidationAdapter<T> : IValidator<T>
{
    private readonly IReadOnlyList<global::FluentValidation.IValidator<T>> _validators;

    /// <summary>
    /// Initializes a new instance of the <see cref="FluentValidationAdapter{T}"/> class.
    /// </summary>
    /// <param name="validators">The FluentValidation validator instances.</param>
    public FluentValidationAdapter(IEnumerable<global::FluentValidation.IValidator<T>> validators)
    {
        _validators = validators.ToList();
    }

    /// <inheritdoc/>
    public async Task<ValidationResult> ValidateAsync(T instance, CancellationToken cancellationToken)
    {
        if (_validators.Count == 0)
        {
            return new ValidationResult();
        }

        var errors = new List<ValidationError>();

        foreach (var validator in _validators)
        {
            var result = await validator.ValidateAsync(instance, cancellationToken);
            errors.AddRange(result.Errors.Select(e => new ValidationError(e.PropertyName, e.ErrorMessage)));
        }

        return new ValidationResult
        {
            Errors = errors
        };
    }
}
