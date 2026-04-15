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
    /// Registers a closed pipeline behavior under its fully-constructed
    /// <see cref="Pipeline.IPipelineBehavior{TRequest, TResponse}"/>
    /// service type.
    /// </summary>
    /// <param name="closedBehaviorServiceType">
    /// The closed <see cref="Pipeline.IPipelineBehavior{TRequest, TResponse}"/>
    /// service type, for example
    /// <c>typeof(IPipelineBehavior&lt;Ping, Pong&gt;)</c>.
    /// </param>
    /// <param name="behaviorType">The closed behavior type.</param>
    /// <param name="order">Execution order (lower values run first). Default is 0.</param>
    /// <returns>This configuration instance for fluent chaining.</returns>
    public MeridianMediatorConfiguration AddClosedBehavior(Type closedBehaviorServiceType, Type behaviorType, int order = 0)
    {
        RegisterClosedBehavior(closedBehaviorServiceType, behaviorType, order);
        return this;
    }

    /// <summary>
    /// Adds a closed pipeline behavior by request type or service type.
    /// </summary>
    /// <remarks>
    /// This overload is ambiguous and kept only for compatibility. Prefer
    /// <see cref="AddClosedBehavior"/> for closed registrations or
    /// <see cref="AddBehavior{TBehavior}"/> / <see cref="AddOpenBehavior"/>
    /// for the common cases.
    /// </remarks>
    /// <param name="requestOrServiceType">
    /// Either the closed request type or the closed
    /// <see cref="Pipeline.IPipelineBehavior{TRequest, TResponse}"/> service
    /// type.
    /// </param>
    /// <param name="behaviorType">The behavior type.</param>
    /// <param name="order">Execution order (lower values run first). Default is 0.</param>
    /// <returns>This configuration instance for fluent chaining.</returns>
    [Obsolete("AddBehavior(Type, Type, ...) is ambiguous. Use AddClosedBehavior(...) for closed registrations or AddBehavior<TBehavior>() / AddOpenBehavior(...) for the common cases.")]
    public MeridianMediatorConfiguration AddBehavior(Type requestOrServiceType, Type behaviorType, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(requestOrServiceType);
        ArgumentNullException.ThrowIfNull(behaviorType);

        var closedServiceType = ResolveClosedServiceType(
            requestOrServiceType,
            behaviorType,
            typeof(Pipeline.IPipelineBehavior<,>),
            nameof(requestOrServiceType),
            nameof(behaviorType),
            "AddOpenBehavior");

        RegisterClosedBehavior(closedServiceType, behaviorType, order);
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
        var interfaces = GetClosedImplementedInterfaces(behaviorType, typeof(Pipeline.IPipelineBehavior<,>));

        foreach (var iface in interfaces)
        {
            RegisterClosedBehavior(iface, behaviorType, order);
        }

        if (interfaces.Count == 0)
        {
            throw new ArgumentException(
                $"Type {behaviorType} must implement {typeof(Pipeline.IPipelineBehavior<,>)}. " +
                $"Use {nameof(AddOpenBehavior)} for open generic registrations.",
                nameof(TBehavior));
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
        ArgumentNullException.ThrowIfNull(openBehaviorType);

        if (!openBehaviorType.IsGenericTypeDefinition)
        {
            throw new ArgumentException(
                $"Type {openBehaviorType} must be an open generic type definition.",
                nameof(openBehaviorType));
        }

        if (!ImplementsOpenGenericInterface(openBehaviorType, typeof(Pipeline.IPipelineBehavior<,>)))
        {
            throw new ArgumentException(
                $"Type {openBehaviorType} must implement {typeof(Pipeline.IPipelineBehavior<,>)}.",
                nameof(openBehaviorType));
        }

        OpenBehaviors.Add((openBehaviorType, order));
        return this;
    }

    /// <summary>
    /// Registers a closed stream pipeline behavior under its fully-constructed
    /// <see cref="Streaming.IStreamPipelineBehavior{TRequest, TResponse}"/>
    /// service type.
    /// </summary>
    /// <param name="closedStreamBehaviorServiceType">
    /// The closed <see cref="Streaming.IStreamPipelineBehavior{TRequest, TResponse}"/>
    /// service type.
    /// </param>
    /// <param name="behaviorType">The stream behavior type.</param>
    /// <returns>This configuration instance for fluent chaining.</returns>
    public MeridianMediatorConfiguration AddClosedStreamBehavior(Type closedStreamBehaviorServiceType, Type behaviorType)
    {
        RegisterClosedStreamBehavior(closedStreamBehaviorServiceType, behaviorType);
        return this;
    }

    /// <summary>
    /// Adds a closed stream pipeline behavior by request type or service type.
    /// </summary>
    /// <remarks>
    /// This overload is ambiguous and kept only for compatibility. Prefer
    /// <see cref="AddClosedStreamBehavior"/> or
    /// <see cref="AddStreamBehavior{TBehavior}"/>.
    /// </remarks>
    /// <param name="requestOrServiceType">
    /// Either the closed stream request type or the closed
    /// <see cref="Streaming.IStreamPipelineBehavior{TRequest, TResponse}"/>
    /// service type.
    /// </param>
    /// <param name="behaviorType">The stream behavior type.</param>
    /// <returns>This configuration instance for fluent chaining.</returns>
    [Obsolete("AddStreamBehavior(Type, Type) is ambiguous. Use AddClosedStreamBehavior(...) or AddStreamBehavior<TBehavior>() instead.")]
    public MeridianMediatorConfiguration AddStreamBehavior(Type requestOrServiceType, Type behaviorType)
    {
        ArgumentNullException.ThrowIfNull(requestOrServiceType);
        ArgumentNullException.ThrowIfNull(behaviorType);

        var closedServiceType = ResolveClosedServiceType(
            requestOrServiceType,
            behaviorType,
            typeof(Streaming.IStreamPipelineBehavior<,>),
            nameof(requestOrServiceType),
            nameof(behaviorType),
            "AddOpenStreamBehavior");

        RegisterClosedStreamBehavior(closedServiceType, behaviorType);
        return this;
    }

    /// <summary>
    /// Adds a closed stream pipeline behavior by implementation type.
    /// </summary>
    /// <typeparam name="TBehavior">The stream behavior type.</typeparam>
    /// <returns>This configuration instance for fluent chaining.</returns>
    public MeridianMediatorConfiguration AddStreamBehavior<TBehavior>()
    {
        var behaviorType = typeof(TBehavior);
        var interfaces = GetClosedImplementedInterfaces(behaviorType, typeof(Streaming.IStreamPipelineBehavior<,>));

        foreach (var iface in interfaces)
        {
            RegisterClosedStreamBehavior(iface, behaviorType);
        }

        if (interfaces.Count == 0)
        {
            throw new ArgumentException(
                $"Type {behaviorType} must implement {typeof(Streaming.IStreamPipelineBehavior<,>)}. " +
                $"Use {nameof(AddOpenStreamBehavior)} for open generic registrations.",
                nameof(TBehavior));
        }

        return this;
    }

    /// <summary>
    /// Adds an open generic stream pipeline behavior.
    /// </summary>
    /// <param name="openBehaviorType">The open generic stream behavior type.</param>
    /// <returns>This configuration instance for fluent chaining.</returns>
    public MeridianMediatorConfiguration AddOpenStreamBehavior(Type openBehaviorType)
    {
        ArgumentNullException.ThrowIfNull(openBehaviorType);

        if (!openBehaviorType.IsGenericTypeDefinition)
        {
            throw new ArgumentException(
                $"Type {openBehaviorType} must be an open generic type definition.",
                nameof(openBehaviorType));
        }

        if (!ImplementsOpenGenericInterface(openBehaviorType, typeof(Streaming.IStreamPipelineBehavior<,>)))
        {
            throw new ArgumentException(
                $"Type {openBehaviorType} must implement {typeof(Streaming.IStreamPipelineBehavior<,>)}.",
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

    /// <summary>
    /// Adds the built-in <see cref="AuditBehavior{TRequest, TResponse}"/> to
    /// the pipeline. Records an <see cref="AuditEntry"/> for every request
    /// via the registered <see cref="IAuditSink"/>. Requires an
    /// <see cref="IAuditSink"/> implementation in DI — register
    /// <see cref="LoggerAuditSink"/> for the default <see cref="ILogger"/>-based
    /// sink, or your own implementation for a database/SIEM destination.
    /// </summary>
    /// <param name="order">
    /// Execution order. Should be HIGHER than CorrelationIdBehavior (so the
    /// audit record sees the established correlation ID) and LOWER than
    /// ValidationBehavior (so failed validations are themselves audited).
    /// Defaults to 10 to leave room for ordering tweaks on either side.
    /// </param>
    /// <returns>This configuration instance for fluent chaining.</returns>
    public MeridianMediatorConfiguration AddAuditBehavior(int order = 10)
    {
        OpenBehaviors.Add((typeof(AuditBehavior<,>), order));
        return this;
    }

    /// <summary>
    /// Adds the localising variant of <see cref="ValidationBehavior{TRequest, TResponse}"/>
    /// — every <see cref="ValidationError.ErrorMessage"/> is routed through
    /// <see cref="Microsoft.Extensions.Localization.IStringLocalizer{TRequest}"/>
    /// before the resulting <see cref="ValidationException"/> is thrown.
    /// Use this in place of <see cref="AddValidationBehavior"/>; do not
    /// register both for the same request.
    /// </summary>
    /// <param name="order">Execution order. Default is 0.</param>
    /// <returns>This configuration instance for fluent chaining.</returns>
    public MeridianMediatorConfiguration AddLocalizedValidationBehavior(int order = 0)
    {
        OpenBehaviors.Add((typeof(LocalizedValidationBehavior<,>), order));
        return this;
    }

    private void RegisterClosedBehavior(Type serviceType, Type behaviorType, int order)
    {
        EnsureClosedServiceType(serviceType, typeof(Pipeline.IPipelineBehavior<,>), nameof(serviceType));
        EnsureConcreteClosedBehaviorType(serviceType, behaviorType, nameof(behaviorType), "AddOpenBehavior");
        ClosedBehaviors.Add((serviceType, behaviorType, order));
    }

    private void RegisterClosedStreamBehavior(Type serviceType, Type behaviorType)
    {
        EnsureClosedServiceType(serviceType, typeof(Streaming.IStreamPipelineBehavior<,>), nameof(serviceType));
        EnsureConcreteClosedBehaviorType(serviceType, behaviorType, nameof(behaviorType), "AddOpenStreamBehavior");
        ClosedStreamBehaviors.Add((serviceType, behaviorType));
    }

    private static Type ResolveClosedServiceType(
        Type requestOrServiceType,
        Type behaviorType,
        Type openBehaviorServiceType,
        string requestOrServiceParamName,
        string behaviorParamName,
        string openRegistrationMethod)
    {
        if (IsClosedConstructedGeneric(requestOrServiceType, openBehaviorServiceType))
        {
            EnsureConcreteClosedBehaviorType(
                requestOrServiceType,
                behaviorType,
                behaviorParamName,
                openRegistrationMethod);
            return requestOrServiceType;
        }

        if (behaviorType.IsGenericTypeDefinition)
        {
            throw new ArgumentException(
                $"Type {behaviorType} is open generic. Use {openRegistrationMethod} for open generic registrations.",
                behaviorParamName);
        }

        var matchingInterfaces = behaviorType.GetInterfaces()
            .Where(i => IsClosedConstructedGeneric(i, openBehaviorServiceType))
            .Where(i => i.GetGenericArguments()[0] == requestOrServiceType)
            .ToList();

        return matchingInterfaces.Count switch
        {
            1 => matchingInterfaces[0],
            > 1 => throw new ArgumentException(
                $"Type {behaviorType} implements multiple closed {openBehaviorServiceType.Name} interfaces for request type {requestOrServiceType}. " +
                "Use the explicit closed service-type overload instead.",
                requestOrServiceParamName),
            _ => throw new ArgumentException(
                $"Type {behaviorType} does not implement a closed {openBehaviorServiceType.Name} for request type {requestOrServiceType}. " +
                "Pass the closed service type explicitly when the request type is ambiguous.",
                behaviorParamName),
        };
    }

    private static List<Type> GetClosedImplementedInterfaces(Type behaviorType, Type openBehaviorServiceType)
    {
        return behaviorType.GetInterfaces()
            .Where(i => IsClosedConstructedGeneric(i, openBehaviorServiceType))
            .ToList();
    }

    private static void EnsureClosedServiceType(Type serviceType, Type openBehaviorServiceType, string paramName)
    {
        if (!IsClosedConstructedGeneric(serviceType, openBehaviorServiceType))
        {
            throw new ArgumentException(
                $"Type {serviceType} must be a closed {openBehaviorServiceType}.",
                paramName);
        }
    }

    private static void EnsureConcreteClosedBehaviorType(
        Type serviceType,
        Type behaviorType,
        string paramName,
        string openRegistrationMethod)
    {
        if (behaviorType.IsGenericTypeDefinition)
        {
            throw new ArgumentException(
                $"Type {behaviorType} is open generic. Use {openRegistrationMethod} for open generic registrations.",
                paramName);
        }

        if (behaviorType.IsInterface || behaviorType.IsAbstract)
        {
            throw new ArgumentException(
                $"Type {behaviorType} must be a concrete implementation type.",
                paramName);
        }

        if (!serviceType.IsAssignableFrom(behaviorType))
        {
            throw new ArgumentException(
                $"Type {behaviorType} does not implement {serviceType}.",
                paramName);
        }
    }

    private static bool IsClosedConstructedGeneric(Type type, Type openGenericType) =>
        type.IsGenericType &&
        !type.ContainsGenericParameters &&
        type.GetGenericTypeDefinition() == openGenericType;

    private static bool ImplementsOpenGenericInterface(Type type, Type openInterfaceType) =>
        type.GetInterfaces().Any(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == openInterfaceType);
}
