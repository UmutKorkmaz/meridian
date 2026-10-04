using Meridian.Mediator.Extensions;
using Meridian.Mediator.Generated;
using Meridian.Mediator.Wrappers;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Generators.Tests;

public class RegistryRegistrationTests
{
    [Fact]
    public void Repeated_Configuration_Deduplicates_And_Uses_Declared_Lifetime()
    {
        var config = new MeridianMediatorConfiguration();
        Assert.False(config.RequireGeneratedDispatch);
        Assert.Same(config, config.AddGeneratedRegistry<Registry>());
        var services = new ServiceCollection();
        services.AddMeridianMediator(c => { c.Lifetime = ServiceLifetime.Scoped; c.AddGeneratedRegistry<Registry>().AddGeneratedRegistry<Registry>(); });
        services.AddMeridianMediator(c => { c.Lifetime = ServiceLifetime.Scoped; c.AddGeneratedRegistry<Registry>(); });
        Assert.Single(services, d => d.ServiceType == typeof(IRequestHandler<Request, int>));
        Assert.Equal(ServiceLifetime.Scoped, services.Single(d => d.ServiceType == typeof(IRequestHandler<Request, int>)).Lifetime);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        Assert.Single(provider.GetServices<IGeneratedMediatorRegistry>());
        using var scope = provider.CreateScope();
        Assert.Same(scope.ServiceProvider.GetRequiredService<IRequestHandler<Request, int>>(), scope.ServiceProvider.GetRequiredService<IRequestHandler<Request, int>>());
    }
    [Fact]
    public void Strict_And_Standard_Capabilities_Fail_Explicitly_Without_Mutating_Services()
    {
        var services = new ServiceCollection();
        Assert.Throws<NotSupportedException>(() => services.AddMeridianMediator(c => { c.RequireGeneratedDispatch = true; c.AddGeneratedRegistry<Registry>(); }));
        Assert.Empty(services);
        Assert.Throws<NotSupportedException>(() => services.AddMeridianStandard<Registry>());
        Assert.Throws<NotSupportedException>(() => services.AddMeridianMediator(c => c.AddGeneratedRegistry<StandardRegistry>()));
        Assert.Empty(services);
    }
    [Fact]
    public void Repeated_Conflicting_Lifetime_And_Id_Collisions_Fail()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(c => c.AddGeneratedRegistry<Registry>());
        Assert.Throws<InvalidOperationException>(() => services.AddMeridianMediator(c => { c.Lifetime = ServiceLifetime.Scoped; c.AddGeneratedRegistry<Registry>(); }));
        Assert.Throws<InvalidOperationException>(() => services.AddMeridianMediator(c => c.AddGeneratedRegistry<CollisionRegistry>()));
        Assert.Throws<InvalidOperationException>(() => services.AddMeridianMediator(c => c.AddGeneratedRegistry<DuplicateRegistry>()));
    }
    [Fact]
    public void Provider_Built_Before_Collection_Edit_Retains_Own_Metadata()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(c => c.AddGeneratedRegistry<Registry>());
        using var before = services.BuildServiceProvider();
        services.AddMeridianMediator(c => c.AddGeneratedRegistry<EmptyRegistry>());
        using var after = services.BuildServiceProvider();
        Assert.Single(before.GetServices<IGeneratedMediatorRegistry>());
        Assert.Equal(2, after.GetServices<IGeneratedMediatorRegistry>().Count());
    }
    [Fact]
    public async Task Legacy_Dispatch_Remains_Operational_After_Generated_Di_Registration()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(c => c.AddGeneratedRegistry<Registry>());
        using var provider = services.BuildServiceProvider();
        Assert.Equal(42, await provider.GetRequiredService<IMediator>().Send(new Request()));
    }
    public sealed class Request : IRequest<int> { }
    public sealed class Handler : IRequestHandler<Request, int> { public Task<int> Handle(Request request, CancellationToken cancellationToken) => Task.FromResult(42); }
    public class Registry : IGeneratedMediatorRegistry
    {
        public virtual string RegistryId => "manual";
        public virtual IReadOnlyCollection<Type> RequestTypes => new[] { typeof(Request) };
        public IReadOnlyCollection<Type> NotificationTypes => Array.Empty<Type>();
        public IReadOnlyCollection<Type> StreamTypes => Array.Empty<Type>();
        public virtual void RegisterServices(IServiceCollection services, ServiceLifetime handlerLifetime) => GeneratedMediatorRegistration.RegisterServices(services, new[] { new GeneratedMediatorContribution(typeof(IRequestHandler<Request, int>), typeof(Handler), null, GeneratedMediatorCategory.RequestHandler, GeneratedMediatorPhase.Handler, 0, 0, GeneratedMediatorLifetime.HandlerLifetime, GeneratedMediatorActivation.Declared, static _ => new Handler()) }, handlerLifetime);
        public RequestHandlerBase? FindRequestHandler(Type requestType) => null;
        public NotificationHandlerWrapperBase? FindNotificationHandler(Type notificationType) => null;
        public StreamRequestHandlerBase? FindStreamHandler(Type requestType) => null;
    }
    public sealed class CollisionRegistry : Registry { }
    public sealed class DuplicateRegistry : Registry { public override string RegistryId => "duplicate"; }
    public sealed class EmptyRegistry : Registry
    {
        public override string RegistryId => "empty";
        public override IReadOnlyCollection<Type> RequestTypes => Array.Empty<Type>();
        public override void RegisterServices(IServiceCollection services, ServiceLifetime handlerLifetime) { }
    }
    public sealed class StandardRegistry : Registry, IGeneratedMediatorContributions
    {
        public IReadOnlyList<GeneratedMediatorContribution> Contributions => Array.Empty<GeneratedMediatorContribution>();
        public bool IncludesStandardPipeline => true;
    }
}
