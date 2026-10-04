using Meridian.Mediator.Behaviors;
using Microsoft.Extensions.Caching.Memory;

namespace Meridian.Mediator.Caching;

/// <summary>
/// Adapts a borrowed memory cache, sharing live misses per provider instance and key.
/// The cache's lifetime and capacity remain the owner's responsibility.
/// </summary>
public sealed class MemoryCacheProvider : IConcurrentCacheProvider
{
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _defaultDuration;
    private readonly object _gate = new();
    private readonly Dictionary<string, Operation> _operations = new(StringComparer.Ordinal);

    /// <summary>Creates a provider borrowing the supplied cache without taking disposal ownership.</summary>
    /// <param name="cache">DI-managed or caller-managed cache.</param>
    /// <param name="options">Optional adapter options, copied at construction.</param>
    public MemoryCacheProvider(IMemoryCache cache, CacheProviderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        _defaultDuration = options?.DefaultDuration ?? TimeSpan.FromMinutes(5);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_defaultDuration, TimeSpan.Zero, nameof(options));
        _cache = cache;
    }

    internal int ActiveOperationCount { get { lock (_gate) { return _operations.Count; } } }

    /// <inheritdoc/>
    public Task<(bool Found, object? Value)> GetAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        var found = _cache.TryGetValue(key, out object? value);
        return Task.FromResult((found, value));
    }

    /// <inheritdoc/>
    public Task SetAsync(string key, object value, TimeSpan? duration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        var retention = duration ?? _defaultDuration;
        if (retention <= TimeSpan.Zero)
        {
            _cache.Remove(key);
        }
        else
        {
            _cache.Set(key, value, retention);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        cancellationToken.ThrowIfCancellationRequested();
        _cache.Remove(key);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<object?> GetOrCreateAsync(string key, Func<CancellationToken, Task<object?>> factory,
        TimeSpan? duration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);
        cancellationToken.ThrowIfCancellationRequested();
        if (_cache.TryGetValue(key, out object? value))
        {
            return Task.FromResult(value);
        }

        Operation operation;
        bool owner;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A prior owner may have stored its value between the first read and election.
            if (_cache.TryGetValue(key, out value))
            {
                return Task.FromResult(value);
            }
            owner = !_operations.TryGetValue(key, out operation!);
            if (owner)
            {
                operation = new Operation();
                _operations.Add(key, operation);
            }
        }

        // This method deliberately is not async: losing factories never enter a waiting frame.
        return owner
            ? RunOwnerAsync(key, operation, factory, duration, cancellationToken)
            : WaitForOwnerAsync(operation.Completion.Task, cancellationToken);
    }

    private static Task<object?> WaitForOwnerAsync(Task<object?> completion, CancellationToken cancellationToken) =>
        cancellationToken.CanBeCanceled ? completion.WaitAsync(cancellationToken) : completion;

    private async Task<object?> RunOwnerAsync(string key, Operation operation,
        Func<CancellationToken, Task<object?>> factory, TimeSpan? duration, CancellationToken cancellationToken)
    {
        object? value = null;
        Exception? failure = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Await the actual factory, even if it ignores cancellation. No detached scoped work.
            value = await factory(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (value is not null)
            {
                await SetAsync(key, value, duration, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            failure = error;
        }

        lock (_gate)
        {
            if (_operations.TryGetValue(key, out var current) && ReferenceEquals(current, operation))
            {
                _operations.Remove(key);
            }
            if (failure is OperationCanceledException canceled)
            {
                operation.Completion.SetCanceled(canceled.CancellationToken);
            }
            else if (failure is not null)
            {
                operation.Completion.SetException(failure);
            }
            else
            {
                operation.Completion.SetResult(value);
            }
        }

        // Observe the shared failure even when no waiter exists.
        return await operation.Completion.Task.ConfigureAwait(false);
    }

    private sealed class Operation
    {
        internal TaskCompletionSource<object?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
