using System.Diagnostics;
using Meridian.Mapping;
using Meridian.Mapping.Execution;
using Meridian.Mapping.Configuration;
using Meridian.Mapping.Extensions;
using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Pipeline;
using Microsoft.Extensions.DependencyInjection;

Console.WriteLine($"HOST runtime={Environment.Version} framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
var services = new ServiceCollection();
#if MIGRATED
services.AddMeridianMapping(c => c.AddProfile<ConsumerProfile>());
#else
services.AddAutoMapper(c => c.AddProfile<ConsumerProfile>());
#endif
services.AddMeridianMediator(c =>
{
    c.RegisterServicesFromAssemblyContaining<Ping>();
    c.AddOpenBehavior(typeof(OpenBehavior<,>), order: -10);
    c.AddBehavior<ClosedBehavior>(order: 10);
});
using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
var mapper = scope.ServiceProvider.GetRequiredService<IMapper>();
Check(mapper.Map<Destination>(new Source { Id = 42 }).Id == 42, "mapping profile/DI shim");
foreach (var marker in new[] { true, false })
{
    var aliasServices = new ServiceCollection();
#if MIGRATED
    if (marker) aliasServices.AddMeridianMapping(typeof(ConsumerProfile));
    else aliasServices.AddMeridianMapping(typeof(ConsumerProfile).Assembly);
#else
    if (marker) aliasServices.AddAutoMapper(typeof(ConsumerProfile));
    else aliasServices.AddAutoMapper(typeof(ConsumerProfile).Assembly);
#endif
    using var aliasProvider = aliasServices.BuildServiceProvider();
    Check(aliasProvider.GetRequiredService<IMapper>().Map<Destination>(new Source { Id = 42 }).Id == 42, "assembly/marker DI overload");
}
var config = new MapperConfiguration(c => c.AddProfile<ConsumerProfile>());
IConfigurationProvider custom = new CustomProvider(config);
custom.AssertConfigurationIsValid();
Check(custom.CreateMapper().Map<Destination>(new Source { Id = 42 }).Id == 42, "custom IConfigurationProvider");
Check(custom.DefaultMaxDepth == 64 && custom.DefaultMaxCollectionItems == 10000 && !custom.AllowNullCollections && custom.AllowNullDestinationValues, "mapping defaults");
IMapperConfigurationExpression legacyExpression = new CustomExpression();
Check(legacyExpression.DefaultMaxDepth == 64, "custom configuration expression");
var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
Check(await mediator.Send(new Ping()) == 42, "request dispatch");
await mediator.Send(new VoidRequest());
Check(VoidHandler.Calls == 1, "void dispatch");
Check(Trace.Entries.Contains("open") && Trace.Entries.Contains("closed") && Trace.Entries.Contains("handler"), "mixed pipeline execution");
Console.WriteLine("COMMON runtime profile=42 custom=42 request=42 void=1 mixed=executed defaults=64/10000/false/true");
Console.WriteLine($"OBSERVED mixed-order={string.Join(',', Trace.Entries)}");
var auth = new AuthorizationBehavior<Authorized, int>(Array.Empty<IAuthorizationHandler<Authorized>>());
var denied = false;
try { Check(await auth.Handle(new Authorized(), () => Task.FromResult(42), default) == 42, "authorization continuation"); }
catch (UnauthorizedException) { denied = true; }
Console.WriteLine($"OBSERVED missing-auth-denied={denied.ToString().ToLowerInvariant()}");
var cache = new MemoryCache();
await new CachingBehavior<Cached, int>(cache).Handle(new Cached(), () => Task.FromResult(42), default);
Console.WriteLine($"OBSERVED cache-key={cache.LastKey}");
var store = new MemoryStore();
await new IdempotencyBehavior<Idempotent, int>(store).Handle(new Idempotent(), () => Task.FromResult(42), default);
Console.WriteLine($"OBSERVED idempotency-key={store.LastKey}");
var enumDefaults = new MapperConfiguration(c => c.CreateMap<EnumSource, EnumDto>());
var undefinedAccepted = false;
string? enumError = null;
try { undefinedAccepted = (int)enumDefaults.CreateMapper().Map<EnumDto>(new EnumSource { Value = "99" }).Value == 99; }
catch (Exception ex) when (ex is InvalidCastException || ex.GetType().FullName == "Meridian.Mapping.MeridianEnumMappingException") { enumError = ex.GetType().Name; }
Console.WriteLine($"OBSERVED undefined-enum-accepted={undefinedAccepted.ToString().ToLowerInvariant()} exception={enumError}");
#if BASELINE
Check(!undefinedAccepted && enumError == nameof(InvalidCastException), "published compiled enum rejection");
Check(!denied && cache.LastKey == "raw" && store.LastKey == "raw", "published baseline behavior");
#else
Check(!undefinedAccepted && enumError == "MeridianEnumMappingException", "secure undefined enum default");
Check(Trace.Entries.SequenceEqual(new[] { "open", "closed", "handler", "open" }), "mixed order and deduplication");
Check(denied && cache.LastKey == CacheKeyComposer.Compose<Cached>("raw") && store.LastKey == IdempotencyKeyComposer.Compose<Idempotent>("raw"), "security defaults");
Check(!legacyExpression.AllowUndefinedEnumValues, "legacy expression default getter");
legacyExpression.AllowUndefinedEnumValues = false;
var unsupportedOptIn = false;
try { legacyExpression.AllowUndefinedEnumValues = true; }
catch (NotSupportedException ex) { unsupportedOptIn = ex.Message.Contains("AllowUndefinedEnumValues", StringComparison.Ordinal); }
Check(unsupportedOptIn && !legacyExpression.AllowUndefinedEnumValues, "legacy expression opt-in fails actionably");
IMapperConfigurationExpression builtInExpression = new MapperConfigurationExpression();
builtInExpression.AllowUndefinedEnumValues = true;
Check(builtInExpression.AllowUndefinedEnumValues, "built-in opt-in override");
builtInExpression.AllowUndefinedEnumValues = false;
Check(!builtInExpression.AllowUndefinedEnumValues, "built-in secure override");
Check(!custom.AllowUndefinedEnumValues, "custom provider default interface method");
Check(!MediatorTelemetryOptions.Default.RecordExceptionMessage && !MediatorTelemetryOptions.Default.RecordExceptionStackTrace, "telemetry privacy defaults");
var authOptOut = new AuthorizationBehavior<Authorized, int>(Array.Empty<IAuthorizationHandler<Authorized>>(), new AuthorizationBehaviorOptions { AllowRequestsWithoutAuthorizationHandlers = true });
Check(await authOptOut.Handle(new Authorized(), () => Task.FromResult(42), default) == 42, "authorization compatibility opt-out");
var cacheOptions = new CachingBehaviorOptions { UseRequestTypeNamespacedKeys = false };
await new CachingBehavior<Cached, int>(cache, cacheOptions).Handle(new Cached(), () => Task.FromResult(42), default);
Check(cache.LastKey == "raw", "cache compatibility opt-out");
await new CacheInvalidationBehavior<Invalidating, int>(cache, cacheOptions).Handle(new Invalidating(), () => Task.FromResult(42), default);
Check(cache.RemovedKey == "raw", "raw invalidation compatibility opt-out");
await new IdempotencyBehavior<Idempotent, int>(store, new IdempotencyBehaviorOptions { UseRequestTypeNamespacedKeys = false }).Handle(new Idempotent(), () => Task.FromResult(42), default);
Check(store.LastKey == "raw", "idempotency compatibility opt-out");
var enumConfig = new MapperConfiguration(c => { c.AllowUndefinedEnumValues = true; c.CreateMap<EnumSource, EnumDto>(); });
Check((int)enumConfig.CreateMapper().Map<EnumDto>(new EnumSource { Value = "99" }).Value == 99, "undefined enum compatibility opt-out");
Console.WriteLine("CANDIDATE opt-outs=authorization/cache/invalidation/idempotency/enum privacy=false/false custom-default=false");
#endif
var privacyDefault = await ProbeTelemetry(new MediatorTelemetryOptions());
Check(privacyDefault.GetTagItem("exception.message") is null && privacyDefault.GetTagItem("exception.stacktrace") is null && privacyDefault.StatusDescription is null, "telemetry default privacy");
var fullDetails = await ProbeTelemetry(new MediatorTelemetryOptions { RecordExceptionMessage = true, RecordExceptionStackTrace = true });
Check(Equals(fullDetails.GetTagItem("exception.message"), "private-marker") && ((string?)fullDetails.GetTagItem("exception.stacktrace"))?.Contains("private-marker", StringComparison.Ordinal) == true, "explicit full telemetry opt-in");
var stackOnly = await ProbeTelemetry(new MediatorTelemetryOptions { RecordExceptionStackTrace = true });
var leaksMessage = ((string?)stackOnly.GetTagItem("exception.stacktrace"))?.Contains("private-marker", StringComparison.Ordinal) == true;
#if BASELINE
Check(leaksMessage, "published stack-only privacy behavior");
#else
Check(!leaksMessage && stackOnly.GetTagItem("exception.message") is null, "independent telemetry switches");
#endif
Console.WriteLine("COMMON privacy default=false/false full-opt-in=true/true");
Console.WriteLine($"OBSERVED stack-only-message-present={leaksMessage.ToString().ToLowerInvariant()}");
static async Task<Activity> ProbeTelemetry(MediatorTelemetryOptions options)
{
    Activity? stopped = null;
    using var listener = new ActivityListener
    {
        ShouldListenTo = source => source.Name == "Meridian.Mediator",
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        ActivityStopped = activity => { if (activity.OperationName == "Mediator.Send Failing") stopped = activity; }
    };
    ActivitySource.AddActivityListener(listener);
    var services = new ServiceCollection();
    services.AddSingleton(options);
    services.AddMeridianMediator(_ => { });
    services.AddTransient<IRequestHandler<Failing, int>, FailingHandler>();
    using var provider = services.BuildServiceProvider();
    try { await provider.GetRequiredService<IMediator>().Send(new Failing()); }
    catch (InvalidOperationException ex) when (ex.Message == "private-marker") { }
    return stopped ?? throw new InvalidOperationException("missing telemetry activity");
}
static void Check(bool result, string name) { if (!result) throw new InvalidOperationException(name); }
public sealed class Source { public int Id { get; set; } }
public sealed class Destination { public int Id { get; set; } }
public sealed class ConsumerProfile : Profile { public ConsumerProfile() { CreateMap<Source, Destination>(); } }
// Deliberately implements only the 2.1.3 interface surface. New interface APIs must have defaults.
public sealed class CustomProvider(IConfigurationProvider inner) : IConfigurationProvider
{
    public TypeMap? FindTypeMap(Type sourceType, Type destinationType) => inner.FindTypeMap(sourceType, destinationType);
    public IMapper CreateMapper() => inner.CreateMapper();
    public IMapper CreateMapper(IServiceProvider serviceProvider) => inner.CreateMapper(serviceProvider);
    public void AssertConfigurationIsValid() => inner.AssertConfigurationIsValid();
    public IReadOnlyCollection<TypeMap> GetAllTypeMaps() => inner.GetAllTypeMaps();
    public bool AllowNullCollections => inner.AllowNullCollections;
    public bool AllowNullDestinationValues => inner.AllowNullDestinationValues;
    public int DefaultMaxDepth => inner.DefaultMaxDepth;
    public int DefaultMaxCollectionItems => inner.DefaultMaxCollectionItems;
    public string GetMappingPlan<TSource, TDestination>() => inner.GetMappingPlan<TSource, TDestination>();
    public string GetMappingPlan(Type sourceType, Type destinationType) => inner.GetMappingPlan(sourceType, destinationType);
}
public sealed record Ping : IRequest<int>;
public sealed record VoidRequest : IRequest;
public sealed class PingHandler : IRequestHandler<Ping, int> { public Task<int> Handle(Ping request, CancellationToken ct) { Trace.Entries.Add("handler"); return Task.FromResult(42); } }
public sealed class VoidHandler : IRequestHandler<VoidRequest> { public static int Calls { get; private set; } public Task Handle(VoidRequest request, CancellationToken ct) { Calls++; return Task.CompletedTask; } }
public static class Trace { public static List<string> Entries { get; } = []; }
public sealed class OpenBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken ct) { Trace.Entries.Add("open"); return next(); }
}
public sealed class ClosedBehavior : IPipelineBehavior<Ping, int>
{
    public Task<int> Handle(Ping request, RequestHandlerDelegate<int> next, CancellationToken ct) { Trace.Entries.Add("closed"); return next(); }
}
public sealed record Authorized : IAuthorizedRequest;
public sealed record Cached : ICacheableQuery { public string CacheKey => "raw"; public TimeSpan? CacheDuration => null; }
public sealed record Invalidating : ICacheInvalidatingRequest { public string[] CacheKeysToInvalidate => ["raw"]; }
public sealed record Idempotent : IIdempotentRequest { public string IdempotencyKey => "raw"; }
public sealed class MemoryCache : ICacheProvider
{
    public string? LastKey { get; private set; }
    public string? RemovedKey { get; private set; }
    public Task<(bool Found, object? Value)> GetAsync(string key, CancellationToken ct) { LastKey = key; return Task.FromResult<(bool, object?)>((false, null)); }
    public Task SetAsync(string key, object value, TimeSpan? duration, CancellationToken ct) { LastKey = key; return Task.CompletedTask; }
    public Task RemoveAsync(string key, CancellationToken ct) { RemovedKey = key; return Task.CompletedTask; }
}
public sealed class MemoryStore : IIdempotencyStore
{
    public string? LastKey { get; private set; }
    public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct) { LastKey = key; return Task.FromResult<(bool, object?)>((false, null)); }
    public Task StoreAsync(string key, object response, CancellationToken ct) { LastKey = key; return Task.CompletedTask; }
}
public enum ExampleEnum { Zero }
public sealed class EnumSource { public string Value { get; set; } = ""; }
public sealed class EnumDto { public ExampleEnum Value { get; set; } }

// Published interface source implementation intentionally has no AllowUndefinedEnumValues member.
public sealed class CustomExpression : IMapperConfigurationExpression
{
    private readonly MapperConfigurationExpression inner = new();
    public IMappingExpression<TSource, TDestination> CreateMap<TSource, TDestination>() => inner.CreateMap<TSource, TDestination>();
    public IMappingExpression<TSource, TDestination> CreateProjection<TSource, TDestination>() => inner.CreateProjection<TSource, TDestination>();
    public void CreateMap(Type sourceType, Type destinationType) => inner.CreateMap(sourceType, destinationType);
    public void AddProfile(Profile profile) => inner.AddProfile(profile);
    public void AddProfile<TProfile>() where TProfile : Profile, new() => inner.AddProfile<TProfile>();
    public void AddProfiles(params System.Reflection.Assembly[] assemblies) => inner.AddProfiles(assemblies);
    public void AddMaps(params System.Reflection.Assembly[] assemblies) => inner.AddMaps(assemblies);
    public void AddMaps(params Type[] markerTypes) => inner.AddMaps(markerTypes);
    public void AddMaps(params string[] assemblyNames) => inner.AddMaps(assemblyNames);
    public bool AllowNullCollections { get => inner.AllowNullCollections; set => inner.AllowNullCollections = value; }
    public bool AllowNullDestinationValues { get => inner.AllowNullDestinationValues; set => inner.AllowNullDestinationValues = value; }
    public int DefaultMaxDepth { get => inner.DefaultMaxDepth; set => inner.DefaultMaxDepth = value; }
    public int DefaultMaxCollectionItems { get => inner.DefaultMaxCollectionItems; set => inner.DefaultMaxCollectionItems = value; }
    public ValueTransformerCollection ValueTransformers => inner.ValueTransformers;
}

public sealed record Failing : IRequest<int>;
public sealed class FailingHandler : IRequestHandler<Failing, int>
{
    public Task<int> Handle(Failing request, CancellationToken ct) => throw new InvalidOperationException("private-marker");
}
