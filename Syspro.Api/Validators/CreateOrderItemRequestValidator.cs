using FluentValidation;
using Syspro.Api.DTOs;

namespace Syspro.Api.Validators;

public sealed class CreateOrderItemRequestValidator : AbstractValidator<CreateOrderItemRequest>
{
    // Lengths and precision mirror the column constraints configured in AppDbContext.
    public const int SkuMaxLength = 50;
    public const int DescriptionMaxLength = 200;
    public const int UnitPricePrecision = 18;
    public const int UnitPriceScale = 2;

    public CreateOrderItemRequestValidator()
    {
        // Report at most one error per property instead of every failing rule.
        RuleLevelCascadeMode = CascadeMode.Stop;

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
