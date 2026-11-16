using System.Reflection;
using System.Security.Cryptography;
using Meridian.Mediator.Behaviors;
using Meridian.Mediator.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

Console.WriteLine($"HOST runtime={Environment.Version}");
var endpoint = Environment.GetEnvironmentVariable("MERIDIAN_TEST_POSTGRES") ?? throw new InvalidOperationException("Required MERIDIAN_TEST_POSTGRES is missing.");
var schema = "c5_consumer_" + Guid.NewGuid().ToString("N");
await using var admin = new NpgsqlConnection(endpoint); await admin.OpenAsync();
await using (var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin)) await create.ExecuteNonQueryAsync();
var path = Path.Combine(Path.GetTempPath(), "meridian-c5-consumer-" + Guid.NewGuid().ToString("N") + ".db");
try
{
    var options = new DbContextOptionsBuilder<Store>().UseSqlite("Data Source=" + path).Options;
    await using (var db = new Store(options))
    {
        await db.Database.EnsureCreatedAsync();
        var provider = new EfTransactionScopeProvider<Store>(db);
        var error = new IOException("rollback");
        try { await provider.ExecuteAsync<int>(async ct => { db.Rows.Add(new() { Id = 1 }); await db.SaveChangesAsync(ct); throw error; }, default); throw new InvalidOperationException("failure swallowed"); }
        catch (IOException actual) { Require(ReferenceEquals(actual, error), "identity"); }
        await using var verify = new Store(options); Require(await verify.Rows.CountAsync() == 0, "real rollback");
    }
    var postgres = new NpgsqlConnectionStringBuilder(endpoint) { SearchPath = schema };
    var pgOptions = new DbContextOptionsBuilder<Store>().UseNpgsql(postgres.ConnectionString).Options;
    await using (var db = new Store(pgOptions))
    {
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE \"Rows\" (\"Id\" integer PRIMARY KEY)");
        var services = new ServiceCollection(); services.AddScoped(_ => db); services.AddMeridianEfCoreTransactions<Store>(); services.AddMeridianEfCoreTransactions<Store>();
        await using var root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }); await using var serviceScope = root.CreateAsyncScope();
        var provider = serviceScope.ServiceProvider.GetRequiredService<ITransactionExecutionProvider>();
        Require(ReferenceEquals(provider, serviceScope.ServiceProvider.GetRequiredService<ITransactionScopeProvider>()), "scoped alias");
        await using var outer = await db.Database.BeginTransactionAsync();
        db.Rows.Add(new() { Id = 1 }); await db.SaveChangesAsync();
        try { await provider.ExecuteAsync<int>(async ct => { db.Rows.Add(new() { Id = 2 }); await db.SaveChangesAsync(ct); throw new IOException("child"); }, default); }
        catch (IOException) { }
        Require(ReferenceEquals(outer, db.Database.CurrentTransaction), "ambient ownership");
        Require(await db.Rows.AsNoTracking().CountAsync() == 1, "savepoint rollback");
        await outer.CommitAsync();
        await using var verify = new Store(pgOptions); Require(await verify.Rows.CountAsync() == 1, "commit state");
    }
    foreach (var assembly in new[] { typeof(EfTransactionScopeProvider<>).Assembly, typeof(ITransactionExecutionProvider).Assembly, typeof(DbContext).Assembly, typeof(RelationalDatabaseFacadeExtensions).Assembly, typeof(NpgsqlConnection).Assembly, typeof(Store).Assembly })
        Console.WriteLine($"LOADED assembly={assembly.GetName().Name} version={assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} path={assembly.Location} sha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant()}");
    Console.WriteLine($"PROVIDER postgres={admin.PostgreSqlVersion}");
    Console.WriteLine("COMMON ef transactions=owned sqliteRollback=true postgresSavepoint=true ambientOwner=caller scopedAliases=true handlerReplay=disabled");
}
finally
{
    await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", admin); await drop.ExecuteNonQueryAsync();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); File.Delete(path);
}
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
internal sealed class Store(DbContextOptions<Store> options) : DbContext(options) { public DbSet<Row> Rows => Set<Row>(); }
internal sealed class Row { public int Id { get; set; } }
