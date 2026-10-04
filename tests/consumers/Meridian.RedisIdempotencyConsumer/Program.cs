using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Caching;
using Meridian.Mediator.Redis;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

Console.WriteLine($"HOST runtime={Environment.Version} framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
var endpoint = Environment.GetEnvironmentVariable("MERIDIAN_TEST_REDIS");
if (string.IsNullOrWhiteSpace(endpoint)) throw new InvalidOperationException("Required Redis idempotency consumer endpoint MERIDIAN_TEST_REDIS is missing.");
Require(!JsonSerializer.IsReflectionEnabledByDefault, "Source generated metadata required");
using var first = await ConnectionMultiplexer.ConnectAsync(endpoint); using var second = await ConnectionMultiplexer.ConnectAsync(endpoint);
var prefix = "c4-consumer-" + Guid.NewGuid().ToString("N"); var registry = new CacheContractRegistry().RegisterContract("reply", 1, ConsumerJson.Default.Reply);
var services = new ServiceCollection(); services.AddSingleton<IConnectionMultiplexer>(first); services.AddMeridianRedisIdempotency(registry, o => o.ApplicationKeyPrefix = prefix).AddMeridianRedisIdempotency(registry);
using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }); using var scope = provider.CreateScope();
var a = scope.ServiceProvider.GetRequiredService<RedisLeasedIdempotencyStore>();
Require(ReferenceEquals(a, scope.ServiceProvider.GetRequiredService<IIdempotencyStore>()), "Owned alias");
var otherRegistry = new CacheContractRegistry().RegisterContract("reply", 1, ConsumerJson.Default.Reply);
var b = new RedisLeasedIdempotencyStore(second, otherRegistry, new JsonCacheValueSerializer(otherRegistry), new() { ApplicationKeyPrefix = prefix });
var keys = new[] { "value", "null", "legacy" };
try
{
    var c = await a.ClaimAsync("value", typeof(Reply), default); Require(c.Status == LeasedIdempotencyStatus.Claimed, "claim");
    Require((await b.ClaimAsync("value", typeof(Reply), default)).Status == LeasedIdempotencyStatus.InFlight, "independent pending");
    Require(await b.RenewAsync(c.Lease!, default), "effective lease renewal"); await a.CompleteAsync(c.Lease!, new Reply("recorded", 7), default);
    Require((await b.ClaimAsync("value", typeof(Reply), default)).CachedResponse is Reply { Value: "recorded", Number: 7 }, "independent reconstructed DTO");
    var n = await a.ClaimAsync("null", typeof(Reply), default); await a.CompleteAsync(n.Lease!, null, default);
    var replay = await b.ClaimAsync("null", typeof(Reply), default); Require(replay.Status == LeasedIdempotencyStatus.Completed && replay.CachedResponse is null, "completed null");
    await a.StoreAsync("legacy", new Reply("old", 1), default); Require((await b.CheckAsync("legacy", default)).CachedResponse is Reply { Number: 1 }, "legacy completed compatibility");
    foreach (var assembly in new[] { typeof(RedisLeasedIdempotencyStore).Assembly, typeof(CacheValueCodec).Assembly, typeof(ILeasedIdempotencyStore).Assembly, typeof(ConnectionMultiplexer).Assembly, typeof(ConsumerJson).Assembly })
        Console.WriteLine($"LOADED assembly={assembly.GetName().Name} path={assembly.Location} sha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant()}");
    Console.WriteLine("COMMON redis leased=true clients=2 generatedDto=reconstructed null=replayed renew=effectiveKey legacy=completed scopedAliases=true reflectionDisabled=true");
}
finally { foreach (var key in keys) await first.GetDatabase().KeyDeleteAsync(CacheKeyDecorator.Compose(key, prefix, null)); }
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
internal sealed record Reply(string Value, int Number);
[JsonSerializable(typeof(Reply))]
internal sealed partial class ConsumerJson : JsonSerializerContext;
