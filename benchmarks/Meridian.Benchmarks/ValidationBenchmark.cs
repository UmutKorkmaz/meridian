using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Meridian.Mediator.Extensions;
using System.Reflection;

namespace Meridian.Benchmarks;

[MemoryDiagnoser]
public class ValidationBenchmark
{
    private IServiceProvider _provider = default!;
    private Assembly[] _assemblies = default!;

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddMeridianMediator(cfg => cfg.RegisterServicesFromAssembly(typeof(ValidationBenchmark).Assembly));
        _provider = services.BuildServiceProvider();
        _assemblies = new[] { typeof(ValidationBenchmark).Assembly, typeof(ValidationBenchmark).Assembly, typeof(ValidationBenchmark).Assembly };
    }

    [Benchmark(Baseline = true)]
    public void AssertHandlerRegistration()
    {
        _provider.AssertHandlerRegistration(_assemblies);
    }
}
