namespace Meridian.Mediator.Caching;

/// <summary>Application-supplied authenticated partition, immutable for its DI scope. Personalized data requires tenant and user (and relevant authorization) dimensions.</summary>
public interface ICacheKeyScope
{
    /// <summary>Returns a nonempty authenticated partition; missing or changing identity fails before backend access.</summary>
    string GetScope();
}
