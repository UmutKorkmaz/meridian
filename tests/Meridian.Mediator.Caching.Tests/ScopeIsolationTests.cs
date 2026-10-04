using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Mediator.Caching.Tests;
public sealed class ScopeIsolationTests
{
    [Fact] public async Task Two_Tenants_Same_Query_Key_Do_Not_Share_Value() => await Isolation("tenant-a", "tenant-b");
    [Fact] public async Task Personalized_User_Keys_Do_Not_Share_Value() => await Isolation(CacheKeyDecorator.Compose("user-a","tenant",null), CacheKeyDecorator.Compose("user-b","tenant",null));
    private static async Task Isolation(string a,string b) { using var di=Services().BuildServiceProvider(new ServiceProviderOptions{ValidateScopes=true,ValidateOnBuild=true}); using var one=di.CreateScope(); using var two=di.CreateScope(); ((Identity)one.ServiceProvider.GetRequiredService<ICacheKeyScope>()).Value=a; ((Identity)two.ServiceProvider.GetRequiredService<ICacheKeyScope>()).Value=b; var p=one.ServiceProvider.GetRequiredService<ICacheProvider>(); var q=two.ServiceProvider.GetRequiredService<ICacheProvider>(); await p.SetAsync("same",1,null,default); await q.SetAsync("same",2,null,default); Assert.Equal(1,(await p.GetAsync("same",default)).Value); Assert.Equal(2,(await q.GetAsync("same",default)).Value); await p.RemoveAsync("same",default); Assert.False((await p.GetAsync("same",default)).Found); Assert.Equal(2,(await q.GetAsync("same",default)).Value); }
    [Theory] [InlineData(false)] [InlineData(true)] public void Reordered_Repeated_Registration_Has_One_Scoped_Concurrent_Provider(bool scopeFirst) { var s=new ServiceCollection(); if(scopeFirst)s.AddMeridianCacheScope<Identity>(); s.AddMeridianMemoryCaching(o=>o.ApplicationKeyPrefix="app"); s.AddMeridianCacheScope<Identity>(); s.AddMeridianMemoryCaching(); using var di=s.BuildServiceProvider(new ServiceProviderOptions{ValidateScopes=true,ValidateOnBuild=true}); using var a=di.CreateScope(); using var b=di.CreateScope(); var p=a.ServiceProvider.GetRequiredService<ICacheProvider>(); Assert.Single(a.ServiceProvider.GetServices<ICacheProvider>()); Assert.IsAssignableFrom<IConcurrentCacheProvider>(p); Assert.Same(p,a.ServiceProvider.GetRequiredService<ICacheProvider>()); Assert.NotSame(p,b.ServiceProvider.GetRequiredService<ICacheProvider>()); Assert.NotSame(a.ServiceProvider.GetRequiredService<MemoryCacheProvider>(),b.ServiceProvider.GetRequiredService<MemoryCacheProvider>()); }
    [Fact] public async Task Malformed_Unicode_Is_Rejected_Before_Decorated_IO_And_Raw_Is_Verbatim() { var bad = new string((char)0xd800, 1); Assert.Equal(bad, CacheKeyDecorator.Compose(bad,null,null)); Assert.Throws<System.Text.EncoderFallbackException>(()=>CacheKeyDecorator.Compose(bad,"app",null)); Assert.Throws<System.Text.EncoderFallbackException>(()=>CacheKeyDecorator.Compose("key",bad,null)); var b=new CountingProvider();var p=new ScopedCacheProvider(b,new(){ApplicationKeyPrefix="app"});await Assert.ThrowsAsync<System.Text.EncoderFallbackException>(()=>p.GetAsync(bad,default));Assert.Equal(0,b.Calls); }
    [Fact] public void Prefix_Only_Is_Singleton_And_Absent_Dimensions_Preserve_Raw_Key() { using var di=new ServiceCollection().AddMeridianMemoryCaching(o=>o.ApplicationKeyPrefix="app").BuildServiceProvider(new ServiceProviderOptions{ValidateScopes=true,ValidateOnBuild=true}); Assert.Same(di.GetRequiredService<ICacheProvider>(),di.GetRequiredService<ICacheProvider>()); Assert.IsAssignableFrom<IConcurrentCacheProvider>(di.GetRequiredService<ICacheProvider>()); Assert.Equal("raw",CacheKeyDecorator.Compose("raw",null,null)); }
    [Fact] public void Injective_Key_Tuple_Preserves_Unicode_Delimiters_And_Marker_Inputs() { var keys=new[]{("a:b","c","d"),("a","b:c","d"),("a","b","c:d"),("ü","租户","x"),("MRCKey1:","a",(string?)null),("MRCKey1:",(string?)null,"a")}; Assert.Equal(keys.Length,keys.Select(x=>CacheKeyDecorator.Compose(x.Item1,x.Item2,x.Item3)).Distinct(StringComparer.Ordinal).Count()); Assert.NotEqual(CacheKeyDecorator.Compose("k","a",null),CacheKeyDecorator.Compose("k",null,"a")); Assert.Throws<ArgumentException>(()=>CacheKeyDecorator.Compose("k","",null)); }
    [Fact] public async Task Missing_And_Changing_Identity_Fail_Before_Backend_IO() { var backend=new CountingProvider(); var identity=new Identity{Value=""}; var p=new ScopedCacheProvider(backend,new(),identity); await Assert.ThrowsAsync<InvalidOperationException>(()=>p.GetAsync("k",default)); Assert.Equal(0,backend.Calls); identity.Value="a"; await p.SetAsync("k",1,null,default); identity.Value="b"; await Assert.ThrowsAsync<InvalidOperationException>(()=>p.RemoveAsync("k",default)); Assert.Equal(1,backend.Calls); }
    [Fact] public async Task Scoped_Memory_Does_Not_Join_Operations_Across_Scopes_But_Joins_Within_Scope() { using var di=Services().BuildServiceProvider(); using var a=di.CreateScope();using var b=di.CreateScope(); var p=(IConcurrentCacheProvider)a.ServiceProvider.GetRequiredService<ICacheProvider>();var q=(IConcurrentCacheProvider)b.ServiceProvider.GetRequiredService<ICacheProvider>(); var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var calls=0;async Task<object?> Factory(CancellationToken cancellationToken){Interlocked.Increment(ref calls);await release.Task;return 42;} var x=p.GetOrCreateAsync("key",Factory,null,default);var waiter=p.GetOrCreateAsync("key",_=>throw new InvalidOperationException("loser"),null,default);var y=q.GetOrCreateAsync("key",Factory,null,default);Assert.Equal(2,calls);Assert.False(x.IsCompleted);Assert.False(y.IsCompleted);release.SetResult();Assert.All(await Task.WhenAll(x,waiter,y),v=>Assert.Equal(42,v)); }
    [Fact] public async Task Identity_Changes_Inside_Forwarded_Factory_Fail_Without_Storing() { using var di=Services().BuildServiceProvider();using var a=di.CreateScope();var identity=(Identity)a.ServiceProvider.GetRequiredService<ICacheKeyScope>();var p=(IConcurrentCacheProvider)a.ServiceProvider.GetRequiredService<ICacheProvider>();await Assert.ThrowsAsync<InvalidOperationException>(()=>p.GetOrCreateAsync("key",_=>{identity.Value="changed";return Task.FromResult<object?>(42);},null,default));Assert.False((await a.ServiceProvider.GetRequiredService<MemoryCacheProvider>().GetAsync(CacheKeyDecorator.Compose("key",null,"public"),default)).Found); }
    [Fact] public void Custom_Providers_Preserved_And_Conflicting_Backend_Or_Scope_Reject() { var s=new ServiceCollection();s.AddSingleton<ICacheProvider,CountingProvider>();s.AddMeridianMemoryCaching();Assert.Single(s,x=>x.ServiceType==typeof(ICacheProvider));Assert.Throws<InvalidOperationException>(()=>s.AddMeridianCacheScope<Identity>());Assert.Throws<InvalidOperationException>(()=>new ServiceCollection().AddMeridianMemoryCaching().AddMeridianDistributedCaching(SerializationContractTests.Registry()));Assert.Throws<InvalidOperationException>(()=>new ServiceCollection().AddMeridianCacheScope<Identity>().AddMeridianCacheScope<OtherIdentity>()); }
    [Fact] public void Distributed_Supports_Scoped_Serializer_And_Does_Not_Advertise_Coalescing() { var s=new ServiceCollection();s.AddSingleton<IDistributedCache,newBackend>();s.AddScoped<ICacheValueSerializer,ScopedSerializer>();s.AddMeridianDistributedCaching(SerializationContractTests.Registry(),o=>o.ApplicationKeyPrefix="a");using var di=s.BuildServiceProvider(new ServiceProviderOptions{ValidateScopes=true,ValidateOnBuild=true});using var scope=di.CreateScope();Assert.False(scope.ServiceProvider.GetRequiredService<ICacheProvider>() is IConcurrentCacheProvider); }
    [Fact]
    public void Raw_Passthrough_Can_Equal_A_Decorated_Output_While_Decorated_Marker_Keys_Remain_Separate()
    {
        var decorated = CacheKeyDecorator.Compose("key", "app", "tenant");
        Assert.Equal(decorated, CacheKeyDecorator.Compose(decorated, null, null));
        Assert.NotEqual(decorated, CacheKeyDecorator.Compose(decorated, "app", "tenant"));
        Assert.NotEqual(CacheKeyDecorator.Compose("key", "app", "tenant"), CacheKeyDecorator.Compose("key", "app", "other"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_Factories_Snapshot_Scope_Selection_When_Collection_Is_Reused(bool distributed)
    {
        using var memory = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var backend = new Microsoft.Extensions.Caching.Distributed.MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new Microsoft.Extensions.Caching.Memory.MemoryDistributedCacheOptions()));
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Caching.Memory.IMemoryCache>(memory);
        services.AddSingleton<IDistributedCache>(backend);
        if (distributed) services.AddMeridianDistributedCaching(SerializationContractTests.Registry(), o => o.ApplicationKeyPrefix = "app");
        else services.AddMeridianMemoryCaching(o => o.ApplicationKeyPrefix = "app");
        var validation = new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true };
        using var a = services.BuildServiceProvider(validation);
        // Intentionally leave A's cache provider unresolved until B's scope registration mutates the collection.
        services.AddMeridianCacheScope<Identity>();
        using var b = services.BuildServiceProvider(validation);
        using var scopeA = a.CreateScope();
        using var scopeB = b.CreateScope();
        var providerA = scopeA.ServiceProvider.GetRequiredService<ICacheProvider>();
        var providerB = scopeB.ServiceProvider.GetRequiredService<ICacheProvider>();
        Assert.Null(scopeA.ServiceProvider.GetService<ICacheKeyScope>());
        Assert.IsType<Identity>(scopeB.ServiceProvider.GetRequiredService<ICacheKeyScope>());
        Assert.Same(providerA, scopeA.ServiceProvider.GetRequiredService<ICacheProvider>());
        if (!distributed) Assert.Same(providerA, a.GetRequiredService<ICacheProvider>());
        await providerA.SetAsync("key", new Reply("prefix-only", 1), null, default);
        await providerB.SetAsync("key", new Reply("scoped", 2), null, default);
        Assert.Equal(new Reply("prefix-only", 1), (await providerA.GetAsync("key", default)).Value);
        Assert.Equal(new Reply("scoped", 2), (await providerB.GetAsync("key", default)).Value);
        var rawA = distributed ? (ICacheProvider)scopeA.ServiceProvider.GetRequiredService<DistributedCacheProvider>()
            : scopeA.ServiceProvider.GetRequiredService<MemoryCacheProvider>();
        Assert.Equal(new Reply("prefix-only", 1), (await rawA.GetAsync(CacheKeyDecorator.Compose("key", "app", null), default)).Value);
        Assert.Equal(new Reply("scoped", 2), (await rawA.GetAsync(CacheKeyDecorator.Compose("key", "app", "public"), default)).Value);
        await providerA.RemoveAsync("key", default);
        Assert.False((await providerA.GetAsync("key", default)).Found);
        Assert.Equal(new Reply("scoped", 2), (await providerB.GetAsync("key", default)).Value);
        await providerB.RemoveAsync("key", default);
    }

    private static ServiceCollection Services() {var s=new ServiceCollection();s.AddMeridianMemoryCaching();s.AddMeridianCacheScope<Identity>();return s;}
    public sealed class Identity : ICacheKeyScope { public string Value{get;set;}="public";public string GetScope()=>Value; }
    public sealed class OtherIdentity : ICacheKeyScope { public string GetScope()=>"other"; }
    public sealed class ScopedSerializer : ICacheValueSerializer { public string GetContractId(Type type)=>"reply.v1";public byte[] Serialize(object? value,string contractId)=>[];public object? Deserialize(ReadOnlyMemory<byte> payload,string contractId)=>null; }
    public sealed class CountingProvider : ICacheProvider { public int Calls{get;private set;}public Task<(bool Found,object? Value)> GetAsync(string key,CancellationToken cancellationToken){Calls++;return Task.FromResult<(bool,object?)>((false,null));}public Task SetAsync(string key,object value,TimeSpan? duration,CancellationToken cancellationToken){Calls++;return Task.CompletedTask;}public Task RemoveAsync(string key,CancellationToken cancellationToken){Calls++;return Task.CompletedTask;} }
    public sealed class newBackend : IDistributedCache { public byte[]? Get(string key)=>null;public Task<byte[]?> GetAsync(string key,CancellationToken token=default)=>Task.FromResult<byte[]?>(null);public void Set(string key,byte[] value,DistributedCacheEntryOptions options){}public Task SetAsync(string key,byte[] value,DistributedCacheEntryOptions options,CancellationToken token=default)=>Task.CompletedTask;public void Remove(string key){}public Task RemoveAsync(string key,CancellationToken token=default)=>Task.CompletedTask;public void Refresh(string key){}public Task RefreshAsync(string key,CancellationToken token=default)=>Task.CompletedTask; }
}
