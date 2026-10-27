using FluentValidation;
using Syspro.Api.DTOs;

namespace Syspro.Api.Validators;

public sealed class CreateOrderItemRequestValidator : AbstractValidator<CreateOrderItemRequest>
{
    // Lengths and precision mirror the column constraints configured in AppDbContext.
    private const int SkuMaxLength = 50;
    private const int DescriptionMaxLength = 200;
    private const int UnitPricePrecision = 18;
    private const int UnitPriceScale = 2;

    public CreateOrderItemRequestValidator()
    {
        RuleFor(x => x.Sku)
            .NotEmpty()
            .MaximumLength(SkuMaxLength);

        RuleFor(x => x.Description)
            .NotNull()
            .MaximumLength(DescriptionMaxLength);

        RuleFor(x => x.Quantity)
            .GreaterThan(0);

        RuleFor(x => x.UnitPrice)
            .GreaterThanOrEqualTo(0)
            .PrecisionScale(UnitPricePrecision, UnitPriceScale, ignoreTrailingZeros: true);
    }
}
