using System.Collections.Concurrent;
using Meridian.Mediator;
using Meridian.Mediator.Extensions;

namespace Meridian.Showcase;

public sealed class ConsoleMediatorLogger : IMediatorLogger
{
    public void LogInformation(string message, params object[] args)
        => Console.WriteLine($"[info] {message} | {string.Join(", ", args.Select(FormatArg))}");

    public void LogWarning(string message, params object[] args)
        => Console.WriteLine($"[warn] {message} | {string.Join(", ", args.Select(FormatArg))}");

    public void LogError(Exception exception, string message, params object[] args)
        => Console.WriteLine($"[error] {message} | {string.Join(", ", args.Select(FormatArg))} | {exception.Message}");

    private static string FormatArg(object? value) => value?.ToString() ?? "<null>";
}

public sealed class InMemoryCacheProvider : ICacheProvider
{
    private readonly ConcurrentDictionary<string, object> _cache = new();

    public Task<(bool Found, object? Value)> GetAsync(string key, CancellationToken cancellationToken)
    {
        var found = _cache.TryGetValue(key, out var value);
        return Task.FromResult((found, value));
    }

    public Task SetAsync(string key, object value, TimeSpan? duration, CancellationToken cancellationToken)
    {
        _cache[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        _cache.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, object> _responses = new();

    public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct)
    {
        var exists = _responses.TryGetValue(key, out var cached);
        return Task.FromResult((exists, cached));
    }

    public Task StoreAsync(string key, object response, CancellationToken ct)
    {
        _responses[key] = response;
        return Task.CompletedTask;
    }
}

public sealed class DemoTransactionScopeProvider : ITransactionScopeProvider
{
    public Task<ITransactionScope> BeginAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("[transaction] begin");
        return Task.FromResult<ITransactionScope>(new DemoTransactionScope());
    }
}

public sealed class DemoTransactionScope : ITransactionScope
{
    public Task CommitAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("[transaction] commit");
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("[transaction] rollback");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
