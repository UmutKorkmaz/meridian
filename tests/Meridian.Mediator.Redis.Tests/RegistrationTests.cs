using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Caching;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Meridian.Mediator.Redis.Tests;

public sealed class RegistrationTests
{
    [Fact]
    public void Repeated_Registration_Preserves_Custom_Store_And_Rejects_Invalid_Options()
    {
        var services = new ServiceCollection(); var custom = new CustomStore(); services.AddSingleton<IIdempotencyStore>(custom); var registry = Registry();
        services.AddMeridianRedisIdempotency(registry).AddMeridianRedisIdempotency(registry);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        Assert.Same(custom, provider.GetRequiredService<IIdempotencyStore>());
        Assert.Throws<ArgumentOutOfRangeException>(() => services.AddMeridianRedisIdempotency(registry, o => o.LeaseDuration = TimeSpan.FromTicks(1)));
        Assert.Throws<InvalidOperationException>(() => services.AddMeridianRedisIdempotency(Registry()));
    }
    [Fact, Trait("Provider", "Redis")]
    public async Task Scoped_Store_Is_Shared_By_Aliases_Without_Capturing_Scoped_Serializer()
    {
        await using var f = await RedisFixture.Create(); f.Key("di"); var services = new ServiceCollection(); var registry = Registry();
        services.AddSingleton<IConnectionMultiplexer>(f.First); services.AddScoped<ICacheValueSerializer, JsonCacheValueSerializer>();
        RedisIdempotencyOptions? captured = null;
        services.AddMeridianRedisIdempotency(registry, o => { o.ApplicationKeyPrefix = f.Prefix; captured = o; }).AddMeridianRedisIdempotency(registry);
        captured!.ApplicationKeyPrefix = "mutated";
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var one = provider.CreateScope(); using var two = provider.CreateScope();
        var a = one.ServiceProvider.GetRequiredService<RedisLeasedIdempotencyStore>(); var b = two.ServiceProvider.GetRequiredService<RedisLeasedIdempotencyStore>();
        Assert.Same(a, one.ServiceProvider.GetRequiredService<IIdempotencyStore>()); Assert.Same(a, one.ServiceProvider.GetRequiredService<ILeasedIdempotencyStore>()); Assert.NotSame(a, b);
        var c = await a.ClaimAsync("di", typeof(string), default); Assert.Equal(f.Key("di"), c.Lease!.Key);
    }
    private static CacheContractRegistry Registry()
    {
        var o = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        return new CacheContractRegistry().RegisterContract("text", 1, (JsonTypeInfo<string>)o.GetTypeInfo(typeof(string)));
    }
    private sealed class CustomStore : IIdempotencyStore
    {
        public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct) => Task.FromResult<(bool, object?)>((false, null));
        public Task StoreAsync(string key, object response, CancellationToken ct) => Task.CompletedTask;
    }
}
