using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Meridian.Mediator;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Benchmarks;

public class TestNotification1 : INotification { }
public class TestNotification2 : INotification { }
public class TestNotification3 : INotification { }
public class TestNotification4 : INotification { }
public class TestNotification5 : INotification { }

public class TestNotification1Handler : INotificationHandler<TestNotification1> { public Task Handle(TestNotification1 notification, CancellationToken cancellationToken) => Task.CompletedTask; }
public class TestNotification2Handler : INotificationHandler<TestNotification2> { public Task Handle(TestNotification2 notification, CancellationToken cancellationToken) => Task.CompletedTask; }
public class TestNotification3Handler : INotificationHandler<TestNotification3> { public Task Handle(TestNotification3 notification, CancellationToken cancellationToken) => Task.CompletedTask; }
public class TestNotification4Handler : INotificationHandler<TestNotification4> { public Task Handle(TestNotification4 notification, CancellationToken cancellationToken) => Task.CompletedTask; }
public class TestNotification5Handler : INotificationHandler<TestNotification5> { public Task Handle(TestNotification5 notification, CancellationToken cancellationToken) => Task.CompletedTask; }


[MemoryDiagnoser]
public class ValidationBenchmarks
{
    private IServiceProvider _provider = default!;

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(ValidationBenchmarks).Assembly);
        });

        _provider = services.BuildServiceProvider();
    }

    [Benchmark]
    public void AssertHandlerRegistration()
    {
        _provider.AssertHandlerRegistration(typeof(ValidationBenchmarks).Assembly);
    }
}
