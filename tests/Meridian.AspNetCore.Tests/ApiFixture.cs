using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Meridian.AspNetCore.Infrastructure;
using Meridian.AspNetCore.Orders;
using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Redis;
using Meridian.Mediator.Streaming;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Meridian.AspNetCore.Tests;

public sealed class ApiFixture(string environment = "Development", Action<IServiceCollection>? customize = null) : WebApplicationFactory<Program>
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "meridian-c6-" + Guid.NewGuid().ToString("N"));
    public byte[] Key { get; } = RandomNumberGenerator.GetBytes(32);
    public Signals Signals { get; } = new();
    public string DatabasePath => Path.Combine(DirectoryPath, "orders.db");
    public string Token(string? tenant = "A", string? user = "1", string permission = "orders") => LocalToken.Create(Key, tenant, user, permission);
    public HttpClient Client(string? token = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (token is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(DirectoryPath);
        builder.UseEnvironment(environment).ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Orders:DatabasePath"] = DatabasePath,
            ["Orders:Redis"] = Environment.GetEnvironmentVariable("MERIDIAN_TEST_REDIS") ?? throw new InvalidOperationException("A dedicated C6 Redis endpoint is required."),
            ["Orders:Namespace"] = Path.GetFileName(DirectoryPath),
            ["LocalAuth:Key"] = Convert.ToBase64String(Key)
        }));
        builder.ConfigureServices(services =>
        {
            ConfigureObservers(services, Signals);
            customize?.Invoke(services);
        });
    }
    public static void ConfigureObservers(IServiceCollection services, Signals state)
    {
        services.AddSingleton(state);
        services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(new OperationTrackingFilter(state));
        var cache = services.Last(x => x.ServiceType == typeof(ICacheProvider));
        services.Replace(ServiceDescriptor.Scoped<ICacheProvider>(sp => new CountingCache((ICacheProvider)cache.ImplementationFactory!(sp), state)));
        services.Replace(ServiceDescriptor.Scoped<IIdempotencyStore>(sp => new ObservedLease(sp.GetRequiredService<RedisLeasedIdempotencyStore>(), state)));
        services.Replace(ServiceDescriptor.Scoped(typeof(IAuthorizationHandler<>), typeof(ObservedAuthorization<>)));
        services.AddScoped<CreateOrderHandler>();
        services.Replace(ServiceDescriptor.Scoped<IRequestHandler<CreateOrder, OrderDto>, ObservedCreate>());
        services.AddScoped<WatchOrdersHandler>();
        services.Replace(ServiceDescriptor.Scoped<IStreamRequestHandler<WatchOrders, OrderEventDto>, ObservedStream>());
        services.AddScoped<OrderCreatedHandler>();
        services.Replace(ServiceDescriptor.Scoped<INotificationHandler<OrderCreated>, ObservedNotification>());
        services.AddScoped<GetOrderHandler>();
        services.Replace(ServiceDescriptor.Scoped<IRequestHandler<GetOrder, OrderDto?>, ObservedGet>());
    }
    public async Task<(int Orders, int Events)> Rows()
    {
        await using var db = new OrdersDbContext(new DbContextOptionsBuilder<OrdersDbContext>().UseSqlite(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString()).Options);
        return (await db.Orders.CountAsync(), await db.Events.CountAsync());
    }
    public override async ValueTask DisposeAsync()
    {
        Signals.Release.TrySetResult(); Signals.CleanupRelease.TrySetResult();
        await Signals.JoinOperations();
        await base.DisposeAsync();
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
    }
}
public sealed class Signals
{
    internal int CompletionFailureKind, RetryCalls;
    internal int Authorizations, HandlerCalls, CacheGets, CacheSets, GetCalls, Claims, Completions, Notifications, StreamStarts, StreamDisposes, StreamCancellations;
    internal bool PauseCreate, ThrowAfterSave, FailCompletion, ThrowNotification, InitialStreamFailure, LateStreamFailure, CleanupFailure, GateCleanup;
    public ConcurrentQueue<string?> Correlations { get; } = new();
    private long _operationId;
    private readonly ConcurrentDictionary<long, TaskCompletionSource> _operations = new();
    public int Active => _operations.Count;
    internal long BeginOperation() { var id = Interlocked.Increment(ref _operationId); _operations[id] = new(TaskCreationOptions.RunContinuationsAsynchronously); return id; }
    internal void EndOperation(long id) { if (_operations.TryRemove(id, out var done)) done.TrySetResult(); }
    public async Task JoinOperations()
    {
        while (!_operations.IsEmpty) await Task.WhenAll(_operations.Values.Select(x => x.Task)).WaitAsync(TimeSpan.FromSeconds(10));
    }
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource StreamDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CleanupEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CleanupRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
public sealed class ObservedAuthorization<T>(AuthenticatedOrderScope scope, Signals state) : IAuthorizationHandler<T> where T : IAuthorizedRequest
{
    public Task<AuthorizationResult> AuthorizeAsync(T request, CancellationToken cancellationToken)
    { Interlocked.Increment(ref state.Authorizations); scope.Authorize(); state.Correlations.Enqueue(CorrelationContext.CorrelationId); return Task.FromResult(AuthorizationResult.Success()); }
}
public sealed class ObservedCreate(CreateOrderHandler inner, Signals state) : IRequestHandler<CreateOrder, OrderDto>
{
    public async Task<OrderDto> Handle(CreateOrder request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref state.HandlerCalls); state.Correlations.Enqueue(CorrelationContext.CorrelationId);
        state.Entered.TrySetResult();
        if (state.PauseCreate) await state.Release.Task.WaitAsync(cancellationToken);
        var dto = await inner.Handle(request, cancellationToken);
        if (state.ThrowAfterSave) throw new IOException("secret-saved-payload");
        return dto;
    }
}
public sealed class CountingCache(ICacheProvider inner, Signals state) : ICacheProvider
{
    public Task<(bool Found, object? Value)> GetAsync(string key, CancellationToken cancellationToken) { Interlocked.Increment(ref state.CacheGets); return inner.GetAsync(key, cancellationToken); }
    public Task SetAsync(string key, object value, TimeSpan? duration, CancellationToken cancellationToken) { Interlocked.Increment(ref state.CacheSets); return inner.SetAsync(key, value, duration, cancellationToken); }
    public Task RemoveAsync(string key, CancellationToken cancellationToken) => inner.RemoveAsync(key, cancellationToken);
}
public sealed class ObservedLease(RedisLeasedIdempotencyStore inner, Signals state) : ILeasedIdempotencyStore
{
    public Task<LeasedIdempotencyClaim> ClaimAsync(string key, Type responseType, CancellationToken ct) { Interlocked.Increment(ref state.Claims); return inner.ClaimAsync(key, responseType, ct); }
    public Task<bool> RenewAsync(IdempotencyLease lease, CancellationToken ct) => inner.RenewAsync(lease, ct);
    public Task ReleaseAsync(IdempotencyLease lease, CancellationToken ct) => inner.ReleaseAsync(lease, ct);
    public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct) => inner.CheckAsync(key, ct);
    public Task StoreAsync(string key, object response, CancellationToken ct) => inner.StoreAsync(key, response, ct);
    public Task CompleteAsync(IdempotencyLease lease, object? response, CancellationToken ct)
    {
        Interlocked.Increment(ref state.Completions);
        if (state.CompletionFailureKind == 1) throw new IOException("secret-post-commit");
        if (state.CompletionFailureKind == 2) throw new AggregateException(new IdempotencyOutcomeUnknownException(), new IOException("secret-cleanup"));
        return state.FailCompletion ? throw new IdempotencyOutcomeUnknownException() : inner.CompleteAsync(lease, response, ct);
    }
}
public sealed class ObservedStream(WatchOrdersHandler inner, Signals state) : IStreamRequestHandler<WatchOrders, OrderEventDto>
{
    public async IAsyncEnumerable<OrderEventDto> Handle(WatchOrders request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref state.StreamStarts);
        try
        {
            if (state.InitialStreamFailure) throw new IOException("secret-initial-stream");
            await foreach (var item in inner.Handle(request, cancellationToken))
            {
                yield return item;
                if (state.LateStreamFailure) throw new IOException("secret-late-stream");
            }
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested) Interlocked.Increment(ref state.StreamCancellations);
            if (state.GateCleanup) { state.CleanupEntered.TrySetResult(); await state.CleanupRelease.Task; }
            Interlocked.Increment(ref state.StreamDisposes); state.StreamDisposed.TrySetResult();
#pragma warning disable CA2219 // Deliberate fault from a finally block: this stream double models a cleanup that throws, and the test asserts how that failure is reported.
            if (state.CleanupFailure) throw new IOException("secret-stream-cleanup");
#pragma warning restore CA2219
        }
    }
}

public sealed class ObservedNotification(OrderCreatedHandler inner, Signals state) : INotificationHandler<OrderCreated>
{
    public async Task Handle(OrderCreated notification, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref state.Notifications); state.Correlations.Enqueue(CorrelationContext.CorrelationId);
        await inner.Handle(notification, cancellationToken);
        if (state.ThrowNotification) throw new IOException("secret-notification");
    }
}
public sealed class ObservedGet(GetOrderHandler inner, Signals state) : IRequestHandler<GetOrder, OrderDto?>
{
    public Task<OrderDto?> Handle(GetOrder query, CancellationToken cancellationToken) { Interlocked.Increment(ref state.GetCalls); return inner.Handle(query, cancellationToken); }
}

internal sealed class OperationTrackingFilter(Signals state) : Microsoft.AspNetCore.Hosting.IStartupFilter
{
    public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
    {
        Microsoft.AspNetCore.Builder.UseExtensions.Use(app, async (context, callback) =>
        {
            if (context.Request.Path.StartsWithSegments("/probe")) { await callback(context); return; }
            var id = state.BeginOperation();
            try { await callback(context); }
            finally { state.EndOperation(id); }
        });
        next(app);
    };
}
