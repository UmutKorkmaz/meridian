using Meridian.Mediator.Behaviors;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;

namespace Meridian.Mediator.Caching.Tests;

public sealed class CoalescingTests
{
    private static readonly CachingBehaviorOptions Enabled = new() { EnableRequestCoalescing = true };
    private static readonly CachingBehaviorOptions RawEnabled = new() { EnableRequestCoalescing = true, UseRequestTypeNamespacedKeys = false };

    [Fact]
    public async Task Twenty_Misses_Run_One_Factory_When_Enabled()
    {
        using var cache = MemoryCacheTests.CreateCache(out _); var provider = new MemoryCacheProvider(cache);
        var behavior = new CachingBehavior<MemoryCacheTests.Query, object>(provider, Enabled);
        var entered = Signal(); var release = Signal(); var value = new object(); var calls = 0;
        async Task<object> Factory() { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task; return value; }
        var owner = behavior.Handle(new("key"), Factory, default); await entered.Task;
        var waiters = Enumerable.Range(0, 19).Select(_ => behavior.Handle(new("key"), Factory, default)).ToArray();
        try { Assert.All(waiters, t => Assert.False(t.IsCompleted)); Assert.Equal(1, calls); Assert.Equal(1, provider.ActiveOperationCount); }
        finally { release.TrySetResult(); }
        Assert.All(await Task.WhenAll(waiters.Prepend(owner)), x => Assert.Same(value, x));
        Assert.Equal(1, calls); Assert.Equal(0, provider.ActiveOperationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disabled_Coalescing_Retains_Prior_Path(bool explicitDisabled)
    {
        using var cache = MemoryCacheTests.CreateCache(out _); var provider = new MemoryCacheProvider(cache);
        var behavior = explicitDisabled ? new CachingBehavior<MemoryCacheTests.Query, int>(provider, new() { EnableRequestCoalescing = false }) : new(provider);
        var release = Signal(); var calls = 0;
        async Task<int> Factory() { calls++; await release.Task; return 1; }
        var tasks = Enumerable.Range(0, 20).Select(_ => behavior.Handle(new("key"), Factory, default)).ToArray();
        try { Assert.Equal(20, calls); Assert.Equal(0, provider.ActiveOperationCount); }
        finally { release.TrySetResult(); }
        Assert.All(await Task.WhenAll(tasks), n => Assert.Equal(1, n));
    }

    [Fact]
    public async Task Waiter_Cancellation_Does_Not_Cancel_Owner()
    {
        using var cache = MemoryCacheTests.CreateCache(out _); var provider = new MemoryCacheProvider(cache);
        var release = Signal(); var entered = Signal(); using var ownerCancellation = new CancellationTokenSource(); using var waiterCancellation = new CancellationTokenSource();
        async Task<object?> Factory(CancellationToken ct) { Assert.Equal(ownerCancellation.Token, ct); entered.SetResult(); await release.Task; return "owner"; }
        var owner = provider.GetOrCreateAsync("key", Factory, null, ownerCancellation.Token); await entered.Task;
        var canceled = provider.GetOrCreateAsync("key", _ => throw new InvalidOperationException("unused factory"), null, waiterCancellation.Token);
        var waiter = provider.GetOrCreateAsync("key", _ => throw new InvalidOperationException("unused factory"), null, default);
        waiterCancellation.Cancel();
        try
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            Assert.Equal(waiterCancellation.Token, error.CancellationToken); Assert.False(owner.IsCompleted); Assert.False(ownerCancellation.IsCancellationRequested); Assert.Equal(1, provider.ActiveOperationCount);
        }
        finally { release.TrySetResult(); }
        Assert.Equal("owner", await owner); Assert.Equal("owner", await waiter); Assert.Equal(0, provider.ActiveOperationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owner_Cancellation_Retires_Only_After_Factory_Settles(bool cooperative)
    {
        using var cache = MemoryCacheTests.CreateCache(out _); var provider = new MemoryCacheProvider(cache);
        var release = Signal(); var entered = Signal(); using var cancel = new CancellationTokenSource();
        async Task<object?> Factory(CancellationToken ct) { entered.SetResult(); if (cooperative) await release.Task.WaitAsync(ct); else await release.Task; return "canceled"; }
        var owner = provider.GetOrCreateAsync("key", Factory, null, cancel.Token); await entered.Task;
        var waiter = provider.GetOrCreateAsync("key", _ => throw new InvalidOperationException(), null, default);
        cancel.Cancel();
        if (!cooperative) { Assert.False(owner.IsCompleted); Assert.False(waiter.IsCompleted); Assert.Equal(1, provider.ActiveOperationCount); }
        release.TrySetResult();
        Assert.Equal(cancel.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner)).CancellationToken);
        Assert.Equal(cancel.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter)).CancellationToken);
        Assert.Equal(0, provider.ActiveOperationCount); Assert.False((await provider.GetAsync("key", default)).Found);
        Assert.Equal("fresh", await provider.GetOrCreateAsync("key", _ => Task.FromResult<object?>("fresh"), null, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Factory_Fault_Is_Original_And_Later_Call_Retries(bool synchronous)
    {
        using var cache = MemoryCacheTests.CreateCache(out _); var provider = new MemoryCacheProvider(cache); var error = new InvalidOperationException("original");
        var release = Signal();
        async Task<object?> AsyncFactory(CancellationToken _) { await release.Task; throw error; }
        if (synchronous)
        {
            Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetOrCreateAsync("key", _ => throw error, null, default)));
        }
        else
        {
            var owner = provider.GetOrCreateAsync("key", AsyncFactory, null, default);
            var waiter = provider.GetOrCreateAsync("key", _ => throw new InvalidOperationException("must not run"), null, default);
            release.SetResult(); Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => owner)); Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => waiter));
        }
        Assert.Equal(0, provider.ActiveOperationCount); Assert.False((await provider.GetAsync("key", default)).Found);
        Assert.Equal(9, await provider.GetOrCreateAsync("key", _ => Task.FromResult<object?>(9), null, default));
    }

    [Fact]
    public async Task Backend_Write_Failure_Reaches_Owner_And_Waiter_And_Cleans_Up()
    {
        using var cache = MemoryCacheTests.CreateCache(out _); var error = new InvalidOperationException("backend"); var provider = new MemoryCacheProvider(new WriteFailingCache(cache, error));
        var release = Signal();
        async Task<object?> Factory(CancellationToken _) { await release.Task; return 1; }
        var owner = provider.GetOrCreateAsync("key", Factory, null, default);
        var waiter = provider.GetOrCreateAsync("key", _ => throw new InvalidOperationException("unused"), null, default); release.SetResult();
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => owner)); Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => waiter)); Assert.Equal(0, provider.ActiveOperationCount);
    }

    [Fact]
    public async Task Distinct_Keys_And_Provider_Instances_Run_Independently()
    {
        using var cache = MemoryCacheTests.CreateCache(out _); var a = new MemoryCacheProvider(cache); var b = new MemoryCacheProvider(cache); var release = Signal(); var calls = 0;
        async Task<object?> Factory(CancellationToken _) { calls++; await release.Task; return 1; }
        var tasks = new[] { a.GetOrCreateAsync("a", Factory, null, default), a.GetOrCreateAsync("b", Factory, null, default), b.GetOrCreateAsync("a", Factory, null, default) };
        try { Assert.Equal(3, calls); Assert.Equal(2, a.ActiveOperationCount); Assert.Equal(1, b.ActiveOperationCount); } finally { release.SetResult(); }
        await Task.WhenAll(tasks); Assert.Equal(0, a.ActiveOperationCount); Assert.Equal(0, b.ActiveOperationCount);
    }

    [Fact]
    public async Task Incompatible_Hit_Uses_One_Correct_Handler_And_Assignable_Hit_Is_Accepted()
    {
        using var cache = MemoryCacheTests.CreateCache(out _); var provider = new MemoryCacheProvider(cache); await provider.SetAsync("key", "wrong", null, default);
        var calls = 0; var behavior = new CachingBehavior<MemoryCacheTests.Query, int>(provider, RawEnabled);
        Assert.Equal(7, await behavior.Handle(new("key"), () => { calls++; return Task.FromResult(7); }, default)); Assert.Equal(1, calls); Assert.Equal(7, (await provider.GetAsync("key", default)).Value);
        var derived = new Derived(); await provider.SetAsync("key", derived, null, default);
        Assert.Same(derived, await new CachingBehavior<MemoryCacheTests.Query, Base>(provider, RawEnabled).Handle(new("key"), () => throw new InvalidOperationException(), default));
    }

    [Fact]
    public async Task Heterogeneous_Raw_InFlight_Response_Falls_Back_Exactly_Once()
    {
        using var cache = MemoryCacheTests.CreateCache(out _); var provider = new MemoryCacheProvider(cache); var release = Signal(); var calls = 0;
        async Task<string> OwnerFactory() { await release.Task; return "owner"; }
        var owner = new CachingBehavior<MemoryCacheTests.Query, string>(provider, RawEnabled).Handle(new("raw"), OwnerFactory, default);
        var waiter = new CachingBehavior<MemoryCacheTests.Query, int>(provider, RawEnabled).Handle(new("raw"), () => { calls++; return Task.FromResult(42); }, default);
        try { Assert.Equal(0, calls); Assert.False(waiter.IsCompleted); } finally { release.SetResult(); }
        Assert.Equal("owner", await owner); Assert.Equal(42, await waiter); Assert.Equal(1, calls); Assert.Equal(42, (await provider.GetAsync("raw", default)).Value); Assert.Equal(0, provider.ActiveOperationCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ambiguous_Shared_Null_Invokes_Each_Waiters_Handler_Once(bool heterogeneous)
    {
        using var cache = MemoryCacheTests.CreateCache(out _); var provider = new MemoryCacheProvider(cache); var release = Signal(); var ownerCalls = 0; var waiterCalls = 0;
        async Task<string?> OwnerFactory() { ownerCalls++; await release.Task; return null; }
        var owner = new CachingBehavior<MemoryCacheTests.Query, string?>(provider, RawEnabled).Handle(new("raw"), OwnerFactory, default);
        Task<object?> waiter = heterogeneous
            ? new CachingBehavior<MemoryCacheTests.Query, object?>(provider, RawEnabled).Handle(new("raw"), () => { waiterCalls++; return Task.FromResult<object?>(42); }, default)
            : Box(new CachingBehavior<MemoryCacheTests.Query, string?>(provider, RawEnabled).Handle(new("raw"), () => { waiterCalls++; return Task.FromResult<string?>(null); }, default));
        try { Assert.Equal(0, waiterCalls); } finally { release.SetResult(); }
        Assert.Null(await owner); Assert.Equal(heterogeneous ? 42 : null, await waiter); Assert.Equal(1, ownerCalls); Assert.Equal(1, waiterCalls);
        Assert.Equal(heterogeneous, (await provider.GetAsync("raw", default)).Found);
        static async Task<object?> Box(Task<string?> task) => await task;
    }

    [Fact]
    public async Task Nullable_Value_Null_Owner_And_Waiter_Keep_Null_Miss_Semantics()
    {
        using var cache = MemoryCacheTests.CreateCache(out _); var provider = new MemoryCacheProvider(cache); var release = Signal(); var calls = 0;
        async Task<int?> Factory() { calls++; await release.Task; return null; }
        var behavior = new CachingBehavior<MemoryCacheTests.Query, int?>(provider, Enabled);
        var owner = behavior.Handle(new("key"), Factory, default); var waiter = behavior.Handle(new("key"), Factory, default); release.SetResult();
        Assert.Null(await owner); Assert.Null(await waiter); Assert.Equal(2, calls); Assert.False((await provider.GetAsync(CacheKeyComposer.Compose<MemoryCacheTests.Query>("key"), default)).Found);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("incompatible")]
    [InlineData(17)]
    public async Task Richer_Provider_Second_Read_Result_Is_Checked(object? result)
    {
        var provider = new SecondReadProvider(result); var calls = 0;
        var behavior = new CachingBehavior<MemoryCacheTests.Query, int>(provider, RawEnabled);
        Assert.Equal(result is int value ? value : 7, await behavior.Handle(new("raw"), () => { calls++; return Task.FromResult(7); }, default));
        Assert.Equal(result is int ? 0 : 1, calls); Assert.Equal(1, provider.ConcurrentCalls); Assert.Equal("raw", provider.Key);
    }

    [Fact]
    public async Task Legacy_Provider_Still_Works_With_Option_Enabled()
    {
        var provider = new LegacyProvider(); var behavior = new CachingBehavior<MemoryCacheTests.Query, int>(provider, Enabled);
        Assert.Equal(7, await behavior.Handle(new("key"), () => Task.FromResult(7), default)); Assert.Equal(7, provider.Value);
    }

    [Fact]
    public async Task High_Key_Churn_Retains_Only_Live_Operations_And_Old_Waiter_Cannot_Retire_New_Owner()
    {
        using var cache = MemoryCacheTests.CreateCache(out _); var provider = new MemoryCacheProvider(cache);
        for (var i = 0; i < 100; i++)
        {
            var key = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.Null(await provider.GetOrCreateAsync(key, _ => Task.FromResult<object?>(null), null, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetOrCreateAsync(key, _ => throw new InvalidOperationException(), null, default));
            using var cancel = new CancellationTokenSource(); var release = Signal();
            async Task<object?> Factory(CancellationToken _) { await release.Task; return 1; }
            var owner = provider.GetOrCreateAsync(key, Factory, null, cancel.Token); cancel.Cancel(); release.SetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner);
            Assert.Equal(1, await provider.GetOrCreateAsync(key, _ => Task.FromResult<object?>(1), TimeSpan.Zero, default)); Assert.Equal(0, provider.ActiveOperationCount);
        }
        var oldRelease = Signal(); var newRelease = Signal(); using var oldWaitCancellation = new CancellationTokenSource();
        async Task<object?> Old(CancellationToken _) { await oldRelease.Task; return null; }
        async Task<object?> New(CancellationToken _) { await newRelease.Task; return "new"; }
        var old = provider.GetOrCreateAsync("same", Old, null, default);
        var oldWait = provider.GetOrCreateAsync("same", _ => throw new InvalidOperationException(), null, oldWaitCancellation.Token);
        oldRelease.SetResult(); await old;
        var fresh = provider.GetOrCreateAsync("same", New, null, default); oldWaitCancellation.Cancel();
        try { await oldWait; } catch (OperationCanceledException) { }
        try { Assert.Equal(1, provider.ActiveOperationCount); Assert.False(fresh.IsCompleted); } finally { newRelease.SetResult(); }
        Assert.Equal("new", await fresh); Assert.Equal(0, provider.ActiveOperationCount);
    }

    [Fact]
    public async Task Memory_Recheck_Under_Election_Gate_Returns_New_Hit()
    {
        using var cache = MemoryCacheTests.CreateCache(out _); var wrapper = new RecheckCache(cache); var provider = new MemoryCacheProvider(wrapper);
        Assert.Equal("rechecked", await provider.GetOrCreateAsync("key", _ => throw new InvalidOperationException("recheck missing"), null, default)); Assert.Equal(2, wrapper.Reads); Assert.Equal(0, provider.ActiveOperationCount);
    }

    [Fact]
    public async Task Scoped_Dependency_Remains_Live_Until_Noncooperative_Owner_Settles()
    {
        var services = new ServiceCollection().AddMeridianMemoryCaching(); services.AddScoped<ScopedState>();
        using var di = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var scope = di.CreateScope(); var state = scope.ServiceProvider.GetRequiredService<ScopedState>(); var provider = (MemoryCacheProvider)di.GetRequiredService<ICacheProvider>();
        using var cancel = new CancellationTokenSource(); var release = Signal();
        async Task<object?> Factory(CancellationToken _) { await release.Task; Assert.False(state.Disposed); return state.Id; }
        var owner = provider.GetOrCreateAsync("key", Factory, null, cancel.Token); cancel.Cancel(); Assert.False(owner.IsCompleted); Assert.False(state.Disposed); release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner); scope.Dispose(); Assert.True(state.Disposed); Assert.Equal(0, provider.ActiveOperationCount);
    }

    [Fact]
    public async Task Waiter_Unused_Factory_Is_Not_Retained()
    {
        using var cache = MemoryCacheTests.CreateCache(out _); var provider = new MemoryCacheProvider(cache); var release = Signal();
        async Task<object?> Factory(CancellationToken _) { await release.Task; return 1; }
        var owner = provider.GetOrCreateAsync("key", Factory, null, default);
        var (waiter, weak) = StartUnusedWaiter(provider);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        try { Assert.False(weak.IsAlive); Assert.False(waiter.IsCompleted); } finally { release.SetResult(); }
        Assert.Equal(1, await waiter); Assert.Equal(1, await owner);
    }

    [Fact]
    public async Task Successful_Value_Is_Stored_Before_Waiter_Publication()
    {
        using var cache = MemoryCacheTests.CreateCache(out _); var provider = new MemoryCacheProvider(cache); var release = Signal();
        async Task<object?> Factory(CancellationToken _) { await release.Task; return 42; }
        var owner = provider.GetOrCreateAsync("key", Factory, null, default);
        var waiter = provider.GetOrCreateAsync("key", _ => throw new InvalidOperationException(), null, default);
        release.SetResult(); Assert.Equal(42, await waiter);
        Assert.Equal(42, (await provider.GetAsync("key", default)).Value); Assert.Equal(0, provider.ActiveOperationCount); await owner;
    }

    [Fact]
    public async Task Retired_Owner_Factory_Does_Not_Retain_Scoped_Dependency()
    {
        using var cache = MemoryCacheTests.CreateCache(out _); var provider = new MemoryCacheProvider(cache);
        var weak = await CompleteScopedOwner(provider);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(weak.IsAlive); Assert.Equal(0, provider.ActiveOperationCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> CompleteScopedOwner(MemoryCacheProvider provider)
    {
        var state = new ScopedState(); var weak = new WeakReference(state);
        await provider.GetOrCreateAsync("key", _ => Task.FromResult<object?>(state.Id), null, default);
        return weak;
    }

    [Fact]
    public async Task Backend_Read_Failure_Propagates_Without_Electing_An_Owner()
    {
        var error = new InvalidOperationException("read failure"); var provider = new MemoryCacheProvider(new ReadFailingCache(error));
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetOrCreateAsync("key", _ => throw new InvalidOperationException("unused"), null, default)));
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => new CachingBehavior<MemoryCacheTests.Query, int>(provider, Enabled).Handle(new("key"), () => throw new InvalidOperationException("unused"), default)));
        Assert.Equal(0, provider.ActiveOperationCount);
    }

    private sealed class ReadFailingCache(Exception error) : IMemoryCache
    {
        public ICacheEntry CreateEntry(object key) => throw new InvalidOperationException();
        public bool TryGetValue(object key, out object? value) => throw error;
        public void Remove(object key) => throw new InvalidOperationException();
        public void Dispose() { }
    }

    [Theory]
    [InlineData("direct")]
    [InlineData("ancestor-chain")]
    [InlineData("raw-typed-handler")]
    public async Task Owner_Ancestor_Reentry_Fails_Fast_Retires_And_Allows_Fresh_Retry(string mode)
    {
        using var cache = MemoryCacheTests.CreateCache(out _);
        var provider = new MemoryCacheProvider(cache);
        var outerEntered = Signal();
        var startNested = Signal();
        var nestedAttempted = Signal();
        using var cleanupGuard = new CancellationTokenSource();
        var innerCalls = 0;
        const string sensitiveKey = "private-key-for-redaction";
        Task<object?> Inner(CancellationToken _) { innerCalls++; return Task.FromResult<object?>(7); }
        Task<object?> JoinAncestor()
        {
            try { return provider.GetOrCreateAsync(sensitiveKey, Inner, null, cleanupGuard.Token); }
            finally { nestedAttempted.TrySetResult(); }
        }
        async Task<object?> Outer(CancellationToken _)
        {
            outerEntered.SetResult();
            await startNested.Task;
            if (mode == "ancestor-chain")
            {
                return await provider.GetOrCreateAsync("middle-key", _ => JoinAncestor(), null, default);
            }
            if (mode == "raw-typed-handler")
            {
                var nestedBehavior = new CachingBehavior<RawNestedQuery, int>(provider, RawEnabled);
                Task<int> Handler() { innerCalls++; return Task.FromResult(7); }
                Task<int> nested;
                try { nested = nestedBehavior.Handle(new(sensitiveKey), Handler, cleanupGuard.Token); }
                finally { nestedAttempted.TrySetResult(); }
                return await nested;
            }
            return await JoinAncestor();
        }
        Task<object?> owner;
        if (mode == "raw-typed-handler")
        {
            var outerBehavior = new CachingBehavior<MemoryCacheTests.Query, string>(provider, RawEnabled);
            async Task<string> OuterHandler() => (await Outer(default))!.ToString()!;
            owner = Box(outerBehavior.Handle(new(sensitiveKey), OuterHandler, default));
        }
        else
        {
            owner = provider.GetOrCreateAsync(sensitiveKey, Outer, null, default);
        }
        await outerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var legitimateWaiter = provider.GetOrCreateAsync(sensitiveKey, _ => throw new InvalidOperationException("unused waiter"), null, default);
        Assert.False(legitimateWaiter.IsCompleted);
        startNested.SetResult();
        await nestedAttempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // The previous implementation is deterministically stuck here. The test-only nested
        // token releases its cycle, so RED fails semantically without leaving a hung runner.
        var pendingBeforeGuard = !owner.IsCompleted;
        var activeBeforeGuard = provider.ActiveOperationCount;
        cleanupGuard.Cancel();
        var error = await Record.ExceptionAsync(() => owner);
        var waiterError = await Record.ExceptionAsync(() => legitimateWaiter);
        Assert.True(error is InvalidOperationException,
            $"Expected fail-fast reentry; observed {error?.GetType().Name}; pendingBeforeGuard={pendingBeforeGuard}; activeBeforeGuard={activeBeforeGuard}.");
        Assert.Same(error, waiterError);
        var reentry = Assert.IsType<InvalidOperationException>(error);
        Assert.Equal("Cache miss coalescing cannot join a live ancestor owner operation. Use distinct logical keys or disable coalescing for nested reads.", reentry.Message);
        Assert.DoesNotContain(sensitiveKey, reentry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("middle-key", reentry.Message, StringComparison.Ordinal);
        Assert.Equal(0, innerCalls);
        Assert.Equal(0, provider.ActiveOperationCount);
        Assert.False((await provider.GetAsync(sensitiveKey, default)).Found);
        Assert.Equal(11, await provider.GetOrCreateAsync(sensitiveKey, _ => Task.FromResult<object?>(11), null, default));
        static async Task<object?> Box(Task<string> value) => await value;
    }

    [Fact]
    public async Task Disabled_Coalescing_Preserves_Finite_Raw_Typed_Nesting()
    {
        using var cache = MemoryCacheTests.CreateCache(out _);
        var provider = new MemoryCacheProvider(cache);
        var options = new CachingBehaviorOptions { UseRequestTypeNamespacedKeys = false };
        var outer = new CachingBehavior<MemoryCacheTests.Query, string>(provider, options);
        var inner = new CachingBehavior<RawNestedQuery, int>(provider, options);
        var innerCalls = 0;
        Task<int> Inner() { innerCalls++; return Task.FromResult(7); }
        async Task<string> Outer() => (await inner.Handle(new("shared"), Inner, default)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("7", await outer.Handle(new("shared"), Outer, default));
        Assert.Equal(1, innerCalls);
        Assert.Equal(0, provider.ActiveOperationCount);
    }

    private sealed record RawNestedQuery(string CacheKey) : ICacheableQuery
    {
        public TimeSpan? CacheDuration => null;
    }

    [Fact]
    public async Task Nested_Distinct_Keys_And_Provider_Instances_Remain_Independent()
    {
        using var cache = MemoryCacheTests.CreateCache(out _);
        var a = new MemoryCacheProvider(cache);
        var b = new MemoryCacheProvider(cache);
        var calls = 0;
        Task<object?> Inner(CancellationToken _) { calls++; return Task.FromResult<object?>(7); }
        async Task<object?> Outer(CancellationToken _)
        {
            var otherKey = await a.GetOrCreateAsync("other", Inner, null, default);
            var otherProvider = await b.GetOrCreateAsync("same", Inner, null, default);
            return (int)otherKey! + (int)otherProvider!;
        }
        Assert.Equal(14, await a.GetOrCreateAsync("same", Outer, null, default));
        Assert.Equal(2, calls);
        Assert.Equal(0, a.ActiveOperationCount);
        Assert.Equal(0, b.ActiveOperationCount);
    }

    [Fact]
    public async Task Retired_Inherited_Context_Does_Not_Reject_A_Replacement_Operation()
    {
        using var cache = MemoryCacheTests.CreateCache(out _);
        var provider = new MemoryCacheProvider(cache);
        var inheritedContextReady = Signal();
        var retiredOwnerReleased = Signal();
        var joinReplacement = Signal();
        var replacementRelease = Signal();
        var descendantJoined = Signal();
        Task<object?>? descendant = null;
        async Task<object?> OldOwner(CancellationToken _)
        {
            descendant = Task.Run(async () =>
            {
                inheritedContextReady.SetResult();
                await joinReplacement.Task;
                Task<object?> waiting;
                try { waiting = provider.GetOrCreateAsync("key", _ => throw new InvalidOperationException("unused descendant"), null, default); }
                finally { descendantJoined.TrySetResult(); }
                return await waiting;
            });
            await inheritedContextReady.Task;
            await retiredOwnerReleased.Task;
            return null;
        }
        var old = provider.GetOrCreateAsync("key", OldOwner, null, default);
        await inheritedContextReady.Task.WaitAsync(TimeSpan.FromSeconds(10));
        retiredOwnerReleased.SetResult();
        Assert.Null(await old);
        async Task<object?> Replacement(CancellationToken _) { await replacementRelease.Task; return 9; }
        var replacement = provider.GetOrCreateAsync("key", Replacement, null, default);
        joinReplacement.SetResult();
        await descendantJoined.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { Assert.False(descendant!.IsCompleted); Assert.Equal(1, provider.ActiveOperationCount); }
        finally { replacementRelease.SetResult(); }
        Assert.Equal(9, await descendant!);
        Assert.Equal(9, await replacement);
        Assert.Equal(0, provider.ActiveOperationCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<object?> Task, WeakReference Weak) StartUnusedWaiter(MemoryCacheProvider provider)
    {
        var scoped = new ScopedState(); var weak = new WeakReference(scoped);
        Task<object?> Unused(CancellationToken _) => Task.FromResult<object?>(scoped.Id);
        return (provider.GetOrCreateAsync("key", Unused, null, default), weak);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private class Base;
    private sealed class Derived : Base;
    private sealed class ScopedState : IDisposable { public Guid Id { get; } = Guid.NewGuid(); public bool Disposed { get; private set; } public void Dispose() => Disposed = true; }
    private class LegacyProvider : ICacheProvider
    {
        public object? Value { get; private set; }
        public Task<(bool Found, object? Value)> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult<(bool, object?)>((false, null));
        public Task SetAsync(string key, object value, TimeSpan? duration, CancellationToken cancellationToken) { Value = value; return Task.CompletedTask; }
        public Task RemoveAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class SecondReadProvider(object? result) : LegacyProvider, IConcurrentCacheProvider
    {
        public int ConcurrentCalls { get; private set; }
        public string? Key { get; private set; }
        public Task<object?> GetOrCreateAsync(string key, Func<CancellationToken, Task<object?>> factory, TimeSpan? duration, CancellationToken cancellationToken) { ConcurrentCalls++; Key = key; return Task.FromResult(result); }
    }
    private sealed class WriteFailingCache(IMemoryCache inner, Exception error) : IMemoryCache
    {
        public ICacheEntry CreateEntry(object key) => throw error;
        public bool TryGetValue(object key, out object? value) => inner.TryGetValue(key, out value);
        public void Remove(object key) => inner.Remove(key);
        public void Dispose() { }
    }
    private sealed class RecheckCache(IMemoryCache inner) : IMemoryCache
    {
        public int Reads { get; private set; }
        public ICacheEntry CreateEntry(object key) => inner.CreateEntry(key);
        public bool TryGetValue(object key, out object? value) { Reads++; value = Reads == 2 ? "rechecked" : null; return Reads == 2; }
        public void Remove(object key) => inner.Remove(key);
        public void Dispose() { }
    }
}
