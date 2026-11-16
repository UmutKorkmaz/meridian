using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using Meridian.Mediator.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Perfolizer.Horology;
using MeridianM = Meridian.Mediator;

namespace Meridian.Benchmarks;

/// <summary>
/// Benchmark entry point. Prefer <c>eng/run-benchmarks.ps1</c>, which runs the semantic preflight, records the host and
/// candidate provenance and then calls this executable. Direct use:
/// <list type="bullet">
/// <item><c>dotnet run -c Release -- --filter "*E3MappingFlat*"</c> runs scenarios with BenchmarkDotNet's own options.</item>
/// <item><c>-- --e3-preflight [ClassName ...]</c> runs the semantic preflight of every (or the named) E3 scenario class
/// in this process and exits non-zero if any scenario produces a result that is not equivalent to its expectation.</item>
/// <item><c>-- --e3-preflight-selftest</c> proves that the preflight rejects deliberately wrong results.</item>
/// </list>
/// Scenario classes whose name starts with <c>E3</c> are the E3 evidence set; the other classes are the frozen B8 (H1) and D5
/// harnesses that are only run for their paired comparisons.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--e3-preflight")
        {
            return E3Preflight.Run(args.Skip(1).ToArray(), Console.Out);
        }

        if (args.Length > 0 && args[0] == "--e3-preflight-selftest")
        {
            return E3Preflight.SelfTest(Console.Out);
        }

        var summaries = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args).ToArray();
        var listingOnly = args.Any(a => a is "--list" or "--info" or "--help" or "-h");
        if (listingOnly)
        {
            return 0;
        }

        // A run that produced no reports, failed validation or failed any benchmark must not look like success.
        var failed = summaries.Length == 0
            || summaries.Any(s => s.HasCriticalValidationErrors || s.Reports.Length == 0 || s.Reports.Any(r => !r.Success));
        return failed ? 1 : 0;
    }
}

/// <summary>
/// The one documented job for the E3 scenarios. Hot rows: 3 launches, 6 warmup and 15 measured iterations of 250 ms
/// each, with the benchmark project's runtime configuration (concurrent server GC, tiered compilation on) and
/// <see cref="MemoryDiagnoser"/>. A short fixed job gives a bounded total runtime; it is a reviewed starting
/// configuration, not a promise of adequate precision for every row, so launch spread and variance are reported with
/// every result. The <c>Smoke</c> profile (environment variable <c>MERIDIAN_BENCH_PROFILE=Smoke</c>) proves that the
/// run executes and is never evidence of speed.
/// </summary>
public abstract class E3ConfigBase : ManualConfig
{
    internal const string ProfileVariable = "MERIDIAN_BENCH_PROFILE";

    protected E3ConfigBase(Job job)
    {
        AddJob(job);
        AddDiagnoser(MemoryDiagnoser.Default);
        AddColumnProvider(DefaultColumnProviders.Instance);
        AddLogger(ConsoleLogger.Default);
        AddExporter(JsonExporter.Full);
        AddExporter(CsvExporter.Default);
        AddExporter(MarkdownExporter.GitHub);
    }

    internal static bool IsSmoke =>
        string.Equals(Environment.GetEnvironmentVariable(ProfileVariable), "Smoke", StringComparison.OrdinalIgnoreCase);

    // One short invocation per launch; used by the smoke profile for every scenario class.
    internal static Job SmokeJob => new Job("E3-smoke").WithStrategy(RunStrategy.ColdStart)
        .WithLaunchCount(1).WithWarmupCount(0).WithIterationCount(1).WithInvocationCount(1).WithUnrollFactor(1);
}

/// <summary>Steady-state in-process scenarios.</summary>
public sealed class E3HotConfig : E3ConfigBase
{
    public E3HotConfig()
        : base(IsSmoke ? SmokeJob : new Job("E3-hot")
            .WithLaunchCount(3).WithWarmupCount(6).WithIterationCount(15)
            .WithIterationTime(TimeInterval.FromMilliseconds(250)))
    {
    }
}

/// <summary>
/// Fresh-process first-use scenarios: ten fresh launches, no warmup, one measured invocation per launch. The measured
/// region starts after the benchmark harness is running, so process launch and runtime initialization are excluded;
/// JIT, type loading and static initialization of the library code under test are included. GlobalSetup in these
/// classes only builds plain payload objects: the semantic checks run in a separate preflight process so they cannot
/// warm the measured process.
/// </summary>
public sealed class E3ColdConfig : E3ConfigBase
{
    public E3ColdConfig()
        : base(IsSmoke ? SmokeJob : new Job("E3-cold")
            .WithStrategy(RunStrategy.ColdStart)
            .WithLaunchCount(10).WithWarmupCount(0).WithIterationCount(1).WithInvocationCount(1).WithUnrollFactor(1))
    {
    }
}

/// <summary>
/// Semantic preflight. For every E3 scenario class and every combination of its <c>[Params]</c> values it builds the
/// state exactly as <c>[GlobalSetup]</c> does, runs the class's own <c>Preflight()</c> equivalence checks (when it has
/// one), invokes every <c>[Benchmark]</c> method once and runs <c>[GlobalCleanup]</c>. A scenario that cannot produce
/// the expected output fails the run before any timing happens.
/// </summary>
internal static class E3Preflight
{
    internal static IEnumerable<Type> ScenarioTypes() => typeof(Program).Assembly.GetTypes()
        .Where(t => t is { IsClass: true, IsAbstract: false } && t.Name.StartsWith("E3", StringComparison.Ordinal))
        .Where(t => t.GetMethods().Any(m => m.GetCustomAttribute<BenchmarkAttribute>() is not null))
        .OrderBy(t => t.Name, StringComparer.Ordinal);

    /// <summary>
    /// BenchmarkDotNet validates every type that has a benchmark method (public, unsealed, not abstract, top level) as soon as
    /// any run starts, even when a filter excludes it. Catching a violation here fails the preflight instead of the timed run.
    /// </summary>
    internal static IEnumerable<Type> UndiscoverableBenchmarkTypes() => typeof(Program).Assembly.GetTypes()
        .Where(t => t.GetMethods().Any(m => m.GetCustomAttribute<BenchmarkAttribute>() is not null))
        .Where(BdnWouldReject);

    internal static bool BdnWouldReject(Type type) => !type.IsPublic || type.IsSealed || type.IsAbstract || type.IsNested;

    internal static int Run(string[] classNames, TextWriter log)
    {
        var undiscoverable = UndiscoverableBenchmarkTypes().ToList();
        if (undiscoverable.Count > 0)
        {
            log.WriteLine("PREFLIGHT FAIL benchmark types that BenchmarkDotNet would reject: " + string.Join(", ", undiscoverable.Select(t => t.FullName)));
            return 1;
        }

        var all = ScenarioTypes().ToList();
        var selected = all;
        if (classNames.Length > 0)
        {
            var unknown = classNames.Where(n => all.All(t => t.Name != n)).ToList();
            if (unknown.Count > 0)
            {
                log.WriteLine("PREFLIGHT FAIL unknown scenario class: " + string.Join(", ", unknown));
                return 2;
            }

            selected = all.Where(t => classNames.Contains(t.Name)).ToList();
        }

        var failures = 0;
        foreach (var type in selected)
        {
            if (!RunType(type, log))
            {
                failures++;
            }
        }

        log.WriteLine($"PREFLIGHT summary classes={selected.Count} failed={failures}");
        return failures == 0 ? 0 : 1;
    }

    internal static bool RunType(Type type, TextWriter log)
    {
        var combinations = ParameterCombinations(type).ToList();
        var benchmarks = type.GetMethods().Where(m => m.GetCustomAttribute<BenchmarkAttribute>() is not null).ToList();
        var ok = true;
        foreach (var combination in combinations)
        {
            var label = combination.Count == 0 ? "" : " [" + string.Join(", ", combination.Select(p => $"{p.Key.Name}={p.Value}")) + "]";
            object? instance = null;
            try
            {
                instance = Activator.CreateInstance(type)!;
                foreach (var (property, value) in combination)
                {
                    property.SetValue(instance, Convert.ChangeType(value, property.PropertyType, System.Globalization.CultureInfo.InvariantCulture));
                }

                foreach (var setup in type.GetMethods().Where(m => m.GetCustomAttribute<GlobalSetupAttribute>() is not null))
                {
                    Await(setup.Invoke(instance, null));
                }

                var preflight = type.GetMethod("Preflight", BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes);
                if (preflight is not null)
                {
                    Await(preflight.Invoke(instance, null));
                }

                foreach (var benchmark in benchmarks)
                {
                    Await(benchmark.Invoke(instance, null));
                }
            }
            catch (Exception exception)
            {
                var inner = exception is TargetInvocationException { InnerException: not null } wrapped ? wrapped.InnerException! : exception;
                log.WriteLine($"PREFLIGHT FAIL {type.Name}{label}: {inner.GetType().Name}: {inner.Message}");
                ok = false;
            }
            finally
            {
                if (instance is not null)
                {
                    foreach (var cleanup in type.GetMethods().Where(m => m.GetCustomAttribute<GlobalCleanupAttribute>() is not null))
                    {
                        try
                        {
                            Await(cleanup.Invoke(instance, null));
                        }
                        catch (Exception exception)
                        {
                            log.WriteLine($"PREFLIGHT FAIL {type.Name}{label}: cleanup: {exception.Message}");
                            ok = false;
                        }
                    }

                    (instance as IDisposable)?.Dispose();
                }
            }
        }

        if (ok)
        {
            log.WriteLine($"PREFLIGHT ok {type.Name} combinations={combinations.Count} benchmarks={benchmarks.Count}");
        }

        return ok;
    }

    private static void Await(object? result)
    {
        switch (result)
        {
            case Task task:
                task.GetAwaiter().GetResult();
                break;
            case null:
                break;
            default:
                if (result.GetType().IsGenericType && result.GetType().GetGenericTypeDefinition() == typeof(ValueTask<>))
                {
                    throw new NotSupportedException("ValueTask benchmark results are not supported by the preflight runner.");
                }

                break;
        }
    }

    private static IEnumerable<List<KeyValuePair<PropertyInfo, object>>> ParameterCombinations(Type type)
    {
        var parameters = type.GetProperties()
            .Select(p => (Property: p, Attribute: p.GetCustomAttribute<ParamsAttribute>()))
            .Where(p => p.Attribute is not null)
            .OrderBy(p => p.Property.Name, StringComparer.Ordinal)
            .ToList();
        IEnumerable<List<KeyValuePair<PropertyInfo, object>>> product = new[] { new List<KeyValuePair<PropertyInfo, object>>() };
        foreach (var (property, attribute) in parameters)
        {
            var values = attribute!.Values;
            product = product.SelectMany(prefix => values.Select(value =>
                new List<KeyValuePair<PropertyInfo, object>>(prefix) { new(property, value!) }));
        }

        return product;
    }

    /// <summary>
    /// Proves that the equivalence machinery is not vacuous: every deliberately wrong result below must be rejected.
    /// </summary>
    internal static int SelfTest(TextWriter log)
    {
        var rejected = 0;
        var accepted = new List<string>();

        void MustReject(string name, Action action)
        {
            try
            {
                action();
                accepted.Add(name);
            }
            catch (InvalidOperationException)
            {
                rejected++;
            }
        }

        MustReject("value mismatch", () => NestedPathSupport.DeepCompare(
            new SourceAddress { Line1 = "a", Line2 = "b", City = "c", PostalCode = "d", Country = "e" },
            new SourceAddress { Line1 = "a", Line2 = "b", City = "DIFFERENT", PostalCode = "d", Country = "e" }));
        MustReject("null mismatch", () => NestedPathSupport.DeepCompare(
            OrderPayload.Create(), WithShipping(OrderPayload.Create(), null)));
        MustReject("collection count mismatch", () =>
        {
            var left = BasketPayload.Create(3);
            var right = BasketPayload.Create(3);
            right.Lines.RemoveAt(2);
            NestedPathSupport.DeepCompare(left, right);
        });
        MustReject("aliased nested instance", () =>
        {
            var left = OrderPayload.Create();
            var right = OrderPayload.Create();
            right.ShippingAddress = left.ShippingAddress;
            NestedPathSupport.DeepCompare(left, right);
        });
        MustReject("default-valued leaf", () => NestedPathSupport.DeepCompare(new FlatSource(), new FlatSource()));
        MustReject("leaf count mismatch", () => NestedPathSupport.Require("selftest", 9, 10));
        MustReject("broken reference identity", () =>
        {
            var result = ReferencePayload.ManualMap(ReferencePayload.Create());
            result.Right = new RefNodeDto { Id = result.Left.Id, Name = result.Left.Name };
            ReferencePayload.Verify("selftest", ReferencePayload.Create(), result);
        });
        MustReject("collapsed distinct identity", () =>
        {
            var result = ReferencePayload.ManualMap(ReferencePayload.Create());
            result.Distinct = result.Left;
            ReferencePayload.Verify("selftest", ReferencePayload.Create(), result);
        });
        MustReject("throwing scenario preflight", () =>
        {
            using var buffer = new StringWriter();
            if (!RunType(typeof(SabotagedScenario), buffer))
            {
                throw new InvalidOperationException(buffer.ToString());
            }
        });

        MustReject("benchmark type BenchmarkDotNet would reject", () =>
        {
            if (!BdnWouldReject(typeof(SabotagedScenario)) || BdnWouldReject(typeof(E3MappingFlat)))
            {
                return;
            }

            throw new InvalidOperationException("detected: a sealed nested internal type is rejected, a public top-level unsealed type is accepted");
        });

        MustReject("factory-registered scoped handler (extra DI hop)", () =>
        {
            var services = E3Scoped.BuildServices();
            services.RemoveAll<MeridianM.IRequestHandler<ScopedPing, Pong>>();
            services.AddTransient<MeridianM.IRequestHandler<ScopedPing, Pong>>(sp => sp.GetRequiredService<E3ScopedPingMeridianHandler>());
            E3Scoped.RequireSymmetricHandlerRegistrations(services);
        });
        MustReject("publisher cap different from the row label", () =>
            E3NotificationTaskWhenAll.RequireCap("selftest", new TaskWhenAllPublisher(), -1));

        foreach (var name in accepted)
        {
            log.WriteLine("SELFTEST FAIL accepted a wrong result: " + name);
        }

        if (accepted.Count > 0)
        {
            return 1;
        }

        log.WriteLine($"SELFTEST PASS rejected={rejected}");
        return 0;

        static SourceOrder WithShipping(SourceOrder order, SourceAddress? address)
        {
            order.ShippingAddress = address!;
            return order;
        }
    }

    /// <summary>
    /// A scenario whose preflight deliberately fails. It has no benchmark method, so BenchmarkDotNet never discovers it, and
    /// its name does not start with E3, so the preflight never discovers it either; only the self-test runs it.
    /// </summary>
    internal sealed class SabotagedScenario
    {
        private int _state;

        [GlobalSetup]
        public void Setup() => _state = 1;

        public void Preflight() => throw new InvalidOperationException("sabotaged preflight must be reported as a failure (state " + _state + ")");
    }
}
