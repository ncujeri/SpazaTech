using SpazaHub.Api.Auth;

namespace SpazaHub.Api.Tests.Auth;

public class ValidatorTests
{
    [Fact]
    public void RegisterOwner_ValidRequest_Passes()
    {
        var validator = new RegisterOwnerCommandValidator();

        var result = validator.Validate(new RegisterOwnerCommand("0821234567", "Mama Thoko's"));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("12345", "Shop")]
    [InlineData("0821234567", "")]
    public void RegisterOwner_InvalidRequest_Fails(string phone, string shopName)
    {
        var validator = new RegisterOwnerCommandValidator();

        var result = validator.Validate(new RegisterOwnerCommand(phone, shopName));

        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("123456", true)]
    [InlineData("12345", false)]
    [InlineData("1234567", false)]
    [InlineData("12a456", false)]
    public void VerifyOtp_CodeMustBeSixDigits(string code, bool expectedValid)
    {
        var validator = new VerifyOtpCommandValidator();

        var result = validator.Validate(new VerifyOtpCommand("0821234567", code, "Owner phone"));

        Assert.Equal(expectedValid, result.IsValid);
    }

    [Theory]
    [InlineData("1234", true)]
    [InlineData("0000", false)]
    [InlineData("123", false)]
    [InlineData("12345", false)]
    [InlineData("abcd", false)]
    public void CreateCashier_PinRules(string pin, bool expectedValid)
    {
        var validator = new CreateCashierCommandValidator();

        var result = validator.Validate(new CreateCashierCommand("Sipho", pin, false));

        Assert.Equal(expectedValid, result.IsValid);
    }

    [Fact]
    public void CashierLogin_RequiresTokenCashierAndPin()
    {
        var validator = new CashierLoginCommandValidator();

        var bad = validator.Validate(new CashierLoginCommand("", Guid.Empty, "12"));
        Assert.False(bad.IsValid);

        var good = validator.Validate(new CashierLoginCommand("token", Guid.NewGuid(), "1234"));
        Assert.True(good.IsValid);
    }
}
