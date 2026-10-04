using Meridian.Mediator;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Generated;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Pipeline;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;

Console.WriteLine($"HOST runtime={Environment.Version} framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
var services = new ServiceCollection();
services.AddScoped<Dependency>();
services.AddSingleton<Trace>();
services.AddSingleton<IAuditSink,AuditSink>();
services.AddSingleton(typeof(IStringLocalizer<>),typeof(Localizer<>));
services.AddMeridianMediator(c => { c.Lifetime = ServiceLifetime.Scoped; c.RequireGeneratedDispatch=true; c.AddGeneratedRegistry<AppRegistry>().AddGeneratedRegistry<AppRegistry>(); });
services.AddMeridianMediator(c => { c.Lifetime = ServiceLifetime.Scoped; c.RequireGeneratedDispatch=true; c.AddGeneratedRegistry<AppRegistry>(); });
using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
Handler first;
using (var scope = provider.CreateScope())
{
    first = (Handler)scope.ServiceProvider.GetRequiredService<IRequestHandler<Query, int>>();
    if (!ReferenceEquals(first, scope.ServiceProvider.GetRequiredService<IRequestHandler<Query, int>>())) throw new Exception("scoped identity");
    if (await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new Query()) != 42) throw new Exception("direct handler output");
    if (scope.ServiceProvider.GetServices<IRequestHandler<Query, int>>().Count() != 1) throw new Exception("duplicate descriptor");
    var mediator=scope.ServiceProvider.GetRequiredService<IMediator>();
    using var token=new CancellationTokenSource();
    await mediator.Send(new Command(),token.Token);
    if (!Equals(Unit.Value,await mediator.Send((object)new UnitCommand(),token.Token))) throw new Exception("unit object dispatch");
    if (scope.ServiceProvider.GetRequiredService<Trace>().Token != token.Token) throw new Exception("token identity");
    var generic = scope.ServiceProvider.GetRequiredService<IRequestHandler<GenericQuery<string>, string>>();
    if (await mediator.Send(new GenericQuery<string>("closed")) != "closed") throw new Exception("closed generic");
}
if (!first.Disposed) throw new Exception("scope disposal");
using (var scope = provider.CreateScope())
    if (ReferenceEquals(first, scope.ServiceProvider.GetRequiredService<IRequestHandler<Query, int>>())) throw new Exception("scope isolation");
var registry = new AppRegistry();
if (registry.RequestTypes.Count != 4 || registry.FindRequestHandler(typeof(Query)) == null) throw new Exception("generated request capability");
var trace=provider.GetRequiredService<Trace>();
if(trace.Validations!=1 || trace.Audits!=4 || trace.Behaviors!=4) throw new Exception("standard/behavior duplication");
Console.WriteLine("COMMON generated-registry=request-dispatch scoped=true deduplicated=true disposed=true closed-generic=true standard=true void-forms=true token=true");
foreach (var assembly in new[] { typeof(IMediator).Assembly, typeof(AppRegistry).Assembly })
    Console.WriteLine($"BINARY {assembly.GetName().Name} path={assembly.Location} sha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant()}");

[GenerateMediatorRegistry(typeof(Handler), typeof(CommandHandler), typeof(GenericHandler<string>),typeof(UnitCommandHandler),BehaviorTypes=new[]{typeof(PassBehavior<,>)},ValidatorTypes=new[]{typeof(QueryValidator)},IncludeStandardPipeline=true, ExpectedRequestTypes = new[] { typeof(Query), typeof(Command), typeof(GenericQuery<string>) })]
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

public sealed class UnitCommand : IRequest;
public sealed class UnitCommandHandler(Trace trace) : IRequestHandler<UnitCommand,Unit> {public Task<Unit> Handle(UnitCommand request,CancellationToken cancellationToken) {trace.Token=cancellationToken;return Task.FromResult(Unit.Value);}}
public sealed class Trace { public int Validations {get;set;} public int Audits {get;set;} public int Behaviors {get;set;} public CancellationToken Token {get;set;} }
public sealed class QueryValidator(Trace trace) : IValidator<Query> {public Task<ValidationResult> ValidateAsync(Query instance,CancellationToken cancellationToken) {trace.Validations++;return Task.FromResult(new ValidationResult());}}
public sealed class PassBehavior<TRequest,TResponse>(Trace trace) : IPipelineBehavior<TRequest,TResponse> where TRequest:notnull {public Task<TResponse> Handle(TRequest request,RequestHandlerDelegate<TResponse> next,CancellationToken cancellationToken) {trace.Behaviors++;return next();}}
public sealed class AuditSink(Trace trace) : IAuditSink {public Task RecordAsync(AuditEntry entry,CancellationToken cancellationToken) {if(string.IsNullOrEmpty(entry.CorrelationId)) throw new Exception("correlation");trace.Audits++;return Task.CompletedTask;}}
public sealed class Localizer<T> : IStringLocalizer<T> {public LocalizedString this[string name]=>new(name,name,true);public LocalizedString this[string name,params object[] arguments]=>new(name,name,true);public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)=>Array.Empty<LocalizedString>();}
