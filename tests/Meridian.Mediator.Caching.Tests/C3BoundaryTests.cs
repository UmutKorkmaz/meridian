using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Caching.Tests;

public sealed class C3BoundaryTests
{
    [Fact]
    public async Task Diagnostic_Reasons_Are_Finite_And_Contain_No_Key_Payload_Or_Remote_Id()
    {
        var labels = new List<KeyValuePair<string, object?>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Meridian.Mediator.Caching" && instrument.Name == "meridian.cache.entry_rejections") meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, state) =>
        {
            lock (labels) labels.AddRange(tags.ToArray());
        });
        listener.Start();
        var backend = new DistributedCacheTests.FakeBackend { Bytes = SerializationContractTests.Frame("private-remote-id", 1, "private-payload") };
        Assert.False((await DistributedCacheTests.Provider(backend).GetAsync("private-key", default)).Found);
        lock (labels)
        {
            Assert.Contains(labels, x => x.Key == "reason" && Equals(x.Value, "UnknownContract"));
            Assert.All(labels, x => { Assert.Equal("reason", x.Key); Assert.True(Enum.TryParse<CacheValueRejectionReason>((string)x.Value!, out _)); });
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Converter_Input_Rejection_And_Programming_Fault_Are_Distinct(bool dataFault)
    {
        var converter = new ThrowingConverter { DataFault = dataFault };
        var options = new JsonSerializerOptions { TypeInfoResolver = JsonTypeInfoResolver.Combine() }; options.Converters.Add(converter);
        var info = JsonMetadataServices.CreateValueInfo<string>(options, converter);
        var registry = new CacheContractRegistry().RegisterContract("converter", 1, info);
        var serializer = new JsonCacheValueSerializer(registry);
        if (dataFault) Assert.Equal(CacheValueRejectionReason.MalformedBody, Assert.Throws<CacheValueDataException>(() => serializer.Deserialize("\"v\""u8.ToArray(), "converter")).Reason);
        else Assert.Same(converter.ProgrammingFailure, Assert.Throws<InvalidOperationException>(() => serializer.Deserialize("\"v\""u8.ToArray(), "converter")));
    }

    [Fact]
    public async Task Read_Identity_Change_While_Backend_Is_Awaited_Does_Not_Return_Response()
    {
        var identity = new ScopeIsolationTests.Identity { Value = "a" };
        var completion = new TaskCompletionSource<(bool Found, object? Value)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new AwaitedProvider(completion);
        var provider = new ScopedCacheProvider(backend, scope: identity);
        var read = provider.GetAsync("key", default);
        identity.Value = "b"; completion.SetResult((true, "private-b"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => read);
        Assert.Equal(CacheKeyDecorator.Compose("key", null, "a"), backend.Key);
    }

    [Fact]
    public async Task Scoped_Wrapper_Preserves_Reentry_Guard_Cancellation_And_Null_Fallback()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var raw = new MemoryCacheProvider(memory);
        var provider = (IConcurrentCacheProvider)ScopedCacheProvider.Create(raw, new() { ApplicationKeyPrefix = "app" }, new ScopeIsolationTests.Identity());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetOrCreateAsync("nested", _ => provider.GetOrCreateAsync("nested", _ => Task.FromResult<object?>(99), null, default), null, default));
        Assert.Equal(1, await provider.GetOrCreateAsync("nested", _ => Task.FromResult<object?>(1), null, default));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = provider.GetOrCreateAsync("cancel", async _ => { await release.Task; return 2; }, null, default);
        using var cancel = new CancellationTokenSource();
        var waiter = provider.GetOrCreateAsync("cancel", _ => throw new InvalidOperationException("unused"), null, cancel.Token);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter); Assert.False(owner.IsCompleted);
        release.SetResult(); Assert.Equal(2, await owner);
        var nullRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerBehavior = new CachingBehavior<MemoryCacheTests.Query, string?>(provider, new() { UseRequestTypeNamespacedKeys = false, EnableRequestCoalescing = true });
        var waiterBehavior = new CachingBehavior<MemoryCacheTests.Query, int>(provider, new() { UseRequestTypeNamespacedKeys = false, EnableRequestCoalescing = true });
        var nullOwner = ownerBehavior.Handle(new("null"), async () => { await nullRelease.Task; return null; }, default);
        var ownCalls = 0;
        var typedWaiter = waiterBehavior.Handle(new("null"), () => { ownCalls++; return Task.FromResult(9); }, default);
        Assert.Equal(0, ownCalls); nullRelease.SetResult(); Assert.Null(await nullOwner); Assert.Equal(9, await typedWaiter); Assert.Equal(1, ownCalls);
    }

    [Fact]
    public async Task Two_Applications_Using_One_Raw_Backend_Do_Not_Share_Or_Invalidate()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var raw = new MemoryCacheProvider(cache);
        var a = ScopedCacheProvider.Create(raw, new() { ApplicationKeyPrefix = "app-a" });
        var b = ScopedCacheProvider.Create(raw, new() { ApplicationKeyPrefix = "app-b" });
        await a.SetAsync("same", 1, null, default); await b.SetAsync("same", 2, null, default);
        Assert.Equal(1, (await a.GetAsync("same", default)).Value); Assert.Equal(2, (await b.GetAsync("same", default)).Value);
        await a.RemoveAsync("same", default); Assert.False((await a.GetAsync("same", default)).Found); Assert.Equal(2, (await b.GetAsync("same", default)).Value);
    }

    [Fact]
    public void Distributed_Repeated_Registration_Is_Idempotent_And_Different_Registry_Rejects()
    {
        var registry = SerializationContractTests.Registry();
        var services = new ServiceCollection(); services.AddSingleton<IDistributedCache, ScopeIsolationTests.newBackend>();
        services.AddMeridianDistributedCaching(registry, o => o.DefaultDuration = TimeSpan.FromSeconds(2));
        services.AddMeridianDistributedCaching(registry, o => o.DefaultDuration = TimeSpan.FromHours(1));
        using var di = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = di.CreateScope(); Assert.Single(scope.ServiceProvider.GetServices<ICacheProvider>());
        Assert.Equal(TimeSpan.FromSeconds(2), scope.ServiceProvider.GetRequiredService<CacheProviderOptions>().DefaultDuration);
        Assert.Throws<InvalidOperationException>(() => services.AddMeridianDistributedCaching(SerializationContractTests.Registry()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceCollection().AddMeridianMemoryCaching(o => o.MaximumEntryBytes = 0));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddMeridianMemoryCaching(o => o.ApplicationKeyPrefix = ""));
    }

    [Fact]
    public async Task Wrapper_Decorates_Once_And_Borrows_Backend()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var raw = new MemoryCacheProvider(cache);
        var provider = (IConcurrentCacheProvider)ScopedCacheProvider.Create(raw, new() { ApplicationKeyPrefix = "app" }, new ScopeIsolationTests.Identity());
        var key = "MRCKey1:raw";
        Assert.Equal(7, await provider.GetOrCreateAsync(key, _ => Task.FromResult<object?>(7), null, default));
        var effective = CacheKeyDecorator.Compose(key, "app", "public");
        Assert.Equal(7, (await raw.GetAsync(effective, default)).Value);
        Assert.False((await raw.GetAsync(CacheKeyDecorator.Compose(effective, "app", "public"), default)).Found);
        await provider.RemoveAsync(key, default); Assert.False((await raw.GetAsync(effective, default)).Found);
        Assert.Throws<InvalidOperationException>(() => ScopedCacheProvider.Create((ICacheProvider)provider));
        var services = new ServiceCollection(); services.AddSingleton<IMemoryCache>(cache); services.AddMeridianMemoryCaching(); services.AddMeridianCacheScope<ScopeIsolationTests.Identity>();
        using (var di = services.BuildServiceProvider()) { using var scope = di.CreateScope(); _ = scope.ServiceProvider.GetRequiredService<ICacheProvider>(); }
        cache.Set("still-owned", 11); Assert.Equal(11, cache.Get<int>("still-owned"));
    }

    [Fact]
    public void Assembly_Qualified_Remote_Contract_Is_Rejected_Before_Any_Serializer_Activation()
    {
        var serializer = new SerializationContractTests.OpaqueSerializer { Failure = new InvalidOperationException("body must never be called") };
        var codec = new CacheValueCodec(SerializationContractTests.Registry(), serializer);
        var frame = SerializationContractTests.Frame("System.String, System.Private.CoreLib", 1, "private-payload");
        Assert.Equal(CacheValueRejectionReason.MalformedEnvelope, Assert.Throws<CacheValueDataException>(() => codec.Decode(frame)).Reason);
    }

    [Fact]
    public void Registered_Metadata_Is_ReadOnly_And_Returned_Type_Is_Not_Assignable_Fallback()
    {
        var registry = SerializationContractTests.Registry();
        var serializer = new JsonCacheValueSerializer(registry);
        Assert.True(TestJson.Default.Reply.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => serializer.GetContractId(typeof(object)));
        Assert.Throws<InvalidOperationException>(() => serializer.Serialize(42, "reply.v1"));
    }

    private sealed class ThrowingConverter : JsonConverter<string>
    {
        public bool DataFault { get; init; }
        public InvalidOperationException ProgrammingFailure { get; } = new("programming failure");
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw (DataFault ? new JsonException("private input") : ProgrammingFailure);
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }
    private sealed class AwaitedProvider(TaskCompletionSource<(bool Found, object? Value)> completion) : ICacheProvider
    {
        public string? Key { get; private set; }
        public Task<(bool Found, object? Value)> GetAsync(string key, CancellationToken cancellationToken) { Key = key; return completion.Task; }
        public Task SetAsync(string key, object value, TimeSpan? duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RemoveAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
