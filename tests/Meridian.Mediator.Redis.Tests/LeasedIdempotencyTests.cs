using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Caching;
using StackExchange.Redis;

namespace Meridian.Mediator.Redis.Tests;

[Trait("Provider", "Redis")]
public sealed class LeasedIdempotencyTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task Loaded_Provider_And_Independent_Client_Identities_Are_Recorded()
    {
        await using var f = await RedisFixture.Create();
        var first = (long)await f.First.GetDatabase().ExecuteAsync("CLIENT", "ID"); var second = (long)await f.Second.GetDatabase().ExecuteAsync("CLIENT", "ID"); Assert.NotEqual(first, second);
        output.WriteLine($"CLIENTS first={first} second={second} runtime={Environment.Version} pid={Environment.ProcessId}");
        foreach (var assembly in new[] { typeof(RedisLeasedIdempotencyStore).Assembly, typeof(CacheValueCodec).Assembly, typeof(ILeasedIdempotencyStore).Assembly, typeof(ConnectionMultiplexer).Assembly, typeof(LeasedIdempotencyTests).Assembly })
            output.WriteLine($"LOADED assembly={assembly.GetName().Name} path={assembly.Location} sha256={Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant()}");
    }

    [Fact]
    public async Task Unknown_Handler_Failure_Is_Not_Released_After_An_Observed_Effect()
    {
        await using var f = await RedisFixture.Create(); var key = f.Key(IdempotencyKeyComposer.Compose<Request>("loss")); var store = f.Store(leaseMs: 350); var original = new IOException("after effect"); var calls = 0;
        var thrown = await Assert.ThrowsAsync<IOException>(() => new RetryBehavior<Request, string>().Handle(new(), () => new IdempotencyBehavior<Request, string>(store).Handle(new(), () => { calls++; throw original; }, default), default));
        Assert.Same(original, thrown); Assert.Equal(1, calls); Assert.Equal("P", (string?)await f.Second.GetDatabase().HashGetAsync(key, "state"));
        await CrashRestartTests.WaitExpired(f.Second.GetDatabase(), key);
        Assert.Equal(LeasedIdempotencyStatus.OutcomeUnknown, (await f.Store(true).ClaimAsync(IdempotencyKeyComposer.Compose<Request>("loss"), typeof(string), default)).Status);
    }

    [Fact]
    public async Task Identical_Completion_Does_Not_Extend_Completed_Retention()
    {
        await using var f = await RedisFixture.Create(); var key = f.Key("repeat"); var store = f.Store(); var c = await store.ClaimAsync("repeat", typeof(string), default);
        await store.CompleteAsync(c.Lease!, "same", default); var before = await f.First.GetDatabase().KeyTimeToLiveAsync(key); await Task.Delay(60);
        await store.CompleteAsync(c.Lease!, "same", default); var after = await f.Second.GetDatabase().KeyTimeToLiveAsync(key); Assert.True(after < before);
        await Assert.ThrowsAsync<IdempotencyOutcomeUnknownException>(() => store.CompleteAsync(c.Lease!, "different", default));
    }

    [Fact]
    public async Task Lost_Response_After_Completion_Is_Replayed()
    {
        await using var f = await RedisFixture.Create();
        f.Key(IdempotencyKeyComposer.Compose<Request>("loss")); var store = f.Store(); var fault = new IOException("injected lost acknowledgement after real Redis completion"); var calls = 0;
        var wrapped = new LostReply(store, fault);
        var error = await Assert.ThrowsAsync<IOException>(() => new RetryBehavior<Request, string?>().Handle(new(), () => new IdempotencyBehavior<Request, string?>(wrapped).Handle(new(), () => { calls++; return Task.FromResult<string?>(null); }, default), default));
        Assert.Same(fault, error); Assert.Equal(1, calls); Assert.False(RetryPolicy.TransientOnly(error));
        var replay = await new IdempotencyBehavior<Request, string?>(f.Store(true)).Handle(new(), () => { calls++; return Task.FromResult<string?>("wrong"); }, default);
        Assert.Null(replay); Assert.Equal(1, calls);
    }
    private sealed record Request : IIdempotentRequest, IRetryableRequest { public string IdempotencyKey => "loss"; }
    private sealed class LostReply(RedisLeasedIdempotencyStore store, Exception error) : ILeasedIdempotencyStore
    {
        public Task<LeasedIdempotencyClaim> ClaimAsync(string key, Type responseType, CancellationToken ct) => store.ClaimAsync(key, responseType, ct);
        public async Task CompleteAsync(IdempotencyLease lease, object? response, CancellationToken ct) { await store.CompleteAsync(lease, response, ct); throw error; }
        public Task ReleaseAsync(IdempotencyLease lease, CancellationToken ct) => store.ReleaseAsync(lease, ct);
        public Task<bool> RenewAsync(IdempotencyLease lease, CancellationToken ct) => store.RenewAsync(lease, ct);
        public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct) => store.CheckAsync(key, ct);
        public Task StoreAsync(string key, object response, CancellationToken ct) => store.StoreAsync(key, response, ct);
    }

    [Fact]
    public async Task Two_Clients_Same_Key_Execute_Once_While_Lease_Is_Valid()
    {
        await using var f = await RedisFixture.Create(); f.Key(IdempotencyKeyComposer.Compose<Request>("loss"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var running = new IdempotencyBehavior<Request, string>(f.Store(leaseMs: 600)).Handle(new(), async () => { Interlocked.Increment(ref calls); entered.SetResult(); await release.Task; return "once"; }, default);
        try
        {
            await entered.Task; await Task.Delay(1300);
            await Assert.ThrowsAsync<IdempotencyInProgressException>(() => new IdempotencyBehavior<Request, string>(f.Store(true)).Handle(new(), () => { Interlocked.Increment(ref calls); return Task.FromResult("wrong"); }, default));
        }
        finally { release.TrySetResult(); }
        Assert.Equal("once", await running); Assert.Equal(1, calls);
        Assert.Equal("once", await new IdempotencyBehavior<Request, string>(f.Store(true)).Handle(new(), () => { Interlocked.Increment(ref calls); return Task.FromResult("wrong"); }, default)); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task Completed_Null_Is_Replayed()
    {
        await using var f = await RedisFixture.Create(); f.Key("null"); var a = f.Store(); var claim = await a.ClaimAsync("null", typeof(string), default);
        await a.CompleteAsync(claim.Lease!, null, default); var replay = await f.Store(true).ClaimAsync("null", typeof(string), default);
        Assert.Equal(LeasedIdempotencyStatus.Completed, replay.Status); Assert.Null(replay.CachedResponse);
        Assert.Equal((true, (object?)null), await a.CheckAsync("null", default));
    }
    [Fact]
    public async Task Stale_Owner_Cannot_Complete_Or_Release()
    {
        await using var f = await RedisFixture.Create(); f.Key("stale"); var store = f.Store(); var claim = await store.ClaimAsync("stale", typeof(string), default);
        var wrong = claim.Lease! with { OwnerToken = new string('f', 32) };
        Assert.False(await store.RenewAsync(wrong, default));
        await Assert.ThrowsAsync<IdempotencyOutcomeUnknownException>(() => store.CompleteAsync(wrong, "bad", default));
        await Assert.ThrowsAsync<IdempotencyOutcomeUnknownException>(() => store.ReleaseAsync(wrong, default));
        Assert.Equal(LeasedIdempotencyStatus.InFlight, (await store.ClaimAsync("stale", typeof(string), default)).Status);
    }
    [Fact]
    public async Task Expired_Pending_Is_OutcomeUnknown()
    {
        await using var f = await RedisFixture.Create(); var key = f.Key("expired"); var store = f.Store(leaseMs: 60);
        var claim = await store.ClaimAsync("expired", typeof(string), default); await Task.Delay(150);
        Assert.Equal(LeasedIdempotencyStatus.OutcomeUnknown, (await f.Store(true).ClaimAsync("expired", typeof(string), default)).Status);
        Assert.Null(await f.Second.GetDatabase().KeyTimeToLiveAsync(key));
        Assert.False(await store.RenewAsync(claim.Lease!, default));
        await Assert.ThrowsAsync<IdempotencyOutcomeUnknownException>(() => store.ReleaseAsync(claim.Lease!, default));
        await Assert.ThrowsAsync<IdempotencyOutcomeUnknownException>(() => store.CheckAsync("expired", default));
    }
    [Fact]
    public async Task Two_Tenants_Same_Idempotency_Key_Are_Isolated()
    {
        await using var f = await RedisFixture.Create(); var aKey = f.Key("same", "a"); var bKey = f.Key("same", "b");
        var a = f.Store(scope: "a"); var b = f.Store(true, "b");
        var ca = await a.ClaimAsync("same", typeof(string), default); var cb = await b.ClaimAsync("same", typeof(string), default);
        Assert.Equal(aKey, ca.Lease!.Key); Assert.Equal(bKey, cb.Lease!.Key);
        Assert.True(await a.RenewAsync(ca.Lease, default)); await a.CompleteAsync(ca.Lease, "A", default); await b.CompleteAsync(cb.Lease, "B", default);
        Assert.Equal("A", (await a.ClaimAsync("same", typeof(string), default)).CachedResponse);
        Assert.Equal("B", (await b.ClaimAsync("same", typeof(string), default)).CachedResponse);
        Assert.False(await f.First.GetDatabase().KeyExistsAsync(CacheKeyDecorator.Compose(aKey, f.Prefix, "a")));
    }
    [Fact]
    public async Task Response_Contract_Mismatch_Is_Rejected()
    {
        await using var f = await RedisFixture.Create(); f.Key("contract"); var a = f.Store(); await a.ClaimAsync("contract", typeof(string), default);
        await Assert.ThrowsAsync<RedisServerException>(() => f.Store(true).ClaimAsync("contract", typeof(int), default));
        Assert.Equal(LeasedIdempotencyStatus.InFlight, (await a.ClaimAsync("contract", typeof(string), default)).Status);
    }
    [Fact]
    public async Task Safe_Failure_Reconciliation_Allows_Retry()
    {
        await using var f = await RedisFixture.Create(); f.Key("safe", "tenant"); var a = f.Store(scope: "tenant", leaseMs: 60);
        var claim = await a.ClaimAsync("safe", typeof(string), default);
        await Assert.ThrowsAsync<IdempotencyOutcomeUnknownException>(() => a.ReconcileAsync("safe", claim.Lease!.OwnerToken, IdempotencyReconciliation.SafeToRetry, null, default));
        await Task.Delay(150); await a.ReconcileAsync("safe", claim.Lease!.OwnerToken, IdempotencyReconciliation.SafeToRetry, null, default);
        var next = await a.ClaimAsync("safe", typeof(string), default);
        Assert.Equal(LeasedIdempotencyStatus.Claimed, next.Status); Assert.NotEqual(claim.Lease.OwnerToken, next.Lease!.OwnerToken);
    }
    [Fact]
    public async Task Reconciliation_Completed_Null_Uses_Retained_Contract()
    {
        await using var f = await RedisFixture.Create(); f.Key("reconciled"); var a = f.Store(leaseMs: 60); var c = await a.ClaimAsync("reconciled", typeof(string), default);
        await Task.Delay(150); await f.Store(true).ReconcileAsync("reconciled", c.Lease!.OwnerToken, IdempotencyReconciliation.Completed, null, default);
        var result = await a.ClaimAsync("reconciled", typeof(string), default); Assert.Equal(LeasedIdempotencyStatus.Completed, result.Status); Assert.Null(result.CachedResponse);
    }
    [Fact]
    public async Task Renewal_Uses_Original_Duration_And_Immutable_Effective_Key()
    {
        await using var f = await RedisFixture.Create(); var key = f.Key("renew"); var owner = f.Store(leaseMs: 700); var other = f.Store(true, leaseMs: 50);
        var c = await owner.ClaimAsync("renew", typeof(string), default); await Task.Delay(200); Assert.True(await other.RenewAsync(c.Lease!, default));
        await Task.Delay(550); Assert.True(await owner.RenewAsync(c.Lease!, default));
        Assert.Equal("700", (string?)await f.First.GetDatabase().HashGetAsync(key, "leaseMs"));
        await owner.CompleteAsync(c.Lease!, "done", default);
    }
    [Theory]
    [InlineData("v", "2")]
    [InlineData("extra", "field")]
    [InlineData("leaseMs", "nan")]
    [InlineData("owner", "bad")]
    [InlineData("schema", "0")]
    public async Task Malformed_Pending_Is_Retained_And_Fails_Closed(string field, string value)
    {
        await using var f = await RedisFixture.Create(); var key = f.Key("malformed"); var store = f.Store(); var c = await store.ClaimAsync("malformed", typeof(string), default);
        await f.First.GetDatabase().HashSetAsync(key, field, value);
        await Assert.ThrowsAsync<RedisServerException>(() => store.ClaimAsync("malformed", typeof(string), default));
        await Assert.ThrowsAsync<RedisServerException>(() => store.RenewAsync(c.Lease!, default));
        Assert.True(await f.Second.GetDatabase().KeyExistsAsync(key));
    }
    [Fact]
    public async Task Corrupt_Completed_Payload_And_Missing_Ttl_Are_Not_Evicted()
    {
        await using var f = await RedisFixture.Create(); var key = f.Key("corrupt"); var store = f.Store(); var c = await store.ClaimAsync("corrupt", typeof(string), default);
        await store.CompleteAsync(c.Lease!, "okay", default); await f.First.GetDatabase().HashSetAsync(key, "payload", "xxxxxxxxxxxx");
        await Assert.ThrowsAsync<CacheValueDataException>(() => store.ClaimAsync("corrupt", typeof(string), default));
        Assert.True(await f.Second.GetDatabase().KeyExistsAsync(key)); await f.First.GetDatabase().KeyPersistAsync(key);
        await Assert.ThrowsAsync<RedisServerException>(() => store.ClaimAsync("corrupt", typeof(string), default));
    }
    [Fact]
    public async Task Legacy_Store_Refuses_Pending_And_Completed_Overwrite()
    {
        await using var f = await RedisFixture.Create(); f.Key("legacy"); var store = f.Store(); var c = await store.ClaimAsync("legacy", typeof(string), default);
        await Assert.ThrowsAsync<IdempotencyOutcomeUnknownException>(() => store.StoreAsync("legacy", "bad", default));
        await store.CompleteAsync(c.Lease!, "original", default);
        await Assert.ThrowsAsync<IdempotencyOutcomeUnknownException>(() => store.StoreAsync("legacy", "bad", default));
        Assert.Equal("original", (await store.CheckAsync("legacy", default)).CachedResponse);
    }
}
