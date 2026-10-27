using FluentValidation.TestHelper;
using Syspro.Api.DTOs;
using Syspro.Api.Validators;
using Xunit;

namespace Syspro.Tests.Validators;

public class CreateOrderRequestValidatorTests
{
    private readonly CreateOrderRequestValidator _validator = new();

    private static CreateOrderRequest CreateValidRequest(
        int? customerId = 1,
        string? legacyCustomerId = null,
        string currency = "ZAR",
        string status = "Created",
        List<CreateOrderItemRequest>? items = null)
    {
        return new CreateOrderRequest
        {
            CustomerId = customerId,
            LegacyCustomerId = legacyCustomerId,
            Currency = currency,
            Status = status,
            Items = items ?? [CreateValidItem()]
        };
    }

    private static CreateOrderItemRequest CreateValidItem(
        string sku = "SKU-001",
        string description = "Keyboard",
        decimal unitPrice = 500m,
        int quantity = 1)
    {
        return new CreateOrderItemRequest
        {
            Sku = sku,
            Description = description,
            UnitPrice = unitPrice,
            Quantity = quantity
        };
    }

    [Fact]
    public void Validate_WithCustomerId_IsValid()
    {
        // Act
        var result = _validator.TestValidate(CreateValidRequest());

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_WithLegacyCustomerIdOnly_IsValid()
    {
        // Arrange
        var request = CreateValidRequest(
            customerId: null,
            legacyCustomerId: "0000012345",
            currency: "zar");

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_WithFreeItem_IsValid()
    {
        // Arrange
        var request = CreateValidRequest(
            items: [CreateValidItem(unitPrice: 0m)]);

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithoutCustomerIdentification_HasCustomerIdError(
        string? legacyCustomerId)
    {
        // Arrange
        var request = CreateValidRequest(
            customerId: null,
            legacyCustomerId: legacyCustomerId);

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.CustomerId)
            .WithErrorMessage("CustomerId or LegacyCustomerId must be provided.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_WithNonPositiveCustomerId_HasCustomerIdError(int customerId)
    {
        // Act
        var result = _validator.TestValidate(
            CreateValidRequest(customerId: customerId));

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.CustomerId);
    }

    [Fact]
    public void Validate_WithTooLongLegacyCustomerId_HasLegacyCustomerIdError()
    {
        // Arrange
        var request = CreateValidRequest(
            customerId: null,
            legacyCustomerId: new string('1', 11));

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.LegacyCustomerId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ZA")]
    [InlineData("ZARR")]
    [InlineData("Z1R")]
    public void Validate_WithInvalidCurrency_HasCurrencyError(string currency)
    {
        // Act
        var result = _validator.TestValidate(
            CreateValidRequest(currency: currency));

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Currency);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789012345678901234567890")]
    public void Validate_WithInvalidStatus_HasStatusError(string status)
    {
        // Act
        var result = _validator.TestValidate(
            CreateValidRequest(status: status));

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Status);
    }

    [Fact]
    public void Validate_WithNoItems_HasItemsError()
    {
        // Act
        var result = _validator.TestValidate(CreateValidRequest(items: []));

        // Assert
        result.ShouldHaveValidationErrorFor(x => x.Items)
            .WithErrorMessage("An order must contain at least one item.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithMissingSku_HasSkuError(string sku)
    {
        // Act
        var result = _validator.TestValidate(
            CreateValidRequest(items: [CreateValidItem(sku: sku)]));

        // Assert
        result.ShouldHaveValidationErrorFor("Items[0].Sku");
    }

    [Fact]
    public void Validate_WithTooLongSku_HasSkuError()
    {
        // Act
        var result = _validator.TestValidate(
            CreateValidRequest(items: [CreateValidItem(sku: new string('S', 51))]));

        // Assert
        result.ShouldHaveValidationErrorFor("Items[0].Sku");
    }

    [Fact]
    public void Validate_WithTooLongDescription_HasDescriptionError()
    {
        // Act
        var result = _validator.TestValidate(
            CreateValidRequest(
                items: [CreateValidItem(description: new string('D', 201))]));

        // Assert
        result.ShouldHaveValidationErrorFor("Items[0].Description");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_WithNonPositiveQuantity_HasQuantityError(int quantity)
    {
        // Act
        var result = _validator.TestValidate(
            CreateValidRequest(items: [CreateValidItem(quantity: quantity)]));

        // Assert
        result.ShouldHaveValidationErrorFor("Items[0].Quantity");
    }

    [Fact]
    public void Validate_WithNegativeUnitPrice_HasUnitPriceError()
    {
        // Act
        var result = _validator.TestValidate(
            CreateValidRequest(items: [CreateValidItem(unitPrice: -0.01m)]));

        // Assert
        result.ShouldHaveValidationErrorFor("Items[0].UnitPrice");
    }

    [Fact]
    public void Validate_WithMoreThanTwoDecimalPlaces_HasUnitPriceError()
    {
        // Act
        var result = _validator.TestValidate(
            CreateValidRequest(items: [CreateValidItem(unitPrice: 10.999m)]));

        // Assert
        result.ShouldHaveValidationErrorFor("Items[0].UnitPrice");
    }

    [Fact]
    public void Validate_WithInvalidSecondItem_ReportsErrorAgainstThatItem()
    {
        // Arrange
        var request = CreateValidRequest(
            items: [CreateValidItem(), CreateValidItem(quantity: 0)]);

        // Act
        var result = _validator.TestValidate(request);

        // Assert
        result.ShouldHaveValidationErrorFor("Items[1].Quantity");
        result.ShouldNotHaveValidationErrorFor("Items[0].Quantity");
    }
}
