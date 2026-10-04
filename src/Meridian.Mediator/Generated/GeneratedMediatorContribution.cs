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
public enum GeneratedMediatorCategory { RequestHandler, NotificationHandler, StreamHandler, Behavior, StreamBehavior, Validator, PreProcessor, PostProcessor, ExceptionHandler, ExceptionAction, Infrastructure }
/// <summary>Registration phase; later dispatch stages normalize pipeline ordering.</summary>
public enum GeneratedMediatorPhase { Handler, Request, AutomaticProcessor, ClosedStream, OpenStream }
/// <summary>Factory lifetime policy.</summary>
public enum GeneratedMediatorLifetime { HandlerLifetime, Transient, Singleton }
/// <summary>Whether a contribution is declared or dormant standard metadata.</summary>
public enum GeneratedMediatorActivation { Declared, StandardOnly }
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
