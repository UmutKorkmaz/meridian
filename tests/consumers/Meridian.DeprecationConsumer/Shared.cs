using System.Reflection;
using System.Runtime.CompilerServices;
using Meridian.Mapping;
using Meridian.Mapping.Converters;
using Meridian.Mapping.Execution;
using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Caching;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Pipeline;
using Meridian.Mediator.Streaming;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

// Every COMMON line names one fixture of docs/deprecations.md and carries only intended, variant-independent values; the
// old (legacy) and new (migrated) builds must print the same set. Variant-specific facts are printed as OBSERVED lines.
Console.WriteLine($"HOST runtime={Environment.Version} framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"OBSERVED variant={Variant.Name}");
Console.WriteLine(Alias("alias-assembly-array", s => Variant.RegisterAssemblies(s, new[] { typeof(OrderProfile).Assembly }), scanned: true));
Console.WriteLine(Alias("alias-type-array", s => Variant.RegisterMarkers(s, new[] { typeof(OrderProfile), typeof(OrderSummaryConverter) }), scanned: true));
Console.WriteLine(Alias("alias-action", s => Variant.RegisterConfiguration(s, c => c.AddProfile<OrderProfile>()), scanned: false));
Console.WriteLine(GeneratedLegacyDefault());
Console.WriteLine(GeneratedStructuralOptIn());
Console.WriteLine(await CacheRawAndComposed());
Console.WriteLine(await CacheScopeSchema());
Console.WriteLine(await IdempotencyBaseAndAtomic());
Console.WriteLine(await RetainedDynamicRegistration());

static string Alias(string fixture, Action<IServiceCollection> register, bool scanned)
{
    var services = new ServiceCollection();
    services.AddSingleton(new ConverterStamp("di"));
    register(services);
    Require(services.Single(d => d.ServiceType == typeof(IConfigurationProvider)).Lifetime == ServiceLifetime.Singleton, $"{fixture}: configuration singleton");
    Require(services.Single(d => d.ServiceType == typeof(IMapper)).Lifetime == ServiceLifetime.Scoped, $"{fixture}: mapper scoped");
    var converters = services.Where(d => d.ServiceType == typeof(OrderSummaryConverter) || d.ServiceType == typeof(ITypeConverter<Order, OrderSummary>)).ToArray();
    // Two descriptors (concrete and interface) per scanned assembly: the marker overload deduplicates its assemblies,
    // and the Action overload scans nothing, so its converter must be registered by the application.
    Require(converters.Length == (scanned ? 2 : 0) && converters.All(d => d.Lifetime == ServiceLifetime.Transient), $"{fixture}: converter scanning");
    if (!scanned) services.AddTransient<OrderSummaryConverter>();
    using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    using var first = provider.CreateScope();
    using var second = provider.CreateScope();
    var mapper = first.ServiceProvider.GetRequiredService<IMapper>();
    Require(ReferenceEquals(mapper, first.ServiceProvider.GetRequiredService<IMapper>()) && !ReferenceEquals(mapper, second.ServiceProvider.GetRequiredService<IMapper>()), $"{fixture}: scoped mapper instances");
    Require(ReferenceEquals(first.ServiceProvider.GetRequiredService<IConfigurationProvider>(), second.ServiceProvider.GetRequiredService<IConfigurationProvider>()), $"{fixture}: shared configuration");
    Require(!ReferenceEquals(first.ServiceProvider.GetRequiredService<OrderSummaryConverter>(), first.ServiceProvider.GetRequiredService<OrderSummaryConverter>()), $"{fixture}: transient converter");
    var dto = mapper.Map<OrderDto>(new Order { Id = 42, Code = 7 });
    // The converter has no parameterless constructor, so this value proves it was resolved from the container.
    var summary = mapper.Map<OrderSummary>(new Order { Id = 42, Code = 7 });
    Require(dto.Id == 42 && dto.Code == 7 && summary.Text == "order-42@di", $"{fixture}: profile and converter mapping");
    return $"COMMON fixture={fixture} configuration=singleton mapper=scoped converters={(scanned ? "scanned-transient" : "application-registered")} converterDescriptors={converters.Length} profile={dto.Id}/{dto.Code} converter={summary.Text}";
}

static string GeneratedLegacyDefault()
{
    var child = new GenChild { Value = 7 };
    var dto = Variant.MapLegacyDefault(new GenSource { Value = 42, Child = child, Name = "Ada" })
        ?? throw new InvalidOperationException("legacy generated mapping returned null for a non-null source");
    var options = new GenerateMapperAttribute(typeof(GenSource), typeof(GenDto));
    Require(options.Mode == GeneratedMappingMode.Legacy, "Legacy stays the generated default mode");
    Require(dto.Value == 42 && ReferenceEquals(dto.Child, child), "legacy generated value and shallow reference");
    Require(dto.Note == "kept", "legacy skipped destination member keeps its initializer");
    Require(Variant.MapLegacyDefault(null!) is null, "legacy null return");
    return $"COMMON fixture=generated-legacy-default value={dto.Value} shallow=true skippedMember={dto.Note} null=null defaultMode={options.Mode}";
}

static string GeneratedStructuralOptIn()
{
    var dto = Variant.MapFlat(new GenFlatSource { Value = 42, Name = "Ada" });
    Require(dto.Value == 42 && dto.Name == "Ada" && dto.Note == "kept", "flat generated mapping");
    Console.WriteLine($"OBSERVED generated-flat-mode={Variant.FlatMode}");
    return $"COMMON fixture=generated-structural-opt-in value={dto.Value} name={dto.Name} unmappedMember={dto.Note}";
}

static async Task<string> CacheRawAndComposed()
{
    using var memory = new MemoryCache(new MemoryCacheOptions());
    var provider = new MemoryCacheProvider(memory);
    var options = Variant.CacheOptions();
    var caching = new CachingBehavior<OrderQuery, string>(provider, options);
    var invalidation = new CacheInvalidationBehavior<InvalidateOrder, int>(provider, options);
    var calls = 0;
    Task<string> Handler() { calls++; return Task.FromResult($"order-42#{calls}"); }
    var query = new OrderQuery(42);
    var key = Variant.ProviderKey(query);
    var first = await caching.Handle(query, Handler, default);
    var warm = await caching.Handle(query, Handler, default);
    Require(first == "order-42#1" && warm == first && calls == 1, "cache warm hit");
    Require(((IMemoryCache)memory).TryGetValue(key, out _), "cache entry under the variant key");
    Require(await invalidation.Handle(new InvalidateOrder([key]), () => Task.FromResult(1), default) == 1, "invalidation handler");
    Require(!((IMemoryCache)memory).TryGetValue(key, out _), "cache entry invalidated");
    var refreshed = await caching.Handle(query, Handler, default);
    Require(refreshed == "order-42#2" && calls == 2, "miss after invalidation");
    // Retained invariant for both variants: with the default options a raw invalidation key fails before the handler.
    var strictCalls = 0;
    try
    {
        await new CacheInvalidationBehavior<InvalidateOrder, int>(provider).Handle(new InvalidateOrder([query.CacheKey]), () => { strictCalls++; return Task.FromResult(1); }, default);
        throw new InvalidOperationException("Expected the default options to reject a raw invalidation key");
    }
    catch (InvalidOperationException error) when (error.Message.StartsWith("Cache invalidation key 'orders:42'", StringComparison.Ordinal)) { }
    Require(strictCalls == 0, "raw key rejected before the handler");
    Console.WriteLine($"OBSERVED cache-provider-key={key}");
    return $"COMMON fixture=cache-raw-and-composed first={first} warm=hit invalidated=true refreshed={refreshed} handlerCalls={calls} defaultRejectsRawBeforeHandler=true";
}

static async Task<string> CacheScopeSchema()
{
    using var memory = new MemoryCache(new MemoryCacheOptions());
    memory.Set("application-entry", 1);
    var services = new ServiceCollection();
    services.AddSingleton<IMemoryCache>(memory);
    services.AddSingleton<CallCounter>();
    services.AddScoped<TenantContext>();
    services.AddSingleton(Variant.CacheOptions());
    services.AddTransient<IRequestHandler<TenantOrderQuery, string>, TenantOrderHandler>();
    services.AddMeridianMemoryCaching();
    Variant.RegisterCacheScope(services);
    services.AddMeridianMediator(c => c.AddCachingBehavior());
    using var di = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    async Task<string> SendAs(string tenant)
    {
        using var scope = di.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Tenant = tenant;
        return await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new TenantOrderQuery(tenant, 42));
    }
    var a = await SendAs("tenant-a");
    var b = await SendAs("tenant-b");
    var warm = await SendAs("tenant-a");
    var calls = di.GetRequiredService<CallCounter>().Calls;
    Require(a == "tenant-a:order-42#1" && b == "tenant-b:order-42#2" && warm == a && calls == 2, "tenant isolation and warm hit");
    Require(((IMemoryCache)memory).TryGetValue(Variant.TenantProviderKey("tenant-a", 42), out _) && ((IMemoryCache)memory).TryGetValue(Variant.TenantProviderKey("tenant-b", 42), out _), "tenant entries under the variant keys");
    Require(memory.Get<int>("application-entry") == 1, "existing provider data is not rewritten or flushed");
    Console.WriteLine($"OBSERVED cache-tenant-key={Variant.TenantProviderKey("tenant-a", 42)}");
    return $"COMMON fixture=cache-scope-schema tenants=2 isolated=true values={a},{b} warm=hit handlerCalls={calls} existingData=preserved";
}

static async Task<string> IdempotencyBaseAndAtomic()
{
    Require(typeof(IIdempotencyStore).IsAssignableFrom(typeof(IAtomicIdempotencyStore)) && typeof(IIdempotencyStore).IsAssignableFrom(typeof(ILeasedIdempotencyStore)), "richer stores inherit IIdempotencyStore");
    IIdempotencyStore store = Variant.CreateIdempotencyStore();
    var behavior = new IdempotencyBehavior<CreateOrder, OrderReceipt?>(store);
    var calls = 0;
    Task<OrderReceipt?> Execute() { calls++; return Task.FromResult<OrderReceipt?>(new OrderReceipt(100 + calls)); }
    var first = await behavior.Handle(new CreateOrder("order-1"), Execute, default);
    var replay = await behavior.Handle(new CreateOrder("order-1"), Execute, default);
    var other = await behavior.Handle(new CreateOrder("order-2"), Execute, default);
    Require(first?.Number == 101 && replay?.Number == 101 && other?.Number == 102 && calls == 2, "sequential replay");

    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var concurrentCalls = 0;
    async Task<OrderReceipt?> Slow() { concurrentCalls++; await gate.Task; return new OrderReceipt(500); }
    var owner = behavior.Handle(new CreateOrder("order-3"), Slow, default);
    var duplicate = behavior.Handle(new CreateOrder("order-3"), Slow, default);
    gate.SetResult();
    string concurrent;
    try { await duplicate; concurrent = "executed"; }
    catch (IdempotencyInProgressException) { concurrent = "rejected-in-flight"; }
    Require((await owner)?.Number == 500, "concurrent owner completes");
    Require(concurrent == Variant.ExpectedConcurrentDuplicate && concurrentCalls == (concurrent == "executed" ? 2 : 1), "concurrent duplicate behavior of the selected store");

    var nullCalls = 0;
    Task<OrderReceipt?> Null() { nullCalls++; return Task.FromResult<OrderReceipt?>(null); }
    await behavior.Handle(new CreateOrder("order-4"), Null, default);
    await behavior.Handle(new CreateOrder("order-4"), Null, default);
    Require(nullCalls == Variant.ExpectedNullExecutions, "successful null response handling of the selected store");
    Console.WriteLine($"OBSERVED idempotency-store={store.GetType().Name} concurrent-duplicate={concurrent} null-executions={nullCalls}");
    return $"COMMON fixture=idempotency-base-and-atomic first={first!.Number} replay={replay!.Number} other={other!.Number} handlerCalls={calls} baseInterface=retained";
}

static async Task<string> RetainedDynamicRegistration()
{
    // Retained and supported in both variants: scanning plus closed registrations of behaviors the scanner cannot see.
    var services = new ServiceCollection();
    services.AddMeridianMediator(c =>
    {
        c.RegisterServicesFromAssembly(typeof(Ping).Assembly);
        c.AddClosedBehavior(typeof(IPipelineBehavior<Ping, int>), typeof(AddOne<Ping>));
        c.AddClosedStreamBehavior(typeof(IStreamPipelineBehavior<CountTo, int>), typeof(Doubling<CountTo>));
    });
    using var di = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    using var scope = di.CreateScope();
    var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
    var response = await mediator.Send(new Ping(42));
    var stream = new List<int>();
    await foreach (var item in mediator.CreateStream(new CountTo(3))) stream.Add(item);
    Require(response == 43 && stream.SequenceEqual(new[] { 2, 4, 6 }), "dynamic registrations execute");
    return $"COMMON fixture=retained-dynamic-registration request={response} stream={string.Join(',', stream)} dynamicApis=supported";
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

/// <summary>The API surface each variant implements: OldApi.cs with the retained legacy calls, NewApi.cs with the replacements.</summary>
internal static partial class Variant
{
    internal static partial void RegisterAssemblies(IServiceCollection services, Assembly[] assemblies);
    internal static partial void RegisterMarkers(IServiceCollection services, Type[] markerTypes);
    internal static partial void RegisterConfiguration(IServiceCollection services, Action<IMapperConfigurationExpression> configure);
    internal static partial GenDto? MapLegacyDefault(GenSource source);
    internal static partial GenFlatDto MapFlat(GenFlatSource source);
    internal static partial CachingBehaviorOptions CacheOptions();
    internal static partial string ProviderKey(OrderQuery query);
    internal static partial string TenantCacheKey(string tenant, int id);
    internal static partial string TenantProviderKey(string tenant, int id);
    internal static partial void RegisterCacheScope(IServiceCollection services);
    internal static partial IIdempotencyStore CreateIdempotencyStore();
}

public sealed class Order { public int Id { get; set; } public int Code { get; set; } }
public sealed class OrderDto { public int Id { get; set; } public int Code { get; set; } }
public sealed class OrderSummary { public string Text { get; init; } = ""; }
public sealed record ConverterStamp(string Source);
public sealed class OrderSummaryConverter(ConverterStamp stamp) : ITypeConverter<Order, OrderSummary>
{
    public OrderSummary Convert(Order source, OrderSummary destination, ResolutionContext context) => new() { Text = $"order-{source.Id}@{stamp.Source}" };
}
public sealed class OrderProfile : Profile
{
    public OrderProfile()
    {
        CreateMap<Order, OrderDto>();
        CreateMap<Order, OrderSummary>().ConvertUsing<OrderSummaryConverter>();
    }
}

public sealed class GenChild { public int Value { get; set; } }
public sealed class GenSource { public int Value { get; set; } public GenChild? Child { get; set; } public string Name { get; set; } = ""; }
public sealed class GenDto { public int Value { get; set; } public GenChild? Child { get; set; } public string Note { get; set; } = "kept"; }
public sealed class GenFlatSource { public int Value { get; set; } public string Name { get; set; } = ""; }
public sealed class GenFlatDto { public int Value { get; set; } public string Name { get; set; } = ""; public string Note { get; set; } = "kept"; }

public sealed record OrderQuery(int Id) : IRequest<string>, ICacheableQuery
{
    public string CacheKey => $"orders:{Id}";
    public TimeSpan? CacheDuration => null;
}
public sealed record InvalidateOrder(string[] CacheKeysToInvalidate) : IRequest<int>, ICacheInvalidatingRequest;
public sealed record TenantOrderQuery(string Tenant, int Id) : IRequest<string>, ICacheableQuery
{
    public string CacheKey => Variant.TenantCacheKey(Tenant, Id);
    public TimeSpan? CacheDuration => null;
}
public sealed class TenantContext { public string Tenant { get; set; } = "unset"; }
public sealed class TenantScope(TenantContext context) : ICacheKeyScope { public string GetScope() => context.Tenant; }
public sealed class CallCounter { public int Calls { get; set; } }
public sealed class TenantOrderHandler(TenantContext context, CallCounter counter) : IRequestHandler<TenantOrderQuery, string>
{
    public Task<string> Handle(TenantOrderQuery request, CancellationToken cancellationToken)
    {
        counter.Calls++;
        return Task.FromResult($"{context.Tenant}:order-{request.Id}#{counter.Calls}");
    }
}

public sealed record CreateOrder(string IdempotencyKey) : IRequest<OrderReceipt?>, IIdempotentRequest;
public sealed record OrderReceipt(int Number);

public sealed record Ping(int Value) : IRequest<int>;
public sealed class PingHandler : IRequestHandler<Ping, int>
{
    public Task<int> Handle(Ping request, CancellationToken cancellationToken) => Task.FromResult(request.Value);
}
public sealed class AddOne<TRequest> : IPipelineBehavior<TRequest, int> where TRequest : IRequest<int>
{
    public async Task<int> Handle(TRequest request, RequestHandlerDelegate<int> next, CancellationToken cancellationToken) => await next() + 1;
}
public sealed record CountTo(int Count) : IStreamRequest<int>;
public sealed class CountToHandler : IStreamRequestHandler<CountTo, int>
{
    public async IAsyncEnumerable<int> Handle(CountTo request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var i = 1; i <= request.Count; i++)
        {
            await Task.Yield();
            yield return i;
        }
    }
}
public sealed class Doubling<TRequest> : IStreamPipelineBehavior<TRequest, int> where TRequest : IStreamRequest<int>
{
    public async IAsyncEnumerable<int> Handle(TRequest request, StreamHandlerDelegate<int> next, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in next().WithCancellation(cancellationToken)) yield return item * 2;
    }
}
