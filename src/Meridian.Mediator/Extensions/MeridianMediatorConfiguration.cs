using System.Reflection;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Publishing;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Extensions;

/// <summary>
/// Configuration class for the Meridian Mediator. Used with <see cref="ServiceCollectionExtensions.AddMeridianMediator"/>
/// to configure handler assemblies, pipeline behaviors, and notification publishing strategies.
/// </summary>
public class MeridianMediatorConfiguration
{
    internal List<Assembly> AssembliesToScan { get; } = new();

    /// <summary>
    /// Gets or sets the service lifetime for handler registrations.
    /// Default is <see cref="ServiceLifetime.Transient"/>.
    /// </summary>
    public ServiceLifetime Lifetime { get; set; } = ServiceLifetime.Transient;

    internal List<(Type ServiceType, Type ImplementationType, int Order)> ClosedBehaviors { get; } = new();
    internal List<(Type BehaviorType, int Order)> OpenBehaviors { get; } = new();
    internal List<(Type ServiceType, Type ImplementationType)> ClosedStreamBehaviors { get; } = new();
    internal List<Type> OpenStreamBehaviors { get; } = new();
    internal List<Type> PreProcessorTypes { get; } = new();
    internal List<Type> PostProcessorTypes { get; } = new();
    internal bool RegisterPreProcessorBehavior { get; private set; }
    internal bool RegisterPostProcessorBehavior { get; private set; }

    /// <summary>
    /// Gets or sets the notification publisher instance. If set, this takes precedence over <see cref="NotificationPublisherType"/>.
    /// </summary>
    public INotificationPublisher? NotificationPublisher { get; set; }

    /// <summary>
    /// Gets or sets the notification publisher type. Used when <see cref="NotificationPublisher"/> is not set.
    /// The type must implement <see cref="INotificationPublisher"/>.
    /// </summary>
    public Type? NotificationPublisherType { get; set; }

    /// <summary>
    /// Registers all handlers and behaviors found in the given assembly.
    /// </summary>
    /// <param name="assembly">The assembly to scan.</param>
    /// <returns>This configuration instance for fluent chaining.</returns>
    public MeridianMediatorConfiguration RegisterServicesFromAssembly(Assembly assembly)
    {
        AssembliesToScan.Add(assembly);
        return this;
    }

    /// <summary>
    /// Registers all handlers and behaviors found in the assembly containing <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">A type in the target assembly.</typeparam>
    /// <returns>This configuration instance for fluent chaining.</returns>
    public MeridianMediatorConfiguration RegisterServicesFromAssemblyContaining<T>()
    {
        return RegisterServicesFromAssembly(typeof(T).Assembly);
    }

    /// <summary>
    /// Adds a closed generic pipeline behavior.
    /// </summary>
    /// <param name="requestType">The closed request type.</param>
    /// <param name="behaviorType">The closed behavior type.</param>
    /// <param name="order">Execution order (lower values run first). Default is 0.</param>
    /// <returns>This configuration instance for fluent chaining.</returns>
    public MeridianMediatorConfiguration AddBehavior(Type requestType, Type behaviorType, int order = 0)
    {
        ClosedBehaviors.Add((requestType, behaviorType, order));
        return this;
    }

    /// <summary>
    /// Adds a closed generic pipeline behavior by type.
    /// </summary>
    /// <typeparam name="TBehavior">The behavior type.</typeparam>
    /// <returns>This configuration instance for fluent chaining.</returns>
    public MeridianMediatorConfiguration AddBehavior<TBehavior>(int order = 0)
    {
        var behaviorType = typeof(TBehavior);
        var interfaces = behaviorType.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(Pipeline.IPipelineBehavior<,>))
            .ToList();

        foreach (var iface in interfaces)
        {
            ClosedBehaviors.Add((iface, behaviorType, order));
        }

        if (interfaces.Count == 0)
        {
            // Might be an open generic registered as a closed type
            OpenBehaviors.Add((behaviorType, order));
        }

        return this;
    }

    /// <summary>
    /// Adds an open generic pipeline behavior. The behavior type must be an open generic type
    /// implementing <see cref="Pipeline.IPipelineBehavior{TRequest, TResponse}"/>.
    /// </summary>
    /// <param name="openBehaviorType">The open generic behavior type.</param>
    /// <param name="order">Execution order (lower values run first). Default is 0.</param>
    /// <returns>This configuration instance for fluent chaining.</returns>
    public MeridianMediatorConfiguration AddOpenBehavior(Type openBehaviorType, int order = 0)
    {
        if (!openBehaviorType.IsGenericTypeDefinition)
        {
            throw new ArgumentException(
                $"Type {openBehaviorType} must be an open generic type definition.",
                nameof(openBehaviorType));
        }

        OpenBehaviors.Add((openBehaviorType, order));
        return this;
    }

    /// <summary>
    /// Adds a stream pipeline behavior.
    /// </summary>
    /// <param name="requestType">The stream request type.</param>
    /// <param name="behaviorType">The stream behavior type.</param>
    /// <returns>This configuration instance for fluent chaining.</returns>
    public MeridianMediatorConfiguration AddStreamBehavior(Type requestType, Type behaviorType)
    {
        ClosedStreamBehaviors.Add((requestType, behaviorType));
        return this;
    }

    /// <summary>
    /// Adds an open generic stream pipeline behavior.
    /// </summary>
    /// <param name="openBehaviorType">The open generic stream behavior type.</param>
    /// <returns>This configuration instance for fluent chaining.</returns>
    public MeridianMediatorConfiguration AddOpenStreamBehavior(Type openBehaviorType)
    {
        if (!openBehaviorType.IsGenericTypeDefinition)
        {
            throw new ArgumentException(
                $"Type {openBehaviorType} must be an open generic type definition.",
                nameof(openBehaviorType));
        }

        OpenStreamBehaviors.Add(openBehaviorType);
        return this;
    }

    /// <summary>
    /// Adds a request pre-processor type. Also registers the built-in
    /// <see cref="Pipeline.RequestPreProcessorBehavior{TRequest, TResponse}"/>.
    /// </summary>
    /// <param name="processorType">The pre-processor type.</param>
    /// <returns>This configuration instance for fluent chaining.</returns>
    public MeridianMediatorConfiguration AddRequestPreProcessor(Type processorType)
    {
        PreProcessorTypes.Add(processorType);
        RegisterPreProcessorBehavior = true;
        return this;
    }

    /// <summary>
    /// Adds a request post-processor type. Also registers the built-in
    /// <see cref="Pipeline.RequestPostProcessorBehavior{TRequest, TResponse}"/>.
    /// </summary>
    /// <param name="processorType">The post-processor type.</param>
    /// <returns>This configuration instance for fluent chaining.</returns>
    public MeridianMediatorConfiguration AddRequestPostProcessor(Type processorType)
    {
        PostProcessorTypes.Add(processorType);
        RegisterPostProcessorBehavior = true;
        return this;
    }

    /// <summary>
    /// Adds the built-in <see cref="ValidationBehavior{TRequest, TResponse}"/> to the pipeline.
    /// Validates all requests that have registered <see cref="IValidator{T}"/> implementations.
    /// </summary>
    /// <param name="order">Execution order (lower values run first). Default is 0.</param>
    /// <returns>This configuration instance for fluent chaining.</returns>
    public MeridianMediatorConfiguration AddValidationBehavior(int order = 0)
    {
        OpenBehaviors.Add((typeof(ValidationBehavior<,>), order));
        return this;
    }

    /// <summary>
    /// Adds the built-in <see cref="LoggingBehavior{TRequest, TResponse}"/> to the pipeline.
    /// Logs request execution with timing for all requests.
    /// Requires an <see cref="IMediatorLogger"/> implementation to be registered in DI.
    /// </summary>
    /// <param name="order">Execution order (lower values run first). Default is 0.</param>
    /// <returns>This configuration instance for fluent chaining.</returns>
    public MeridianMediatorConfiguration AddLoggingBehavior(int order = 0)
    {
        OpenBehaviors.Add((typeof(LoggingBehavior<,>), order));
        return this;
    }

    /// <summary>
    /// Adds the built-in <see cref="TransactionBehavior{TRequest, TResponse}"/> to the pipeline.
    /// Wraps requests implementing <see cref="ITransactionalRequest"/> in a transaction.
    /// Requires an <see cref="ITransactionScopeProvider"/> implementation to be registered in DI.
    /// </summary>
    /// <param name="order">Execution order (lower values run first). Default is 0.</param>
    /// <returns>This configuration instance for fluent chaining.</returns>
    public MeridianMediatorConfiguration AddTransactionBehavior(int order = 0)
    {
        OpenBehaviors.Add((typeof(TransactionBehavior<,>), order));
        return this;
    }

    /// <summary>
    /// Adds the built-in <see cref="CachingBehavior{TRequest, TResponse}"/> and
    /// <see cref="CacheInvalidationBehavior{TRequest, TResponse}"/> to the pipeline.
    /// Caches results for <see cref="ICacheableQuery"/> requests and invalidates cache for
    /// <see cref="ICacheInvalidatingRequest"/> requests.
    /// Requires an <see cref="ICacheProvider"/> implementation to be registered in DI.
    /// </summary>
    /// <param name="order">Execution order (lower values run first). Default is 0.</param>
    /// <returns>This configuration instance for fluent chaining.</returns>
    public MeridianMediatorConfiguration AddCachingBehavior(int order = 0)
    {
        OpenBehaviors.Add((typeof(CachingBehavior<,>), order));
        OpenBehaviors.Add((typeof(CacheInvalidationBehavior<,>), order));
        return this;
    }

    /// <summary>
    /// Adds the built-in <see cref="RetryBehavior{TRequest, TResponse}"/> to the pipeline.
    /// Retries requests implementing <see cref="IRetryableRequest"/> on transient failures
    /// with exponential backoff.
    /// </summary>
    /// <param name="order">Execution order (lower values run first). Default is 0.</param>
    /// <returns>This configuration instance for fluent chaining.</returns>
    public MeridianMediatorConfiguration AddRetryBehavior(int order = 0)
    {
        OpenBehaviors.Add((typeof(RetryBehavior<,>), order));
        return this;
    }

    /// <summary>
    /// Adds the built-in <see cref="AuthorizationBehavior{TRequest, TResponse}"/> to the pipeline.
    /// Enforces authorization for requests implementing <see cref="IAuthorizedRequest"/>.
    /// Requires <see cref="IAuthorizationHandler{TRequest}"/> implementations to be registered in DI.
    /// </summary>
    /// <param name="order">Execution order (lower values run first). Default is 0.</param>
    /// <returns>This configuration instance for fluent chaining.</returns>
    public MeridianMediatorConfiguration AddAuthorizationBehavior(int order = 0)
    {
        OpenBehaviors.Add((typeof(AuthorizationBehavior<,>), order));
        return this;
    }

    /// <summary>
    /// Adds the built-in <see cref="CorrelationIdBehavior{TRequest, TResponse}"/> to the pipeline.
    /// Ensures a correlation ID exists in the async context for every request,
    /// enabling distributed tracing.
    /// </summary>
    /// <param name="order">Execution order (lower values run first). Default is 0.</param>
    /// <returns>This configuration instance for fluent chaining.</returns>
    public MeridianMediatorConfiguration AddCorrelationIdBehavior(int order = 0)
    {
        OpenBehaviors.Add((typeof(CorrelationIdBehavior<,>), order));
        return this;
    }

    /// <summary>
    /// Adds the built-in <see cref="IdempotencyBehavior{TRequest, TResponse}"/> to the pipeline.
    /// Ensures requests implementing <see cref="IIdempotentRequest"/> are executed at most once.
    /// Requires an <see cref="IIdempotencyStore"/> implementation to be registered in DI.
    /// </summary>
    /// <param name="order">Execution order (lower values run first). Default is 0.</param>
    /// <returns>This configuration instance for fluent chaining.</returns>
    public MeridianMediatorConfiguration AddIdempotencyBehavior(int order = 0)
    {
        OpenBehaviors.Add((typeof(IdempotencyBehavior<,>), order));
        return this;
    }
}
