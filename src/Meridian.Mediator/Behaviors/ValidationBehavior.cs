using Meridian.Mediator.Pipeline;

namespace Meridian.Mediator.Behaviors;

/// <summary>
/// Validation interface. Users implement for each request type or use a FluentValidation adapter.
/// </summary>
/// <typeparam name="T">The type to validate.</typeparam>
public interface IValidator<in T>
{
    /// <summary>
    /// Validates the specified instance.
    /// </summary>
    /// <param name="instance">The instance to validate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A validation result containing any errors.</returns>
    Task<ValidationResult> ValidateAsync(T instance, CancellationToken cancellationToken);
}

/// <summary>
/// Represents the result of a validation operation.
/// </summary>
public class ValidationResult
{
    /// <summary>
    /// Gets a value indicating whether the validation passed (no errors).
    /// </summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>
    /// Gets the list of validation errors.
    /// </summary>
    public List<ValidationError> Errors { get; init; } = new();
}

/// <summary>
/// Represents a single validation error.
/// </summary>
/// <param name="PropertyName">The name of the property that failed validation.</param>
/// <param name="ErrorMessage">The error message describing the validation failure.</param>
public record ValidationError(string PropertyName, string ErrorMessage);

/// <summary>
/// Exception thrown when one or more validation errors occur.
/// </summary>
public class ValidationException : Exception
{
    /// <summary>
    /// Gets the collection of validation errors that caused this exception.
    /// </summary>
    public IReadOnlyList<ValidationError> Errors { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ValidationException"/> class.
    /// </summary>
    /// <param name="errors">The validation errors.</param>
    public ValidationException(IEnumerable<ValidationError> errors) : base("Validation failed")
    {
        Errors = errors.ToList().AsReadOnly();
    }
}

/// <summary>
/// Pipeline behavior that validates requests before handler execution.
/// Resolves all <see cref="IValidator{T}"/> implementations from DI for the request type,
/// runs them all, and throws a <see cref="ValidationException"/> if any validation errors are found.
/// </summary>
/// <typeparam name="TRequest">Request type.</typeparam>
/// <typeparam name="TResponse">Response type.</typeparam>
public class ValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    /// <summary>
    /// Initializes a new instance of the <see cref="ValidationBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="validators">The validators for the request type.</param>
    public ValidationBehavior(IEnumerable<IValidator<TRequest>> validators)
    {
        _validators = validators ?? throw new ArgumentNullException(nameof(validators));
    }

    /// <inheritdoc/>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var errors = new List<ValidationError>();

        foreach (var validator in _validators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken);
            if (!result.IsValid)
            {
                errors.AddRange(result.Errors);
            }
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }

        return await next();
    }
}
