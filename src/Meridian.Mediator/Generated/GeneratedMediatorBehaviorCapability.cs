namespace Meridian.Mediator.Generated;

/// <summary>Finite compile-time evidence for an open behavior over one known request.</summary>
public sealed record GeneratedMediatorBehaviorCapability(Type RequestType, Type ResponseType, Type DefinitionType, GeneratedMediatorCategory Category, bool Applies);

/// <summary>Optional constraint applicability metadata. Missing evidence is never treated as a constraint mismatch.</summary>
public interface IGeneratedMediatorBehaviorCapabilities
{
    /// <summary>Immutable applicability decisions made for the registry's finite request set.</summary>
    IReadOnlyList<GeneratedMediatorBehaviorCapability> BehaviorCapabilities { get; }
}
