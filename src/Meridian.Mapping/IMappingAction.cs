using Meridian.Mapping.Execution;

namespace Meridian.Mapping;

/// <summary>
/// Represents a DI-friendly mapping action that runs before or after member mapping.
/// </summary>
public interface IMappingAction<in TSource, in TDestination>
{
    /// <summary>
    /// Executes the mapping action.
    /// </summary>
    void Process(TSource source, TDestination destination, ResolutionContext context);
}
