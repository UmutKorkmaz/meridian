using System.Reflection;
using Meridian.Mediator.Behaviors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meridian.Mediator.Extensions;

/// <summary>
/// One-liner DI wiring for the recommended Meridian pipeline composition:
/// correlation ID → audit → localised validation → handler. This
/// extension is deliberately opinionated. Projects that need a different
/// composition should call
/// <see cref="ServiceCollectionExtensions.AddMeridianMediator"/> directly
/// and assemble their own behaviour stack.
/// </summary>
public static class StandardPipelineExtensions
{
    /// <summary>
    /// Registers Meridian Mediator with the recommended pipeline:
    ///
    /// <list type="number">
    /// <item><description><see cref="CorrelationIdBehavior{TRequest, TResponse}"/> at order -100 — establishes the correlation ID before anything else runs.</description></item>
    /// <item><description><see cref="AuditBehavior{TRequest, TResponse}"/> at order -50 — records what was attempted before validation can reject it.</description></item>
    /// <item><description><see cref="LocalizedValidationBehavior{TRequest, TResponse}"/> at order 0 — runs validators with localised error messages.</description></item>
    /// </list>
    ///
    /// Handlers are auto-registered from <paramref name="assemblies"/>;
    /// pass at least the assembly that contains your <c>IRequestHandler</c>
    /// implementations. The default audit sink is
    /// <see cref="LoggerAuditSink"/> — replace it with
    /// <c>services.AddSingleton&lt;IAuditSink, MyDatabaseSink&gt;()</c>
    /// before calling this method if you want records persisted somewhere
    /// other than the application log.
    /// </summary>
    /// <param name="services">The DI container.</param>
    /// <param name="assemblies">
    /// Assemblies containing <c>IRequestHandler</c> and <c>IValidator</c>
    /// implementations. Typically <c>typeof(Program).Assembly</c> alone
    /// is sufficient for a single-assembly application.
    /// </param>
    /// <returns>The service collection for fluent chaining.</returns>
    /// <example>
    /// <code>
    /// // Program.cs
    /// builder.Services.AddMeridianStandard(typeof(Program).Assembly);
    /// </code>
    /// </example>
    public static IServiceCollection AddMeridianStandard(
        this IServiceCollection services,
        params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (assemblies is null || assemblies.Length == 0)
            throw new ArgumentException(
                "At least one assembly must be supplied for handler scanning. " +
                "Pass typeof(Program).Assembly for a single-project application.",
                nameof(assemblies));

        // Default audit sink unless the consumer registered their own.
        services.TryAddSingleton<IAuditSink, LoggerAuditSink>();

        services.AddMeridianMediator(c =>
        {
            foreach (var assembly in assemblies)
                c.RegisterServicesFromAssembly(assembly);

            c.AddCorrelationIdBehavior(order: -100);
            c.AddAuditBehavior(order: -50);
            c.AddLocalizedValidationBehavior(order: 0);
        });

        // The mediator's built-in handler scanner does not pick up
        // IValidator<T> implementations because validation is an
        // optional behaviour, not a core handler contract. The standard
        // pipeline includes localised validation, so we scan for and
        // register validators here as well — otherwise the wired
        // behaviour would silently no-op when handlers are registered
        // but no validators are.
        foreach (var assembly in assemblies)
            RegisterValidatorsFromAssembly(services, assembly);

        return services;
    }

    private static void RegisterValidatorsFromAssembly(
        IServiceCollection services, Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (type is not { IsClass: true, IsAbstract: false }) continue;
            if (type.IsGenericTypeDefinition) continue;

            foreach (var iface in type.GetInterfaces())
            {
                if (!iface.IsGenericType) continue;
                if (iface.GetGenericTypeDefinition() != typeof(IValidator<>)) continue;
                if (iface.ContainsGenericParameters) continue;

                services.AddTransient(iface, type);
            }
        }
    }
}
