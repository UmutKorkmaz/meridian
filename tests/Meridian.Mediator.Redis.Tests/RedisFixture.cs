using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Meridian.Mediator.Caching;
using StackExchange.Redis;

namespace Meridian.Mediator.Redis.Tests;

internal sealed class RedisFixture : IAsyncDisposable
{
    internal readonly string Prefix = "c4-test-" + Guid.NewGuid().ToString("N");
    internal ConnectionMultiplexer First { get; private set; } = null!;
    internal ConnectionMultiplexer Second { get; private set; } = null!;
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);
    internal static async Task<RedisFixture> Create()
    {
        var endpoint = Environment.GetEnvironmentVariable("MERIDIAN_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(endpoint)) throw new InvalidOperationException("Required isolated Redis endpoint MERIDIAN_TEST_REDIS is missing; provider support gate cannot skip.");
        var fixture = new RedisFixture();
        fixture.First = await ConnectionMultiplexer.ConnectAsync(endpoint);
        try { fixture.Second = await ConnectionMultiplexer.ConnectAsync(endpoint); }
        catch { await fixture.First.DisposeAsync(); throw; }
        return fixture;
    }
    internal RedisLeasedIdempotencyStore Store(bool second = false, string? scope = null, int leaseMs = 1500)
    {
        var json = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        var registry = new CacheContractRegistry().RegisterContract("text", 1, (JsonTypeInfo<string>)json.GetTypeInfo(typeof(string)))
            .RegisterContract("number", 1, (JsonTypeInfo<int>)json.GetTypeInfo(typeof(int)));
        return new(second ? Second : First, registry, new JsonCacheValueSerializer(registry),
            new() { ApplicationKeyPrefix = Prefix, LeaseDuration = TimeSpan.FromMilliseconds(leaseMs), CompletedRetention = TimeSpan.FromMinutes(5) },
            scope is null ? null : new Scope(scope));
    }
    internal string Key(string logical, string? scope = null)
    {
        var key = CacheKeyDecorator.Compose(logical, Prefix, scope); _keys.Add(key); return key;
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var key in _keys) await First.GetDatabase().KeyDeleteAsync(key);
        await Second.DisposeAsync(); await First.DisposeAsync();
    }
    private sealed record Scope(string Value) : ICacheKeyScope { public string GetScope() => Value; }
}
