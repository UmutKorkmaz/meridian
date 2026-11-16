using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Meridian.Mapping.EntityFramework.Tests;

public sealed class SqliteFixture : ProjectionFixture
{
    private readonly SqliteConnection connection;

    public SqliteFixture() : this(null) { }

    internal SqliteFixture(DbCommandInterceptor? failure)
    {
        connection = new SqliteConnection("Data Source=:memory:");
        try
        {
            connection.Open();
            Context = new ProjectionContext(new DbContextOptionsBuilder<ProjectionContext>()
                .UseSqlite(connection).AddInterceptors(failure == null ? [Commands] : [Commands, failure]).Options);
            Context.Database.EnsureCreated();
            Seed();
        }
        catch
        {
            try { Dispose(); }
            catch { /* Preserve the primary setup failure. */ }
            throw;
        }
    }

    public override void Dispose()
    {
        try { base.Dispose(); }
        finally { connection.Dispose(); }
    }
}
