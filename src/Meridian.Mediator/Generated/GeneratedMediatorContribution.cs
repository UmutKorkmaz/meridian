using Microsoft.Extensions.DependencyInjection;
namespace Meridian.Mediator.Generated;
/// <summary>Optional finite contribution metadata for generated composition.</summary>
public interface IGeneratedMediatorContributions
{
    /// <summary>Immutable constructor metadata; never resolved services.</summary>
    IReadOnlyList<GeneratedMediatorContribution> Contributions { get; }
    /// <summary>Whether the declaration selected the standard pipeline.</summary>
    bool IncludesStandardPipeline { get; }
}
/// <summary>Contribution service category.</summary>
public enum GeneratedMediatorCategory
{
    /// <summary>An <c>IRequestHandler</c> registration.</summary>
    RequestHandler,
    /// <summary>An <c>INotificationHandler</c> registration.</summary>
    NotificationHandler,
    /// <summary>An <c>IStreamRequestHandler</c> registration.</summary>
    StreamHandler,
    /// <summary>An <c>IPipelineBehavior</c> registration.</summary>
    Behavior,
    /// <summary>An <c>IStreamPipelineBehavior</c> registration.</summary>
    StreamBehavior,
    /// <summary>A validator registration.</summary>
    Validator,
    /// <summary>An <c>IRequestPreProcessor</c> registration.</summary>
    PreProcessor,
    /// <summary>An <c>IRequestPostProcessor</c> registration.</summary>
    PostProcessor,
    /// <summary>An <c>IRequestExceptionHandler</c> registration.</summary>
    ExceptionHandler,
    /// <summary>An <c>IRequestExceptionAction</c> registration.</summary>
    ExceptionAction,
    /// <summary>Supporting infrastructure required by a generated contribution.</summary>
    Infrastructure,
}
/// <summary>Registration phase; later dispatch stages normalize pipeline ordering.</summary>
public enum GeneratedMediatorPhase
{
    /// <summary>Handler registration.</summary>
    Handler,
    /// <summary>Request pipeline contribution.</summary>
    Request,
    /// <summary>Automatically composed pre/post-processor driver.</summary>
    AutomaticProcessor,
    /// <summary>Closed stream pipeline contribution.</summary>
    ClosedStream,
    /// <summary>Open stream pipeline contribution.</summary>
    OpenStream,
}
/// <summary>Factory lifetime policy.</summary>
public enum GeneratedMediatorLifetime
{
    /// <summary>Uses the configured mediator handler lifetime.</summary>
    HandlerLifetime,
    /// <summary>Always transient.</summary>
    Transient,
    /// <summary>Always singleton.</summary>
    Singleton,
}
/// <summary>Whether a contribution is declared or dormant standard metadata.</summary>
public enum GeneratedMediatorActivation
{
    /// <summary>Explicitly declared by the registry.</summary>
    Declared,
    /// <summary>Dormant metadata that activates only with the standard pipeline.</summary>
    StandardOnly,
}
/// <summary>Immutable exact types and a closed factory; the factory receives the resolving provider.</summary>
public sealed class GeneratedMediatorContribution
{
    /// <summary>Creates immutable contribution metadata.</summary>
    public GeneratedMediatorContribution(Type serviceType, Type implementationType, Type? definitionType,
        GeneratedMediatorCategory category, GeneratedMediatorPhase phase, int order, int declarationOrdinal,
        GeneratedMediatorLifetime lifetime, GeneratedMediatorActivation activation, Func<IServiceProvider, object> factory)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        ArgumentNullException.ThrowIfNull(implementationType);
        ArgumentNullException.ThrowIfNull(factory);
        if (serviceType.ContainsGenericParameters || implementationType.ContainsGenericParameters)
            throw new ArgumentException("Generated contribution service and implementation types must be closed.");
        ServiceType = serviceType; ImplementationType = implementationType; DefinitionType = definitionType;
        Category = category; Phase = phase; Order = order; DeclarationOrdinal = declarationOrdinal;
        Lifetime = lifetime; Activation = activation; Factory = factory;
    }
    /// <summary>Closed service contract.</summary>
    public Type ServiceType { get; }
    /// <summary>Closed implementation identity.</summary>
    public Type ImplementationType { get; }
    /// <summary>Optional original open definition.</summary>
    public Type? DefinitionType { get; }
    /// <summary>Service category.</summary>
    public GeneratedMediatorCategory Category { get; }
    /// <summary>Ordering phase.</summary>
    public GeneratedMediatorPhase Phase { get; }
    /// <summary>Default order.</summary>
    public int Order { get; }
    /// <summary>Declaration occurrence identity.</summary>
    public int DeclarationOrdinal { get; }
    /// <summary>Lifetime policy.</summary>
    public GeneratedMediatorLifetime Lifetime { get; }
    /// <summary>Activation policy.</summary>
    public GeneratedMediatorActivation Activation { get; }
    /// <summary>Closed constructor factory; no service/provider is captured.</summary>
    public Func<IServiceProvider, object> Factory { get; }
}
