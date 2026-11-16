using System.Security.Claims;
using Meridian.AspNetCore.Infrastructure;
using Meridian.AspNetCore.Orders;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Caching;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
namespace Meridian.AspNetCore.Tests;

public sealed class CorrelationScopeTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task Restores_Preceding_Ambient_And_Ignores_Untrusted_Header(string shape)
    {
        CorrelationContext.CorrelationId = "preceding"; var context = new DefaultHttpContext(); context.Request.Headers["X-Correlation-ID"] = new string('x', 200);
        string? observed = null;
        var middleware = new OrderCorrelationMiddleware(async http =>
        {
            observed = CorrelationContext.CorrelationId; await Task.Yield(); Assert.Equal(observed, CorrelationContext.CorrelationId);
            if (shape == "failure") throw new IOException("secret"); if (shape == "cancel") throw new OperationCanceledException();
        });
        if (shape == "success") await middleware.InvokeAsync(context); else await Assert.ThrowsAnyAsync<Exception>(() => middleware.InvokeAsync(context));
        Assert.Equal("preceding", CorrelationContext.CorrelationId); Assert.Equal(context.TraceIdentifier, observed); Assert.Equal(32, observed!.Length);
        CorrelationContext.CorrelationId = null;
    }
    [Fact]
    public async Task Concurrent_Requests_Isolate_Ambient_Ids()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
        async Task<string> Request()
        {
            var context = new DefaultHttpContext();
            await new OrderCorrelationMiddleware(async _ =>
            {
                var id = CorrelationContext.CorrelationId; if (Interlocked.Increment(ref count) == 2) entered.SetResult(); await entered.Task; Assert.Equal(id, CorrelationContext.CorrelationId);
            }).InvokeAsync(context); return context.TraceIdentifier;
        }
        var rows = await Task.WhenAll(Request(), Request()); Assert.NotEqual(rows[0], rows[1]); Assert.Null(CorrelationContext.CorrelationId);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Memory_And_Distributed_Providers_Isolate_Complete_Scopes_And_Removal(bool distributed, bool scopeFirst)
    {
        using var memory = new MemoryCache(new MemoryCacheOptions()); var backend = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        ServiceProvider Provider(string tenant, string user, string space = "application")
        {
            var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton<IMemoryCache>(memory); services.AddSingleton<IDistributedCache>(backend);
            var accessor = new Accessor { HttpContext = new DefaultHttpContext { User = Principal(tenant, user) } };
            services.AddSingleton<IHttpContextAccessor>(accessor); services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Orders:Namespace"] = space }).Build());
            void Scope() => services.AddMeridianCacheScope<AuthenticatedOrderScope>();
            void Cache()
            {
                if (distributed) services.AddMeridianDistributedCaching(new CacheContractRegistry().RegisterContract("order", 1, OrdersJsonContext.Default.OrderDto), o => o.ApplicationKeyPrefix = "app");
                else services.AddMeridianMemoryCaching(o => o.ApplicationKeyPrefix = "app");
            }
            if (scopeFirst) { Scope(); Cache(); } else { Cache(); Scope(); }
            Scope(); // Exact repeated registration does not replace a scope/provider.
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }
        await using var a = Provider("A", "1"); await using var same = Provider("A", "1"); await using var user = Provider("A", "2"); await using var tenant = Provider("B", "1"); await using var app = Provider("A", "1", "another");
        await using var sa = a.CreateAsyncScope(); await using var ss = same.CreateAsyncScope(); await using var su = user.CreateAsyncScope(); await using var st = tenant.CreateAsyncScope(); await using var sx = app.CreateAsyncScope();
        var key = CacheKeyComposer.Compose<GetOrder>("key"); var dto = new OrderDto(Guid.NewGuid(), "book", 2, DateTimeOffset.UtcNow);
        var ca = sa.ServiceProvider.GetRequiredService<ICacheProvider>(); var cs = ss.ServiceProvider.GetRequiredService<ICacheProvider>();
        await ca.SetAsync(key, dto, null, default); Assert.True((await cs.GetAsync(key, default)).Found);
        foreach (var scope in new[] { su, st, sx }) Assert.False((await scope.ServiceProvider.GetRequiredService<ICacheProvider>().GetAsync(key, default)).Found);
        await su.ServiceProvider.GetRequiredService<ICacheProvider>().RemoveAsync(key, default); Assert.True((await ca.GetAsync(key, default)).Found);
        await cs.RemoveAsync(key, default); Assert.False((await ca.GetAsync(key, default)).Found);
        var identity = sa.ServiceProvider.GetRequiredService<IHttpContextAccessor>(); identity.HttpContext!.User = Principal("A", "2");
        Assert.Throws<InvalidOperationException>(() => sa.ServiceProvider.GetRequiredService<AuthenticatedOrderScope>().GetScope());
    }
    [Fact]
    public void Conflicting_Backend_Scope_And_Registry_Fail_Explicitly()
    {
        var services = new ServiceCollection(); services.AddMeridianCacheScope<AuthenticatedOrderScope>(); services.AddMeridianMemoryCaching();
        Assert.Throws<InvalidOperationException>(() => services.AddMeridianDistributedCaching(new CacheContractRegistry()));
        Assert.Throws<InvalidOperationException>(() => services.AddMeridianCacheScope<OtherScope>());
        var separate = new ServiceCollection(); separate.AddMeridianDistributedCaching(new CacheContractRegistry());
        Assert.Throws<InvalidOperationException>(() => separate.AddMeridianDistributedCaching(new CacheContractRegistry()));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_And_Contradictory_Identity_Fail_Before_Scope_Is_Returned(bool contradictory)
    {
        var principal = Principal("A", "1"); var identity = (ClaimsIdentity)principal.Identity!;
        if (contradictory) identity.AddClaim(new("tenant", "B"));
        else identity.RemoveClaim(identity.FindFirst(ClaimTypes.NameIdentifier)!);
        var accessor = new Accessor { HttpContext = new DefaultHttpContext { User = principal } };
        var scope = new AuthenticatedOrderScope(accessor, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Orders:Namespace"] = "isolated" }).Build());
        if (contradictory) Assert.Throws<InvalidOperationException>(() => scope.GetScope());
        else Assert.Throws<Meridian.Mediator.Behaviors.UnauthorizedException>(() => scope.GetScope());
    }
    private static ClaimsPrincipal Principal(string tenant, string user) => new(new ClaimsIdentity([new("iss", LocalAuthSettings.Issuer), new("tenant", tenant), new(ClaimTypes.NameIdentifier, user), new("permission", "orders")], "local"));
    private sealed class Accessor : IHttpContextAccessor { public HttpContext? HttpContext { get; set; } }
    private sealed class OtherScope : ICacheKeyScope { public string GetScope() => "other"; }
}
