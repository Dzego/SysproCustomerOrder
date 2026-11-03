using FluentValidation;
using Syspro.Api.DTOs;

namespace Syspro.Api.Validators;

public sealed class CreateOrderRequestValidator : AbstractValidator<CreateOrderRequest>
{
    // Lengths mirror the column constraints configured in AppDbContext.
    public const int LegacyCustomerIdMaxLength = 10;
    public const int StatusMaxLength = 30;

    // \z anchors at the true end of input; $ would also accept a trailing newline.
    private const string CurrencyCodePattern = @"^[A-Za-z]{3}\z";

    public CreateOrderRequestValidator(
        IValidator<CreateOrderItemRequest> itemValidator)
    {
        // Report at most one error per property instead of every failing rule.
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(x => x.CustomerId)
            .NotNull()
            .When(x => string.IsNullOrWhiteSpace(x.LegacyCustomerId))
            .WithMessage("CustomerId or LegacyCustomerId must be provided.");

        RuleFor(x => x.CustomerId)
            .GreaterThan(0)
            .When(x => x.CustomerId.HasValue);

        RuleFor(x => x.LegacyCustomerId)
            .MaximumLength(LegacyCustomerIdMaxLength);

        RuleFor(x => x.Currency)
            .NotEmpty()
            .Matches(CurrencyCodePattern)
            .WithMessage("Currency must be a 3-letter ISO 4217 code.");

        RuleFor(x => x.Status)
            .NotEmpty()
            .MaximumLength(StatusMaxLength);

        RuleFor(x => x.Items)
            .NotEmpty()
            .WithMessage("An order must contain at least one item.");

        RuleForEach(x => x.Items)
            .NotNull()
            .WithMessage("Order item must not be null.")
            .SetValidator(itemValidator);
    }
}
