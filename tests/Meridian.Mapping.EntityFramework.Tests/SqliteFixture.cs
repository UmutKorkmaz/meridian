using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Meridian.Mapping.EntityFramework.Tests;

public sealed class SqliteFixture : ProjectionFixture
{
    private readonly SqliteConnection connection;

    public SqliteFixture()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        Context = new ProjectionContext(new DbContextOptionsBuilder<ProjectionContext>()
            .UseSqlite(connection).AddInterceptors(Commands).Options);
        Context.Database.EnsureCreated();
        Seed();
    }

    public override void Dispose()
    {
        base.Dispose();
        connection.Dispose();
    }
}
