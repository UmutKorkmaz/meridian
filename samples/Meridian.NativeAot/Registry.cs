using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Generated;
using Meridian.Mediator.Pipeline;
using Meridian.Mediator.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Meridian.NativeAot.Fixture;

// Closed generated registrations. Every request, notification, stream, behavior and validator that the
// native binary dispatches is declared here; nothing is discovered by scanning or constructed reflectively.
[GenerateMediatorRegistry(
    typeof(GetOrderHandler), typeof(PingHandler), typeof(UnitPingHandler), typeof(WhoAmIHandler),
    typeof(NoticeHandlerOne), typeof(FaultFirstHandler), typeof(FaultSecondHandler),
    typeof(NumbersHandler), typeof(FaultyNumbersHandler),
    BehaviorTypes = new[] { typeof(PassBehavior<,>) },
    StreamBehaviorTypes = new[] { typeof(StreamLogBehavior<,>) },
    ValidatorTypes = new[] { typeof(GetOrderValidator) },
    IncludeStandardPipeline = true,
    ExpectedRequestTypes = new[] { typeof(GetOrder), typeof(Ping), typeof(UnitPing), typeof(WhoAmIQuery) })]
public partial class AppRegistry { }

// A second registry contributes a second handler for the same notification type.
[GenerateMediatorRegistry(typeof(NoticeHandlerTwo))]
public partial class ExtraRegistry { }

// A small standard-pipeline registry used to prove repeated/combined registration runs each behavior once.
[GenerateMediatorRegistry(
    typeof(StandardHandler),
    ValidatorTypes = new[] { typeof(StandardValidator) },
    IncludeStandardPipeline = true,
    ExpectedRequestTypes = new[] { typeof(StandardQuery) })]
public partial class StandardRegistry { }

public sealed record GetOrder(int Id) : IRequest<OrderDto>;
public sealed class Ping : IRequest;
public sealed class UnitPing : IRequest;
public sealed class WhoAmIQuery : IRequest<string>;
public sealed record StandardQuery(int Value) : IRequest<int>;

public sealed class Notice : INotification;
public sealed class FaultNotice : INotification;
public sealed record Numbers(int Count) : IStreamRequest<int>;
public sealed class FaultyNumbers : IStreamRequest<int>;

// Compile-known contracts that are deliberately omitted from every registry.
public sealed class UnregisteredQuery : IRequest<int>;
public sealed class UnregisteredNotice : INotification;
public sealed class UnregisteredNumbers : IStreamRequest<int>;
public sealed class UnrootedBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken) => next();
}

public sealed class UnregisteredQueryHandler : IRequestHandler<UnregisteredQuery, int>
{
    private static int _calls;
    public static int Calls => Volatile.Read(ref _calls);
    public Task<int> Handle(UnregisteredQuery request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(-1);
    }
}

public sealed class Marker
{
    public Marker(string name) { Name = name; }
    public string Name { get; }
    public ConcurrentQueue<ScopedDependency> Created { get; } = new();
}

public sealed class ScopedDependency : IDisposable
{
    private static int _next;
    private int _disposals;
    public ScopedDependency(Marker marker)
    {
        Marker = marker;
        Id = Interlocked.Increment(ref _next);
        marker.Created.Enqueue(this);
    }
    public int Id { get; }
    public Marker Marker { get; }
    public int Disposals => Volatile.Read(ref _disposals);
    public void Dispose() => Interlocked.Increment(ref _disposals);
    public int Lookups { get; private set; }
    public Order Find(int id)
    {
        Lookups++;
        return new() { Id = id, Name = $"order-{id}", Quantity = id * 2 };
    }
}

public sealed class CallTrace
{
    private readonly object _gate = new();
    private readonly List<string> _events = new();
    public object? LastRequest { get; set; }
    public object? LastResponse { get; set; }
    public CancellationToken LastToken { get; set; }
    public string? CorrelationSeen { get; set; }
    public List<CancellationToken> NoticeTokens { get; } = new();
    public void Log(string name) { lock (_gate) _events.Add(name); }
    public string[] Events() { lock (_gate) return _events.ToArray(); }
    public int Count(string name) { lock (_gate) return _events.Count(e => e == name); }
}

public sealed class RecordingAuditSink : IAuditSink
{
    private readonly ConcurrentQueue<AuditEntry> _entries = new();
    public AuditEntry[] Entries => _entries.ToArray();
    public Task RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        _entries.Enqueue(entry);
        return Task.CompletedTask;
    }
}

public sealed class Localizer<T> : IStringLocalizer<T>
{
    public LocalizedString this[string name] => new(name, "localized:" + name, false);
    public LocalizedString this[string name, params object[] arguments] => new(name, "localized:" + name, false);
    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
}

public sealed class GetOrderHandler(ScopedDependency dependency, CallTrace trace) : IRequestHandler<GetOrder, OrderDto>
{
    public Task<OrderDto> Handle(GetOrder request, CancellationToken cancellationToken)
    {
        trace.Log("handler:GetOrder");
        trace.LastRequest = request;
        trace.LastToken = cancellationToken;
        trace.CorrelationSeen = CorrelationContext.CorrelationId;
        var dto = Maps.MapToOrderDto(dependency.Find(request.Id));
        trace.LastResponse = dto;
        return Task.FromResult(dto);
    }
}

public sealed class GetOrderValidator(CallTrace trace) : IValidator<GetOrder>
{
    public Task<ValidationResult> ValidateAsync(GetOrder instance, CancellationToken cancellationToken)
    {
        trace.Log("validator:GetOrder");
        var result = new ValidationResult();
        if (instance.Id <= 0) result.Errors.Add(new ValidationError(nameof(GetOrder.Id), "validation.id.positive"));
        return Task.FromResult(result);
    }
}

// IRequestHandler<TRequest> (void form).
public sealed class PingHandler(CallTrace trace) : IRequestHandler<Ping>
{
    public Task Handle(Ping request, CancellationToken cancellationToken)
    {
        trace.Log("handler:Ping");
        trace.LastRequest = request;
        trace.LastToken = cancellationToken;
        return Task.CompletedTask;
    }
}

// IRequestHandler<TRequest, Unit> (Unit-returning form).
public sealed class UnitPingHandler(CallTrace trace) : IRequestHandler<UnitPing, Unit>
{
    public Task<Unit> Handle(UnitPing request, CancellationToken cancellationToken)
    {
        trace.Log("handler:UnitPing");
        trace.LastRequest = request;
        trace.LastToken = cancellationToken;
        return Task.FromResult(Unit.Value);
    }
}

public sealed class WhoAmIHandler(Marker marker, ScopedDependency dependency) : IRequestHandler<WhoAmIQuery, string>
{
    public Task<string> Handle(WhoAmIQuery request, CancellationToken cancellationToken) =>
        Task.FromResult($"{marker.Name}:{dependency.Id}");
}

public sealed class PassBehavior<TRequest, TResponse>(CallTrace trace) : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        trace.Log("pass:" + typeof(TRequest).Name);
        return next();
    }
}

public sealed class NoticeHandlerOne(CallTrace trace) : INotificationHandler<Notice>
{
    public Task Handle(Notice notification, CancellationToken cancellationToken)
    {
        trace.Log("notice:one");
        lock (trace.NoticeTokens) trace.NoticeTokens.Add(cancellationToken);
        return Task.CompletedTask;
    }
}

public sealed class NoticeHandlerTwo(CallTrace trace) : INotificationHandler<Notice>
{
    public Task Handle(Notice notification, CancellationToken cancellationToken)
    {
        trace.Log("notice:two");
        lock (trace.NoticeTokens) trace.NoticeTokens.Add(cancellationToken);
        return Task.CompletedTask;
    }
}

public sealed class FaultFirstHandler(CallTrace trace) : INotificationHandler<FaultNotice>
{
    public static readonly InvalidOperationException Failure = new("fault-first");
    public Task Handle(FaultNotice notification, CancellationToken cancellationToken)
    {
        trace.Log("fault:first");
        throw Failure;
    }
}

public sealed class FaultSecondHandler(CallTrace trace) : INotificationHandler<FaultNotice>
{
    public Task Handle(FaultNotice notification, CancellationToken cancellationToken)
    {
        trace.Log("fault:second");
        return Task.CompletedTask;
    }
}

public sealed class NumbersHandler(CallTrace trace, ScopedDependency dependency) : IStreamRequestHandler<Numbers, int>
{
    public async IAsyncEnumerable<int> Handle(Numbers request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        trace.Log("handler:enter");
        try
        {
            for (var i = 1; i <= request.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                trace.Log(dependency.Disposals == 0 ? "dep:alive" : "dep:disposed");
                yield return i * 10;
                await Task.Yield();
            }
        }
        finally
        {
            trace.Log("handler:finally");
        }
    }
}

public sealed class FaultyNumbersHandler : IStreamRequestHandler<FaultyNumbers, int>
{
    public static readonly InvalidOperationException Failure = new("stream-boom");
    public async IAsyncEnumerable<int> Handle(FaultyNumbers request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return 1;
        await Task.Yield();
        throw Failure;
    }
}

public sealed class StreamLogBehavior<TRequest, TResponse>(CallTrace trace) : IStreamPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async IAsyncEnumerable<TResponse> Handle(TRequest request, StreamHandlerDelegate<TResponse> next, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        trace.Log("behavior:enter");
        try
        {
            await foreach (var item in next().WithCancellation(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            trace.Log("behavior:finally");
        }
    }
}

public sealed class StandardHandler(CallTrace trace) : IRequestHandler<StandardQuery, int>
{
    public Task<int> Handle(StandardQuery request, CancellationToken cancellationToken)
    {
        trace.Log("handler:Standard");
        return Task.FromResult(request.Value * 2);
    }
}

public sealed class StandardValidator(CallTrace trace) : IValidator<StandardQuery>
{
    public Task<ValidationResult> ValidateAsync(StandardQuery instance, CancellationToken cancellationToken)
    {
        trace.Log("validator:Standard");
        var result = new ValidationResult();
        if (instance.Value < 0) result.Errors.Add(new ValidationError(nameof(StandardQuery.Value), "validation.value.negative"));
        return Task.FromResult(result);
    }
}

public static class FixtureServices
{
    // Direct factories supply the audit sink, typed localizers and scoped dependencies; no resource scanning,
    // exporter or open-generic container mapping is involved.
    public static IServiceCollection AddFixtureServices(this IServiceCollection services, Marker marker)
    {
        services.AddSingleton(marker);
        services.AddSingleton<RecordingAuditSink>();
        services.AddSingleton<IAuditSink>(sp => sp.GetRequiredService<RecordingAuditSink>());
        services.AddScoped<CallTrace>();
        services.AddScoped<ScopedDependency>();
        services.AddSingleton<IStringLocalizer<GetOrder>>(_ => new Localizer<GetOrder>());
        services.AddSingleton<IStringLocalizer<Ping>>(_ => new Localizer<Ping>());
        services.AddSingleton<IStringLocalizer<UnitPing>>(_ => new Localizer<UnitPing>());
        services.AddSingleton<IStringLocalizer<WhoAmIQuery>>(_ => new Localizer<WhoAmIQuery>());
        services.AddSingleton<IStringLocalizer<StandardQuery>>(_ => new Localizer<StandardQuery>());
        return services;
    }
}
