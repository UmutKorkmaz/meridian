using System.Reflection;
using Meridian.Mediator.Pipeline;
using Meridian.Mediator.Streaming;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Extensions;

/// <summary>
/// Provides handler registration validation for the Meridian Mediator.
/// Call <see cref="AssertHandlerRegistration(IServiceProvider, Assembly[])"/> at application startup to detect
/// missing or duplicate handler registrations before runtime errors occur.
/// </summary>
public static class MediatorHandlerValidation
{
    /// <summary>
    /// Validates that all request/notification types in the scanned assemblies have handlers.
    /// Throws <see cref="InvalidOperationException"/> if request-handler issues exist.
    /// </summary>
    /// <param name="provider">The built service provider.</param>
    /// <param name="assemblies">The assemblies to scan for request and notification types.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when requests have no handler, duplicate handlers, or notifications have no handlers.
    /// </exception>
    public static void AssertHandlerRegistration(this IServiceProvider provider, params Assembly[] assemblies)
    {
        if (provider is null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        var issueCount = provider.TryGetHandlerRegistrationDiagnostics(
            out var errors,
            out var warnings,
            assemblies);

        if (issueCount)
        {
            throw new InvalidOperationException(BuildValidationMessage(errors, warnings));
        }
    }

    /// <summary>
    /// Validates that the specified request/notification types have handlers.
    /// Throws <see cref="InvalidOperationException"/> when issues are found.
    /// </summary>
    /// <param name="provider">The built service provider.</param>
    /// <param name="requestTypes">The request/notification types to validate.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when requests have no handler or duplicate handlers.
    /// </exception>
    public static void AssertHandlerRegistration(this IServiceProvider provider, params Type[] requestTypes)
    {
        if (provider is null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        var issueCount = provider.TryGetHandlerRegistrationDiagnostics(
            out var errors,
            out var warnings,
            requestTypes);

        if (issueCount)
        {
            throw new InvalidOperationException(BuildValidationMessage(errors, warnings));
        }
    }

    /// <summary>
    /// Collects handler registration diagnostics without throwing.
    /// Returns <see langword="true"/> when any errors or warnings are found.
    /// </summary>
    /// <param name="provider">The built service provider.</param>
    /// <param name="errors">Collected error messages.</param>
    /// <param name="warnings">Collected warning messages.</param>
    /// <param name="assemblies">The assemblies to scan for request and notification types.</param>
    /// <returns><see langword="true"/> if diagnostics found any issues.</returns>
    public static bool TryGetHandlerRegistrationDiagnostics(
        this IServiceProvider provider,
        out IReadOnlyList<string> errors,
        out IReadOnlyList<string> warnings,
        params Assembly[] assemblies)
    {
        if (provider is null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        var requestTypes = assemblies
            .Distinct()
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false });

        return ValidateTypes(provider, requestTypes, out errors, out warnings);
    }

    /// <summary>
    /// Collects handler registration diagnostics for the provided request/notification types without throwing.
    /// </summary>
    /// <param name="provider">The built service provider.</param>
    /// <param name="errors">Collected error messages.</param>
    /// <param name="warnings">Collected warning messages.</param>
    /// <param name="requestTypes">The request/notification types to validate.</param>
    /// <returns><see langword="true"/> if diagnostics found any issues.</returns>
    public static bool TryGetHandlerRegistrationDiagnostics(
        this IServiceProvider provider,
        out IReadOnlyList<string> errors,
        out IReadOnlyList<string> warnings,
        params Type[] requestTypes)
    {
        if (provider is null)
        {
            throw new ArgumentNullException(nameof(provider));
        }

        return ValidateTypes(provider, requestTypes, out errors, out warnings);
    }

    private static bool ValidateTypes(
        IServiceProvider provider,
        IEnumerable<Type> requestTypes,
        out IReadOnlyList<string> errors,
        out IReadOnlyList<string> warnings)
    {
        var validationErrors = new List<string>();
        var validationWarnings = new List<string>();

        foreach (var type in requestTypes)
        {
            if (type.IsGenericTypeDefinition)
            {
                continue;
            }

            foreach (var iface in type.GetInterfaces())
            {
                if (iface == typeof(INotification))
                {
                    var handlerType = typeof(INotificationHandler<>).MakeGenericType(type);
                    var handlers = provider.GetServices(handlerType).ToList();
                    if (handlers.Count == 0)
                    {
                        validationWarnings.Add($"Notification '{type.FullName}' has no registered handlers.");
                    }

                    continue;
                }

                if (iface == typeof(IRequest))
                {
                    var handlerType = typeof(IRequestHandler<>).MakeGenericType(type);
                    ValidateRequestHandler(provider, type, handlerType, validationErrors);
                    continue;
                }

                if (!iface.IsGenericType || iface.ContainsGenericParameters)
                {
                    continue;
                }

                var genericDef = iface.GetGenericTypeDefinition();

                if (genericDef == typeof(IRequest<>))
                {
                    var responseType = iface.GetGenericArguments()[0];
                    var handlerType = typeof(IRequestHandler<,>).MakeGenericType(type, responseType);
                    ValidateRequestHandler(provider, type, handlerType, validationErrors);
                    continue;
                }

                if (genericDef == typeof(IStreamRequest<>))
                {
                    var responseType = iface.GetGenericArguments()[0];
                    var handlerType = typeof(IStreamRequestHandler<,>).MakeGenericType(type, responseType);
                    ValidateRequestHandler(provider, type, handlerType, validationErrors);
                }
            }
        }

        errors = validationErrors;
        warnings = validationWarnings;
        return validationErrors.Count > 0 || validationWarnings.Count > 0;
    }

    private static void ValidateRequestHandler(IServiceProvider provider, Type requestType, Type handlerServiceType, List<string> errors)
    {
        try
        {
            var handlers = provider.GetServices(handlerServiceType).ToList();
            if (handlers.Count == 0)
            {
                errors.Add($"Request '{requestType.FullName}' has no registered handler for '{handlerServiceType.Name}'.");
            }
            else if (handlers.Count > 1)
            {
                var handlerNames = string.Join(", ", handlers.Select(h => h?.GetType().FullName ?? "null"));
                errors.Add(
                    $"Request '{requestType.FullName}' has {handlers.Count} handlers registered ({handlerNames}). " +
                    "Only one handler per request type is allowed.");
            }
        }
        catch (Exception ex)
        {
            errors.Add(
                $"Request '{requestType.FullName}': failed to resolve handler '{handlerServiceType.Name}': {ex.Message}");
        }
    }

    private static string BuildValidationMessage(IReadOnlyList<string> errors, IReadOnlyList<string> warnings)
    {
        var message = $"Meridian Mediator handler registration validation failed with {errors.Count} error(s):\n" +
                      string.Join("\n", errors.Select((e, i) => $"  {i + 1}. {e}"));

        if (warnings.Count > 0)
        {
            message += $"\n\nAdditionally, {warnings.Count} warning(s):\n" +
                       string.Join("\n", warnings.Select((w, i) => $"  {i + 1}. {w}"));
        }

        return message;
    }
}
