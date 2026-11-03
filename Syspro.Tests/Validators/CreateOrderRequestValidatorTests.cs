using FluentValidation.TestHelper;
using Syspro.Api.DTOs;
using Syspro.Api.Validators;
using Xunit;

namespace Syspro.Tests.Validators;

public class CreateOrderRequestValidatorTests
{
    private readonly CreateOrderRequestValidator _validator =
        new(new CreateOrderItemRequestValidator());

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

    private static CreateOrderRequest CreateRequestWithItem(CreateOrderItemRequest item)
    {
        return CreateValidRequest(items: [item]);
    }

    // decimal is not a valid attribute argument, so prices use TheoryData.
    public static TheoryData<decimal> ValidUnitPrices => new()
    {
        0m,
        0.01m,
        10.99m,
        10.990m,
        9_999_999_999_999_999.99m
    };

    public static TheoryData<decimal> InvalidUnitPrices => new()
    {
        -0.01m,
        -500m,
        10.999m,
        0.001m,
        10_000_000_000_000_000m
    };

    // ---------- Valid requests ----------

    [Fact]
    public void Validate_WithCustomerId_IsValid()
    {
        var result = _validator.TestValidate(CreateValidRequest());

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_WithLegacyCustomerIdOnly_IsValid()
    {
        var request = CreateValidRequest(
            customerId: null,
            legacyCustomerId: "0000012345");

        var result = _validator.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_WithBothCustomerIdentifiers_IsValid()
    {
        var request = CreateValidRequest(
            customerId: 1,
            legacyCustomerId: "0000012345");

        var result = _validator.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_WithMultipleValidItems_IsValid()
    {
        var request = CreateValidRequest(
            items: [CreateValidItem(sku: "SKU-001"), CreateValidItem(sku: "SKU-002")]);

        var result = _validator.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    // ---------- Customer identification ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithoutCustomerIdentification_HasCustomerIdError(
        string? legacyCustomerId)
    {
        var request = CreateValidRequest(
            customerId: null,
            legacyCustomerId: legacyCustomerId);

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.CustomerId)
            .WithErrorMessage("CustomerId or LegacyCustomerId must be provided.");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Validate_WithPositiveCustomerId_HasNoCustomerIdError(int customerId)
    {
        var result = _validator.TestValidate(CreateValidRequest(customerId: customerId));

        result.ShouldNotHaveValidationErrorFor(x => x.CustomerId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Validate_WithNonPositiveCustomerId_HasCustomerIdError(int customerId)
    {
        var result = _validator.TestValidate(CreateValidRequest(customerId: customerId));

        result.ShouldHaveValidationErrorFor(x => x.CustomerId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(CreateOrderRequestValidator.LegacyCustomerIdMaxLength)]
    public void Validate_WithLegacyCustomerIdWithinMaxLength_IsValid(int length)
    {
        var request = CreateValidRequest(
            customerId: null,
            legacyCustomerId: new string('1', length));

        var result = _validator.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_WithLegacyCustomerIdOverMaxLength_HasLegacyCustomerIdError()
    {
        var request = CreateValidRequest(
            customerId: null,
            legacyCustomerId: new string('1', CreateOrderRequestValidator.LegacyCustomerIdMaxLength + 1));

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.LegacyCustomerId);
    }

    // A usable identifier does not excuse an invalid one sent alongside it.
    [Fact]
    public void Validate_WithValidLegacyIdButInvalidCustomerId_HasCustomerIdError()
    {
        var result = _validator.TestValidate(
            CreateValidRequest(customerId: 0, legacyCustomerId: "0000012345"));

        result.ShouldHaveValidationErrorFor(x => x.CustomerId);
    }

    [Fact]
    public void Validate_WithValidCustomerIdButOverLongLegacyId_HasLegacyCustomerIdError()
    {
        var result = _validator.TestValidate(CreateValidRequest(
            customerId: 1,
            legacyCustomerId: new string('1', CreateOrderRequestValidator.LegacyCustomerIdMaxLength + 1)));

        result.ShouldHaveValidationErrorFor(x => x.LegacyCustomerId);
    }

    // ---------- Currency ----------

    [Theory]
    [InlineData("ZAR")]
    [InlineData("zar")]
    [InlineData("Usd")]
    public void Validate_WithThreeLetterCurrency_HasNoCurrencyError(string currency)
    {
        var result = _validator.TestValidate(CreateValidRequest(currency: currency));

        result.ShouldNotHaveValidationErrorFor(x => x.Currency);
    }

    [Theory]
    [InlineData("ZA")]
    [InlineData("ZARR")]
    [InlineData("Z1R")]
    [InlineData("ZA ")]
    [InlineData(" ZAR")]
    [InlineData("ZAR\n")]
    [InlineData("Z-R")]
    public void Validate_WithMalformedCurrency_HasCurrencyFormatError(string currency)
    {
        var result = _validator.TestValidate(CreateValidRequest(currency: currency));

        result.ShouldHaveValidationErrorFor(x => x.Currency)
            .WithErrorMessage("Currency must be a 3-letter ISO 4217 code.");
    }

    // Letters outside A-Z would pass a check based on char.IsLetter.
    [Theory]
    [InlineData("ÄBC")]
    [InlineData("日本語")]
    public void Validate_WithNonAsciiCurrency_HasCurrencyFormatError(string currency)
    {
        var result = _validator.TestValidate(CreateValidRequest(currency: currency));

        result.ShouldHaveValidationErrorFor(x => x.Currency);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_WithMissingCurrency_HasSingleCurrencyError(string? currency)
    {
        var request = CreateValidRequest(currency: currency!);

        var result = _validator.TestValidate(request);

        Assert.Single(result.Errors, error => error.PropertyName == "Currency");
    }

    // ---------- Status ----------

    [Theory]
    [InlineData(1)]
    [InlineData(CreateOrderRequestValidator.StatusMaxLength)]
    public void Validate_WithStatusWithinMaxLength_HasNoStatusError(int length)
    {
        var result = _validator.TestValidate(
            CreateValidRequest(status: new string('S', length)));

        result.ShouldNotHaveValidationErrorFor(x => x.Status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_WithMissingStatus_HasStatusError(string? status)
    {
        var result = _validator.TestValidate(CreateValidRequest(status: status!));

        result.ShouldHaveValidationErrorFor(x => x.Status);
    }

    [Fact]
    public void Validate_WithStatusOverMaxLength_HasStatusError()
    {
        var result = _validator.TestValidate(
            CreateValidRequest(status: new string('S', CreateOrderRequestValidator.StatusMaxLength + 1)));

        result.ShouldHaveValidationErrorFor(x => x.Status);
    }

    // ---------- Items collection ----------

    [Fact]
    public void Validate_WithEmptyItems_HasItemsError()
    {
        var result = _validator.TestValidate(CreateValidRequest(items: []));

        result.ShouldHaveValidationErrorFor(x => x.Items)
            .WithErrorMessage("An order must contain at least one item.");
    }

    [Fact]
    public void Validate_WithNullItems_HasItemsError()
    {
        var request = new CreateOrderRequest
        {
            CustomerId = 1,
            Currency = "ZAR",
            Status = "Created",
            Items = null!
        };

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Items)
            .WithErrorMessage("An order must contain at least one item.");
    }

    [Fact]
    public void Validate_WithNullItemEntry_HasErrorForThatEntry()
    {
        var request = CreateValidRequest(items: [CreateValidItem(), null!]);

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor("Items[1]")
            .WithErrorMessage("Order item must not be null.");
        result.ShouldNotHaveValidationErrorFor("Items[0]");
    }

    [Fact]
    public void Validate_WithInvalidSecondItem_ReportsErrorAgainstThatItemOnly()
    {
        var request = CreateValidRequest(
            items: [CreateValidItem(), CreateValidItem(quantity: 0)]);

        var result = _validator.TestValidate(request);

        result.ShouldHaveValidationErrorFor("Items[1].Quantity");
        result.ShouldNotHaveValidationErrorFor("Items[0].Quantity");
    }

    // ---------- SKU ----------

    [Theory]
    [InlineData(1)]
    [InlineData(CreateOrderItemRequestValidator.SkuMaxLength)]
    public void Validate_WithSkuWithinMaxLength_HasNoSkuError(int length)
    {
        var result = _validator.TestValidate(
            CreateRequestWithItem(CreateValidItem(sku: new string('S', length))));

        result.ShouldNotHaveValidationErrorFor("Items[0].Sku");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_WithMissingSku_HasSkuError(string? sku)
    {
        var result = _validator.TestValidate(
            CreateRequestWithItem(CreateValidItem(sku: sku!)));

        result.ShouldHaveValidationErrorFor("Items[0].Sku");
    }

    [Fact]
    public void Validate_WithSkuOverMaxLength_HasSkuError()
    {
        var result = _validator.TestValidate(
            CreateRequestWithItem(CreateValidItem(
                sku: new string('S', CreateOrderItemRequestValidator.SkuMaxLength + 1))));

        result.ShouldHaveValidationErrorFor("Items[0].Sku");
    }

    // ---------- Description ----------

    [Theory]
    [InlineData(0)]
    [InlineData(CreateOrderItemRequestValidator.DescriptionMaxLength)]
    public void Validate_WithDescriptionWithinMaxLength_HasNoDescriptionError(int length)
    {
        var result = _validator.TestValidate(
            CreateRequestWithItem(CreateValidItem(description: new string('D', length))));

        result.ShouldNotHaveValidationErrorFor("Items[0].Description");
    }

    [Fact]
    public void Validate_WithNullDescription_HasDescriptionError()
    {
        var result = _validator.TestValidate(
            CreateRequestWithItem(CreateValidItem(description: null!)));

        result.ShouldHaveValidationErrorFor("Items[0].Description");
    }

    [Fact]
    public void Validate_WithDescriptionOverMaxLength_HasDescriptionError()
    {
        var result = _validator.TestValidate(
            CreateRequestWithItem(CreateValidItem(
                description: new string('D', CreateOrderItemRequestValidator.DescriptionMaxLength + 1))));

        result.ShouldHaveValidationErrorFor("Items[0].Description");
    }

    // ---------- Quantity ----------

    [Theory]
    [InlineData(1)]
    [InlineData(int.MaxValue)]
    public void Validate_WithPositiveQuantity_HasNoQuantityError(int quantity)
    {
        var result = _validator.TestValidate(
            CreateRequestWithItem(CreateValidItem(quantity: quantity)));

        result.ShouldNotHaveValidationErrorFor("Items[0].Quantity");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Validate_WithNonPositiveQuantity_HasQuantityError(int quantity)
    {
        var result = _validator.TestValidate(
            CreateRequestWithItem(CreateValidItem(quantity: quantity)));

        result.ShouldHaveValidationErrorFor("Items[0].Quantity");
    }

    // ---------- Unit price ----------

    [Theory]
    [MemberData(nameof(ValidUnitPrices))]
    public void Validate_WithValidUnitPrice_HasNoUnitPriceError(decimal unitPrice)
    {
        var result = _validator.TestValidate(
            CreateRequestWithItem(CreateValidItem(unitPrice: unitPrice)));

        result.ShouldNotHaveValidationErrorFor("Items[0].UnitPrice");
    }

    [Theory]
    [MemberData(nameof(InvalidUnitPrices))]
    public void Validate_WithInvalidUnitPrice_HasSingleUnitPriceError(decimal unitPrice)
    {
        var result = _validator.TestValidate(
            CreateRequestWithItem(CreateValidItem(unitPrice: unitPrice)));

        Assert.Single(result.Errors, error => error.PropertyName == "Items[0].UnitPrice");
    }

    // ---------- Multiple errors ----------

    [Fact]
    public void Validate_WithManyInvalidFields_ReportsOneErrorPerField()
    {
        var request = new CreateOrderRequest
        {
            CustomerId = null,
            LegacyCustomerId = null,
            Currency = "",
            Status = "",
            Items =
            [
                new CreateOrderItemRequest
                {
                    Sku = "",
                    Description = null!,
                    UnitPrice = -1m,
                    Quantity = 0
                }
            ]
        };

        var result = _validator.TestValidate(request);

        var failingProperties = result.Errors
            .Select(error => error.PropertyName)
            .ToList();

        Assert.Equal(
            new[]
            {
                "CustomerId",
                "Currency",
                "Status",
                "Items[0].Sku",
                "Items[0].Description",
                "Items[0].Quantity",
                "Items[0].UnitPrice"
            },
            failingProperties);
    }
}
