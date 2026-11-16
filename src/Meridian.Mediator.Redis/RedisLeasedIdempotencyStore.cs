using System.Diagnostics;
using System.Security.Cryptography;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Caching;
using StackExchange.Redis;

namespace Meridian.Mediator.Redis;

/// <summary>One-key Redis owned idempotency. Borrows an application-lifetime transport; never deletes malformed or expired pending data.</summary>
public sealed class RedisLeasedIdempotencyStore : ILeasedIdempotencyStore
{
    private readonly IDatabase _database;
    private readonly CacheContractRegistry _registry;
    private readonly ICacheValueSerializer _serializer;
    private readonly CacheValueCodec _codec;
    private readonly long _leaseMs, _retentionMs;
    private readonly int _maximum;
    private readonly string? _prefix;
    private readonly ICacheKeyScope? _scope;
    private readonly object _scopeGate = new();
    private string? _capturedScope;

    /// <summary>Creates an adapter borrowing the transport. Registry and serializers must share the same declared contract allowlist.</summary>
    public RedisLeasedIdempotencyStore(IConnectionMultiplexer connection, CacheContractRegistry registry, ICacheValueSerializer serializer,
        RedisIdempotencyOptions? options = null, ICacheKeyScope? scope = null)
    {
        ArgumentNullException.ThrowIfNull(connection); ArgumentNullException.ThrowIfNull(registry); ArgumentNullException.ThrowIfNull(serializer);
        options ??= new(); _leaseMs = RedisIdempotencyOptions.Milliseconds(options.LeaseDuration, nameof(options.LeaseDuration));
        _retentionMs = RedisIdempotencyOptions.Milliseconds(options.CompletedRetention, nameof(options.CompletedRetention));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MaximumEntryBytes, 0);
        _prefix = options.ApplicationKeyPrefix; CacheKeyDecorator.Compose("", _prefix, null);
        _maximum = options.MaximumEntryBytes; _scope = scope; _database = connection.GetDatabase(); _registry = registry; _serializer = serializer;
        _codec = new(registry, serializer, new CacheProviderOptions { MaximumEntryBytes = _maximum });
    }

    /// <inheritdoc/>
    public async Task<LeasedIdempotencyClaim> ClaimAsync(string key, Type responseType, CancellationToken ct)
    {
        var effective = EffectiveKey(key); var contract = _serializer.GetContractId(responseType); var schema = _registry.GetSchemaVersion(contract);
        var stamp = Stopwatch.GetTimestamp();
        var r = await Execute(effective, "claim", Token(), contract, schema, [], ct).ConfigureAwait(false);
        var received = TimeProvider.System.GetUtcNow(); var elapsed = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds;
        EffectiveKey(key);
        return Text(r[0]) switch
        {
            "claimed" => new(LeasedIdempotencyStatus.Claimed, Lease: new(effective, Text(r[1]), contract,
                received.AddMilliseconds(Math.Max(0, (long)r[5] - (long)r[4] - elapsed)))),
            "completed" => new(LeasedIdempotencyStatus.Completed, _codec.Decode((byte[])r[4]!, contract)),
            "inflight" => new(LeasedIdempotencyStatus.InFlight),
            "unknown" => new(LeasedIdempotencyStatus.OutcomeUnknown),
            _ => throw new InvalidOperationException("Invalid Redis claim response.")
        };
    }

    /// <inheritdoc/>
    public Task CompleteAsync(IdempotencyLease lease, object? response, CancellationToken ct)
    {
        ValidateLease(lease); var payload = _codec.Encode(response, lease.ResponseContractId);
        return RequireSuccess(Execute(lease.Key, "complete", lease.OwnerToken, lease.ResponseContractId, _registry.GetSchemaVersion(lease.ResponseContractId), payload, ct));
    }
    /// <inheritdoc/>
    public Task ReleaseAsync(IdempotencyLease lease, CancellationToken ct)
    {
        ValidateLease(lease);
        return RequireSuccess(Execute(lease.Key, "release", lease.OwnerToken, lease.ResponseContractId, _registry.GetSchemaVersion(lease.ResponseContractId), [], ct));
    }
    /// <inheritdoc/>
    public async Task<bool> RenewAsync(IdempotencyLease lease, CancellationToken ct)
    {
        ValidateLease(lease);
        var r = await Execute(lease.Key, "renew", lease.OwnerToken, lease.ResponseContractId, _registry.GetSchemaVersion(lease.ResponseContractId), [], ct).ConfigureAwait(false);
        return Text(r[0]) == "ok";
    }
    /// <summary>Reconciles an expired retained owner only after application-established quiescence. Logical key is decorated once.</summary>
    public async Task ReconcileAsync(string key, string expectedOwnerToken, IdempotencyReconciliation resolution, object? response, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedOwnerToken);
        if (!Enum.IsDefined(resolution) || resolution == IdempotencyReconciliation.SafeToRetry && response is not null)
            throw new ArgumentException("Invalid reconciliation decision or response.", nameof(resolution));
        var effective = EffectiveKey(key);
        var r = await Execute(effective, "read", "", "", 0, [], ct).ConfigureAwait(false);
        EffectiveKey(key);
        if (Text(r[0]) != "unknown" || Text(r[1]) != expectedOwnerToken) throw new IdempotencyOutcomeUnknownException();
        var contract = Text(r[2]); var schema = _registry.GetSchemaVersion(contract);
        if ((long)r[3] != schema) throw new InvalidOperationException("Retained response schema differs from the local contract.");
        var payload = resolution == IdempotencyReconciliation.Completed ? _codec.Encode(response, contract) : [];
        await RequireSuccess(Execute(effective, resolution == IdempotencyReconciliation.Completed ? "reconcile-complete" : "reconcile-release",
            expectedOwnerToken, contract, schema, payload, ct, Text(r[4]))).ConfigureAwait(false);
    }
    /// <inheritdoc/>
    public async Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct)
    {
        var r = await Execute(EffectiveKey(key), "read", "", "", 0, [], ct).ConfigureAwait(false); EffectiveKey(key);
        return Text(r[0]) switch
        {
            "absent" => (false, null),
            "completed" => DecodeCompleted(r),
            "inflight" => throw new IdempotencyInProgressException(typeof(IIdempotentRequest)),
            _ => throw new IdempotencyOutcomeUnknownException()
        };
    }
    /// <inheritdoc/>
    public Task StoreAsync(string key, object response, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response); var contract = _serializer.GetContractId(response.GetType());
        return RequireSuccess(Execute(EffectiveKey(key), "store", Token(), contract, _registry.GetSchemaVersion(contract), _codec.Encode(response, contract), ct));
    }
    private (bool, object?) DecodeCompleted(RedisResult[] r)
    {
        var contract = Text(r[2]);
        if (_registry.GetSchemaVersion(contract) != (long)r[3]) throw new InvalidOperationException("Retained schema differs from the local contract.");
        return (true, _codec.Decode((byte[])r[4]!, contract));
    }
    private string EffectiveKey(string key)
    {
        string? partition = null;
        if (_scope is not null)
        {
            partition = _scope.GetScope();
            if (string.IsNullOrWhiteSpace(partition)) throw new InvalidOperationException("Authenticated idempotency scope is absent or changed.");
            lock (_scopeGate)
            {
                if (_capturedScope is null) _capturedScope = partition;
                else if (!StringComparer.Ordinal.Equals(partition, _capturedScope)) throw new InvalidOperationException("Authenticated idempotency scope is absent or changed.");
            }
        }
        return CacheKeyDecorator.Compose(key, _prefix, partition);
    }
    private Task<RedisResult[]> Execute(string key, string operation, string owner, string contract, int schema, byte[] payload, CancellationToken ct, string snapshot = "")
    {
        ct.ThrowIfCancellationRequested();
        // Materialize every argument before issuance. A canceled wait retains only the transport task, never this scoped adapter.
        RedisKey[] keys = [key]; RedisValue[] args = [operation, owner, contract, schema, _leaseMs, _retentionMs, payload, _maximum, snapshot];
        Task<RedisResult> issued;
        using (ExecutionContext.SuppressFlow()) issued = _database.ScriptEvaluateAsync(RedisIdempotencyScripts.Transition, keys, args);
        return AwaitTransport(issued, ct);
    }
    private static async Task<RedisResult[]> AwaitTransport(Task<RedisResult> issued, CancellationToken ct)
    {
        try { return (RedisResult[])(await issued.WaitAsync(ct).ConfigureAwait(false))!; }
        catch
        {
            using (ExecutionContext.SuppressFlow())
                _ = issued.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }
    private static async Task RequireSuccess(Task<RedisResult[]> task)
    {
        if (Text((await task.ConfigureAwait(false))[0]) != "ok") throw new IdempotencyOutcomeUnknownException();
    }
    private static void ValidateLease(IdempotencyLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease); ArgumentException.ThrowIfNullOrWhiteSpace(lease.Key);
        ArgumentException.ThrowIfNullOrWhiteSpace(lease.OwnerToken); ArgumentException.ThrowIfNullOrWhiteSpace(lease.ResponseContractId);
    }
    private static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private static string Text(RedisResult value) => (string)value!;
}
