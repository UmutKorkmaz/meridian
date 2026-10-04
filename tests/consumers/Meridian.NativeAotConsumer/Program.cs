using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Meridian.Mapping;
using Meridian.Mediator;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Extensions;
using Meridian.NativeAot.Fixture;
using Microsoft.Extensions.DependencyInjection;

// Self-checking mediator + mapping consumer. Managed runs (ordinary package verification) execute the shared
// cases; `--require-native` additionally executes the native-only cases and asserts that the process really is
// a Native AOT binary on the expected OS/architecture. Every case is recorded only after all of its assertions.
var requireNative = args.Contains("--require-native");
string? ArgValue(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

var sharedCases = new[]
{
    "typed-object-query", "void-forms", "standard-pipeline", "standard-composition", "scoped-construction",
    "notification-composition", "stream-lifetime", "generated-mapping", "strict-failure",
};
var nativeCases = new[] { "native-runtime", "native-nonstrict-unknown", "native-scanning-unsupported" };
var expected = requireNative ? sharedCases.Concat(nativeCases).ToArray() : sharedCases;
var ledger = new List<string>();
var failures = new List<string>();

Console.WriteLine($"HOST runtime={Environment.Version} framework={RuntimeInformation.FrameworkDescription}");

async Task Case(string id, Func<Task> body)
{
    try
    {
        await body().ConfigureAwait(false);
        ledger.Add(id);
        Console.WriteLine($"CASE {id} PASS");
    }
    catch (Exception ex)
    {
        failures.Add(id);
        Console.WriteLine($"CASE {id} FAIL {ex.GetType().Name}: {ex.Message}");
    }
}

ServiceProvider BuildApp(string name, out Marker marker, bool metrics = false)
{
    marker = new Marker(name);
    var services = new ServiceCollection().AddFixtureServices(marker);
    // The first call composes both registries and repeats AppRegistry; the second repeats AppRegistry again.
    services.AddMeridianMediator(c =>
    {
        c.Lifetime = ServiceLifetime.Scoped;
        c.RequireGeneratedDispatch = true;
        c.AddGeneratedRegistry<AppRegistry>().AddGeneratedRegistry<ExtraRegistry>().AddGeneratedRegistry<AppRegistry>();
        if (metrics) c.Metrics = new MediatorMetricsOptions { Enabled = true };
    });
    services.AddMeridianMediator(c =>
    {
        c.Lifetime = ServiceLifetime.Scoped;
        c.RequireGeneratedDispatch = true;
        c.AddGeneratedRegistry<AppRegistry>();
    });
    return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
}

await Case("typed-object-query", async () =>
{
    foreach (var typedFirst in Expected.Orders)
    {
        using var provider = BuildApp("typed-object", out _);
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var trace = scope.ServiceProvider.GetRequiredService<CallTrace>();
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;

        async Task Typed(int id)
        {
            var request = new GetOrder(id);
            var dto = await mediator.Send(request, token);
            Check.Equal((long)id, dto.Id);
            Check.Equal($"order-{id}", dto.Name);
            Check.Equal((long)id * 2, dto.Quantity);
            Check.Same(request, trace.LastRequest);
            Check.Same(dto, trace.LastResponse);
            Check.Equal(token, trace.LastToken);
        }

        async Task Boxed(int id)
        {
            var request = new GetOrder(id);
            var result = await mediator.Send((object)request, token);
            var dto = Check.IsType<OrderDto>(result);
            Check.Equal((long)id, dto.Id);
            Check.Equal($"order-{id}", dto.Name);
            Check.Equal((long)id * 2, dto.Quantity);
            Check.Same(request, trace.LastRequest);
            Check.Same(dto, trace.LastResponse);
            Check.Equal(token, trace.LastToken);
        }

        if (typedFirst) { await Typed(7); await Boxed(9); } else { await Boxed(9); await Typed(7); }
        Check.Equal(2, trace.Count("handler:GetOrder"));
        Check.Equal(2, trace.Count("pass:GetOrder"));
        Check.Equal(2, trace.Count("validator:GetOrder"));
    }

    // Opt-in metrics stay Native AOT clean: an enabled provider creates its meter and observes a typed and an object send.
    var observed = 0L;
    using var metricsProvider = BuildApp("typed-object-metrics", out _, metrics: true);
    using var listener = new System.Diagnostics.Metrics.MeterListener();
    listener.InstrumentPublished = (instrument, l) =>
    {
        if (instrument.Meter.Name == "Meridian.Mediator") l.EnableMeasurementEvents(instrument);
    };
    listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
    {
        if (instrument.Name == "meridian.mediator.request.count") observed += value;
    });
    listener.Start();
    using var metricsScope = metricsProvider.CreateScope();
    var metricsMediator = metricsScope.ServiceProvider.GetRequiredService<IMediator>();
    Check.Equal(4L, (await metricsMediator.Send(new GetOrder(4))).Id);
    Check.IsType<OrderDto>(await metricsMediator.Send((object)new GetOrder(5)));
    Check.Equal(2L, observed);
});

await Case("void-forms", async () =>
{
    using var provider = BuildApp("void", out _);
    using var scope = provider.CreateScope();
    var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
    var trace = scope.ServiceProvider.GetRequiredService<CallTrace>();
    using var cancellation = new CancellationTokenSource();
    var token = cancellation.Token;

    // IRequestHandler<Ping> through the void-compatible typed entry, the Unit entry and the object entry.
    var ping = new Ping();
    await mediator.Send(ping, token);
    Check.Same(ping, trace.LastRequest);
    Check.Equal(token, trace.LastToken);
    Check.Equal(Unit.Value, await mediator.Send((IRequest<Unit>)ping, token));
    Check.Equal(Unit.Value, await mediator.Send((object)ping, token));
    Check.Equal(3, trace.Count("handler:Ping"));

    // IRequestHandler<UnitPing, Unit> is a separate request slot reached through the same three entries.
    var unitPing = new UnitPing();
    await mediator.Send(unitPing, token);
    Check.Same(unitPing, trace.LastRequest);
    Check.Equal(token, trace.LastToken);
    Check.Equal(Unit.Value, await mediator.Send((IRequest<Unit>)unitPing, token));
    Check.Equal(Unit.Value, await mediator.Send((object)unitPing, token));
    Check.Equal(3, trace.Count("handler:UnitPing"));
    Check.Equal(6, trace.Count("pass:Ping") + trace.Count("pass:UnitPing"));
});

await Case("standard-pipeline", async () =>
{
    using var provider = BuildApp("standard", out _);
    var sink = provider.GetRequiredService<RecordingAuditSink>();
    Check.Null(CorrelationContext.CorrelationId);
    using var scope = provider.CreateScope();
    var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
    var trace = scope.ServiceProvider.GetRequiredService<CallTrace>();

    await mediator.Send(new GetOrder(3));
    Check.Null(CorrelationContext.CorrelationId); // the caller's ambient context is restored
    Check.Equal(1, trace.Count("validator:GetOrder"));
    Check.Equal(1, trace.Count("handler:GetOrder"));
    Check.NotEmpty(trace.CorrelationSeen);
    var success = Check.Single(sink.Entries);
    Check.True(success.Success, "valid request audited as success");
    Check.Equal(trace.CorrelationSeen, success.CorrelationId);
    Check.Equal(typeof(GetOrder).FullName, success.RequestTypeName);

    var error = await Check.ThrowsAsync<ValidationException>(() => mediator.Send(new GetOrder(0)));
    var validation = Check.Single(error.Errors);
    Check.Equal(nameof(GetOrder.Id), validation.PropertyName);
    Check.Equal("localized:validation.id.positive", validation.ErrorMessage);
    Check.Equal(2, trace.Count("validator:GetOrder"));
    Check.Equal(1, trace.Count("handler:GetOrder")); // the invalid request never reached the handler
    var entries = sink.Entries;
    Check.Equal(2, entries.Length);
    var failure = entries[1];
    Check.True(!failure.Success, "invalid request audited as failure");
    Check.NotEmpty(failure.CorrelationId);
    Check.Equal(typeof(ValidationException).FullName, failure.FailureType);
    Check.Equal("An error occurred during request processing.", failure.FailureMessage); // redacted by default
    Check.Null(CorrelationContext.CorrelationId);

    // Default telemetry privacy: failure activities carry the exception type but no message or stack trace.
    var stopped = new List<Activity>();
    using var listener = new ActivityListener
    {
        ShouldListenTo = source => source.Name == "Meridian.Mediator",
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        ActivityStopped = activity => { lock (stopped) stopped.Add(activity); },
    };
    ActivitySource.AddActivityListener(listener);
    await Check.ThrowsAsync<ValidationException>(() => mediator.Send(new GetOrder(-1)));
    Activity[] seen;
    lock (stopped) seen = stopped.ToArray();
    var send = Check.Single(seen.Where(a => a.DisplayName == "Mediator.Send GetOrder").ToArray());
    Check.Equal(ActivityStatusCode.Error, send.Status);
    Check.Equal(typeof(ValidationException).FullName, send.GetTagItem("exception.type") as string);
    Check.Null(send.GetTagItem("exception.message"));
    Check.Null(send.GetTagItem("exception.stacktrace"));
    Check.True(string.IsNullOrEmpty(send.StatusDescription), "no status description by default");
});

await Case("standard-composition", async () =>
{
    // IncludeStandardPipeline on the registry, generic standard registration and a plain registry registration
    // are combined and repeated. Each validator and audit record must still run exactly once per send.
    var marker = new Marker("composition");
    var services = new ServiceCollection().AddFixtureServices(marker);
    services.AddMeridianStandard<StandardRegistry>(c => { c.Lifetime = ServiceLifetime.Scoped; c.RequireGeneratedDispatch = true; });
    services.AddMeridianStandard<StandardRegistry>(c => { c.Lifetime = ServiceLifetime.Scoped; c.RequireGeneratedDispatch = true; });
    services.AddMeridianMediator(c => { c.Lifetime = ServiceLifetime.Scoped; c.RequireGeneratedDispatch = true; c.AddGeneratedRegistry<StandardRegistry>(); });
    using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    var sink = provider.GetRequiredService<RecordingAuditSink>();
    using var scope = provider.CreateScope();
    var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
    var trace = scope.ServiceProvider.GetRequiredService<CallTrace>();

    Check.Equal(10, await mediator.Send(new StandardQuery(5)));
    Check.Equal(1, trace.Count("validator:Standard"));
    Check.Equal(1, trace.Count("handler:Standard"));
    Check.Equal(1, sink.Entries.Length);
    await Check.ThrowsAsync<ValidationException>(() => mediator.Send((object)new StandardQuery(-1)));
    Check.Equal(2, trace.Count("validator:Standard"));
    Check.Equal(1, trace.Count("handler:Standard"));
    Check.Equal(2, sink.Entries.Length);
    Check.Equal(1, scope.ServiceProvider.GetServices<Meridian.Mediator.Generated.IGeneratedMediatorRegistry>().Count(r => r is StandardRegistry));
});

await Case("scoped-construction", async () =>
{
    using var providerA = BuildApp("A", out var markerA);
    using var providerB = BuildApp("B", out var markerB);
    // Registration and provider construction never run a handler or dependency constructor.
    Check.Equal(0, markerA.Created.Count + markerB.Created.Count);

    ScopedDependency first;
    using (var scope = providerA.CreateScope())
    {
        Check.Equal(0, markerA.Created.Count); // creating a scope constructs nothing
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var one = await mediator.Send(new WhoAmIQuery());
        var two = await mediator.Send((object)new WhoAmIQuery());
        Check.Equal(one, (string?)two); // one scoped dependency per scope
        Check.True(one.StartsWith("A:", StringComparison.Ordinal), "provider A marker");
        Check.Equal(1, markerA.Created.Count);
        first = scope.ServiceProvider.GetRequiredService<ScopedDependency>();
        Check.Equal(0, first.Disposals);
    }
    Check.Equal(1, first.Disposals); // disposed exactly once with its scope
    using (var scope = providerA.CreateScope())
    {
        var again = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new WhoAmIQuery());
        Check.True(again != $"A:{first.Id}", "a fresh scope gets a fresh dependency");
    }
    using (var scope = providerB.CreateScope())
    {
        var fromB = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(new WhoAmIQuery());
        Check.True(fromB.StartsWith("B:", StringComparison.Ordinal), "provider B marker");
    }

    // Alternate and concurrent sends through both providers keep marker ownership.
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var work = Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
    {
        var useA = index % 2 == 0;
        using var scope = (useA ? providerA : providerB).CreateScope();
        await gate.Task;
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var prefix = useA ? "A:" : "B:";
        var one = await mediator.Send(new WhoAmIQuery());
        var two = await mediator.Send((object)new WhoAmIQuery());
        Check.True(one.StartsWith(prefix, StringComparison.Ordinal) && one == (string?)two, "concurrent scope ownership");
    })).ToArray();
    gate.SetResult();
    await Task.WhenAll(work).WaitAsync(TimeSpan.FromSeconds(30));
    Check.True(markerA.Created.All(d => d.Disposals == 1) && markerB.Created.All(d => d.Disposals == 1), "every scoped dependency disposed once");
});

await Case("notification-composition", async () =>
{
    using var provider = BuildApp("notification", out _);
    using var scope = provider.CreateScope();
    var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
    var trace = scope.ServiceProvider.GetRequiredService<CallTrace>();
    using var cancellation = new CancellationTokenSource();
    var token = cancellation.Token;

    await mediator.Publish(new Notice(), token);
    await mediator.Publish((object)new Notice(), token);
    // Two registries share the notification; the repeated AppRegistry registration adds no duplicate delivery.
    Check.Equal(Expected.Notices, trace.Events().Where(e => e.StartsWith("notice:", StringComparison.Ordinal)).ToArray());
    Check.Equal(4, trace.NoticeTokens.Count);
    Check.True(trace.NoticeTokens.All(t => t == token), "notification token identity");

    // The default publisher is sequential: the first fault prevents the second handler.
    var fault = await Check.ThrowsAsync<InvalidOperationException>(() => mediator.Publish(new FaultNotice(), token));
    Check.Same(FaultFirstHandler.Failure, fault);
    Check.Equal(1, trace.Count("fault:first"));
    Check.Equal(0, trace.Count("fault:second"));
});

await Case("stream-lifetime", async () =>
{
    async Task Exercise(IServiceProvider root)
    {
        using var scope = root.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var trace = scope.ServiceProvider.GetRequiredService<CallTrace>();
        var dependency = scope.ServiceProvider.GetRequiredService<ScopedDependency>();

        // Typed and object streams complete once with exact values and nested lifetimes.
        var typed = new List<int>();
        await foreach (var value in mediator.CreateStream(new Numbers(3))) typed.Add(value);
        Check.Equal(Expected.Numbers, typed.ToArray());
        Check.Equal(
            Expected.FullStream,
            trace.Events());
        var boxed = new List<object?>();
        await foreach (var value in mediator.CreateStream((object)new Numbers(3))) boxed.Add(value);
        Check.Equal(Expected.BoxedNumbers, boxed.ToArray());
        Check.Equal(2, trace.Count("handler:finally"));
        Check.Equal(0, dependency.Disposals); // the scoped dependency stays usable until its scope ends

        // Early break after the first item disposes the inner handler before the outer behavior.
        var before = trace.Events().Length;
        await foreach (var value in mediator.CreateStream(new Numbers(3))) { Check.Equal(10, value); break; }
        var segment = trace.Events().Skip(before).ToArray();
        Check.Equal(Expected.EarlyBreakStream, segment);
        await foreach (var value in mediator.CreateStream((object)new Numbers(2))) { Check.Equal((object?)10, value); break; }
        Check.Equal(4, trace.Count("handler:finally"));
        Check.Equal(4, trace.Count("behavior:finally"));

        // Pre-cancelled caller token: cancellation surfaces and nothing is yielded.
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        var aliveBefore = trace.Count("dep:alive");
        await Check.ThrowsAsync<OperationCanceledException>(async () => { await foreach (var _ in mediator.CreateStream(new Numbers(3), preCancelled.Token)) { } });
        Check.Equal(aliveBefore, trace.Count("dep:alive"));
        Check.Equal(trace.Count("handler:enter"), trace.Count("handler:finally"));

        // Cancellation after the first yield stops the stream; both lifetimes still unwind.
        using var afterFirst = new CancellationTokenSource();
        var received = 0;
        await Check.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in mediator.CreateStream(new Numbers(5), afterFirst.Token)) { received++; afterFirst.Cancel(); }
        });
        Check.Equal(1, received);
        Check.Equal(trace.Count("handler:enter"), trace.Count("handler:finally"));
        Check.Equal(trace.Count("behavior:enter"), trace.Count("behavior:finally"));

        // The enumerator token (WithCancellation) is forwarded independently of the caller token.
        using var enumeratorToken = new CancellationTokenSource();
        received = 0;
        await Check.ThrowsAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in mediator.CreateStream(new Numbers(5)).WithCancellation(enumeratorToken.Token)) { received++; enumeratorToken.Cancel(); }
        });
        Check.Equal(1, received);
        Check.Equal(trace.Count("handler:enter"), trace.Count("handler:finally"));

        // A deferred handler exception surfaces on enumeration, not when the stream is created.
        var faulty = mediator.CreateStream(new FaultyNumbers());
        await using (var enumerator = faulty.GetAsyncEnumerator())
        {
            Check.True(await enumerator.MoveNextAsync(), "first item");
            Check.Equal(1, enumerator.Current);
            var boom = await Check.ThrowsAsync<InvalidOperationException>(async () => await enumerator.MoveNextAsync());
            Check.Same(FaultyNumbersHandler.Failure, boom);
        }
        Check.Equal(0, dependency.Disposals);
    }

    using var provider = BuildApp("streams", out _);
    await Exercise(provider);

    // The same paths run again with an in-process listener, which routes through the activity-aware wrappers.
    var started = 0;
    using var listener = new ActivityListener
    {
        ShouldListenTo = source => source.Name == "Meridian.Mediator",
        Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        ActivityStarted = _ => Interlocked.Increment(ref started),
    };
    ActivitySource.AddActivityListener(listener);
    await Exercise(provider);
    Check.True(Volatile.Read(ref started) >= 6, "activity-aware stream wrappers executed");
});

await Case("generated-mapping", () =>
{
    var order = Maps.MapToOrderDto(new Order { Id = 5, Name = "five", Quantity = 6 });
    Check.Equal(5L, order.Id);
    Check.Equal("five", order.Name);
    Check.Equal(6L, order.Quantity);

    var source = new Node { Value = 42 };
    source.Next = source;
    var mapped = Maps.MapToNodeDto(source);
    Check.Equal(42L, mapped.Value);
    Check.True(ReferenceEquals(mapped, mapped.Next), "cycle preserved");
    var batch = Maps.MapToBatchDto(new Batch(Expected.Twice(source)));
    Check.True(batch.Nodes.Count == 2 && ReferenceEquals(batch.Nodes[0], batch.Nodes[1]), "repeated element identity");
    Check.True(Maps.MapToNodeDto(source, new GeneratedMappingOptions { MaxDepth = 1 }).Next is null, "depth bound");
    Check.Throws<MeridianMappingLimitException>(() => Maps.MapToBatchDto(new Batch(Expected.Twice(source)), new GeneratedMappingOptions { MaxCollectionItems = 1 }));
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    Check.Throws<OperationCanceledException>(() => Maps.MapToNodeDto(source, new GeneratedMappingOptions { CancellationToken = cancellation.Token }));
    return Task.CompletedTask;
});

await Case("strict-failure", async () =>
{
    using var provider = BuildApp("strict", out _);
    using var scope = provider.CreateScope();
    var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
    var trace = scope.ServiceProvider.GetRequiredService<CallTrace>();

    // Compile-known contracts that no registry declares fail with an actionable message and run nothing.
    var typed = await Check.ThrowsAsync<InvalidOperationException>(() => mediator.Send(new UnregisteredQuery()));
    Check.Contains(nameof(UnregisteredQuery), typed.Message);
    Check.Contains("generated", typed.Message);
    var boxed = await Check.ThrowsAsync<InvalidOperationException>(() => mediator.Send((object)new UnregisteredQuery()));
    Check.Contains(nameof(UnregisteredQuery), boxed.Message);
    Check.Equal(0, UnregisteredQueryHandler.Calls);
    var notification = await Check.ThrowsAsync<InvalidOperationException>(() => mediator.Publish(new UnregisteredNotice()));
    Check.Contains(nameof(UnregisteredNotice), notification.Message);
    var stream = Check.Throws<InvalidOperationException>(() => mediator.CreateStream(new UnregisteredNumbers()));
    Check.Contains(nameof(UnregisteredNumbers), stream.Message);
    Check.Equal(0, trace.Events().Length);

    // Unsupported controlled registrations fail while strict configuration is built.
    var unrooted = Check.Throws<InvalidOperationException>(() => new ServiceCollection().AddFixtureServices(new Marker("unrooted")).AddMeridianMediator(c =>
    {
        c.Lifetime = ServiceLifetime.Scoped;
        c.RequireGeneratedDispatch = true;
        c.AddGeneratedRegistry<AppRegistry>();
        c.AddOpenBehavior(typeof(UnrootedBehavior<,>));
    }));
    Check.Contains(nameof(UnrootedBehavior<object, object>).Split('`')[0], unrooted.Message);
    Check.Contains("declare", unrooted.Message);
    var scanning = Check.Throws<InvalidOperationException>(() => new ServiceCollection().AddMeridianMediator(c =>
    {
        c.RequireGeneratedDispatch = true;
        c.RegisterServicesFromAssembly(typeof(AppRegistry).Assembly);
    }));
    Check.Contains("scan", scanning.Message);
});

if (requireNative)
{
    await Case("native-runtime", () =>
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" : "other";
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        Console.WriteLine($"NATIVE os={os} arch={arch} dynamicCodeSupported={RuntimeFeature.IsDynamicCodeSupported.ToString().ToLowerInvariant()} dynamicCodeCompiled={RuntimeFeature.IsDynamicCodeCompiled.ToString().ToLowerInvariant()} runtime={RuntimeInformation.FrameworkDescription}");
        Check.True(!RuntimeFeature.IsDynamicCodeSupported, "native binary must not support dynamic code");
        Check.True(!RuntimeFeature.IsDynamicCodeCompiled, "native binary must not JIT");
        Check.Equal(ArgValue("--expect-os"), os);
        Check.Equal(ArgValue("--expect-arch"), arch);
        return Task.CompletedTask;
    });

    await Case("native-nonstrict-unknown", async () =>
    {
        // Without strict dispatch the legacy reflective fallback is not available in a native binary. An unknown
        // request fails with the same actionable message instead of attempting runtime generic instantiation.
        var services = new ServiceCollection().AddFixtureServices(new Marker("nonstrict"));
        services.AddMeridianMediator(c => c.Lifetime = ServiceLifetime.Scoped);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var typed = await Check.ThrowsAsync<InvalidOperationException>(() => mediator.Send(new UnregisteredQuery()));
        Check.Contains(nameof(UnregisteredQuery), typed.Message);
        Check.Contains("generated", typed.Message);
        var notification = await Check.ThrowsAsync<InvalidOperationException>(() => mediator.Publish(new UnregisteredNotice()));
        Check.Contains(nameof(UnregisteredNotice), notification.Message);
        var stream = Check.Throws<InvalidOperationException>(() => mediator.CreateStream(new UnregisteredNumbers()));
        Check.Contains(nameof(UnregisteredNumbers), stream.Message);
        Check.Equal(0, UnregisteredQueryHandler.Calls);
    });

    await Case("native-scanning-unsupported", () =>
    {
        var error = Check.Throws<NotSupportedException>(() => new ServiceCollection().AddMeridianMediator(c =>
            c.RegisterServicesFromAssembly(typeof(AppRegistry).Assembly)));
        Check.Contains("generated", error.Message);
        Check.Contains("RegisterServicesFromAssembly", error.Message);
        return Task.CompletedTask;
    });
}

var missing = expected.Except(ledger).ToArray();
var unexpected = ledger.Except(expected).ToArray();
var duplicated = ledger.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
var complete = failures.Count == 0 && missing.Length == 0 && unexpected.Length == 0 && duplicated.Length == 0 && ledger.Count == expected.Length;
Console.WriteLine($"SUMMARY schema=meridian-native-aot-cases/1 mode={(requireNative ? "native" : "managed")} count={ledger.Count} expected={expected.Length} ids={string.Join(",", ledger)}");
if (!complete)
{
    Console.WriteLine($"FAILED failures={string.Join(",", failures)} missing={string.Join(",", missing)} unexpected={string.Join(",", unexpected)} duplicated={string.Join(",", duplicated)}");
    return 1;
}

Console.WriteLine($"COMMON native-aot-cases={ledger.Count} mediator=request,void,standard,scoped,notification,stream,strict mapping=generated");
return 0;

internal static class Expected
{
    public static readonly bool[] Orders = { true, false };
    public static readonly string[] Notices = { "notice:one", "notice:two", "notice:one", "notice:two" };
    public static readonly int[] Numbers = { 10, 20, 30 };
    public static readonly object?[] BoxedNumbers = { 10, 20, 30 };
    public static readonly string[] FullStream = { "behavior:enter", "handler:enter", "dep:alive", "dep:alive", "dep:alive", "handler:finally", "behavior:finally" };
    public static readonly string[] EarlyBreakStream = { "behavior:enter", "handler:enter", "dep:alive", "handler:finally", "behavior:finally" };

    public static Node[] Twice(Node node) => new[] { node, node };
}

internal static class Check
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("check failed: " + message);
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"expected <{expected}> but was <{actual}>");
    }

    public static void Equal<T>(T[] expected, T[] actual)
    {
        if (!expected.SequenceEqual(actual)) throw new InvalidOperationException($"expected [{string.Join(", ", expected)}] but was [{string.Join(", ", actual)}]");
    }

    public static void Same(object? expected, object? actual)
    {
        if (!ReferenceEquals(expected, actual)) throw new InvalidOperationException("expected the same instance");
    }

    public static void Null(object? value)
    {
        if (value is not null) throw new InvalidOperationException($"expected null but was <{value}>");
    }

    public static void NotEmpty(string? value)
    {
        if (string.IsNullOrEmpty(value)) throw new InvalidOperationException("expected a non-empty string");
    }

    public static void Contains(string fragment, string text)
    {
        if (!text.Contains(fragment, StringComparison.Ordinal)) throw new InvalidOperationException($"expected <{text}> to contain <{fragment}>");
    }

    public static T Single<T>(IReadOnlyCollection<T> values) =>
        values.Count == 1 ? values.First() : throw new InvalidOperationException($"expected one item but found {values.Count}");

    public static T IsType<T>(object? value) =>
        value is T typed ? typed : throw new InvalidOperationException($"expected {typeof(T).Name} but was {value?.GetType().Name ?? "null"}");

    public static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T expected) { return expected; }
        throw new InvalidOperationException($"expected {typeof(T).Name} but nothing was thrown");
    }

    public static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action().ConfigureAwait(false); }
        catch (T expected) { return expected; }
        throw new InvalidOperationException($"expected {typeof(T).Name} but nothing was thrown");
    }
}
