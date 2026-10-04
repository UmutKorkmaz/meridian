using Meridian.Mediator;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Generated;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;

Console.WriteLine($"HOST runtime={Environment.Version} framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
var services = new ServiceCollection();
services.AddScoped<Dependency>();
services.AddMeridianMediator(c => { c.Lifetime = ServiceLifetime.Scoped; c.AddGeneratedRegistry<AppRegistry>().AddGeneratedRegistry<AppRegistry>(); });
services.AddMeridianMediator(c => { c.Lifetime = ServiceLifetime.Scoped; c.AddGeneratedRegistry<AppRegistry>(); });
using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
Handler first;
using (var scope = provider.CreateScope())
{
    first = (Handler)scope.ServiceProvider.GetRequiredService<IRequestHandler<Query, int>>();
    if (!ReferenceEquals(first, scope.ServiceProvider.GetRequiredService<IRequestHandler<Query, int>>())) throw new Exception("scoped identity");
    if (await first.Handle(new Query(), default) != 42) throw new Exception("direct handler output");
    if (scope.ServiceProvider.GetServices<IRequestHandler<Query, int>>().Count() != 1) throw new Exception("duplicate descriptor");
    var command = scope.ServiceProvider.GetRequiredService<IRequestHandler<Command>>(); await command.Handle(new Command(), default);
    var generic = scope.ServiceProvider.GetRequiredService<IRequestHandler<GenericQuery<string>, string>>();
    if (await generic.Handle(new GenericQuery<string>("closed"), default) != "closed") throw new Exception("closed generic");
}
if (!first.Disposed) throw new Exception("scope disposal");
using (var scope = provider.CreateScope())
    if (ReferenceEquals(first, scope.ServiceProvider.GetRequiredService<IRequestHandler<Query, int>>())) throw new Exception("scope isolation");
var registry = new AppRegistry();
if (registry.RequestTypes.Count != 3 || registry.Contributions.Count != 3 || registry.FindRequestHandler(typeof(Query)) != null) throw new Exception("D1 metadata capability");
try { new ServiceCollection().AddMeridianMediator(c => { c.RequireGeneratedDispatch = true; c.AddGeneratedRegistry<AppRegistry>(); }); throw new Exception("strict was silently accepted"); }
catch (NotSupportedException) { }
Console.WriteLine("COMMON generated-registry=direct-di scoped=true deduplicated=true disposed=true closed-generic=true metadata-only=true");
foreach (var assembly in new[] { typeof(IMediator).Assembly, typeof(AppRegistry).Assembly })
    Console.WriteLine($"BINARY {assembly.GetName().Name} path={assembly.Location} sha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant()}");

[GenerateMediatorRegistry(typeof(Handler), typeof(CommandHandler), typeof(GenericHandler<string>), ExpectedRequestTypes = new[] { typeof(Query), typeof(Command), typeof(GenericQuery<string>) })]
public partial class AppRegistry { }
public sealed class Query : IRequest<int> { }
public sealed class Dependency { public int Value => 42; }
public sealed class Handler(Dependency dependency) : IRequestHandler<Query, int>, IDisposable
{
    public bool Disposed { get; private set; }
    public Task<int> Handle(Query request, CancellationToken cancellationToken) => Task.FromResult(dependency.Value);
    public void Dispose() => Disposed = true;
}
public sealed class Command : IRequest { }
public sealed class CommandHandler : IRequestHandler<Command> { public Task Handle(Command request, CancellationToken cancellationToken) => Task.CompletedTask; }
public sealed record GenericQuery<T>(T Value) : IRequest<T>;
public sealed class GenericHandler<T> : IRequestHandler<GenericQuery<T>, T> { public Task<T> Handle(GenericQuery<T> request, CancellationToken cancellationToken) => Task.FromResult(request.Value); }
