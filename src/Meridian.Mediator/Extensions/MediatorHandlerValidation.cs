using System.Reflection;
using Meridian.Mediator.Pipeline;
using Meridian.Mediator.Streaming;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Extensions;

/// <summary>
/// Provides handler registration validation for the Meridian Mediator.
/// Call <see cref="AssertHandlerRegistration"/> at application startup to detect
/// missing or duplicate handler registrations before runtime errors occur.
/// </summary>
public static class MediatorHandlerValidation
{
    /// <summary>
    /// Validates that all request types in the scanned assemblies have exactly one handler registered.
    /// Throws <see cref="InvalidOperationException"/> if any validation errors are found.
    /// </summary>
    /// <param name="provider">The built service provider.</param>
    /// <param name="assemblies">The assemblies to scan for request types.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when requests have no handler, duplicate handlers, or notifications have no handlers.
    /// </exception>
    public static void AssertHandlerRegistration(this IServiceProvider provider, params Assembly[] assemblies)
    {
        var requestTypes = new List<Type>();
        foreach (var assembly in assemblies.Distinct())
        {
            requestTypes.AddRange(
                assembly.GetTypes()
                    .Where(t => t is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false }));
        }
        ValidateTypes(provider, requestTypes);
    }

    /// <summary>
    /// Validates that the specified request/notification types have handlers registered.
    /// More precise than <see cref="AssertHandlerRegistration(IServiceProvider, Assembly[])"/> —
    /// validates only the listed types.
    /// </summary>
    /// <param name="provider">The built service provider.</param>
    /// <param name="requestTypes">The request/notification types to validate.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when requests have no handler or duplicate handlers.
    /// </exception>
    public static void AssertHandlerRegistration(this IServiceProvider provider, params Type[] requestTypes)
    {
        ValidateTypes(provider, requestTypes);
    }

    private static void ValidateTypes(IServiceProvider provider, IEnumerable<Type> types)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        foreach (var type in types)
        {
            if (type.IsGenericTypeDefinition)
                continue;

            foreach (var iface in type.GetInterfaces())
            {
                // Check INotification (non-generic) — warn if no handlers
                if (iface == typeof(INotification))
                {
                    var handlerType = typeof(INotificationHandler<>).MakeGenericType(type);
                    var handlers = provider.GetServices(handlerType).ToList();
                    if (handlers.Count == 0)
                    {
                        warnings.Add($"Notification '{type.FullName}' has no registered handlers.");
                    }
                    continue;
                }

                // Check IRequest (void, non-generic) — must have exactly one handler
                if (iface == typeof(IRequest))
                {
                    var handlerType = typeof(IRequestHandler<>).MakeGenericType(type);
                    ValidateRequestHandler(provider, type, handlerType, errors);
                    continue;
                }

                if (!iface.IsGenericType || iface.ContainsGenericParameters)
                    continue;

                var genericDef = iface.GetGenericTypeDefinition();

                // Check IRequest<TResponse> — must have exactly one handler
                if (genericDef == typeof(IRequest<>))
                {
                    var responseType = iface.GetGenericArguments()[0];
                    var handlerType = typeof(IRequestHandler<,>).MakeGenericType(type, responseType);
                    ValidateRequestHandler(provider, type, handlerType, errors);
                }

                // Check IStreamRequest<TResponse> — must have exactly one handler
                if (genericDef == typeof(IStreamRequest<>))
                {
                    var responseType = iface.GetGenericArguments()[0];
                    var handlerType = typeof(IStreamRequestHandler<,>).MakeGenericType(type, responseType);
                    ValidateRequestHandler(provider, type, handlerType, errors);
                }
            }
        }

        if (errors.Count > 0)
        {
            var message = $"Meridian Mediator handler registration validation failed with {errors.Count} error(s):\n" +
                          string.Join("\n", errors.Select((e, i) => $"  {i + 1}. {e}"));
            if (warnings.Count > 0)
            {
                message += $"\n\nAdditionally, {warnings.Count} warning(s):\n" +
                          string.Join("\n", warnings.Select((w, i) => $"  {i + 1}. {w}"));
            }
            throw new InvalidOperationException(message);
        }
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
                errors.Add($"Request '{requestType.FullName}' has {handlers.Count} handlers registered ({handlerNames}). Only one handler per request type is allowed.");
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Request '{requestType.FullName}': failed to resolve handler '{handlerServiceType.Name}': {ex.Message}");
        }
    }
}
