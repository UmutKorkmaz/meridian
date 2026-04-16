using BenchmarkDotNet.Running;

namespace Meridian.Benchmarks;

/// <summary>
/// Benchmark entry point. Run with: <c>dotnet run -c Release -- *</c> to run
/// every benchmark, or <c>dotnet run -c Release -- --filter *Mapping*</c> for
/// a subset. See BenchmarkDotNet docs for more filter options.
/// </summary>
public static class Program
{
    public static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
