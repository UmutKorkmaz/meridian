#pragma warning disable CA1707 // Contract test names are deliberately descriptive.
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Diagnostics;
using Meridian.Mediator.Extensions;
using Meridian.Mediator.Streaming;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Tests;

[Collection("Metrics")]
public class MetricsPrivacyTests
{
    private static readonly string[] AllowedKeys = { "request.type", "outcome", "phase", "state", "reason", "mode" };

    private static readonly HashSet<string> FiniteValues = new(StringComparer.Ordinal)
    {
        "success", "failure", "cancellation", "disposed",
        "dispatch", "enumeration",
        "hit", "miss", "error",
        "denied", "missing_handler",
        "legacy", "atomic", "leased",
        "claimed", "replayed", "in_flight", "completed", "released", "lease_lost", "uncertain",
        MediatorMetrics.OverflowLabel,
    };

    [Fact]
    public async Task Disabled_Metrics_Do_Not_Emit()
    {
        foreach (var options in new MediatorMetricsOptions?[] { null, new MediatorMetricsOptions { MaximumRequestTypes = 5 } })
        {
            using var capture = new MetricCapture(null, MediatorMetrics.MeterName);
            using var provider = Mx.Build(
                s =>
                {
                    s.AddTransient<IRequestHandler<MxQuery, int>, MxQueryHandler>();
                    s.AddTransient<IRequestHandler<MxCommand>, MxCommandHandler>();
                    s.AddTransient<INotificationHandler<MxNote>, MxNoteHandler>();
                    s.AddTransient<IStreamRequestHandler<MxStream, int>, MxStreamHandler>();
                    s.AddTransient<IRequestHandler<MxSecretRequest, int>, MxSecretHandler>();
                    s.AddSingleton<ICacheProvider, MxMemoryCache>();
                    s.AddSingleton<IIdempotencyStore, MxIdempotencyStore>();
                    s.AddSingleton<IAuthorizationHandler<MxSecretRequest>, MxDenyAuthorization>();
                },
                c =>
                {
                    c.AddOpenBehavior(typeof(RetryBehavior<,>));
                    c.AddOpenBehavior(typeof(AuthorizationBehavior<,>));
                    c.AddOpenBehavior(typeof(CachingBehavior<,>));
                    c.AddOpenBehavior(typeof(IdempotencyBehavior<,>));
                },
                options);

            var metrics = Mx.MetricsOf(provider);
            Assert.False(metrics.IsEnabled);
            Assert.Null(metrics.Meter);
            var mediator = provider.GetRequiredService<IMediator>();
            Assert.Equal(2, await mediator.Send(new MxQuery(1)));
            Assert.Equal(2, await mediator.Send((object)new MxQuery(1)) is int i ? i : -1);
            await mediator.Send(new MxCommand());
            await mediator.Publish(new MxNote());
            await foreach (var _ in mediator.CreateStream(new MxStream()))
            {
            }

            await Assert.ThrowsAsync<UnauthorizedException>(() => mediator.Send(new MxSecretRequest()));
            await Assert.ThrowsAsync<ArgumentNullException>(() => mediator.Send((IRequest<int>)null!));

            Assert.Empty(capture.PublishedInstruments);
            Assert.Empty(capture.All);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Metric_Tags_Exclude_Keys_Payload_User_And_Exception_Text(bool legacyDetailsEnabled)
    {
        using var provider = Mx.Build(
            s =>
            {
                s.AddTransient<IRequestHandler<MxSecretRequest, int>, MxSecretHandler>();
                s.AddTransient<IRequestHandler<MxQuery, int>, MxQueryHandler>();
                s.AddTransient<IRequestHandler<MxSecretValidated, int>, MxSecretValidatedHandler>();
                s.AddSingleton<ICacheProvider, MxMemoryCache>();
                s.AddSingleton<IIdempotencyStore, MxIdempotencyStore>();
                s.AddSingleton<IValidator<MxSecretValidated>, MxSecretValidator>();
                s.AddTransient<IAuthorizationHandler<MxSecretRequest>, MxAllowThenFail>();
                if (legacyDetailsEnabled)
                {
                    s.AddSingleton(new MediatorTelemetryOptions { RecordExceptionMessage = true, RecordExceptionStackTrace = true });
                }
            },
            c =>
            {
                c.AddOpenBehavior(typeof(ValidationBehavior<,>));
                c.AddOpenBehavior(typeof(RetryBehavior<,>));
                c.AddOpenBehavior(typeof(AuthorizationBehavior<,>));
                c.AddOpenBehavior(typeof(CachingBehavior<,>));
                c.AddOpenBehavior(typeof(IdempotencyBehavior<,>));
            },
            Mx.On());
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        var mediator = provider.GetRequiredService<IMediator>();

        // Cache key, idempotency key, user, payload and exception text all flow through the pipeline.
        var request = new MxSecretRequest();
        await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Send(request));
        await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Send(request));
        await Assert.ThrowsAsync<ValidationException>(() => mediator.Send(new MxSecretValidated()));
        Assert.Equal(2, await mediator.Send(new MxQuery(1)));

        Assert.NotEmpty(capture.All);
        var sentinels = new[] { MxSecretRequest.Key, MxSecretRequest.Payload, MxSecretRequest.User, MxSecretRequest.Reason, MxSecretRequest.ExceptionText, MxSecretValidator.ErrorText, nameof(MxSecretHandler), "meridian:cache" };
        foreach (var measurement in capture.All)
        {
            foreach (var tag in measurement.Tags)
            {
                Assert.Contains(tag.Key, AllowedKeys);
                var value = Assert.IsType<string>(tag.Value);
                if (tag.Key == "request.type")
                {
                    Assert.True(
                        value == MediatorMetrics.OverflowLabel || value == Mx.Label(typeof(MxSecretRequest)) ||
                        value == Mx.Label(typeof(MxSecretValidated)) || value == Mx.Label(typeof(MxQuery)),
                        value);
                }
                else
                {
                    Assert.Contains(value, FiniteValues);
                }

                foreach (var sentinel in sentinels)
                {
                    Assert.DoesNotContain(sentinel, value, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(sentinel, tag.Key, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        // The behaviors really emitted the privacy-relevant events (and not just the request counters).
        Assert.True(capture.CountOf(Mx.CacheLookup) > 0);
        Assert.True(capture.CountOf(Mx.IdempotencyState) > 0);
        Assert.True(capture.CountOf(Mx.ValidationRejection) > 0);
        Assert.True(capture.CountOf(Mx.RequestCount, ("outcome", "failure")) > 0);
    }

    [Fact]
    public async Task Unknown_Types_Use_One_Overflow_Bucket()
    {
        using var provider = Mx.Build(
            s =>
            {
                s.AddTransient<IRequestHandler<MxQuery, int>, MxQueryHandler>();
                s.AddTransient<IRequestHandler<MxSecretRequest, int>, MxSecretHandler>();
                s.AddTransient<IRequestHandler<MxSecretValidated, int>, MxSecretValidatedHandler>();
            },
            c => { },
            Mx.On(maximum: 1),
            afterMediator: s =>
            {
                // Registered after AddMeridianMediator: unknown to the startup snapshot.
                s.AddTransient<IRequestHandler<MxGeneric<int>, int>, MxGenericHandler<int>>();
                s.AddTransient<IRequestHandler<MxGeneric<string>, int>, MxGenericHandler<string>>();
            });
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        var mediator = provider.GetRequiredService<IMediator>();

        await mediator.Send(new MxQuery(1));
        await mediator.Send(new MxQuery(2));
        await Assert.ThrowsAnyAsync<Exception>(() => mediator.Send(new MxSecretRequest()));
        await mediator.Send(new MxSecretValidated());
        await mediator.Send(new MxGeneric<int>());
        await mediator.Send(new MxGeneric<string>());
        await Assert.ThrowsAnyAsync<Exception>(() => mediator.Send(new MxUnregistered()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => mediator.Send((IRequest<int>)null!));

        var labels = capture.Of(Mx.RequestCount).Select(m => m.Tag("request.type")).Distinct().OrderBy(l => l, StringComparer.Ordinal).ToArray();
        // Deterministic by canonical name: MxGeneric... sort before MxQuery, but are unknown; of the known types the
        // lowest canonical name wins the single slot.
        var admitted = new[] { typeof(MxQuery), typeof(MxSecretRequest), typeof(MxSecretValidated) }
            .Select(Mx.Label).OrderBy(l => l, StringComparer.Ordinal).First();
        Assert.Equal(new[] { admitted, MediatorMetrics.OverflowLabel }.OrderBy(l => l, StringComparer.Ordinal), labels);
        Assert.Equal(1, Mx.MetricsOf(provider).AdmittedTypeCount);
        // Two sends use the admitted label; the six others share the single overflow bucket.
        var overflow = capture.Sum(Mx.RequestCount, ("request.type", MediatorMetrics.OverflowLabel));
        var named = capture.Sum(Mx.RequestCount, ("request.type", admitted));
        Assert.Equal(6, overflow);
        Assert.Equal(2, named);
    }

    [Fact]
    public void Admission_Is_Deterministic_Bounded_And_Ignores_Rejected_Types()
    {
        var types = typeof(int).Assembly.GetExportedTypes()
            .Where(t => !t.IsGenericTypeDefinition && !t.ContainsGenericParameters && t.IsClass && MediatorMetrics.DescribeType(t) is { Length: <= 128 })
            .OrderByDescending(t => t.FullName, StringComparer.Ordinal)
            .Take(150)
            .ToArray();
        Assert.True(types.Length > 120);

        using var defaults = new MediatorMetrics(Mx.On(), types);
        Assert.Equal(100, defaults.AdmittedTypeCount);
        using var reversed = new MediatorMetrics(Mx.On(), Enumerable.Reverse(types));
        foreach (var type in types)
        {
            Assert.Equal(defaults.LabelFor(type), reversed.LabelFor(type));
        }

        var expected = types.Select(t => (Type: t, Label: Mx.Label(t))).OrderBy(x => x.Label, StringComparer.Ordinal).Take(100).Select(x => x.Type).ToHashSet();
        foreach (var type in types)
        {
            Assert.Equal(expected.Contains(type) ? Mx.Label(type) : MediatorMetrics.OverflowLabel, defaults.LabelFor(type));
        }

        using var none = new MediatorMetrics(Mx.On(maximum: 0), types);
        Assert.Equal(0, none.AdmittedTypeCount);
        Assert.All(types, t => Assert.Equal(MediatorMetrics.OverflowLabel, none.LabelFor(t)));
        using var one = new MediatorMetrics(Mx.On(maximum: 1), types);
        Assert.Equal(1, one.AdmittedTypeCount);
        Assert.Equal(MediatorMetrics.OverflowLabel, one.LabelFor(null));
        Assert.Equal(MediatorMetrics.OverflowLabel, one.LabelFor(typeof(MxUnregistered)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MediatorMetrics(new MediatorMetricsOptions { Enabled = true, MaximumRequestTypes = -1 }, types));
    }

    [Fact]
    public void Overlength_Colliding_And_Open_Names_Are_Never_Admitted()
    {
        var deep = typeof(int);
        for (var i = 0; i < 12; i++)
        {
            deep = typeof(Tuple<>).MakeGenericType(deep);
        }

        Assert.True(MediatorMetrics.DescribeType(deep)!.Length > MediatorMetrics.MaximumLabelLength);

        var first = DefineType("Collide.Same");
        var second = DefineType("Collide.Same");
        Assert.NotEqual(first, second);
        Assert.Equal(MediatorMetrics.DescribeType(first), MediatorMetrics.DescribeType(second));

        using var metrics = new MediatorMetrics(Mx.On(), new[] { deep, first, second, typeof(List<>), typeof(MxQuery), typeof(MxQuery) });
        Assert.Equal(1, metrics.AdmittedTypeCount);
        Assert.Equal(Mx.Label(typeof(MxQuery)), metrics.LabelFor(typeof(MxQuery)));
        Assert.Equal(MediatorMetrics.OverflowLabel, metrics.LabelFor(deep));
        Assert.Equal(MediatorMetrics.OverflowLabel, metrics.LabelFor(first));
        Assert.Equal(MediatorMetrics.OverflowLabel, metrics.LabelFor(second));

        Assert.Equal("Meridian.Mediator.Tests.MxGeneric<System.Int32>", MediatorMetrics.DescribeType(typeof(MxGeneric<int>)));
        Assert.Equal("System.Collections.Generic.Dictionary<System.String,System.Int32[]>", MediatorMetrics.DescribeType(typeof(Dictionary<string, int[]>)));
        Assert.Equal("Meridian.Mediator.Tests.MetricsPrivacyTests+Nested", MediatorMetrics.DescribeType(typeof(Nested)));
        Assert.Null(MediatorMetrics.DescribeType(typeof(List<>)));

        static Type DefineType(string name)
        {
            var assembly = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(
                new System.Reflection.AssemblyName("MetricsDyn" + Guid.NewGuid().ToString("N")), System.Reflection.Emit.AssemblyBuilderAccess.Run);
            return assembly.DefineDynamicModule("m").DefineType(name, System.Reflection.TypeAttributes.Public).CreateType()!;
        }
    }

    [Fact]
    public void Types_Nested_In_Generic_Types_Are_Described_Without_Unbounded_Recursion()
    {
        const string ns = "Meridian.Mediator.Tests.";
        Assert.Equal(ns + "MxOuter<System.Int32>+Inner", MediatorMetrics.DescribeType(typeof(MxOuter<int>.Inner)));
        Assert.Equal(ns + "MxOuter<System.Int32>+InnerG<System.String>", MediatorMetrics.DescribeType(typeof(MxOuter<int>.InnerG<string>)));
        Assert.Equal(ns + "MxOuter<System.Int32>+Mid+Deep", MediatorMetrics.DescribeType(typeof(MxOuter<int>.Mid.Deep)));
        Assert.Equal(ns + "MxOuter2<System.Int32,System.String>+Pair<System.Int64>", MediatorMetrics.DescribeType(typeof(MxOuter2<int, string>.Pair<long>)));
        Assert.Equal(ns + "MxGeneric<" + ns + "MxOuter<System.Int32>+Inner>", MediatorMetrics.DescribeType(typeof(MxGeneric<MxOuter<int>.Inner>)));
        Assert.Equal(ns + "MxOuter<System.Int32>+Inner[]", MediatorMetrics.DescribeType(typeof(MxOuter<int>.Inner[])));

        // Open nested definitions and generic parameters are never described.
        Assert.Null(MediatorMetrics.DescribeType(typeof(MxOuter<>.Inner)));
        Assert.Null(MediatorMetrics.DescribeType(typeof(MxOuter<>.InnerG<>)));
        Assert.Null(MediatorMetrics.DescribeType(typeof(MxOuter<>).GetGenericArguments()[0]));

        var known = new[]
        {
            typeof(MxOuter<int>.Inner),
            typeof(MxOuter<int>.InnerG<string>),
            typeof(MxGeneric<MxOuter<int>.Inner>),
            typeof(MxQuery),
        };
        using var metrics = new MediatorMetrics(Mx.On(), known);
        Assert.Equal(known.Length, metrics.AdmittedTypeCount);
        Assert.Equal(ns + "MxOuter<System.Int32>+Inner", metrics.LabelFor(typeof(MxOuter<int>.Inner)));
        Assert.Equal(Mx.Label(typeof(MxQuery)), metrics.LabelFor(typeof(MxQuery)));
        Assert.Equal(MediatorMetrics.OverflowLabel, metrics.LabelFor(typeof(MxOuter<long>.Inner)));
    }

    [Fact]
    public void Instruments_Have_Exact_Names_Units_And_Types()
    {
        using var provider = Mx.BuildStandard();
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        var published = capture.PublishedInstruments.ToDictionary(p => p.Name);
        var expected = new (string Name, string Unit, string Type)[]
        {
            (Mx.RequestCount, "{operation}", "Counter`1"),
            (Mx.RequestDuration, "ms", "Histogram`1"),
            (Mx.NotificationCount, "{operation}", "Counter`1"),
            (Mx.StreamCount, "{operation}", "Counter`1"),
            (Mx.StreamDuration, "ms", "Histogram`1"),
            (Mx.CacheLookup, "{lookup}", "Counter`1"),
            (Mx.RetryAttempt, "{attempt}", "Counter`1"),
            (Mx.ValidationRejection, "{rejection}", "Counter`1"),
            (Mx.AuthorizationRejection, "{rejection}", "Counter`1"),
            (Mx.IdempotencyState, "{transition}", "Counter`1"),
        };
        Assert.Equal(expected.Length, published.Count);
        foreach (var (name, unit, type) in expected)
        {
            Assert.Equal(unit, published[name].Unit);
            Assert.Equal(type, published[name].InstrumentType.Name);
            Assert.Equal(type == "Histogram`1" ? typeof(double) : typeof(long), published[name].InstrumentType.GenericTypeArguments[0]);
        }
    }

    public sealed class Nested
    {
    }
}

public sealed class MxOuter<T>
{
    public sealed class Inner : IRequest<int>
    {
    }

    public sealed class InnerG<TInner> : IRequest<int>
    {
    }

    public static class Mid
    {
        public sealed class Deep : IRequest<int>
        {
        }
    }
}

public sealed class MxOuter2<T1, T2>
{
    public sealed class Pair<TPair> : IRequest<int>
    {
    }
}

public sealed class MxUnregistered : IRequest<int>;

public sealed class MxSecretRequest : IRequest<int>, ICacheableQuery, IIdempotentRequest, IAuthorizedRequest, IRetryableRequest
{
    public const string Key = "secret-key-7731";
    public const string Payload = "payload-secret-9921";
    public const string User = "user-secret-4410";
    public const string Reason = "reason-secret-5512";
    public const string ExceptionText = "exception-secret-6603";

    public string CacheKey => Key;

    public TimeSpan? CacheDuration => null;

    public string IdempotencyKey => Key + User;

    public string Body => Payload;

    public int MaxRetries => 1;

    public TimeSpan RetryDelay => TimeSpan.Zero;
}

public sealed class MxSecretHandler : IRequestHandler<MxSecretRequest, int>
{
    public Task<int> Handle(MxSecretRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(MxSecretRequest.ExceptionText + request.Body);
}

public sealed class MxSecretValidated : IRequest<int>;

public sealed class MxSecretValidatedHandler : IRequestHandler<MxSecretValidated, int>
{
    public Task<int> Handle(MxSecretValidated request, CancellationToken cancellationToken) => Task.FromResult(1);
}

public sealed class MxSecretValidator : IValidator<MxSecretValidated>
{
    public const string ErrorText = "validation-secret-8814";

    public Task<ValidationResult> ValidateAsync(MxSecretValidated instance, CancellationToken cancellationToken) =>
        Task.FromResult(new ValidationResult { Errors = { new ValidationError("Secret", ErrorText) } });
}

public sealed class MxDenyAuthorization : IAuthorizationHandler<MxSecretRequest>
{
    public Task<AuthorizationResult> AuthorizeAsync(MxSecretRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(AuthorizationResult.Fail(MxSecretRequest.Reason));
}

public sealed class MxAllowThenFail : IAuthorizationHandler<MxSecretRequest>
{
    public Task<AuthorizationResult> AuthorizeAsync(MxSecretRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(AuthorizationResult.Success());
}

public sealed class MxMemoryCache : ICacheProvider
{
    private readonly Dictionary<string, object> _items = new();

    public Task<(bool Found, object? Value)> GetAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult<(bool, object?)>(_items.TryGetValue(key, out var value) ? (true, value) : (false, null));

    public Task SetAsync(string key, object value, TimeSpan? duration, CancellationToken cancellationToken)
    {
        _items[key] = value;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        _items.Remove(key);
        return Task.CompletedTask;
    }
}

public sealed class MxIdempotencyStore : IIdempotencyStore
{
    private readonly Dictionary<string, object> _items = new();

    public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct) =>
        Task.FromResult<(bool, object?)>(_items.TryGetValue(key, out var value) ? (true, value) : (false, null));

    public Task StoreAsync(string key, object response, CancellationToken ct)
    {
        _items[key] = response;
        return Task.CompletedTask;
    }
}
