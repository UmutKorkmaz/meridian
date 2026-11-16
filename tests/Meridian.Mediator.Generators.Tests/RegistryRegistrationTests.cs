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
    public void Manual_Null_Request_Wrapper_Fails_Strict_And_Missing_Standard_Metadata_Fails_Explicitly()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddMeridianMediator(c => { c.RequireGeneratedDispatch = true; c.AddGeneratedRegistry<Registry>(); }));
        Assert.Empty(services);
        Assert.Throws<NotSupportedException>(() => services.AddMeridianStandard<Registry>());
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
    [Fact]
    public async Task Complete_Manual_Registry_Works_In_Strict_Mode()
    {
        var services=new ServiceCollection();
        services.AddMeridianMediator(c=>{c.AddGeneratedRegistry<CompleteRegistry>();c.RequireGeneratedDispatch=true;});
        using var provider=services.BuildServiceProvider();
        Assert.Equal(42,await provider.GetRequiredService<IMediator>().Send(new Request()));
    }
    [Fact]
    public void Contradictory_Applicability_Metadata_Is_Rejected()
    {
        Assert.Throws<InvalidOperationException>(()=>new ServiceCollection().AddMeridianMediator(c=>{c.AddGeneratedRegistry<ContradictoryRegistry>();c.RequireGeneratedDispatch=true;}));
    }
    public class CompleteRegistry : Registry
    {
        public override RequestHandlerBase? FindRequestHandler(Type requestType)=>requestType==typeof(Request) ? new RequestHandlerWrapperImpl<Request,int>() : null;
    }
    public sealed class ContradictoryRegistry : CompleteRegistry,IGeneratedMediatorBehaviorCapabilities
    {
        public IReadOnlyList<GeneratedMediatorBehaviorCapability> BehaviorCapabilities {get;}=Array.AsReadOnly(new[]{
            new GeneratedMediatorBehaviorCapability(typeof(Request),typeof(int),typeof(Meridian.Mediator.Behaviors.CorrelationIdBehavior<,>),GeneratedMediatorCategory.Behavior,true),
            new GeneratedMediatorBehaviorCapability(typeof(Request),typeof(int),typeof(Meridian.Mediator.Behaviors.CorrelationIdBehavior<,>),GeneratedMediatorCategory.Behavior,false)});
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
        public virtual RequestHandlerBase? FindRequestHandler(Type requestType) => null;
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
