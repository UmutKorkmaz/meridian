using Meridian.Mediator.Behaviors;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Internal;

namespace Meridian.Mediator.Caching.Tests;

public sealed class MemoryCacheTests
{
    [Fact]
    public async Task Hit_Returns_Exact_Type()
    {
        using var cache = CreateCache(out _);
        var provider = new MemoryCacheProvider(cache);
        var value = new Response("exact");
        await provider.SetAsync("ref", value, null, default);
        var hit = await provider.GetAsync("ref", default);
        Assert.True(hit.Found); Assert.Same(value, hit.Value);
        await provider.SetAsync("int", 42, null, default);
        Assert.Equal(42, Assert.IsType<int>((await provider.GetAsync("int", default)).Value));
        int? nullable = 7;
        await provider.SetAsync("nullable", nullable, null, default);
        Assert.Equal(7, (int?)(await provider.GetAsync("nullable", default)).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expiry_Becomes_Miss(bool configured)
    {
        using var cache = CreateCache(out var clock);
        var duration = configured ? TimeSpan.FromSeconds(3) : TimeSpan.FromMinutes(5);
        var provider = new MemoryCacheProvider(cache, configured ? new() { DefaultDuration = duration } : null);
        await provider.SetAsync("key", 1, null, default);
        clock.Advance(duration - TimeSpan.FromTicks(1));
        Assert.True((await provider.GetAsync("key", default)).Found);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.False((await provider.GetAsync("key", default)).Found);
        await provider.SetAsync("key", 2, TimeSpan.FromSeconds(9), default);
        clock.Advance(TimeSpan.FromSeconds(8)); Assert.True((await provider.GetAsync("key", default)).Found);
        await provider.SetAsync("key", 3, TimeSpan.FromSeconds(2), default);
        clock.Advance(TimeSpan.FromSeconds(2)); Assert.False((await provider.GetAsync("key", default)).Found);
    }

    [Fact]
    public async Task Invalidating_Command_Removes_Composed_Key()
    {
        using var cache = CreateCache(out _);
        var provider = new MemoryCacheProvider(cache);
        var behavior = new CachingBehavior<Query, Response>(provider);
        var calls = 0;
        Task<Response> Next() { calls++; return Task.FromResult(new Response("result")); }
        var first = await behavior.Handle(new("key"), Next, default);
        Assert.Same(first, await behavior.Handle(new("key"), Next, default)); Assert.Equal(1, calls);
        var invalidation = new CacheInvalidationBehavior<Command, int>(provider);
        Assert.Equal(1, await invalidation.Handle(new([CacheKeyComposer.Compose<Query>("key")]), () => Task.FromResult(1), default));
        await behavior.Handle(new("key"), Next, default); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Raw_Key_OptOut_Remains_Working()
    {
        using var cache = CreateCache(out _); var provider = new MemoryCacheProvider(cache);
        var options = new CachingBehaviorOptions { UseRequestTypeNamespacedKeys = false };
        var behavior = new CachingBehavior<Query, int>(provider, options);
        Assert.Equal(7, await behavior.Handle(new("raw"), () => Task.FromResult(7), default));
        Assert.Equal(7, (await provider.GetAsync("raw", default)).Value);
        await new CacheInvalidationBehavior<Command, int>(provider, options).Handle(new(["raw"]), () => Task.FromResult(1), default);
        Assert.False((await provider.GetAsync("raw", default)).Found);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Nonpositive_Duration_Removes_Existing_Entry(int seconds)
    {
        using var cache = CreateCache(out _); var provider = new MemoryCacheProvider(cache);
        await provider.SetAsync("key", 1, null, default);
        await provider.SetAsync("key", 2, TimeSpan.FromSeconds(seconds), default);
        Assert.False((await provider.GetAsync("key", default)).Found);
        Assert.Equal(3, await provider.GetOrCreateAsync("key", _ => Task.FromResult<object?>(3), TimeSpan.FromSeconds(seconds), default));
        Assert.False((await provider.GetAsync("key", default)).Found); Assert.Equal(0, provider.ActiveOperationCount);
    }

    [Fact]
    public async Task Null_Is_Not_Stored_And_Arguments_Cancellation_Are_Validated()
    {
        using var cache = CreateCache(out _); var provider = new MemoryCacheProvider(cache);
        Assert.Null(await provider.GetOrCreateAsync("key", _ => Task.FromResult<object?>(null), null, default));
        Assert.False((await provider.GetAsync("key", default)).Found);
        Assert.Throws<ArgumentNullException>(() => new MemoryCacheProvider(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryCacheProvider(cache, new() { DefaultDuration = TimeSpan.Zero }));
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.GetAsync(null!, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.SetAsync("key", null!, null, default));
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.GetOrCreateAsync("key", null!, null, default));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetAsync("key", cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SetAsync("key", 1, null, cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.RemoveAsync("key", cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetOrCreateAsync("key", _ => throw new InvalidOperationException(), null, cancel.Token));
        Assert.Equal(0, provider.ActiveOperationCount);
    }

    [Fact]
    public async Task Repeated_Registration_First_Options_Win_And_Backend_Is_Borrowed()
    {
        Assert.True(new CachingBehaviorOptions().UseRequestTypeNamespacedKeys);
        Assert.False(new CachingBehaviorOptions().EnableRequestCoalescing);
        using var cache = CreateCache(out var clock);
        var services = new ServiceCollection(); services.AddSingleton<IMemoryCache>(cache);
        Assert.Same(services, services.AddMeridianMemoryCaching(o => o.DefaultDuration = TimeSpan.FromSeconds(2)));
        services.AddMeridianMemoryCaching(o => o.DefaultDuration = TimeSpan.FromSeconds(9));
        using (var di = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }))
        {
            Assert.Single(di.GetServices<ICacheProvider>());
            Assert.Same(di.GetRequiredService<ICacheProvider>(), di.GetRequiredService<ICacheProvider>());
            Assert.Same(cache, di.GetRequiredService<IMemoryCache>());
            await di.GetRequiredService<ICacheProvider>().SetAsync("key", 1, null, default);
            clock.Advance(TimeSpan.FromSeconds(2)); Assert.False((await di.GetRequiredService<ICacheProvider>().GetAsync("key", default)).Found);
        }
        cache.Set("alive", 1); Assert.Equal(1, cache.Get<int>("alive"));
        Assert.Throws<ArgumentNullException>(() => ServiceCollectionExtensions.AddMeridianMemoryCaching(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddMeridianMemoryCaching(o => o.DefaultDuration = TimeSpan.Zero));
    }

    [Fact]
    public void Custom_Scoped_Provider_Is_Preserved_And_Default_Backend_Is_DI_Owned()
    {
        var services = new ServiceCollection(); services.AddScoped<ICacheProvider, MemoryCacheProvider>();
        services.AddMeridianMemoryCaching(); services.AddMeridianMemoryCaching();
        using var di = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var a = di.CreateScope(); using var b = di.CreateScope();
        Assert.Single(a.ServiceProvider.GetServices<ICacheProvider>());
        Assert.NotSame(a.ServiceProvider.GetRequiredService<ICacheProvider>(), b.ServiceProvider.GetRequiredService<ICacheProvider>());
        Assert.Same(a.ServiceProvider.GetRequiredService<IMemoryCache>(), b.ServiceProvider.GetRequiredService<IMemoryCache>());
        var defaults = new ServiceCollection().AddMeridianMemoryCaching().BuildServiceProvider();
        var owned = defaults.GetRequiredService<IMemoryCache>(); defaults.Dispose();
        Assert.Throws<ObjectDisposedException>(() => owned.TryGetValue("key", out _));
    }

    internal static MemoryCache CreateCache(out Clock clock)
    {
        clock = new Clock();
        return new MemoryCache(new MemoryCacheOptions { Clock = clock });
    }
    internal sealed class Clock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        internal void Advance(TimeSpan by) => UtcNow += by;
    }
    internal sealed record Response(string Value);
    internal sealed record Query(string CacheKey) : ICacheableQuery { public TimeSpan? CacheDuration => null; }
    internal sealed record Command(string[] CacheKeysToInvalidate) : ICacheInvalidatingRequest;
}
