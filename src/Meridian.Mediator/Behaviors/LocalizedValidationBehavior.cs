using Meridian.Mediator.Diagnostics;
using Meridian.Mediator.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Meridian.Mediator.Behaviors;

/// <summary>
/// Validation pipeline behaviour that routes every <see cref="ValidationError.ErrorMessage"/>
/// through <see cref="IStringLocalizer"/> before throwing
/// <see cref="ValidationException"/>. Adopters whose validators emit
/// resource-key strings (e.g. <c>"Workflow.Title.Required"</c>) get
/// localised messages without changing validator code.
/// </summary>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
/// <remarks>
/// <para>
/// Use this in place of <see cref="ValidationBehavior{TRequest, TResponse}"/>
/// — both implement the same pipeline contract; register exactly one of
/// them. The localised variant resolves <see cref="IStringLocalizer{TRequest}"/>
/// from DI so each request type can have its own resource scope without
/// extra wiring.
/// </para>
/// <para>
/// Messages that don't have a matching resource key fall back unchanged
/// — matching <see cref="LocalizedString.ResourceNotFound"/> semantics —
/// so adopters can introduce localisation incrementally without breaking
/// existing validators.
/// </para>
/// </remarks>
public sealed class LocalizedValidationBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly IEnumerable<IValidator<TRequest>> _validators;
    private readonly IStringLocalizer<TRequest> _localizer;
    private readonly MediatorMetrics _metrics;

    /// <summary>Initializes a new instance without metrics.</summary>
    /// <param name="validators">The validators for the request type.</param>
    /// <param name="localizer">The localizer for the request type.</param>
    public LocalizedValidationBehavior(
        IEnumerable<IValidator<TRequest>> validators,
        IStringLocalizer<TRequest> localizer)
        : this(validators, localizer, null)
    {
    }

    /// <summary>
    /// Initializes a new instance that reports rejections to a provider-owned metrics component.
    /// </summary>
    /// <param name="validators">The validators for the request type.</param>
    /// <param name="localizer">The localizer for the request type.</param>
    /// <param name="metrics">The metrics component. A null value leaves metrics disabled.</param>
    [ActivatorUtilitiesConstructor]
    public LocalizedValidationBehavior(
        IEnumerable<IValidator<TRequest>> validators,
        IStringLocalizer<TRequest> localizer,
        MediatorMetrics? metrics)
    {
        _validators = validators ?? throw new ArgumentNullException(nameof(validators));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _metrics = metrics ?? MediatorMetrics.Disabled;
    }

    /// <inheritdoc/>
    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var errors = new List<ValidationError>();

        foreach (var validator in _validators)
        {
            var result = await validator.ValidateAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.IsValid) continue;

            foreach (var raw in result.Errors)
            {
                var localised = _localizer[raw.ErrorMessage];
                // ResourceNotFound == true means no key in the resx — keep
                // the original message rather than letting the localiser's
                // fallback (which echoes the key) leak through opaque.
                var message = localised.ResourceNotFound ? raw.ErrorMessage : localised.Value;
                errors.Add(new ValidationError(raw.PropertyName, message));
            }
        }

        if (errors.Count > 0)
        {
            if (_metrics.ValidationEnabled)
            {
                _metrics.RecordValidationRejection(typeof(TRequest));
            }
            throw new ValidationException(errors);
        }

        return await next().ConfigureAwait(false);
    }
}
