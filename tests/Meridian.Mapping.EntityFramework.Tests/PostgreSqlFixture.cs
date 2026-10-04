using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Meridian.Mapping.EntityFramework.Tests;

public sealed class PostgreSqlFixture : ProjectionFixture
{
    // A per-fixture search path isolates concurrent test runs without touching public tables.
    private readonly string schema = "meridian_b7_" + Guid.NewGuid().ToString("N");

    public PostgreSqlFixture()
    {
        var connection = Environment.GetEnvironmentVariable("MERIDIAN_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("PostgreSQL is required. Set MERIDIAN_TEST_POSTGRES to an isolated test service; no skip or business database fallback is allowed.");
        var builder = new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema };
        Context = new ProjectionContext(new DbContextOptionsBuilder<ProjectionContext>()
            .UseNpgsql(builder.ConnectionString).AddInterceptors(Commands).Options);
        Context.Database.OpenConnection();
        Context.Database.ExecuteSqlRaw($"CREATE SCHEMA \"{schema}\"");
        try
        {
            Context.Database.ExecuteSqlRaw(Context.Database.GenerateCreateScript());
            Seed();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public override void Dispose()
    {
        try { Context.Database.ExecuteSqlRaw($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE"); }
        finally { base.Dispose(); }
    }
}
