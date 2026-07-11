using SpazaHub.Api.Auth;

namespace SpazaHub.Api.Tests.Auth;

public class PhoneNumberRulesTests
{
    [Theory]
    [InlineData("0821234567", "+27821234567")]
    [InlineData("+27821234567", "+27821234567")]
    [InlineData("082 123 4567", "+27821234567")]
    [InlineData("082-123-4567", "+27821234567")]
    public void TryNormalize_AcceptsValidZaNumbers(string input, string expected)
    {
        Assert.True(PhoneNumberRules.TryNormalize(input, out string normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("00821234567")]
    [InlineData("082123456")]
    [InlineData("08212345678")]
    [InlineData("+1555123456")]
    [InlineData("hello")]
    public void TryNormalize_RejectsInvalidInput(string? input)
    {
        Assert.False(PhoneNumberRules.TryNormalize(input, out _));
    }
}
