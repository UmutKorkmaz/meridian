using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.Caching;
using Meridian.Mediator.EntityFrameworkCore;
using Meridian.Mediator.Redis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace Meridian.Mediator.EntityFrameworkCore.Tests;

public sealed class RegistrationAndCompositionTests(ITestOutputHelper output)
{
    [Fact]
    public void Repeated_Registration_Preserves_Scoped_Aliases_And_Option_Snapshot()
    {
        var services = new ServiceCollection();
        services.AddDbContext<TestContext>(o => o.UseSqlite("Data Source=:memory:"));
        EfTransactionOptions? captured = null;
        services.AddMeridianEfCoreTransactions<TestContext>(o => captured = o);
        services.AddMeridianEfCoreTransactions<TestContext>(o => throw new InvalidOperationException("repeated configuration"));
        captured!.UseExecutionStrategy = true;
        using var root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var first = root.CreateScope(); using var second = root.CreateScope();
        var scope = first.ServiceProvider.GetRequiredService<ITransactionScopeProvider>();
        Assert.Same(scope, first.ServiceProvider.GetRequiredService<ITransactionExecutionProvider>());
        Assert.Same(scope, first.ServiceProvider.GetRequiredService<EfTransactionScopeProvider<TestContext>>());
        Assert.NotSame(scope, second.ServiceProvider.GetRequiredService<ITransactionScopeProvider>());
        Assert.Single(services, x => x.ServiceType == typeof(ITransactionScopeProvider));
    }

    [Fact, Trait("Provider", "PostgreSQL")]
    public async Task Registration_Options_Are_Snapshotted_Before_Scope_Resolution()
    {
        await using var db = await DatabaseFixture.CreateFaultAsync(true, true);
        var services = new ServiceCollection(); services.AddScoped(_ => db.Context);
        EfTransactionOptions? captured = null;
        services.AddMeridianEfCoreTransactions<TestContext>(options => captured = options);
        captured!.UseExecutionStrategy = true;
        await using var root = services.BuildServiceProvider(); await using var scope = root.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredService<ITransactionExecutionProvider>();
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExecuteAsync(ct => { calls++; return Task.FromResult(1); }, default));
        Assert.Equal(0, calls);
    }

    [Fact, Trait("Provider", "PostgreSQL")]
    public async Task PostgreSql_Native_Provider_Identity_Is_Emitted()
    {
        await using var db = await DatabaseFixture.CreateAsync(true);
        await db.Context.Database.OpenConnectionAsync();
        output.WriteLine($"PROVIDER postgres={db.Context.Database.GetDbConnection().ServerVersion} provider={db.Context.Database.ProviderName} runtime={Environment.Version}");
        await using var transaction = await db.Context.Database.BeginTransactionAsync();
        output.WriteLine($"TRANSACTION implementation={transaction.GetType().FullName} savepoints={transaction.SupportsSavepoints}");
        Assert.True(transaction.SupportsSavepoints);
        await transaction.RollbackAsync();
    }

    [Fact]
    public void Existing_Consumer_Provider_Is_Not_Replaced()
    {
        var services = new ServiceCollection(); var custom = new CustomProvider();
        services.AddSingleton<ITransactionScopeProvider>(custom);
        services.AddMeridianEfCoreTransactions<TestContext>();
        using var root = services.BuildServiceProvider();
        Assert.Same(custom, root.GetRequiredService<ITransactionScopeProvider>());
    }

    [Fact, Trait("Provider", "Redis")]
    public async Task Real_Leased_Claim_Remains_Retained_After_Inner_Commit_Uncertainty()
    {
        var endpoint = Environment.GetEnvironmentVariable("MERIDIAN_TEST_REDIS") ?? throw new InvalidOperationException("MERIDIAN_TEST_REDIS required.");
        await using var redis = await ConnectionMultiplexer.ConnectAsync(endpoint);
        await using var db = await DatabaseFixture.CreateFaultAsync(false);
        var prefix = "c5-" + Guid.NewGuid().ToString("N");
        var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        var registry = new CacheContractRegistry().RegisterContract("text", 1, (JsonTypeInfo<string>)options.GetTypeInfo(typeof(string)));
        var store = new RedisLeasedIdempotencyStore(redis, registry, new JsonCacheValueSerializer(registry), new() { ApplicationKeyPrefix = prefix });
        var request = new Command(); var key = IdempotencyKeyComposer.Compose<Command>(request.IdempotencyKey);
        var uncertain = new IOException("committed but acknowledgement failed"); db.Context.Faults.AfterCommit = uncertain;
        var tx = new TransactionBehavior<Command, string>(new EfTransactionScopeProvider<TestContext>(db.Context));
        var idempotency = new IdempotencyBehavior<Command, string>(store); var calls = 0;
        try
        {
            var error = await Record.ExceptionAsync(() => new RetryBehavior<Command, string>().Handle(request,
                () => idempotency.Handle(request, () => tx.Handle(request, async () => { calls++; await db.WriteAsync("committed"); return "done"; }, default), default), default));
            Assert.NotNull(error); if (error is AggregateException a) Assert.Same(uncertain, a.InnerExceptions[0]); else Assert.Same(uncertain, error);
            Assert.Equal(1, calls); Assert.Single(await db.ReadIndependentAsync());
            Assert.Equal(LeasedIdempotencyStatus.InFlight, (await store.ClaimAsync(key, typeof(string), default)).Status);
            await Assert.ThrowsAsync<IdempotencyInProgressException>(() => idempotency.Handle(request, () => { calls++; return Task.FromResult("bad"); }, default));
            Assert.Equal(1, calls);
        }
        finally { await redis.GetDatabase().KeyDeleteAsync(CacheKeyDecorator.Compose(key, prefix, null)); }
    }

    [Fact]
    public async Task Native_Provider_And_Assembly_Identity_Is_Emitted()
    {
        await using var db = await DatabaseFixture.CreateAsync(false);
        await db.Context.Database.OpenConnectionAsync();
        output.WriteLine($"HOST runtime={Environment.Version} framework={AppContext.TargetFrameworkName} sqlite={db.Context.Database.GetDbConnection().ServerVersion}");
        foreach (var assembly in new[] { typeof(DbContext).Assembly, typeof(RelationalDatabaseFacadeExtensions).Assembly,
            typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly, typeof(Npgsql.NpgsqlConnection).Assembly,
            typeof(Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder).Assembly,
            typeof(EfTransactionScopeProvider<>).Assembly, typeof(ITransactionExecutionProvider).Assembly, GetType().Assembly })
            output.WriteLine($"LOADED assembly={assembly.GetName().Name} version={assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} path={assembly.Location} sha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant()}");
    }
    private sealed class CustomProvider : ITransactionScopeProvider
    { public Task<ITransactionScope> BeginAsync(CancellationToken cancellationToken) => throw new NotSupportedException(); }
    private sealed class Command : ITransactionalRequest, IIdempotentRequest, IRetryableRequest
    { public string IdempotencyKey => "once"; public int MaxRetries => 3; public TimeSpan RetryDelay => TimeSpan.Zero; public bool ShouldRetry(Exception exception) => true; }
}
