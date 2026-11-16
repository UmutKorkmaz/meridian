using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meridian.Mediator.AspNetCore;

/// <summary>Registers optional HTTP error integration without installing middleware.</summary>
public static class MeridianHttpExtensions
{
    /// <summary>Adds one singleton redacting handler and standard ProblemDetails services. Install UseExceptionHandler in every environment.</summary>
    public static IServiceCollection AddMeridianProblemDetails(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(x => x.ServiceType == typeof(Registration))) return services;
        services.AddSingleton<Registration>();
        services.AddProblemDetails();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IExceptionHandler, MeridianExceptionHandler>());
        return services;
    }
    private sealed class Registration;
}
