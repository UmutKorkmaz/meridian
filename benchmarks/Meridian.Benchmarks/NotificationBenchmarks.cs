using BenchmarkDotNet.Attributes;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;
using MeridianM = Meridian.Mediator;

namespace Meridian.Benchmarks;

/// <summary>
/// Publish one notification to three handlers with each library's DEFAULT (sequential) publisher: Meridian runtime and
/// MediatR 12.5.0 (historical pin). Every handler does the identical advertised work (it counts its invocation), the
/// providers are retained and disposed in GlobalCleanup, and the preflight asserts that exactly three handlers ran for one
/// publication in both libraries. Handlers complete synchronously: this says nothing about asynchronous handler latency.
/// </summary>
[Config(typeof(E3HotConfig))]
[BenchmarkCategory("E3", "mediator")]
public class E3NotificationPublish
{
    private static readonly TestNotification Notification = new();
    private ServiceProvider _meridianProvider = default!;
    private ServiceProvider _mediatRProvider = default!;
    private MeridianM.IMediator _meridian = default!;
    private MediatR.IMediator _mediatR = default!;

    [GlobalSetup]
    public void Setup()
    {
        var meridian = new ServiceCollection();
        meridian.AddTransient<MeridianM.INotificationHandler<TestNotification>, TestNotificationMeridianHandler1>();
        meridian.AddTransient<MeridianM.INotificationHandler<TestNotification>, TestNotificationMeridianHandler2>();
        meridian.AddTransient<MeridianM.INotificationHandler<TestNotification>, TestNotificationMeridianHandler3>();
        meridian.AddMeridianMediator(_ => { });
        _meridianProvider = meridian.BuildServiceProvider();
        _meridian = _meridianProvider.GetRequiredService<MeridianM.IMediator>();

        var mediatR = new ServiceCollection();
        mediatR.AddTransient<MediatR.INotificationHandler<TestNotification>, TestNotificationMediatRHandler1>();
        mediatR.AddTransient<MediatR.INotificationHandler<TestNotification>, TestNotificationMediatRHandler2>();
        mediatR.AddTransient<MediatR.INotificationHandler<TestNotification>, TestNotificationMediatRHandler3>();
        E3MediatR.Add(mediatR);
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
        foreach (var (name, publish) in new (string, Func<Task>)[] { (nameof(MeridianPublish), MeridianPublish), (nameof(MediatRV12Publish), MediatRV12Publish) })
        {
            E3Work.Reset();
            publish().GetAwaiter().GetResult();
            E3Work.Require(E3Work.NotificationCalls == 3, $"{name}: {E3Work.NotificationCalls} handlers ran, expected 3.");
        }
    }

    [Benchmark(Baseline = true, Description = "Meridian runtime Publish x3 (sequential default)")]
    public Task MeridianPublish() => _meridian.Publish(Notification);

    [Benchmark(Description = "MediatR 12.5.0 Publish x3 (historical pin, sequential default)")]
    public Task MediatRV12Publish() => _mediatR.Publish(Notification);
}

public sealed record TestNotification() : MeridianM.INotification, MediatR.INotification;

public sealed class TestNotificationMeridianHandler1 : MeridianM.INotificationHandler<TestNotification>
{
    public Task Handle(TestNotification notification, CancellationToken cancellationToken)
    {
        E3Work.NotificationCalls++;
        return Task.CompletedTask;
    }
}

public sealed class TestNotificationMeridianHandler2 : MeridianM.INotificationHandler<TestNotification>
{
    public Task Handle(TestNotification notification, CancellationToken cancellationToken)
    {
        E3Work.NotificationCalls++;
        return Task.CompletedTask;
    }
}

public sealed class TestNotificationMeridianHandler3 : MeridianM.INotificationHandler<TestNotification>
{
    public Task Handle(TestNotification notification, CancellationToken cancellationToken)
    {
        E3Work.NotificationCalls++;
        return Task.CompletedTask;
    }
}

public sealed class TestNotificationMediatRHandler1 : MediatR.INotificationHandler<TestNotification>
{
    public Task Handle(TestNotification notification, CancellationToken cancellationToken)
    {
        E3Work.NotificationCalls++;
        return Task.CompletedTask;
    }
}

public sealed class TestNotificationMediatRHandler2 : MediatR.INotificationHandler<TestNotification>
{
    public Task Handle(TestNotification notification, CancellationToken cancellationToken)
    {
        E3Work.NotificationCalls++;
        return Task.CompletedTask;
    }
}

public sealed class TestNotificationMediatRHandler3 : MediatR.INotificationHandler<TestNotification>
{
    public Task Handle(TestNotification notification, CancellationToken cancellationToken)
    {
        E3Work.NotificationCalls++;
        return Task.CompletedTask;
    }
}
