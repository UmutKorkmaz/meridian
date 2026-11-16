using System.Security.Cryptography;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace Meridian.Mediator.Caching.Tests;
public sealed class RedisProviderTests(ITestOutputHelper output)
{
    [Fact] [Trait("Provider","Redis")]
    public async Task Independent_Registries_Clients_Cross_Read_Invalidate_And_Isolate()
    {
        var endpoint=Environment.GetEnvironmentVariable("MERIDIAN_TEST_REDIS");
        Assert.False(string.IsNullOrWhiteSpace(endpoint),"Required Redis provider lane is selected: MERIDIAN_TEST_REDIS must point to its provisioned isolated backend.");
        using var a=new RedisCache(Options.Create(new RedisCacheOptions{Configuration=endpoint}));
        using var b=new RedisCache(Options.Create(new RedisCacheOptions{Configuration=endpoint}));
        var one=DistributedCacheTests.ProviderForBackend(a);var two=DistributedCacheTests.ProviderForBackend(b);
        var prefix="C3-"+Guid.NewGuid().ToString("N");
        var pa=new ScopedCacheProvider(one,new(){ApplicationKeyPrefix=prefix},new ScopeIsolationTests.Identity{Value="tenant-a"});
        var pb=new ScopedCacheProvider(two,new(){ApplicationKeyPrefix=prefix},new ScopeIsolationTests.Identity{Value="tenant-a"});
        var other=new ScopedCacheProvider(two,new(){ApplicationKeyPrefix=prefix},new ScopeIsolationTests.Identity{Value="tenant-b"});
        var userA=new ScopedCacheProvider(one,new(){ApplicationKeyPrefix=prefix},new ScopeIsolationTests.Identity{Value=CacheKeyDecorator.Compose("user-a","tenant-a",null)});
        var userB=new ScopedCacheProvider(two,new(){ApplicationKeyPrefix=prefix},new ScopeIsolationTests.Identity{Value=CacheKeyDecorator.Compose("user-b","tenant-a",null)});
        try
        {
            await pa.SetAsync("same",new Reply("a",11),null,default);
            var cross=await pb.GetAsync("same",default);Assert.True(cross.Found);Assert.Equal(new Reply("a",11),cross.Value);Assert.False((await other.GetAsync("same",default)).Found);
            await pb.SetAsync("same",new Reply("b",22),null,default);Assert.Equal(new Reply("b",22),(await pa.GetAsync("same",default)).Value);
            await other.SetAsync("same",new Reply("private-tenant",33),null,default);await pa.RemoveAsync("same",default);Assert.False((await pb.GetAsync("same",default)).Found);Assert.Equal(new Reply("private-tenant",33),(await other.GetAsync("same",default)).Value);
            await userA.SetAsync("same",new Reply("private-user-a",44),null,default);await userB.SetAsync("same",new Reply("private-user-b",55),null,default);Assert.Equal(new Reply("private-user-a",44),(await userA.GetAsync("same",default)).Value);await userA.RemoveAsync("same",default);Assert.False((await userA.GetAsync("same",default)).Found);Assert.Equal(new Reply("private-user-b",55),(await userB.GetAsync("same",default)).Value);
            var corrupt=CacheKeyDecorator.Compose("corrupt",prefix,"tenant-a");await a.SetAsync(corrupt,SerializationContractTests.Frame("unknown",1,"secret"));Assert.False((await pb.GetAsync("corrupt",default)).Found);Assert.Null(await a.GetAsync(corrupt));
            var schema=CacheKeyDecorator.Compose("schema",prefix,"tenant-a");await a.SetAsync(schema,SerializationContractTests.Codec(2).Encode(new Reply("future",2),"reply.v1"));Assert.False((await pb.GetAsync("schema",default)).Found);Assert.Null(await a.GetAsync(schema));
            foreach(var assembly in new[]{typeof(RedisCache).Assembly,typeof(StackExchange.Redis.ConnectionMultiplexer).Assembly,typeof(DistributedCacheProvider).Assembly,typeof(RedisProviderTests).Assembly}) output.WriteLine($"LOADED {assembly.GetName().Name} version={assembly.GetName().Version} sha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant()} path={assembly.Location}");
            output.WriteLine($"HOST runtime={Environment.Version} framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
            output.WriteLine("PROVIDER Redis independentRegistries=2 independentClients=2 crossRead=both-directions deserialize=exact-DTO invalidation=cross-client tenant=isolated user=isolated corrupted=removed schema=removed skipped=0");
        }
        finally {foreach(var p in new[]{pa,pb,other,userA,userB})foreach(var key in new[]{"same","corrupt","schema"})await p.RemoveAsync(key,default);}
    }
}
