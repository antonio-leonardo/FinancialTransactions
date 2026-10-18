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
        var createdFirst = MovementAmount.TryCreate(10.10m, out var first, out _);
        var createdSecond = MovementAmount.TryCreate(10.10m, out var second, out _);
        var createdOther = MovementAmount.TryCreate(10.11m, out var other, out _);

        // Pré-condições: se qualquer criação falhar, o teste reprova aqui em vez de
        // comparar nulos por acidente (um nulo faria Assert.NotEqual passar sem provar nada).
        Assert.True(createdFirst);
        Assert.True(createdSecond);
        Assert.True(createdOther);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(other);

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void EqualityIgnoresTrailingZeroScale()
    {
        var createdPlain = MovementAmount.TryCreate(10.10m, out var plain, out _);
        var createdScaled = MovementAmount.TryCreate(10.100m, out var scaled, out _);

        Assert.True(createdPlain);
        Assert.True(createdScaled);
        Assert.NotNull(plain);
        Assert.NotNull(scaled);

        // 10.10m e 10.100m são o mesmo valor em centavos; a igualdade do record segue decimal.Equals.
        Assert.Equal(plain, scaled);
        Assert.Equal(plain.GetHashCode(), scaled.GetHashCode());
    }
}
