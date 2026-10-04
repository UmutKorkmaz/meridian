using Microsoft.EntityFrameworkCore;
namespace Meridian.AspNetCore.Infrastructure;

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderEvent> Events => Set<OrderEvent>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>().HasKey(x => x.Id);
        modelBuilder.Entity<Order>().HasIndex(x => x.Scope);
        modelBuilder.Entity<OrderEvent>().HasKey(x => x.Sequence);
        modelBuilder.Entity<OrderEvent>().HasIndex(x => new { x.Scope, x.Sequence });
        modelBuilder.Entity<OrderEvent>().HasOne<Order>().WithMany().HasForeignKey(x => x.OrderId);
    }
}
public sealed class Order
{
    public Guid Id { get; set; }
    public string Scope { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string Tenant { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Product { get; set; } = "";
    public int Quantity { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
public sealed class OrderEvent
{
    public long Sequence { get; set; }
    public Guid OrderId { get; set; }
    public string Scope { get; set; } = "";
    public string Product { get; set; } = "";
}
