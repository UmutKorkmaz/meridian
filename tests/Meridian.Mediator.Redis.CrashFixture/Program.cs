using System.Text.Json.Serialization;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Caching;
using Meridian.Mediator.Redis;
using StackExchange.Redis;

var endpoint = args[0]; var prefix = args[1]; var mode = args[2]; var marker = args[3]; var leaseMs = int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
using var connection = await ConnectionMultiplexer.ConnectAsync(endpoint);
var registry = new CacheContractRegistry().RegisterContract("text", 1, TextContext.Default.String);
var store = new RedisLeasedIdempotencyStore(connection, registry, new JsonCacheValueSerializer(registry), new() { ApplicationKeyPrefix = prefix, LeaseDuration = TimeSpan.FromMilliseconds(leaseMs), CompletedRetention = TimeSpan.FromMinutes(10) });
IIdempotencyStore selected = mode == "after-completion" ? new CompletionBarrier(store) : store;
try
{
    var response = await new IdempotencyBehavior<CrashRequest, string?>(selected).Handle(new(), async () =>
    {
        if (mode == "before-effect") { Console.WriteLine("claimed-before-effect"); await Task.Delay(Timeout.Infinite, CancellationToken.None); }
        using (var stream = new FileStream(marker, FileMode.Append, FileAccess.Write, FileShare.Read))
        { stream.Write("effect\n"u8); stream.Flush(flushToDisk: true); }
        Console.WriteLine("effect-recorded");
        if (mode == "after-effect") await Task.Delay(Timeout.Infinite, CancellationToken.None);
        return null;
    }, default);
    Console.WriteLine(response is null ? "result:null" : "result:" + response);
}
catch (IdempotencyInProgressException) { Console.WriteLine("status:inflight"); }
catch (IdempotencyOutcomeUnknownException) { Console.WriteLine("status:unknown"); }

sealed record CrashRequest : IIdempotentRequest { public string IdempotencyKey => "crash"; }
[JsonSerializable(typeof(string))]
internal sealed partial class TextContext : JsonSerializerContext;
sealed class CompletionBarrier(RedisLeasedIdempotencyStore store) : ILeasedIdempotencyStore
{
    public Task<LeasedIdempotencyClaim> ClaimAsync(string key, Type responseType, CancellationToken ct) => store.ClaimAsync(key, responseType, ct);
    public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct) => store.CheckAsync(key, ct);
    public Task StoreAsync(string key, object response, CancellationToken ct) => store.StoreAsync(key, response, ct);
    public Task ReleaseAsync(IdempotencyLease lease, CancellationToken ct) => store.ReleaseAsync(lease, ct);
    public Task<bool> RenewAsync(IdempotencyLease lease, CancellationToken ct) => store.RenewAsync(lease, ct);
    public async Task CompleteAsync(IdempotencyLease lease, object? response, CancellationToken ct)
    { await store.CompleteAsync(lease, response, ct); Console.WriteLine("completion-recorded"); await Task.Delay(Timeout.Infinite, CancellationToken.None); }
}
