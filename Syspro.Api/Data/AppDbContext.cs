using Microsoft.EntityFrameworkCore;
using Syspro.Api.Models;

namespace Syspro.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<ImportLog> ImportLogs => Set<ImportLog>();
    public DbSet<ImportError> ImportErrors => Set<ImportError>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureCustomer(modelBuilder);
        ConfigureOrder(modelBuilder);
        ConfigureOrderItem(modelBuilder);
        ConfigureImportLog(modelBuilder);
        ConfigureImportError(modelBuilder);
    }

    private static void ConfigureCustomer(ModelBuilder modelBuilder)
    {
        var customer = modelBuilder.Entity<Customer>();

        customer.HasKey(x => x.Id);

        customer.Property(x => x.LegacyCustomerId)
            .HasMaxLength(10)
            .IsUnicode(false)
            .IsRequired();

        customer.Property(x => x.Name)
            .HasMaxLength(100)
            .IsRequired();

        customer.Property(x => x.Email)
            .HasMaxLength(254)
            .IsRequired();

        customer.Property(x => x.Tier)
            .HasMaxLength(1)
            .IsUnicode(false)
            .IsFixedLength()
            .IsRequired();

        customer.Property(x => x.SignupDate)
            .HasColumnType("date");

        customer.HasIndex(x => x.LegacyCustomerId)
            .IsUnique();

        customer.HasMany(x => x.Orders)
            .WithOne(x => x.Customer)
            .HasForeignKey(x => x.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureOrder(ModelBuilder modelBuilder)
    {
        var order = modelBuilder.Entity<Order>();

        order.HasKey(x => x.Id);

        order.Property(x => x.OrderDate)
            .HasColumnType("datetime2");

        order.Property(x => x.Currency)
            .HasMaxLength(3)
            .IsUnicode(false)
            .IsFixedLength()
            .IsRequired();

        order.Property(x => x.Status)
            .HasMaxLength(30)
            .IsRequired();

        order.HasIndex(x => x.CustomerId);
        order.HasIndex(x => x.OrderDate);

        order.HasMany(x => x.Items)
            .WithOne(x => x.Order)
            .HasForeignKey(x => x.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureOrderItem(ModelBuilder modelBuilder)
    {
        var orderItem = modelBuilder.Entity<OrderItem>();

        orderItem.HasKey(x => x.Id);

        orderItem.Property(x => x.Sku)
            .HasMaxLength(50)
            .IsRequired();

        orderItem.Property(x => x.Description)
            .HasMaxLength(200)
            .IsRequired();

        orderItem.Property(x => x.UnitPrice)
            .HasColumnType("decimal(18,2)");

        orderItem.HasIndex(x => x.OrderId);
    }

    private static void ConfigureImportLog(ModelBuilder modelBuilder)
    {
        var importLog = modelBuilder.Entity<ImportLog>();

        importLog.HasKey(x => x.Id);

        importLog.Property(x => x.StartedAt)
            .HasColumnType("datetime2");

        importLog.Property(x => x.CompletedAt)
            .HasColumnType("datetime2");
    }

    private static void ConfigureImportError(ModelBuilder modelBuilder)
    {
        var importError = modelBuilder.Entity<ImportError>();

        importError.HasKey(x => x.Id);

        importError.Property(x => x.RawData)
            .HasMaxLength(80)
            .IsRequired();

        importError.Property(x => x.Reason)
            .HasMaxLength(500)
            .IsRequired();

        importError.HasIndex(x => x.ImportLogId);

        importError.HasOne(x => x.ImportLog)
            .WithMany(x => x.Errors)
            .HasForeignKey(x => x.ImportLogId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}