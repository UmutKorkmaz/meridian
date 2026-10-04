using Meridian.Mediator.Behaviors;
using Meridian.Mediator.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Mediator.EntityFrameworkCore.Tests;

public sealed class TransactionIntegrationTests
{
    [Fact]
    public async Task Failed_Handler_Rolls_Back_Real_Db_Write()
    {
        await using var db = await DatabaseFixture.CreateAsync(false);
        var primary = new InvalidOperationException("handler");
        var provider = new EfTransactionScopeProvider<TestContext>(db.Context);
        Assert.Same(primary, await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExecuteAsync<int>(async ct =>
        {
            await db.WriteAsync("rollback", ct);
            throw primary;
        }, default)));
        Assert.Empty(await db.ReadIndependentAsync());
    }

    [Fact]
    public async Task Successful_Handler_Commits()
    {
        await using var db = await DatabaseFixture.CreateAsync(false);
        var provider = new EfTransactionScopeProvider<TestContext>(db.Context);
        Assert.Equal(42, await provider.ExecuteAsync(async ct => { await db.WriteAsync("commit", ct); return 42; }, default));
        Assert.Equal("commit", Assert.Single(await db.ReadIndependentAsync()));
        Assert.Null(db.Context.Database.CurrentTransaction);
    }

    [Fact]
    public async Task Ambient_Transaction_Is_Not_Committed_Or_Disposed()
    {
        await using var db = await DatabaseFixture.CreateAsync(false);
        await using var ambient = await db.Context.Database.BeginTransactionAsync();
        var provider = new EfTransactionScopeProvider<TestContext>(db.Context);
        await provider.ExecuteAsync(async ct => { await db.WriteAsync("ambient", ct); return 1; }, default);
        Assert.Same(ambient, db.Context.Database.CurrentTransaction);
        await ambient.RollbackAsync();
        Assert.Empty(await db.ReadIndependentAsync());
    }

    [Fact, Trait("Provider", "PostgreSQL")]
    public async Task Nested_Failure_Rolls_Back_To_Savepoint()
    {
        await using var db = await DatabaseFixture.CreateAsync(true);
        var provider = new EfTransactionScopeProvider<TestContext>(db.Context);
        await provider.ExecuteAsync(async ct =>
        {
            await db.WriteAsync("outer", ct);
            await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ExecuteAsync<int>(async child =>
            {
                await db.WriteAsync("inner", child);
                throw new InvalidOperationException("nested");
            }, ct));
            Assert.Equal("outer", Assert.Single(await db.Context.Rows.AsNoTracking().OrderBy(x => x.Name).Select(x => x.Name).ToArrayAsync(ct)));
            return 1;
        }, default);
        Assert.Equal("outer", Assert.Single(await db.ReadIndependentAsync()));
    }

    [Fact]
    public async Task Cancellation_Is_Propagated()
    {
        await using var db = await DatabaseFixture.CreateAsync(false);
        using var cancellation = new CancellationTokenSource();
        var primary = new OperationCanceledException(cancellation.Token);
        var provider = new EfTransactionScopeProvider<TestContext>(db.Context);
        Assert.Same(primary, await Assert.ThrowsAsync<OperationCanceledException>(() => provider.ExecuteAsync<int>(async ct =>
        {
            Assert.Equal(cancellation.Token, ct);
            await db.WriteAsync("canceled", ct);
            cancellation.Cancel();
            throw primary;
        }, cancellation.Token)));
        Assert.Empty(await db.ReadIndependentAsync());
    }
}

public sealed class TestContext(DbContextOptions<TestContext> options) : DbContext(options)
{
    public DbSet<TestRow> Rows => Set<TestRow>();
    internal TransactionFaults Faults { get; } = new();
}
public sealed class TestRow
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

internal sealed class DatabaseFixture : IAsyncDisposable
{
    private readonly DbContextOptions<TestContext> _options;
    private readonly string? _sqlitePath;
    private readonly string? _schema;
    public TestContext Context { get; }
    private DatabaseFixture(DbContextOptions<TestContext> options, string? path, string? schema)
    { _options = options; _sqlitePath = path; _schema = schema; Context = new(options); }
    public static Task<DatabaseFixture> CreateAsync(bool postgres, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) => CreateCoreAsync(postgres, false, false, interceptors);
    public static Task<DatabaseFixture> CreateFaultAsync(bool postgres, bool retry = false, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) => CreateCoreAsync(postgres, retry, true, interceptors);
    private static async Task<DatabaseFixture> CreateCoreAsync(bool postgres, bool retry, bool faults, Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<TestContext>();
        string? path = null; string? schema = null;
        if (postgres)
        {
            var endpoint = Environment.GetEnvironmentVariable("MERIDIAN_TEST_POSTGRES")
                ?? throw new InvalidOperationException("MERIDIAN_TEST_POSTGRES is required for selected PostgreSQL tests.");
            schema = "c5_" + Guid.NewGuid().ToString("N");
            await using var connection = new Npgsql.NpgsqlConnection(endpoint);
            await connection.OpenAsync();
            await using var command = new Npgsql.NpgsqlCommand($"CREATE SCHEMA {schema}", connection);
            await command.ExecuteNonQueryAsync();
            var scoped = new Npgsql.NpgsqlConnectionStringBuilder(endpoint) { SearchPath = schema };
            builder.UseNpgsql(scoped.ConnectionString, options => { if (retry) options.EnableRetryOnFailure(2, TimeSpan.Zero, null); });
        }
        else { path = Path.Combine(Path.GetTempPath(), "meridian-c5-" + Guid.NewGuid().ToString("N") + ".db"); builder.UseSqlite("Data Source=" + path); }
        if (faults) builder.ReplaceService<Microsoft.EntityFrameworkCore.Storage.IRelationalTransactionFactory, FaultTransactionFactory>();
        if (interceptors.Length > 0) builder.AddInterceptors(interceptors);
        var db = new DatabaseFixture(builder.Options, path, schema);
        await db.Context.Database.ExecuteSqlRawAsync("CREATE TABLE \"Rows\" (\"Id\" integer PRIMARY KEY, \"Name\" text NOT NULL)");
        return db;
    }
    public async Task WriteAsync(string value, CancellationToken ct = default)
    {
        var row = new TestRow { Id = Random.Shared.Next(1, int.MaxValue), Name = value };
        Context.Rows.Add(row); await Context.SaveChangesAsync(ct);
    }
    public async Task<string[]> ReadIndependentAsync()
    { await using var verify = new TestContext(_options); return await verify.Rows.AsNoTracking().OrderBy(x => x.Name).Select(x => x.Name).ToArrayAsync(); }
    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        if (_schema is not null)
        {
            await using var connection = new Npgsql.NpgsqlConnection(Environment.GetEnvironmentVariable("MERIDIAN_TEST_POSTGRES"));
            await connection.OpenAsync(); await using var command = new Npgsql.NpgsqlCommand($"DROP SCHEMA {_schema} CASCADE", connection); await command.ExecuteNonQueryAsync();
        }
        if (_sqlitePath is not null) { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(_sqlitePath); }
    }
}
