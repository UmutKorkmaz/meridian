using System.Collections.Concurrent;
using System.Security.Cryptography;
using Meridian.AspNetCore.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit.Abstractions;

namespace Meridian.AspNetCore.Tests;

public sealed class StartupOwnershipTests(ITestOutputHelper output)
{
    private static readonly string[] CleanupOrder = ["startup", "scope", "root"];
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Startup_Failure_Disposes_Materialized_Root_And_Preserves_Ordered_Objects(bool scopeFails, bool rootFails)
    {
        var primary = new IOException("startup-marker"); var scopeError = new IOException("scope-marker"); var rootError = new IOException("root-marker");
        var occurrences = new ConcurrentQueue<string>();
        var root = new TrackedResource("root", occurrences, rootFails ? rootError : null);
        var scope = new TrackedResource("scope", occurrences, scopeFails ? scopeError : null);
        IServiceProvider? capturedRoot = null;
        try
        {
            var error = await Record.ExceptionAsync(() => OrderApplication.BuildAsync([], builder =>
            {
                builder.Services.AddSingleton(_ => root);
                builder.Services.Replace(ServiceDescriptor.Singleton<LocalAuthSettings>(sp =>
                {
                    capturedRoot = sp; _ = sp.GetRequiredService<TrackedResource>();
                    return Settings();
                }));
                // A separate type materializes the startup-scope disposable before throwing.
                builder.Services.AddScoped(_ => new ScopedResource(scope));
                builder.Services.Replace(ServiceDescriptor.Scoped<OrdersDbContext>(sp =>
                {
                    _ = sp.GetRequiredService<ScopedResource>(); occurrences.Enqueue("startup"); throw primary;
                }));
            }));
            output.WriteLine($"rootMaterialized={capturedRoot is not null} disposedBeforeCallerCleanup={root.Disposals} scopeDisposed={scope.Disposals}");
            Assert.NotNull(capturedRoot); Assert.Equal(1, root.Disposals); Assert.Equal(1, scope.Disposals);
            var expected = new List<Exception> { primary };
            if (scopeFails) expected.Add(scopeError); if (rootFails) expected.Add(rootError);
            if (expected.Count == 1) Assert.Same(primary, error);
            else
            {
                var aggregate = Assert.IsType<AggregateException>(error); Assert.Equal(expected.Count, aggregate.InnerExceptions.Count);
                for (var i = 0; i < expected.Count; i++) Assert.Same(expected[i], aggregate.InnerExceptions[i]);
            }
            Assert.Equal(CleanupOrder, occurrences.ToArray());
        }
        finally
        {
            // RED must clean the materialized root even when the ownership assertion fails.
            if (capturedRoot is IAsyncDisposable disposable) { try { await disposable.DisposeAsync(); } catch (IOException) { } }
        }
    }

    [Fact]
    [Trait("Provider", "Redis")]
    public async Task Successful_Build_Transfers_Root_Ownership_To_Caller()
    {
        var directory = Path.Combine(Path.GetTempPath(), "meridian-startup-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var root = new TrackedResource("root", new()); WebApplication? app = null;
        try
        {
            app = await OrderApplication.BuildAsync([], builder =>
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LocalAuth:Key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                    ["Orders:DatabasePath"] = Path.Combine(directory, "orders.db"), ["Orders:Namespace"] = Path.GetFileName(directory),
                    ["Orders:Redis"] = Environment.GetEnvironmentVariable("MERIDIAN_TEST_REDIS") ?? throw new InvalidOperationException("Dedicated Redis required.")
                });
                builder.Services.AddSingleton(_ => root);
                builder.Services.Replace(ServiceDescriptor.Singleton<LocalAuthSettings>(sp => { _ = sp.GetRequiredService<TrackedResource>(); return new(sp.GetRequiredService<IConfiguration>()); }));
            });
            Assert.Equal(0, root.Disposals); Assert.True(File.Exists(Path.Combine(directory, "orders.db")));
            await app.DisposeAsync(); app = null; Assert.Equal(1, root.Disposals);
        }
        finally { if (app is not null) await app.DisposeAsync(); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Independent_App_Disposal_Control_Materializes_And_Disposes_Singleton()
    {
        var root = new TrackedResource("root", new()); var builder = WebApplication.CreateBuilder(); builder.Services.AddSingleton(_ => root);
        var app = builder.Build(); Assert.Same(root, app.Services.GetRequiredService<TrackedResource>()); Assert.Equal(0, root.Disposals);
        await app.DisposeAsync(); Assert.Equal(1, root.Disposals);
    }
    private static LocalAuthSettings Settings() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["LocalAuth:Key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }).Build());
    private sealed class ScopedResource(TrackedResource resource) : IAsyncDisposable { public ValueTask DisposeAsync() => resource.DisposeAsync(); }
    private sealed class TrackedResource(string name, ConcurrentQueue<string> occurrences, Exception? error = null) : IAsyncDisposable
    {
        public int Disposals { get; private set; }
        public async ValueTask DisposeAsync() { await Task.Yield(); Disposals++; occurrences.Enqueue(name); if (error is not null) throw error; }
    }
}
