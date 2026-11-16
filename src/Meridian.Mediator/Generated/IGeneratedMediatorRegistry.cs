using Meridian.Mediator.Wrappers;
using Microsoft.Extensions.DependencyInjection;
namespace Meridian.Mediator.Generated;
/// <summary>Closed generated type metadata and registration capability. Contains no resolved services.</summary>
public interface IGeneratedMediatorRegistry
{
    /// <summary>Stable registry identity.</summary>
    string RegistryId { get; }
    /// <summary>Declared ordinary requests.</summary>
    IReadOnlyCollection<Type> RequestTypes { get; }
    /// <summary>Declared notifications.</summary>
    IReadOnlyCollection<Type> NotificationTypes { get; }
    /// <summary>Declared stream requests.</summary>
    IReadOnlyCollection<Type> StreamTypes { get; }
    /// <summary>Installs closed factories using the supplied handler lifetime.</summary>
    void RegisterServices(IServiceCollection services, ServiceLifetime handlerLifetime);
    /// <summary>Returns a generated request wrapper when this capability is available.</summary>
    RequestHandlerBase? FindRequestHandler(Type requestType);
    /// <summary>Returns a generated notification wrapper when this capability is available.</summary>
    NotificationHandlerWrapperBase? FindNotificationHandler(Type notificationType);
    /// <summary>Returns a generated stream wrapper when this capability is available.</summary>
    StreamRequestHandlerBase? FindStreamHandler(Type requestType);
}
