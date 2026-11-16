using Meridian.Mediator.Behaviors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meridian.Mediator.EntityFrameworkCore;

/// <summary>Registers scoped EF transaction ownership.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers the first context and options once. Existing consumer contracts are preserved.</summary>
    /// <typeparam name="TContext">The scoped context registered by the application.</typeparam>
    /// <param name="services">Application service collection.</param>
    /// <param name="configure">Explicit options for the first registration of this context.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddMeridianEfCoreTransactions<TContext>(this IServiceCollection services, Action<EfTransactionOptions>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.Any(x => x.ServiceType == typeof(EfTransactionScopeProvider<TContext>))) return services;
        var options = new EfTransactionOptions(); configure?.Invoke(options);
        var snapshot = new EfTransactionOptions { UseExecutionStrategy = options.UseExecutionStrategy, JoinWithoutSavepoint = options.JoinWithoutSavepoint };
        services.TryAddScoped<EfTransactionScopeProvider<TContext>>(provider => new(provider.GetRequiredService<TContext>(), snapshot));
        services.TryAddScoped<ITransactionScopeProvider>(provider => provider.GetRequiredService<EfTransactionScopeProvider<TContext>>());
        services.TryAddScoped<ITransactionExecutionProvider>(provider => provider.GetRequiredService<ITransactionScopeProvider>() as ITransactionExecutionProvider
            ?? throw new InvalidOperationException("The application transaction provider does not implement ITransactionExecutionProvider."));
        return services;
    }
}
