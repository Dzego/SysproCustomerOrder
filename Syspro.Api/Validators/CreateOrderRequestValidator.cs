using FluentValidation;
using Syspro.Api.DTOs;

namespace Syspro.Api.Validators;

public sealed class CreateOrderRequestValidator : AbstractValidator<CreateOrderRequest>
{
    // Lengths mirror the column constraints configured in AppDbContext.
    private const int LegacyCustomerIdMaxLength = 10;
    private const int StatusMaxLength = 30;

    public CreateOrderRequestValidator()
    {
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
            .Matches("^[A-Za-z]{3}$")
            .WithMessage("Currency must be a 3-letter ISO 4217 code.");

        RuleFor(x => x.Status)
            .NotEmpty()
            .MaximumLength(StatusMaxLength);

        RuleFor(x => x.Items)
            .NotEmpty()
            .WithMessage("An order must contain at least one item.");

        RuleForEach(x => x.Items)
            .NotNull()
            .SetValidator(new CreateOrderItemRequestValidator());
    }
}
