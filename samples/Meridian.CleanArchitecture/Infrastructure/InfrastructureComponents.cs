using Meridian.Mediator;

namespace Meridian.CleanArchitecture;

public sealed class DemoMediatorLogger : IMediatorLogger
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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (object Value, DateTimeOffset Expires)> _cache = new();

    public Task<(bool Found, object? Value)> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            return Task.FromResult<(bool Found, object? Value)>((true, entry.Value));
        }

        return Task.FromResult<(bool Found, object? Value)>((false, null));
    }

    public Task SetAsync(string key, object value, TimeSpan? duration, CancellationToken cancellationToken)
    {
        var expires = DateTimeOffset.UtcNow + (duration ?? TimeSpan.FromHours(1));
        _cache[key] = (value, expires);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        _cache.TryRemove(key, out _);
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
