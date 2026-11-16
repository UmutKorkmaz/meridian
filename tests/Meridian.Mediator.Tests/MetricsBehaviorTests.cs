#pragma warning disable CA1707 // Contract test names are deliberately descriptive.
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Diagnostics;
using Meridian.Mediator.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace Meridian.Mediator.Tests;

[Collection("Metrics")]
public class MetricsCacheTests
{
    [Fact]
    public async Task Initial_Lookup_Is_Classified_Once_As_Miss_Then_Hit()
    {
        var calls = 0;
        using var provider = Provider(s => s.AddSingleton<ICacheProvider, MxMemoryCache>(), () => ++calls);
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        var mediator = provider.GetRequiredService<IMediator>();

        Assert.Equal(1, await mediator.Send(new MxCachedQuery()));
        Assert.Equal(1, await mediator.Send(new MxCachedQuery()));

        Assert.Equal(1, calls);
        Assert.Equal(new[] { "miss", "hit" }, capture.Of(Mx.CacheLookup).Select(m => m.Tag("state")));
        Assert.All(capture.Of(Mx.CacheLookup), m =>
        {
            Assert.Equal(Mx.Label(typeof(MxCachedQuery)), m.Tag("request.type"));
            Assert.Equal("{lookup}", m.Unit);
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Incompatible_Or_Null_Stored_Value_Is_A_Miss(bool incompatible)
    {
        var calls = 0;
        using var provider = Provider(
            s => s.AddSingleton<ICacheProvider>(new MxScriptedCache { Get = (_, _) => Task.FromResult<(bool, object?)>((true, incompatible ? "text" : null)) }),
            () => ++calls);
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);

        Assert.Equal(1, await provider.GetRequiredService<IMediator>().Send(new MxCachedQuery()));
        Assert.Equal(1, calls);
        Assert.Equal("miss", Assert.Single(capture.Of(Mx.CacheLookup)).Tag("state"));
    }

    [Fact]
    public async Task Lookup_Fault_And_Cancellation_Keep_Their_Exceptions()
    {
        var original = new IOException("lookup");
        using var provider = Provider(
            s => s.AddSingleton<ICacheProvider>(new MxScriptedCache { Get = (_, ct) => ct.IsCancellationRequested ? Task.FromCanceled<(bool, object?)>(ct) : Task.FromException<(bool, object?)>(original) }),
            () => 1);
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        var mediator = provider.GetRequiredService<IMediator>();

        Assert.Same(original, await Assert.ThrowsAsync<IOException>(() => mediator.Send(new MxCachedQuery())));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mediator.Send(new MxCachedQuery(), cts.Token));

        Assert.Equal(new[] { "error", "cancellation" }, capture.Of(Mx.CacheLookup).Select(m => m.Tag("state")));
        Assert.Equal(2, capture.Sum(Mx.RequestCount, ("outcome", "failure")) + capture.Sum(Mx.RequestCount, ("outcome", "cancellation")));
    }

    [Fact]
    public async Task Coalesced_Miss_Is_Not_Reinterpreted_As_A_Hit_Or_Follower()
    {
        var calls = 0;
        var cache = new MxScriptedCache
        {
            Get = (_, _) => Task.FromResult<(bool, object?)>((false, null)),
            GetOrCreate = async (_, factory, _, ct) => await factory(ct),
        };
        using var provider = Provider(
            s =>
            {
                s.AddSingleton<ICacheProvider>(cache);
                s.AddSingleton(new CachingBehaviorOptions { EnableRequestCoalescing = true });
            },
            () => ++calls);
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);

        Assert.Equal(1, await provider.GetRequiredService<IMediator>().Send(new MxCachedQuery()));
        Assert.Equal(1, calls);
        Assert.Equal("miss", Assert.Single(capture.Of(Mx.CacheLookup)).Tag("state"));
    }

    private static ServiceProvider Provider(Action<IServiceCollection> register, Func<int> handler) =>
        Mx.Build(
            s =>
            {
                s.AddSingleton(new MxCachedCalls(handler));
                s.AddTransient<IRequestHandler<MxCachedQuery, int>, MxCachedHandler>();
                register(s);
            },
            c => c.AddOpenBehavior(typeof(CachingBehavior<,>)),
            Mx.On());
}

public sealed class MxCachedQuery : IRequest<int>, ICacheableQuery
{
    public string CacheKey => "k";

    public TimeSpan? CacheDuration => null;
}

public sealed class MxCachedCalls(Func<int> next)
{
    public int Next() => next();
}

public sealed class MxCachedHandler(MxCachedCalls calls) : IRequestHandler<MxCachedQuery, int>
{
    public Task<int> Handle(MxCachedQuery request, CancellationToken cancellationToken) => Task.FromResult(calls.Next());
}

public sealed class MxScriptedCache : IConcurrentCacheProvider
{
    public Func<string, CancellationToken, Task<(bool, object?)>> Get { get; set; } = (_, _) => Task.FromResult<(bool, object?)>((false, null));

    public Func<string, Func<CancellationToken, Task<object?>>, TimeSpan?, CancellationToken, Task<object?>>? GetOrCreate { get; set; }

    public async Task<(bool Found, object? Value)> GetAsync(string key, CancellationToken cancellationToken) => await Get(key, cancellationToken);

    public Task SetAsync(string key, object value, TimeSpan? duration, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RemoveAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<object?> GetOrCreateAsync(string key, Func<CancellationToken, Task<object?>> factory, TimeSpan? duration, CancellationToken cancellationToken) =>
        GetOrCreate is null ? factory(cancellationToken) : GetOrCreate(key, factory, duration, cancellationToken);
}

[Collection("Metrics")]
public class MetricsValidationAndAuthorizationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Multiple_Validation_Errors_Count_As_One_Rejection(bool localized)
    {
        var calls = 0;
        using var provider = ValidationProvider(localized, _ => { }, () => ++calls);
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        var error = await Assert.ThrowsAsync<ValidationException>(() => provider.GetRequiredService<IMediator>().Send(new MxValidated()));

        Assert.Equal(3, error.Errors.Count);
        Assert.Equal(0, calls);
        var label = Mx.Label(typeof(MxValidated));
        Assert.Equal(1, capture.Sum(Mx.ValidationRejection, ("request.type", label)));
        Assert.Single(capture.Of(Mx.ValidationRejection));
        Assert.Equal("{rejection}", Assert.Single(capture.Of(Mx.ValidationRejection)).Unit);
        Assert.Equal(1, capture.Sum(Mx.RequestCount, ("outcome", "failure")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Infrastructure_Errors_And_Downstream_Validation_Are_Not_Rejections(bool localized)
    {
        var infrastructure = new IOException("validator down");
        using var broken = ValidationProvider(localized, s => s.AddSingleton<IValidator<MxValidated>>(new MxFaultingValidator(infrastructure)), () => 1, errors: false);
        using var brokenCapture = new MetricCapture(Mx.MetricsOf(broken).Meter);
        Assert.Same(infrastructure, await Assert.ThrowsAsync<IOException>(() => broken.GetRequiredService<IMediator>().Send(new MxValidated())));
        Assert.Empty(brokenCapture.Of(Mx.ValidationRejection));

        var downstream = new ValidationException(new[] { new ValidationError("A", "from handler") });
        using var handler = ValidationProvider(localized, _ => { }, () => throw downstream, errors: false);
        using var handlerCapture = new MetricCapture(Mx.MetricsOf(handler).Meter);
        Assert.Same(downstream, await Assert.ThrowsAsync<ValidationException>(() => handler.GetRequiredService<IMediator>().Send(new MxValidated())));
        Assert.Empty(handlerCapture.Of(Mx.ValidationRejection));

        using var valid = ValidationProvider(localized, _ => { }, () => 9, errors: false);
        using var validCapture = new MetricCapture(Mx.MetricsOf(valid).Meter);
        Assert.Equal(9, await valid.GetRequiredService<IMediator>().Send(new MxValidated()));
        Assert.Empty(validCapture.Of(Mx.ValidationRejection));
    }

    [Fact]
    public async Task Authorization_Denial_And_Missing_Handler_Use_Their_Fixed_Reasons()
    {
        var label = Mx.Label(typeof(MxAuthorized));
        using var denied = AuthorizationProvider(s => s.AddSingleton<IAuthorizationHandler<MxAuthorized>>(new MxAuthorizationHandler(AuthorizationResult.Fail("private reason"))));
        using var deniedCapture = new MetricCapture(Mx.MetricsOf(denied).Meter);
        await Assert.ThrowsAsync<UnauthorizedException>(() => denied.GetRequiredService<IMediator>().Send(new MxAuthorized()));
        var denial = Assert.Single(deniedCapture.Of(Mx.AuthorizationRejection));
        Assert.Equal("denied", denial.Tag("reason"));
        Assert.Equal(label, denial.Tag("request.type"));
        Assert.Equal("{rejection}", denial.Unit);

        using var missing = AuthorizationProvider(_ => { });
        using var missingCapture = new MetricCapture(Mx.MetricsOf(missing).Meter);
        await Assert.ThrowsAsync<MissingAuthorizationHandlerException>(() => missing.GetRequiredService<IMediator>().Send(new MxAuthorized()));
        Assert.Equal("missing_handler", Assert.Single(missingCapture.Of(Mx.AuthorizationRejection)).Tag("reason"));
    }

    [Fact]
    public async Task Allowed_Compatibility_And_Provider_Faults_Are_Not_Rejections()
    {
        using var allowed = AuthorizationProvider(s => s.AddSingleton<IAuthorizationHandler<MxAuthorized>>(new MxAuthorizationHandler(AuthorizationResult.Success())));
        using var allowedCapture = new MetricCapture(Mx.MetricsOf(allowed).Meter);
        Assert.Equal(4, await allowed.GetRequiredService<IMediator>().Send(new MxAuthorized()));
        Assert.Empty(allowedCapture.Of(Mx.AuthorizationRejection));

        using var compat = AuthorizationProvider(s => s.AddSingleton(new AuthorizationBehaviorOptions { AllowRequestsWithoutAuthorizationHandlers = true }));
        using var compatCapture = new MetricCapture(Mx.MetricsOf(compat).Meter);
        Assert.Equal(4, await compat.GetRequiredService<IMediator>().Send(new MxAuthorized()));
        Assert.Empty(compatCapture.Of(Mx.AuthorizationRejection));

        var fault = new IOException("authorizer down");
        using var faulty = AuthorizationProvider(s => s.AddSingleton<IAuthorizationHandler<MxAuthorized>>(new MxAuthorizationHandler(null, fault)));
        using var faultyCapture = new MetricCapture(Mx.MetricsOf(faulty).Meter);
        Assert.Same(fault, await Assert.ThrowsAsync<IOException>(() => faulty.GetRequiredService<IMediator>().Send(new MxAuthorized())));
        Assert.Empty(faultyCapture.Of(Mx.AuthorizationRejection));
        Assert.Equal(1, faultyCapture.Sum(Mx.RequestCount, ("outcome", "failure")));
    }

    private static ServiceProvider ValidationProvider(bool localized, Action<IServiceCollection> register, Func<int> handler, bool errors = true) =>
        Mx.Build(
            s =>
            {
                s.AddSingleton(new MxValidatedCalls(handler));
                s.AddTransient<IRequestHandler<MxValidated, int>, MxValidatedHandler>();
                if (errors)
                {
                    s.AddSingleton<IValidator<MxValidated>>(new MxCountingValidator(2));
                    s.AddSingleton<IValidator<MxValidated>>(new MxCountingValidator(1));
                }

                s.AddSingleton<IStringLocalizer<MxValidated>, MxLocalizer<MxValidated>>();
                register(s);
            },
            c => c.AddOpenBehavior(localized ? typeof(LocalizedValidationBehavior<,>) : typeof(ValidationBehavior<,>)),
            Mx.On());

    private static ServiceProvider AuthorizationProvider(Action<IServiceCollection> register) =>
        Mx.Build(
            s =>
            {
                s.AddTransient<IRequestHandler<MxAuthorized, int>, MxAuthorizedHandler>();
                register(s);
            },
            c => c.AddOpenBehavior(typeof(AuthorizationBehavior<,>)),
            Mx.On());
}

public sealed class MxValidated : IRequest<int>;

public sealed class MxValidatedCalls(Func<int> next)
{
    public int Next() => next();
}

public sealed class MxValidatedHandler(MxValidatedCalls calls) : IRequestHandler<MxValidated, int>
{
    public Task<int> Handle(MxValidated request, CancellationToken cancellationToken) => Task.FromResult(calls.Next());
}

public sealed class MxCountingValidator(int errors) : IValidator<MxValidated>
{
    public Task<ValidationResult> ValidateAsync(MxValidated instance, CancellationToken cancellationToken) =>
        Task.FromResult(new ValidationResult { Errors = Enumerable.Range(0, errors).Select(i => new ValidationError("P" + i, "private validation text " + i)).ToList() });
}

public sealed class MxFaultingValidator(Exception failure) : IValidator<MxValidated>
{
    public Task<ValidationResult> ValidateAsync(MxValidated instance, CancellationToken cancellationToken) => Task.FromException<ValidationResult>(failure);
}

public sealed class MxLocalizer<T> : IStringLocalizer<T>
{
    public LocalizedString this[string name] => new(name, name, true);

    public LocalizedString this[string name, params object[] arguments] => new(name, name, true);

    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
}

public sealed class MxAuthorized : IRequest<int>, IAuthorizedRequest;

public sealed class MxAuthorizedHandler : IRequestHandler<MxAuthorized, int>
{
    public Task<int> Handle(MxAuthorized request, CancellationToken cancellationToken) => Task.FromResult(4);
}

public sealed class MxAuthorizationHandler(AuthorizationResult? result, Exception? fault = null) : IAuthorizationHandler<MxAuthorized>
{
    public Task<AuthorizationResult> AuthorizeAsync(MxAuthorized request, CancellationToken cancellationToken) =>
        fault is not null ? Task.FromException<AuthorizationResult>(fault) : Task.FromResult(result!);
}

[Collection("Metrics")]
public class MetricsIdempotencyTests
{
    private static string[] States(MetricCapture capture) =>
        capture.Of(Mx.IdempotencyState).Select(m => m.Tag("mode") + ":" + m.Tag("state")).ToArray();

    // Legacy store ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Legacy_Miss_Completed_Then_Replay()
    {
        var calls = 0;
        using var provider = Provider(s => s.AddSingleton<IIdempotencyStore, MxIdempotencyStore>(), () => ++calls);
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        var mediator = provider.GetRequiredService<IMediator>();

        Assert.Equal(1, await mediator.Send(new MxIdempotent()));
        Assert.Equal(new[] { "legacy:miss", "legacy:completed" }, States(capture));
        Assert.Equal(1, await mediator.Send(new MxIdempotent()));
        Assert.Equal(new[] { "legacy:miss", "legacy:completed", "legacy:replayed" }, States(capture));
        Assert.Equal(1, calls);
        Assert.All(capture.Of(Mx.IdempotencyState), m => Assert.Equal("{transition}", m.Unit));
    }

    [Fact]
    public async Task Legacy_Incompatible_Replay_And_Provider_Faults_Are_Errors_Or_Cancellations()
    {
        using var incompatible = Provider(s => s.AddSingleton<IIdempotencyStore>(new MxScriptedIdempotencyStore { Check = (_, _) => Task.FromResult<(bool, object?)>((true, "text")) }), () => 1);
        using var incompatibleCapture = new MetricCapture(Mx.MetricsOf(incompatible).Meter);
        await Assert.ThrowsAsync<InvalidOperationException>(() => incompatible.GetRequiredService<IMediator>().Send(new MxIdempotent()));
        Assert.Equal(new[] { "legacy:error" }, States(incompatibleCapture));

        var fault = new IOException("check");
        using var faulty = Provider(s => s.AddSingleton<IIdempotencyStore>(new MxScriptedIdempotencyStore { Check = (_, ct) => ct.IsCancellationRequested ? Task.FromCanceled<(bool, object?)>(ct) : Task.FromException<(bool, object?)>(fault) }), () => 1);
        using var faultyCapture = new MetricCapture(Mx.MetricsOf(faulty).Meter);
        Assert.Same(fault, await Assert.ThrowsAsync<IOException>(() => faulty.GetRequiredService<IMediator>().Send(new MxIdempotent())));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => faulty.GetRequiredService<IMediator>().Send(new MxIdempotent(), cts.Token));
        Assert.Equal(new[] { "legacy:error", "legacy:cancellation" }, States(faultyCapture));
    }

    [Fact]
    public async Task Legacy_Store_Failure_After_Handler_Is_Uncertain_And_Retry_Suppressed()
    {
        var original = new IOException("store");
        using var provider = Provider(s => s.AddSingleton<IIdempotencyStore>(new MxScriptedIdempotencyStore { Store = (_, _, _) => Task.FromException(original) }), () => 1);
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);

        var thrown = await Assert.ThrowsAsync<IOException>(() => provider.GetRequiredService<IMediator>().Send(new MxIdempotent()));
        Assert.Same(original, thrown);
        Assert.False(RetryPolicy.TransientOnly(thrown));
        Assert.Equal(new[] { "legacy:miss", "legacy:uncertain" }, States(capture));
    }

    [Fact]
    public async Task Legacy_Null_Response_Is_Not_Stored_So_It_Is_Not_Completed()
    {
        using var provider = Mx.Build(
            s =>
            {
                s.AddSingleton<IIdempotencyStore, MxIdempotencyStore>();
                s.AddTransient<IRequestHandler<MxIdempotentText, string?>, MxIdempotentTextHandler>();
            },
            c => c.AddOpenBehavior(typeof(IdempotencyBehavior<,>)),
            Mx.On());
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);

        Assert.Null(await provider.GetRequiredService<IMediator>().Send(new MxIdempotentText()));
        Assert.Equal(new[] { "legacy:miss" }, States(capture));
    }

    // Atomic store ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Atomic_Claim_Complete_Replay_And_InFlight()
    {
        var store = new MxAtomicStore();
        using var provider = Provider(s => s.AddSingleton<IIdempotencyStore>(store), () => 1);
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        var mediator = provider.GetRequiredService<IMediator>();

        Assert.Equal(1, await mediator.Send(new MxIdempotent()));
        Assert.Equal(new[] { "atomic:claimed", "atomic:completed" }, States(capture));

        store.Claim = new IdempotencyClaimResult(IdempotencyClaimStatus.Completed, 7);
        Assert.Equal(7, await mediator.Send(new MxIdempotent()));
        store.Claim = new IdempotencyClaimResult(IdempotencyClaimStatus.Completed, "text");
        await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Send(new MxIdempotent()));
        store.Claim = new IdempotencyClaimResult(IdempotencyClaimStatus.InFlight);
        await Assert.ThrowsAsync<IdempotencyInProgressException>(() => mediator.Send(new MxIdempotent()));
        store.ClaimFault = new IOException("claim");
        await Assert.ThrowsAsync<IOException>(() => mediator.Send(new MxIdempotent()));

        Assert.Equal(
            new[] { "atomic:claimed", "atomic:completed", "atomic:replayed", "atomic:error", "atomic:in_flight", "atomic:error" },
            States(capture));
    }

    [Fact]
    public async Task Atomic_Handler_Failure_Reports_Confirmed_Release_Or_Uncertainty_Without_Rerunning()
    {
        var original = new InvalidOperationException("handler");
        var store = new MxAtomicStore();
        var handlerCalls = 0;
        using var provider = Provider(s => s.AddSingleton<IIdempotencyStore>(store), () =>
        {
            handlerCalls++;
            throw original;
        });
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        var mediator = provider.GetRequiredService<IMediator>();

        Assert.Same(original, await Assert.ThrowsAsync<InvalidOperationException>(() => mediator.Send(new MxIdempotent())));
        Assert.Equal(1, store.Releases);
        Assert.Equal(new[] { "atomic:claimed", "atomic:released" }, States(capture));

        var releaseFault = new IOException("release");
        store.ReleaseFault = releaseFault;
        var aggregate = await Assert.ThrowsAsync<AggregateException>(() => mediator.Send(new MxIdempotent()));
        Assert.Collection(aggregate.InnerExceptions, e => Assert.Same(original, e), e => Assert.Same(releaseFault, e));
        Assert.False(RetryPolicy.TransientOnly(aggregate));
        Assert.Equal(
            new[] { "atomic:claimed", "atomic:released", "atomic:claimed", "atomic:uncertain" },
            States(capture));
        Assert.Equal(2, handlerCalls);
    }

    [Fact]
    public async Task Atomic_Retry_Suppressed_Failure_Is_Not_Released_And_Completion_Failure_Is_Uncertain()
    {
        var suppressed = new IOException("inner commit");
        BehaviorFailure.SuppressRetry(suppressed);
        var store = new MxAtomicStore();
        var failure = new Func<int>(() => throw suppressed);
        using var provider = Provider(s => s.AddSingleton<IIdempotencyStore>(store), () => failure());
        using var capture = new MetricCapture(Mx.MetricsOf(provider).Meter);
        var mediator = provider.GetRequiredService<IMediator>();

        Assert.Same(suppressed, await Assert.ThrowsAsync<IOException>(() => mediator.Send(new MxIdempotent())));
        Assert.Equal(0, store.Releases);
        Assert.Equal(new[] { "atomic:claimed", "atomic:uncertain" }, States(capture));

        failure = () => 3;
        var completion = new IOException("complete");
        store.CompleteFault = completion;
        var thrown = await Assert.ThrowsAsync<IOException>(() => mediator.Send(new MxIdempotent()));
        Assert.Same(completion, thrown);
        Assert.False(RetryPolicy.TransientOnly(thrown));
        Assert.Equal(new[] { "atomic:claimed", "atomic:uncertain", "atomic:claimed", "atomic:uncertain" }, States(capture));
    }

    // Leased store ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Leased_Claim_And_Completion_Are_Each_Reported_Once()
    {
        var store = new MxLeasedStore();
        var (behavior, capture) = Leased(store);
        using (capture)
        {
            Assert.Equal("done", await behavior.Handle(new MxLeasedRequest(), () => Task.FromResult("done"), default));
            Assert.Equal(new[] { "leased:claimed", "leased:completed" }, States(capture));
            Assert.Equal(1, store.Completions);
            Assert.Equal(0, store.Releases);
        }
    }

    [Fact]
    public async Task Leased_Replay_InFlight_OutcomeUnknown_And_Claim_Errors()
    {
        var store = new MxLeasedStore();
        var (behavior, capture) = Leased(store);
        using (capture)
        {
            var calls = 0;
            RequestHandlerDelegate<string> handler = () =>
            {
                calls++;
                return Task.FromResult("wrong");
            };

            store.Claim = new LeasedIdempotencyClaim(LeasedIdempotencyStatus.Completed, "cached");
            Assert.Equal("cached", await behavior.Handle(new MxLeasedRequest(), handler, default));
            store.Claim = new LeasedIdempotencyClaim(LeasedIdempotencyStatus.Completed, 5);
            await Assert.ThrowsAsync<InvalidOperationException>(() => behavior.Handle(new MxLeasedRequest(), handler, default));
            store.Claim = new LeasedIdempotencyClaim(LeasedIdempotencyStatus.InFlight);
            await Assert.ThrowsAsync<IdempotencyInProgressException>(() => behavior.Handle(new MxLeasedRequest(), handler, default));
            store.Claim = new LeasedIdempotencyClaim(LeasedIdempotencyStatus.OutcomeUnknown);
            await Assert.ThrowsAsync<IdempotencyOutcomeUnknownException>(() => behavior.Handle(new MxLeasedRequest(), handler, default));
            store.Claim = new LeasedIdempotencyClaim(LeasedIdempotencyStatus.Claimed);
            await Assert.ThrowsAsync<InvalidOperationException>(() => behavior.Handle(new MxLeasedRequest(), handler, default));
            store.Claim = null;
            store.ClaimFault = new IOException("claim");
            await Assert.ThrowsAsync<IOException>(() => behavior.Handle(new MxLeasedRequest(), handler, default));

            Assert.Equal(0, calls);
            Assert.Equal(
                new[] { "leased:replayed", "leased:error", "leased:in_flight", "leased:uncertain", "leased:error", "leased:error" },
                States(capture));
        }
    }

    [Fact]
    public async Task Leased_Handler_Failure_Leaves_Ownership_Uncertain_Without_Release()
    {
        var store = new MxLeasedStore();
        var (behavior, capture) = Leased(store);
        using (capture)
        {
            var original = new IOException("effect may have happened");
            var thrown = await Assert.ThrowsAsync<IOException>(() => behavior.Handle(new MxLeasedRequest(), () => throw original, default));
            Assert.Same(original, thrown);
            Assert.False(RetryPolicy.TransientOnly(thrown));
            Assert.Equal(0, store.Releases);
            Assert.Equal(new[] { "leased:claimed", "leased:uncertain" }, States(capture));
        }
    }

    [Fact]
    public async Task Leased_Completion_Failure_Is_Uncertain_And_Never_Reruns_The_Handler()
    {
        var store = new MxLeasedStore { CompleteFault = new IOException("complete") };
        var (behavior, capture) = Leased(store);
        using (capture)
        {
            var calls = 0;
            var thrown = await Assert.ThrowsAsync<IOException>(() => behavior.Handle(new MxLeasedRequest(), () =>
            {
                calls++;
                return Task.FromResult("done");
            }, default));
            Assert.Same(store.CompleteFault, thrown);
            Assert.Equal(1, calls);
            Assert.Equal(new[] { "leased:claimed", "leased:uncertain" }, States(capture));
        }
    }

    [Fact]
    public async Task Confirmed_False_Renewal_Reports_Lease_Loss_Once_And_Provider_Faults_Do_Not()
    {
        var store = new MxLeasedStore();
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Renew = (_, _) =>
        {
            renewed.TrySetResult();
            return Task.FromResult(false);
        };
        var (behavior, capture) = Leased(store, lifetimeMs: 900);
        using (capture)
        {
            var calls = 0;
            await Assert.ThrowsAsync<IdempotencyOutcomeUnknownException>(() => behavior.Handle(new MxLeasedRequest(), async () =>
            {
                calls++;
                await renewed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return "effect";
            }, default));
            Assert.Equal(1, calls);
            Assert.Equal(0, store.Completions);
            Assert.Equal(new[] { "leased:claimed", "leased:lease_lost", "leased:uncertain" }, States(capture));
        }

        // A provider that merely throws the same exception type is not a confirmed lease loss.
        var faulty = new MxLeasedStore();
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        faulty.Renew = (_, _) =>
        {
            attempted.TrySetResult();
            return Task.FromException<bool>(new IdempotencyOutcomeUnknownException());
        };
        var (second, secondCapture) = Leased(faulty, lifetimeMs: 900);
        using (secondCapture)
        {
            await Assert.ThrowsAsync<IdempotencyOutcomeUnknownException>(() => second.Handle(new MxLeasedRequest(), async () =>
            {
                await attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return "effect";
            }, default));
            Assert.Equal(new[] { "leased:claimed", "leased:uncertain" }, States(secondCapture));
        }
    }

    [Fact]
    public async Task Unusable_Lease_Interval_After_Claim_Is_Uncertain_And_Invalid_Lease_Is_An_Error()
    {
        var expired = new MxLeasedStore();
        var (behavior, capture) = Leased(expired, lifetimeMs: -1000);
        using (capture)
        {
            await Assert.ThrowsAsync<IdempotencyOutcomeUnknownException>(() => behavior.Handle(new MxLeasedRequest(), () => Task.FromResult("x"), default));
            Assert.Equal(new[] { "leased:claimed", "leased:uncertain" }, States(capture));
        }

        var invalid = new MxLeasedStore { Claim = new LeasedIdempotencyClaim(LeasedIdempotencyStatus.Claimed, Lease: new IdempotencyLease(" ", "o", "c", DateTimeOffset.UtcNow.AddSeconds(5))) };
        var (second, secondCapture) = Leased(invalid);
        using (secondCapture)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => second.Handle(new MxLeasedRequest(), () => Task.FromResult("x"), default));
            Assert.Equal(new[] { "leased:error" }, States(secondCapture));
        }
    }

    private static (IdempotencyBehavior<MxLeasedRequest, string> Behavior, MetricCapture Capture) Leased(MxLeasedStore store, int lifetimeMs = 30_000)
    {
        if (store.Claim?.Lease is null && store.Claim is null && store.ClaimFault is null)
        {
            store.Claim = new LeasedIdempotencyClaim(
                LeasedIdempotencyStatus.Claimed,
                Lease: new IdempotencyLease("effective", "owner", "text", DateTimeOffset.UtcNow.AddMilliseconds(lifetimeMs)));
        }

        var metrics = new MediatorMetrics(Mx.On(), new[] { typeof(MxLeasedRequest) });
        var capture = new MetricCapture(metrics.Meter);
        // The behavior owns no disposable state; the capture disposes the listener and the meter is collected with it.
        return (new IdempotencyBehavior<MxLeasedRequest, string>(store, null, metrics), capture);
    }

    private static ServiceProvider Provider(Action<IServiceCollection> register, Func<int> handler) =>
        Mx.Build(
            s =>
            {
                s.AddSingleton(new MxIdempotentCalls(handler));
                s.AddTransient<IRequestHandler<MxIdempotent, int>, MxIdempotentHandler>();
                register(s);
            },
            c => c.AddOpenBehavior(typeof(IdempotencyBehavior<,>)),
            Mx.On());
}

public sealed class MxIdempotent : IRequest<int>, IIdempotentRequest
{
    public string IdempotencyKey => "idem-key";
}

public sealed class MxIdempotentText : IRequest<string?>, IIdempotentRequest
{
    public string IdempotencyKey => "text-key";
}

public sealed class MxIdempotentTextHandler : IRequestHandler<MxIdempotentText, string?>
{
    public Task<string?> Handle(MxIdempotentText request, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

public sealed class MxIdempotentCalls(Func<int> next)
{
    public int Next() => next();
}

public sealed class MxIdempotentHandler(MxIdempotentCalls calls) : IRequestHandler<MxIdempotent, int>
{
    public Task<int> Handle(MxIdempotent request, CancellationToken cancellationToken) => Task.FromResult(calls.Next());
}

public sealed class MxLeasedRequest : IIdempotentRequest
{
    public string IdempotencyKey => "leased-key";
}

public sealed class MxScriptedIdempotencyStore : IIdempotencyStore
{
    public Func<string, CancellationToken, Task<(bool, object?)>> Check { get; set; } = (_, _) => Task.FromResult<(bool, object?)>((false, null));

    public Func<string, object, CancellationToken, Task> Store { get; set; } = (_, _, _) => Task.CompletedTask;

    public async Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct) => await Check(key, ct);

    public Task StoreAsync(string key, object response, CancellationToken ct) => Store(key, response, ct);
}

public sealed class MxAtomicStore : IAtomicIdempotencyStore
{
    public IdempotencyClaimResult Claim { get; set; } = new(IdempotencyClaimStatus.Claimed);

    public Exception? ClaimFault { get; set; }

    public Exception? CompleteFault { get; set; }

    public Exception? ReleaseFault { get; set; }

    public int Releases { get; private set; }

    public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct) => Task.FromResult<(bool, object?)>((false, null));

    public Task StoreAsync(string key, object response, CancellationToken ct) => Task.CompletedTask;

    public Task<IdempotencyClaimResult> ClaimAsync(string key, CancellationToken ct)
    {
        if (ClaimFault is not null)
        {
            var fault = ClaimFault;
            ClaimFault = null;
            return Task.FromException<IdempotencyClaimResult>(fault);
        }

        return Task.FromResult(Claim);
    }

    public Task CompleteAsync(string key, object? response, CancellationToken ct) =>
        CompleteFault is null ? Task.CompletedTask : Task.FromException(CompleteFault);

    public Task ReleaseAsync(string key, CancellationToken ct)
    {
        Releases++;
        return ReleaseFault is null ? Task.CompletedTask : Task.FromException(ReleaseFault);
    }
}

public sealed class MxLeasedStore : ILeasedIdempotencyStore
{
    public LeasedIdempotencyClaim? Claim { get; set; }

    public Exception? ClaimFault { get; set; }

    public Exception? CompleteFault { get; set; }

    public Func<IdempotencyLease, CancellationToken, Task<bool>>? Renew { get; set; }

    public int Completions { get; private set; }

    public int Releases { get; private set; }

    public Task<(bool Exists, object? CachedResponse)> CheckAsync(string key, CancellationToken ct) => Task.FromResult<(bool, object?)>((false, null));

    public Task StoreAsync(string key, object response, CancellationToken ct) => Task.CompletedTask;

    public Task<LeasedIdempotencyClaim> ClaimAsync(string key, Type responseType, CancellationToken ct) =>
        ClaimFault is not null ? Task.FromException<LeasedIdempotencyClaim>(ClaimFault) : Task.FromResult(Claim!);

    public Task CompleteAsync(IdempotencyLease lease, object? response, CancellationToken ct)
    {
        Completions++;
        return CompleteFault is null ? Task.CompletedTask : Task.FromException(CompleteFault);
    }

    public Task ReleaseAsync(IdempotencyLease lease, CancellationToken ct)
    {
        Releases++;
        return Task.CompletedTask;
    }

    public Task<bool> RenewAsync(IdempotencyLease lease, CancellationToken ct) => Renew?.Invoke(lease, ct) ?? Task.FromResult(true);
}
