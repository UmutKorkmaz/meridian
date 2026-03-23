using Meridian.Mediator;
using System.Collections.Concurrent;

namespace Meridian.EventDrivenCqrs;

public sealed class EventDrivenMarker;

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
