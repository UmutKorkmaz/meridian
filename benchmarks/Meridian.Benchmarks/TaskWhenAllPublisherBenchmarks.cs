using BenchmarkDotNet.Attributes;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Publishing;
using Microsoft.Extensions.DependencyInjection;
using MeridianM = Meridian.Mediator;

namespace Meridian.Benchmarks;

/// <summary>
/// The opt-in task-when-all publisher with 10 or 100 handlers for Meridian and for MediatR 12.5.0's equivalent publisher.
/// Handlers complete synchronously and only count their invocation, so this measures the publisher's own fan-out and
/// aggregation overhead. It cannot establish asynchronous I/O latency or any production parallelism gain. The providers are
/// retained and disposed in GlobalCleanup; the preflight asserts that every handler ran exactly once per publication.
/// </summary>
[Config(typeof(E3HotConfig))]
[BenchmarkCategory("E3", "mediator")]
public class E3NotificationTaskWhenAll
{
    private static readonly MyNotification Notification = new(42);
    private ServiceProvider _meridianProvider = default!;
    private ServiceProvider _mediatRProvider = default!;
    private MeridianM.IMediator _meridian = default!;
    private MediatR.IMediator _mediatR = default!;

    [Params(10, 100)]
    public int HandlerCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var meridian = new ServiceCollection();
        meridian.AddMeridianMediator(cfg => cfg.NotificationPublisherType = typeof(TaskWhenAllPublisher));
        for (var i = 0; i < HandlerCount; i++)
        {
            meridian.AddSingleton<MeridianM.INotificationHandler<MyNotification>>(new DummyHandler());
        }

        _meridianProvider = meridian.BuildServiceProvider();
        _meridian = _meridianProvider.GetRequiredService<MeridianM.IMediator>();

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

    [GlobalCleanup]
    public void Cleanup()
    {
        _meridianProvider.Dispose();
        _mediatRProvider.Dispose();
    }

    public void Preflight()
    {
        foreach (var (name, publish) in new (string, Func<Task>)[] { (nameof(MeridianTaskWhenAll), MeridianTaskWhenAll), (nameof(MediatRV12TaskWhenAll), MediatRV12TaskWhenAll) })
        {
            E3Work.Reset();
            publish().GetAwaiter().GetResult();
            E3Work.Require(E3Work.NotificationCalls == HandlerCount, $"{name}: {E3Work.NotificationCalls} handlers ran, expected {HandlerCount}.");
        }
    }

    [Benchmark(Baseline = true, Description = "Meridian TaskWhenAllPublisher")]
    public Task MeridianTaskWhenAll() => _meridian.Publish(Notification);

    [Benchmark(Description = "MediatR 12.5.0 TaskWhenAllPublisher (historical pin)")]
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
