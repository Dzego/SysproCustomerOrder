using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Syspro.Api.Data;
using Syspro.Api.Models;
using Syspro.Api.Validators;
using Xunit;

namespace Syspro.Tests.Validators;

// Guards against the validator limits drifting away from the EF Core schema.
// Building the SQL Server model does not open a database connection.
public class ValidatorDatabaseConstraintTests
{
    private static readonly IModel Model = CreateModel();

    private static IModel CreateModel()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=unused;Database=unused")
            .Options;

        using var dbContext = new AppDbContext(options);

        return dbContext.Model;
    }

    private static IProperty GetProperty<TEntity>(string propertyName)
    {
        return Model.FindEntityType(typeof(TEntity))!.FindProperty(propertyName)!;
    }

    [Fact]
    public void LegacyCustomerIdMaxLength_MatchesCustomerColumn()
    {
        Assert.Equal(
            GetProperty<Customer>(nameof(Customer.LegacyCustomerId)).GetMaxLength(),
            CreateOrderRequestValidator.LegacyCustomerIdMaxLength);
    }

    [Fact]
    public void StatusMaxLength_MatchesOrderColumn()
    {
        Assert.Equal(
            GetProperty<Order>(nameof(Order.Status)).GetMaxLength(),
            CreateOrderRequestValidator.StatusMaxLength);
    }

    [Fact]
    public void SkuMaxLength_MatchesOrderItemColumn()
    {
        Assert.Equal(
            GetProperty<OrderItem>(nameof(OrderItem.Sku)).GetMaxLength(),
            CreateOrderItemRequestValidator.SkuMaxLength);
    }

    [Fact]
    public void DescriptionMaxLength_MatchesOrderItemColumn()
    {
        Assert.Equal(
            GetProperty<OrderItem>(nameof(OrderItem.Description)).GetMaxLength(),
            CreateOrderItemRequestValidator.DescriptionMaxLength);
    }

    [Fact]
    public void UnitPricePrecisionAndScale_MatchOrderItemColumn()
    {
        var expectedColumnType =
            $"decimal({CreateOrderItemRequestValidator.UnitPricePrecision}," +
            $"{CreateOrderItemRequestValidator.UnitPriceScale})";

        Assert.Equal(
            expectedColumnType,
            GetProperty<OrderItem>(nameof(OrderItem.UnitPrice)).GetColumnType());
    }
}
