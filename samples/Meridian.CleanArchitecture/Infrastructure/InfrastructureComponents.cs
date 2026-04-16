using Meridian.Mediator;

namespace Meridian.CleanArchitecture;

public sealed class DemoMediatorLogger : IMediatorLogger
{
    public void LogInformation(string message, params object[] args)
        => Console.WriteLine($"[info] {Substitute(message, args)}");

    public void LogWarning(string message, params object[] args)
        => Console.WriteLine($"[warn] {Substitute(message, args)}");

    public void LogError(Exception exception, string message, params object[] args)
        => Console.WriteLine($"[error] {Substitute(message, args)} | {exception.Message}");

    private static string FormatArg(object? value) => value?.ToString() ?? "<null>";

    private static string Substitute(string message, object[] args)
    {
        if (args.Length == 0) return message;
        var i = 0;
        return System.Text.RegularExpressions.Regex.Replace(
            message, @"\{[^{}]+\}",
            _ => i < args.Length ? FormatArg(args[i++]) : "{?}");
    }
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
