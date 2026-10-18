using System.Globalization;
using FinancialTransactions.Domain;

namespace FinancialTransactions.UnitTests;

public class MovementAmountTests
{
    [Theory]
    [InlineData("0.01")]
    [InlineData("10")]
    [InlineData("10.00")]
    [InlineData("10.10")]
    [InlineData("10.100")]
    [InlineData("1000000000")]
    public void TryCreateAcceptsValidAmounts(string input)
    {
        var expected = decimal.Parse(input, CultureInfo.InvariantCulture);

        var created = MovementAmount.TryCreate(expected, out var amount, out var error);

        Assert.True(created);
        Assert.NotNull(amount);
        Assert.Equal(expected, amount.Value);
        Assert.Equal("BRL", amount.Currency);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("0", "amount_must_be_positive")]
    [InlineData("-1", "amount_must_be_positive")]
    [InlineData("10.005", "amount_scale_exceeds_2")]
    [InlineData("0.001", "amount_scale_exceeds_2")]
    [InlineData("1000000000.01", "amount_exceeds_maximum")]
    public void TryCreateRejectsInvalidAmounts(string input, string expectedError)
    {
        var value = decimal.Parse(input, CultureInfo.InvariantCulture);

        var created = MovementAmount.TryCreate(value, out var amount, out var error);

        Assert.False(created);
        Assert.Null(amount);
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void EqualityIsByValue()
    {
        MovementAmount.TryCreate(10.10m, out var first, out _);
        MovementAmount.TryCreate(10.10m, out var second, out _);
        MovementAmount.TryCreate(10.11m, out var other, out _);

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }
}
