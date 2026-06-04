using BenchmarkDotNet.Attributes;
using Meridian.Mediator;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Publishing;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Benchmarks;

[MemoryDiagnoser]
public class TaskWhenAllPublisherBenchmarks
{
    private IMediator _mediator = default!;
    private static readonly MyNotification _notification = new(42);

    [Params(10, 100)]
    public int HandlerCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();

        services.AddMeridianMediator(cfg =>
        {
            cfg.NotificationPublisherType = typeof(TaskWhenAllPublisher);
        });

        for (int i = 0; i < HandlerCount; i++)
        {
            services.AddSingleton<INotificationHandler<MyNotification>>(new DummyHandler());
        }

        var provider = services.BuildServiceProvider();
        _mediator = provider.GetRequiredService<IMediator>();
    }

    [Benchmark(Baseline = true)]
    public Task Publish_TaskWhenAll() => _mediator.Publish(_notification);
}

public sealed record MyNotification(int Value) : INotification;

public sealed class DummyHandler : INotificationHandler<MyNotification>
{
    public Task Handle(MyNotification notification, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
