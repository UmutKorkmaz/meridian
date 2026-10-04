using Meridian.CachingConsumer;
using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Caching;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Internal;

Console.WriteLine($"HOST runtime={Environment.Version} framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
var clock = new Clock();
using var cache = new MemoryCache(new MemoryCacheOptions { Clock = clock });
var services = new ServiceCollection();
services.AddSingleton<IMemoryCache>(cache);
services.AddMeridianMemoryCaching(o => o.DefaultDuration = TimeSpan.FromSeconds(2));
services.AddMeridianMemoryCaching(o => o.DefaultDuration = TimeSpan.FromHours(1));
services.AddScoped<ScopeState>();
services.AddTransient<IRequestHandler<Query, string>, Handler>();
services.AddTransient<IRequestHandler<Invalidate, int>, InvalidationHandler>();
services.AddSingleton(new CachingBehaviorOptions { EnableRequestCoalescing = true });
services.AddMeridianMediator(c => c.AddCachingBehavior());
using (var di = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }))
{
    Require(di.GetServices<ICacheProvider>().Count() == 1, "Single registration");
    Require(ReferenceEquals(di.GetRequiredService<ICacheProvider>(), di.GetRequiredService<ICacheProvider>()), "Singleton provider");
    using var scope = di.CreateScope();
    var state = scope.ServiceProvider.GetRequiredService<ScopeState>();
    var sender = scope.ServiceProvider.GetRequiredService<IMediator>();
    var query = new Query("composed", Signal(), Signal());
    var owner = sender.Send(query);
    await query.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var waiters = Enumerable.Range(0, 19).Select(_ => sender.Send(query)).ToArray();
    Require(state.Calls == 1 && waiters.All(t => !t.IsCompleted), "Twenty joined misses");
    query.Release.SetResult();
    Require((await Task.WhenAll(waiters.Prepend(owner))).All(x => x == "value"), "Exact shared value");
    Require(state.Calls == 1, "Single owner factory");
    Require(await sender.Send(query) == "value" && state.Calls == 1, "Warm hit");
    clock.Advance(TimeSpan.FromSeconds(2));
    Require(await sender.Send(query) == "value" && state.Calls == 2, "First registration options and real expiration");
    Require(await sender.Send(new Invalidate([CacheKeyComposer.Compose<Query>(query.CacheKey)])) == 1, "Command invalidation");
    Require(await sender.Send(query) == "value" && state.Calls == 3, "Composed key removed");
    var provider = (IConcurrentCacheProvider)di.GetRequiredService<ICacheProvider>();
    using var cancel = new CancellationTokenSource(); var release = Signal(); var entered = Signal();
    async Task<object?> Factory(CancellationToken ct) { Require(!ct.CanBeCanceled, "Owner token"); entered.SetResult(); await release.Task; return 42; }
    var pending = provider.GetOrCreateAsync("waiter", Factory, null, default); await entered.Task;
    var canceled = provider.GetOrCreateAsync("waiter", _ => throw new InvalidOperationException("Unused scoped factory"), null, cancel.Token);
    cancel.Cancel();
    try { await canceled; throw new InvalidOperationException("Expected waiter cancellation"); }
    catch (OperationCanceledException error) { Require(error.CancellationToken == cancel.Token && !pending.IsCompleted, "Waiter isolation"); }
    release.SetResult(); Require((int)(await pending)! == 42, "Owner survives");
    var raw = new CachingBehavior<Query, int>(provider, new() { UseRequestTypeNamespacedKeys = false, EnableRequestCoalescing = true });
    await provider.SetAsync("raw", "incompatible", null, default); var rawCalls = 0;
    Require(await raw.Handle(new("raw", Signal(), Signal()), () => { rawCalls++; return Task.FromResult(7); }, default) == 7 && rawCalls == 1, "Typed fallback");
    Require((int)(await provider.GetAsync("raw", default)).Value! == 7, "Raw key preserved");
    await new CacheInvalidationBehavior<Invalidate, int>(provider, new() { UseRequestTypeNamespacedKeys = false }).Handle(new(["raw"]), () => Task.FromResult(1), default);
    Require(!(await provider.GetAsync("raw", default)).Found, "Raw invalidation");
    var nullRelease = Signal(); var nullCalls = 0; var waiterCalls = 0;
    async Task<string?> NullFactory() { nullCalls++; await nullRelease.Task; return null; }
    var nullBehavior = new CachingBehavior<Query, string?>(provider, new() { UseRequestTypeNamespacedKeys = false, EnableRequestCoalescing = true });
    var nullOwner = nullBehavior.Handle(new("null", Signal(), Signal()), NullFactory, default);
    var nullWaiter = raw.Handle(new("null", Signal(), Signal()), () => { waiterCalls++; return Task.FromResult(9); }, default);
    Require(waiterCalls == 0, "Null waiter joins"); nullRelease.SetResult();
    Require(await nullOwner is null && await nullWaiter == 9 && nullCalls == 1 && waiterCalls == 1, "Ambiguous shared null fallback");
    var canceledOwnerRelease = Signal(); using var ownerCancel = new CancellationTokenSource();
    async Task<object?> Noncooperative(CancellationToken _) { await canceledOwnerRelease.Task; Require(!state.Disposed, "Live scoped owner"); return 1; }
    var canceledOwner = provider.GetOrCreateAsync("owner-cancel", Noncooperative, null, ownerCancel.Token);
    ownerCancel.Cancel(); Require(!canceledOwner.IsCompleted && !state.Disposed, "Direct factory await"); canceledOwnerRelease.SetResult();
    try { await canceledOwner; throw new InvalidOperationException("Expected owner cancellation"); }
    catch (OperationCanceledException error) { Require(error.CancellationToken == ownerCancel.Token, "Owner token preserved"); }
    Require(!(await provider.GetAsync("owner-cancel", default)).Found, "Canceled value not stored");
    Require((int)(await provider.GetOrCreateAsync("owner-cancel", _ => Task.FromResult<object?>(11), null, default))! == 11, "Fresh owner retry");
    var reentryCalls = 0;
    const string privateReentryKey = "private-reentry-key";
    Task<object?> InnerReentry(CancellationToken _) { reentryCalls++; return Task.FromResult<object?>(1); }
    try
    {
        await provider.GetOrCreateAsync(privateReentryKey,
            _ => provider.GetOrCreateAsync(privateReentryKey, InnerReentry, null, default), null, default);
        throw new InvalidOperationException("Expected reentry rejection");
    }
    catch (InvalidOperationException error)
    {
        Require(error.Message == "Cache miss coalescing cannot join a live ancestor owner operation. Use distinct logical keys or disable coalescing for nested reads.", "Redacted reentry failure");
    }
    Require(reentryCalls == 0 && !(await provider.GetAsync(privateReentryKey, default)).Found, "Reentry cleanup");
    Require((int)(await provider.GetOrCreateAsync(privateReentryKey, InnerReentry, null, default))! == 1 && reentryCalls == 1, "Fresh reentry retry");
    var disabled = new CachingBehavior<Query, int>(provider); var disabledRelease = Signal(); var disabledCalls = 0;
    async Task<int> DisabledFactory() { disabledCalls++; await disabledRelease.Task; return 1; }
    var disabledTasks = Enumerable.Range(0, 20).Select(_ => disabled.Handle(new("disabled", Signal(), Signal()), DisabledFactory, default)).ToArray();
    Require(disabledCalls == 20, "Default disabled path"); disabledRelease.SetResult(); await Task.WhenAll(disabledTasks);
}
cache.Set("borrowed", 1); Require(cache.Get<int>("borrowed") == 1, "Borrowed backend remains alive");
foreach (var assembly in new[] { typeof(MemoryCacheProvider).Assembly, typeof(IMediator).Assembly, typeof(MemoryCache).Assembly })
{
    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant();
    Console.WriteLine($"LOADED assembly={assembly.GetName().Name} path={assembly.Location} sha256={hash}");
}
Console.WriteLine("COMMON caching backend=actual-memory expiry=fake-clock coalescing=20-to-1 default=disabled cancellation=isolated scoped=live invalidation=composed-and-raw types=checked null=per-caller registration=once borrowed=preserved reentry=fail-fast-and-retry isolatedNuGet=true");
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
namespace Meridian.CachingConsumer
{
    public sealed class Clock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan duration) => UtcNow += duration;
    }
    public sealed record Query(string CacheKey, TaskCompletionSource Entered, TaskCompletionSource Release) : IRequest<string>, ICacheableQuery { public TimeSpan? CacheDuration => null; }
    public sealed record Invalidate(string[] CacheKeysToInvalidate) : IRequest<int>, ICacheInvalidatingRequest;
    public sealed class ScopeState : IDisposable { public int Calls { get; set; } public bool Disposed { get; private set; } public void Dispose() => Disposed = true; }
    public sealed class Handler(ScopeState state) : IRequestHandler<Query, string>
    {
        public async Task<string> Handle(Query request, CancellationToken cancellationToken)
        {
            state.Calls++; request.Entered.TrySetResult(); await request.Release.Task; if (state.Disposed) throw new InvalidOperationException("Disposed owner"); return "value";
        }
    }
    public sealed class InvalidationHandler : IRequestHandler<Invalidate, int> { public Task<int> Handle(Invalidate request, CancellationToken cancellationToken) => Task.FromResult(1); }

}
