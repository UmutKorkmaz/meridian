using BenchmarkDotNet.Attributes;
using MediatR;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;
using MeridianM = Meridian.Mediator;

namespace Meridian.Benchmarks;

/// <summary>
/// Compares Meridian.Mediator against MediatR v12 (last MIT) on a plain
/// request/response dispatch with no pipeline behaviors. Representative of
/// real CQRS workloads where the bulk of requests carry a single
/// validation behavior wrapping ~100 handlers — common in line-of-business
/// .NET applications.
/// </summary>
[MemoryDiagnoser]
public class MediatorBenchmarks
{
    private MeridianM.IMediator _meridian = default!;
    private MediatR.IMediator _mediatR = default!;
    private static readonly Ping _request = new(42);

    [GlobalSetup]
    public void Setup()
    {
        var mediatorServices = new ServiceCollection();
        mediatorServices.AddMeridianMediator(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(MediatorBenchmarks).Assembly);
        });
        var mediatorProvider = mediatorServices.BuildServiceProvider();
        _meridian = mediatorProvider.GetRequiredService<MeridianM.IMediator>();

        var mediatRServices = new ServiceCollection();
        mediatRServices.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssembly(typeof(MediatorBenchmarks).Assembly);
        });
        var mediatRProvider = mediatRServices.BuildServiceProvider();
        _mediatR = mediatRProvider.GetRequiredService<MediatR.IMediator>();
    }

    [Benchmark(Baseline = true, Description = "Meridian.Mediator — Send")]
    public Task<Pong> Meridian() => _meridian.Send(_request);

    [Benchmark(Description = "MediatR v12 — Send")]
    public Task<Pong> MediatR() => _mediatR.Send(_request);
}

// Request/handler shapes visible to BOTH mediators — each lib reads its own
// interfaces at registration time via assembly scanning.

public sealed record Ping(int Value) : MeridianM.IRequest<Pong>, MediatR.IRequest<Pong>;

public sealed record Pong(int Value);

public sealed class PingMeridianHandler : MeridianM.IRequestHandler<Ping, Pong>
{
    public Task<Pong> Handle(Ping request, CancellationToken cancellationToken)
        => Task.FromResult(new Pong(request.Value + 1));
}

public sealed class PingMediatRHandler : MediatR.IRequestHandler<Ping, Pong>
{
    public Task<Pong> Handle(Ping request, CancellationToken cancellationToken)
        => Task.FromResult(new Pong(request.Value + 1));
}
