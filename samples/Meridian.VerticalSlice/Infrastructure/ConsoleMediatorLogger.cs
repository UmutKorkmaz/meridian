using Meridian.Mediator;

namespace Meridian.VerticalSlice;

public sealed class ConsoleMediatorLogger : IMediatorLogger
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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _cache = new();

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

public sealed class VerticalSliceMarker;
