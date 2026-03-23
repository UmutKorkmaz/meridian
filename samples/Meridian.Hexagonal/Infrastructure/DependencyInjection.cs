using System.Linq;

namespace Meridian.Hexagonal;

public static class DependencyInjection
{
    public static IServiceCollection AddHexagonalServices(this IServiceCollection services)
    {
        services.AddSingleton<IClockPort, SystemClock>();
        services.AddSingleton<IMediatorLogger, HexagonalMediatorLogger>();
        services.AddSingleton<ICacheProvider, HexagonalCacheProvider>();
        services.AddSingleton<ITransactionScopeProvider, HexagonalTransactionScopeProvider>();
        services.AddSingleton<IProductCatalogPort, InMemoryProductCatalogAdapter>();
        services.AddSingleton<IOrderRepositoryPort, InMemoryOrderRepositoryAdapter>();
        services.AddSingleton<IOrderEventSinkPort, ConsoleOrderEventSinkAdapter>();

        services.AddMeridianMapping(cfg =>
        {
            cfg.AddProfile<HexagonalProfile>();
        });

        services.AddMeridianMediator(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<HexagonalAssemblyMarker>();
            cfg.AddValidationBehavior();
            cfg.AddLoggingBehavior();
            cfg.AddCachingBehavior();
            cfg.AddTransactionBehavior();
        });

        services.AddTransient<IValidator<BrowseCatalogQuery>, BrowseCatalogQueryValidator>();
        services.AddTransient<IValidator<PlaceOrderCommand>, PlaceOrderValidator>();
        services.AddTransient<IValidator<GetOrderQuery>, GetOrderQueryValidator>();

        return services;
    }
}

internal sealed class SystemClock : IClockPort
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

internal sealed class HexagonalMediatorLogger : IMediatorLogger
{
    public void LogInformation(string message, params object[] args)
        => Console.WriteLine($"[info] {message} | {string.Join(", ", args.Select(FormatArg))}");

    public void LogWarning(string message, params object[] args)
        => Console.WriteLine($"[warn] {message} | {string.Join(", ", args.Select(FormatArg))}");

    public void LogError(Exception exception, string message, params object[] args)
        => Console.WriteLine($"[error] {message} | {exception.Message}");

    private static string FormatArg(object? value) => value?.ToString() ?? "<null>";
}

internal sealed class HexagonalCacheProvider : ICacheProvider
{
    private readonly Dictionary<string, (object Value, DateTimeOffset ExpireAt)> _items = [];

    public Task<(bool Found, object? Value)> GetAsync(string key, CancellationToken cancellationToken)
    {
        if (_items.TryGetValue(key, out var entry))
        {
            if (entry.ExpireAt == default || entry.ExpireAt > DateTimeOffset.UtcNow)
            {
                return Task.FromResult<(bool Found, object? Value)>((true, entry.Value));
            }

            _items.Remove(key);
        }

        return Task.FromResult<(bool Found, object? Value)>((false, null));
    }

    public Task SetAsync(string key, object value, TimeSpan? duration, CancellationToken cancellationToken)
    {
        var expires = DateTimeOffset.UtcNow + (duration ?? TimeSpan.FromMinutes(1));
        _items[key] = (value, expires);
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        _items.Remove(key);
        return Task.CompletedTask;
    }
}

internal sealed class HexagonalTransactionScopeProvider : ITransactionScopeProvider
{
    public Task<ITransactionScope> BeginAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("[transaction] begin");
        return Task.FromResult<ITransactionScope>(new HexagonalTransactionScope());
    }
}

internal sealed class HexagonalTransactionScope : ITransactionScope
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
