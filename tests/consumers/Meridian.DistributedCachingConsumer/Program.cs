using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Caching;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

Console.WriteLine($"HOST runtime={Environment.Version} framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
Require(!JsonSerializer.IsReflectionEnabledByDefault,"Reflection is disabled");
var redisEndpoint=Environment.GetEnvironmentVariable("MERIDIAN_TEST_REDIS");
if(Environment.GetEnvironmentVariable("MERIDIAN_TEST_REDIS_REQUIRED")=="1" && string.IsNullOrWhiteSpace(redisEndpoint)) throw new InvalidOperationException("Selected Redis package consumer requires its provisioned endpoint.");
IDistributedCache backend=string.IsNullOrWhiteSpace(redisEndpoint)
    ? new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()))
    : new RedisCache(Options.Create(new RedisCacheOptions { Configuration=redisEndpoint }));
IDistributedCache backendB=string.IsNullOrWhiteSpace(redisEndpoint) ? backend : new RedisCache(Options.Create(new RedisCacheOptions { Configuration=redisEndpoint }));
using var backendOwner=backend as IDisposable;
using var backendOwnerB=ReferenceEquals(backend,backendB) ? null : backendB as IDisposable;
var applicationPrefix="consumer-app-"+Guid.NewGuid().ToString("N");
var registryA=new CacheContractRegistry().RegisterContract("consumer.reply.v1",1,ConsumerJson.Default.Reply);
var registryB=new CacheContractRegistry().RegisterContract("consumer.reply.v1",1,ConsumerJson.Default.Reply);
var a=Services(registryA,backend);var b=Services(registryB,backendB);
using var da=a.BuildServiceProvider(new ServiceProviderOptions{ValidateScopes=true,ValidateOnBuild=true});using var db=b.BuildServiceProvider(new ServiceProviderOptions{ValidateScopes=true,ValidateOnBuild=true});
using var scopeA=da.CreateScope();using var scopeB=db.CreateScope();
var pa=scopeA.ServiceProvider.GetRequiredService<ICacheProvider>();var pb=scopeB.ServiceProvider.GetRequiredService<ICacheProvider>();
Require(pa is not IConcurrentCacheProvider,"No invented distributed coalescing");
await pa.SetAsync("same",new Reply("generated-a",11),null,default);Require((await pb.GetAsync("same",default)).Value is Reply {Value:"generated-a",Number:11},"Independent generated DTO reconstruction");
await pb.SetAsync("same",new Reply("generated-b",22),null,default);Require((await pa.GetAsync("same",default)).Value is Reply {Value:"generated-b",Number:22},"Reverse crossread");
using var tenant=db.CreateScope();((Identity)tenant.ServiceProvider.GetRequiredService<ICacheKeyScope>()).Partition="tenant-b";var other=tenant.ServiceProvider.GetRequiredService<ICacheProvider>();Require(!(await other.GetAsync("same",default)).Found,"Tenant isolation");await other.SetAsync("same",new Reply("private",33),null,default);
await pa.RemoveAsync("same",default);Require(!(await pb.GetAsync("same",default)).Found,"Cross-client invalidation");Require((await other.GetAsync("same",default)).Value is Reply {Number:33},"Scoped invalidation");
var codec=scopeA.ServiceProvider.GetRequiredService<CacheValueCodec>();Require(codec.Decode(codec.Encode(null,"consumer.reply.v1"),"consumer.reply.v1") is null,"Known null codec replay");
var effective=CacheKeyDecorator.Compose("null",applicationPrefix,"tenant-a");await backend.SetAsync(effective,codec.Encode(null,"consumer.reply.v1"));Require(!(await pa.GetAsync("null",default)).Found && await backend.GetAsync(effective) is null,"Ordinary null removed");
Require(CacheKeyDecorator.Compose("raw",null,null)=="raw","Raw preserved");Require(CacheKeyDecorator.Compose("a:b","c","d")!=CacheKeyDecorator.Compose("a","b:c","d"),"Injective dimensions");
foreach(var assembly in new[]{typeof(DistributedCacheProvider).Assembly,typeof(ICacheProvider).Assembly,backend.GetType().Assembly,typeof(ConsumerJson).Assembly})Console.WriteLine($"LOADED assembly={assembly.GetName().Name} path={assembly.Location} sha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant()}");
await other.RemoveAsync("same",default);
Console.WriteLine($"PROVIDER transport={(backend is RedisCache ? "Redis" : "MemoryDistributedCache")} reflectionDisabled=true independentRegistries=2 clients=2 actualDeserialize=true scopeInvalidation=true");
Console.WriteLine("COMMON distributed sourceGenerated=true reflectionDisabled=true independentRegistries=2 clients=2 roundtrip=both-directions tenant=isolated invalidation=scoped ordinaryNull=removed codecNull=replayed serializer=allowlisted decorated=once isolatedNuGet=true");
ServiceCollection Services(CacheContractRegistry r,IDistributedCache cache){var s=new ServiceCollection();s.AddSingleton<IDistributedCache>(cache);s.AddMeridianCacheScope<Identity>();s.AddMeridianDistributedCaching(r,o=>o.ApplicationKeyPrefix=applicationPrefix);return s;}
static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
public sealed record Reply(string Value,int Number);
public sealed class Identity : ICacheKeyScope { public string Partition{get;set;}="tenant-a";public string GetScope()=>Partition; }
[JsonSerializable(typeof(Reply))] internal sealed partial class ConsumerJson : JsonSerializerContext { }
