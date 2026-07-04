using FluentValidation;

namespace SpazaHub.Application.Auth;

public sealed class RegisterOwnerCommandValidator : AbstractValidator<RegisterOwnerCommand>
{
    public RegisterOwnerCommandValidator()
    {
        RuleFor(x => x.Phone)
            .Must(PhoneNumberRules.IsValid)
            .WithMessage("Enter a valid South African cellphone number, e.g. 0821234567.");

        RuleFor(x => x.ShopName)
            .NotEmpty()
            .MaximumLength(100);
    }
}

public sealed class VerifyOtpCommandValidator : AbstractValidator<VerifyOtpCommand>
{
    public VerifyOtpCommandValidator()
    {
        RuleFor(x => x.Phone)
            .Must(PhoneNumberRules.IsValid)
            .WithMessage("Enter a valid South African cellphone number.");

        RuleFor(x => x.Code)
            .Matches(@"^\d{6}$")
            .WithMessage("The code is the 6 digits from the SMS.");

        RuleFor(x => x.DeviceName)
            .NotEmpty()
            .MaximumLength(80);
    }
}

public sealed class RefreshTokenCommandValidator : AbstractValidator<RefreshTokenCommand>
{
    public RefreshTokenCommandValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty();
    }
}

public sealed class CashierLoginCommandValidator : AbstractValidator<CashierLoginCommand>
{
    public CashierLoginCommandValidator()
    {
        RuleFor(x => x.DeviceRefreshToken).NotEmpty();

        RuleFor(x => x.CashierId).NotEmpty();

        RuleFor(x => x.Pin)
            .Matches(@"^\d{4}$")
            .WithMessage("PIN is exactly 4 digits.");
    }
}

public sealed class CreateCashierCommandValidator : AbstractValidator<CreateCashierCommand>
{
    public CreateCashierCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(80);

        RuleFor(x => x.Pin)
            .Matches(@"^\d{4}$")
            .WithMessage("PIN is exactly 4 digits.")
            .Must(pin => pin.Distinct().Count() > 1)
            .WithMessage("PIN cannot be four identical digits.");
    }
}
