using BenchmarkDotNet.Attributes;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Publishing;
using Microsoft.Extensions.DependencyInjection;
using MeridianM = Meridian.Mediator;

namespace Meridian.Benchmarks;

/// <summary>
/// The opt-in task-when-all publisher with 10 or 100 handlers: Meridian bounded (the default concurrency cap of 16),
/// Meridian unbounded (<c>new TaskWhenAllPublisher(-1)</c>, the legacy fan-out) and MediatR 12.5.0's publisher, which is an
/// unbounded <c>Task.WhenAll</c>. The unbounded Meridian row is the like-for-like comparison with MediatR; the bounded row
/// is a different contract (it allocates a <c>SemaphoreSlim</c> and one async state machine per handler to honor the cap)
/// and is shown separately so the cost of the cap is not attributed to the publisher's fan-out and aggregation itself.
/// Handlers complete synchronously and only count their invocation, so this cannot establish asynchronous I/O latency or any
/// production parallelism gain. The providers are retained and disposed in GlobalCleanup; the preflight asserts that every
/// handler ran exactly once per publication and that each Meridian row really uses the cap it is labeled with.
/// </summary>
[Config(typeof(E3HotConfig))]
[BenchmarkCategory("E3", "mediator")]
public class E3NotificationTaskWhenAll
{
    private static readonly MyNotification Notification = new(42);
    private ServiceProvider _boundedProvider = default!;
    private ServiceProvider _unboundedProvider = default!;
    private ServiceProvider _mediatRProvider = default!;
    private TaskWhenAllPublisher _unboundedPublisher = default!;
    private MeridianM.IMediator _bounded = default!;
    private MeridianM.IMediator _unbounded = default!;
    private MediatR.IMediator _mediatR = default!;

    [Params(10, 100)]
    public int HandlerCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var bounded = new ServiceCollection();
        bounded.AddMeridianMediator(cfg => cfg.NotificationPublisherType = typeof(TaskWhenAllPublisher));
        AddMeridianHandlers(bounded);
        _boundedProvider = bounded.BuildServiceProvider();
        _bounded = _boundedProvider.GetRequiredService<MeridianM.IMediator>();

        var unbounded = new ServiceCollection();
        _unboundedPublisher = new TaskWhenAllPublisher(-1);
        unbounded.AddMeridianMediator(cfg => cfg.NotificationPublisher = _unboundedPublisher);
        AddMeridianHandlers(unbounded);
        _unboundedProvider = unbounded.BuildServiceProvider();
        _unbounded = _unboundedProvider.GetRequiredService<MeridianM.IMediator>();

        var mediatR = new ServiceCollection();
        E3MediatR.Add(mediatR, cfg => cfg.NotificationPublisherType = typeof(MediatR.NotificationPublishers.TaskWhenAllPublisher));
        for (var i = 0; i < HandlerCount; i++)
        {
            mediatR.AddSingleton<MediatR.INotificationHandler<MyNotification>>(new DummyMediatRHandler());
        }

        _mediatRProvider = mediatR.BuildServiceProvider();
        _mediatR = _mediatRProvider.GetRequiredService<MediatR.IMediator>();

        Preflight();
    }

    private void AddMeridianHandlers(IServiceCollection services)
    {
        for (var i = 0; i < HandlerCount; i++)
        {
            services.AddSingleton<MeridianM.INotificationHandler<MyNotification>>(new DummyHandler());
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _boundedProvider.Dispose();
        _unboundedProvider.Dispose();
        _mediatRProvider.Dispose();
    }

    public void Preflight()
    {
        RequireCap(nameof(MeridianTaskWhenAllBounded16), _boundedProvider.GetRequiredService<MeridianM.Publishing.INotificationPublisher>(), 16);
        RequireCap(nameof(MeridianTaskWhenAllUnbounded), _unboundedPublisher, -1);
        foreach (var (name, publish) in new (string, Func<Task>)[]
                 {
                     (nameof(MeridianTaskWhenAllUnbounded), MeridianTaskWhenAllUnbounded),
                     (nameof(MeridianTaskWhenAllBounded16), MeridianTaskWhenAllBounded16),
                     (nameof(MediatRV12TaskWhenAll), MediatRV12TaskWhenAll),
                 })
        {
            E3Work.Reset();
            publish().GetAwaiter().GetResult();
            E3Work.Require(E3Work.NotificationCalls == HandlerCount, $"{name}: {E3Work.NotificationCalls} handlers ran, expected {HandlerCount}.");
        }
    }

    /// <summary>The row label is a contract: the publisher behind it must be a Meridian task-when-all publisher with exactly this cap.</summary>
    internal static void RequireCap(string row, object publisher, int expected)
    {
        var field = typeof(TaskWhenAllPublisher).GetField("_maxDegreeOfParallelism", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        E3Work.Require(field is not null, "TaskWhenAllPublisher no longer exposes the _maxDegreeOfParallelism field the preflight reads; update the preflight.");
        E3Work.Require(publisher.GetType() == typeof(TaskWhenAllPublisher), $"{row}: the publisher is {publisher.GetType().Name}, not TaskWhenAllPublisher.");
        var actual = (int)field!.GetValue(publisher)!;
        E3Work.Require(actual == expected, $"{row}: the publisher cap is {actual} but the row is labeled {expected}.");
    }

    [Benchmark(Baseline = true, Description = "Meridian TaskWhenAllPublisher, unbounded (-1): like-for-like with MediatR")]
    public Task MeridianTaskWhenAllUnbounded() => _unbounded.Publish(Notification);

    [Benchmark(Description = "Meridian TaskWhenAllPublisher, bounded default (cap of 16): a different contract")]
    public Task MeridianTaskWhenAllBounded16() => _bounded.Publish(Notification);

    [Benchmark(Description = "MediatR 12.5.0 TaskWhenAllPublisher, unbounded (historical pin)")]
    public Task MediatRV12TaskWhenAll() => _mediatR.Publish(Notification);
}

public sealed record MyNotification(int Value) : MeridianM.INotification, MediatR.INotification;

public sealed class DummyHandler : MeridianM.INotificationHandler<MyNotification>
{
    public Task Handle(MyNotification notification, CancellationToken cancellationToken)
    {
        E3Work.NotificationCalls++;
        return Task.CompletedTask;
    }
}

public sealed class DummyMediatRHandler : MediatR.INotificationHandler<MyNotification>
{
    public Task Handle(MyNotification notification, CancellationToken cancellationToken)
    {
        E3Work.NotificationCalls++;
        return Task.CompletedTask;
    }
}
