using BenchmarkDotNet.Attributes;
using MediatR;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;
using MeridianM = Meridian.Mediator;

namespace Meridian.Benchmarks;

[MemoryDiagnoser]
public class NotificationBenchmarks
{
    private MeridianM.IMediator _meridian = default!;
    private MediatR.IMediator _mediatR = default!;
    private static readonly TestNotification _notification = new();

    [GlobalSetup]
    public void Setup()
    {
        var mediatorServices = new ServiceCollection();
        mediatorServices.AddMeridianMediator(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(NotificationBenchmarks).Assembly);
        });
        var mediatorProvider = mediatorServices.BuildServiceProvider();
        _meridian = mediatorProvider.GetRequiredService<MeridianM.IMediator>();

        var mediatRServices = new ServiceCollection();
        mediatRServices.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(NotificationBenchmarks).Assembly);
        });
        var mediatRProvider = mediatRServices.BuildServiceProvider();
        _mediatR = mediatRProvider.GetRequiredService<MediatR.IMediator>();
    }

    [Benchmark(Baseline = true, Description = "Meridian.Mediator — Publish")]
    public Task MeridianPublish() => _meridian.Publish(_notification);

    [Benchmark(Description = "MediatR v12 — Publish")]
    public Task MediatRPublish() => _mediatR.Publish(_notification);
}

public sealed record TestNotification() : MeridianM.INotification, MediatR.INotification;

public sealed class TestNotificationMeridianHandler1 : MeridianM.INotificationHandler<TestNotification>
{
    public Task Handle(TestNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class TestNotificationMeridianHandler2 : MeridianM.INotificationHandler<TestNotification>
{
    public Task Handle(TestNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class TestNotificationMeridianHandler3 : MeridianM.INotificationHandler<TestNotification>
{
    public Task Handle(TestNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class TestNotificationMediatRHandler1 : MediatR.INotificationHandler<TestNotification>
{
    public Task Handle(TestNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class TestNotificationMediatRHandler2 : MediatR.INotificationHandler<TestNotification>
{
    public Task Handle(TestNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class TestNotificationMediatRHandler3 : MediatR.INotificationHandler<TestNotification>
{
    public Task Handle(TestNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
}
