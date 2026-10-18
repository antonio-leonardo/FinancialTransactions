using System.Globalization;
using FinancialTransactions.Domain;

namespace FinancialTransactions.UnitTests;

public class MovementRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset OpenedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    [Fact]
    public void EffectiveAtExactlyAtToleranceIsAccepted()
    {
        var result = MovementRules.ValidateEffectiveAt(Now + Tolerance, OpenedAt, Now, Tolerance);

        Assert.True(result.IsValid);
        Assert.Null(result.Code);
    }

    [Fact]
    public void EffectiveAtOneSecondBeyondToleranceIsRejectedAsFuture()
    {
        var result = MovementRules.ValidateEffectiveAt(Now + Tolerance + TimeSpan.FromSeconds(1), OpenedAt, Now, Tolerance);

        Assert.False(result.IsValid);
        Assert.Equal("effective_at_in_future", result.Code);
    }

    [Fact]
    public void EffectiveAtBeforeOpeningIsRejected()
    {
        var result = MovementRules.ValidateEffectiveAt(OpenedAt - TimeSpan.FromSeconds(1), OpenedAt, Now, Tolerance);

        Assert.False(result.IsValid);
        Assert.Equal("effective_at_before_opening", result.Code);
    }

    [Fact]
    public void EffectiveAtEqualToOpeningIsAccepted()
    {
        var result = MovementRules.ValidateEffectiveAt(OpenedAt, OpenedAt, Now, Tolerance);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("2026-01-01T00:00:00-03:00", true)]   // 03:00Z, depois da abertura
    [InlineData("2025-12-31T22:00:00-03:00", true)]   // 01:00Z, depois da abertura
    [InlineData("2025-12-31T20:00:00-03:00", false)]  // 23:00Z do dia anterior, antes da abertura
    public void EffectiveAtIsComparedAsInstantNotAsLocalClock(string effectiveAt, bool expectedValid)
    {
        var value = DateTimeOffset.Parse(effectiveAt, CultureInfo.InvariantCulture);

        var result = MovementRules.ValidateEffectiveAt(value, OpenedAt, Now, Tolerance);

        Assert.Equal(expectedValid, result.IsValid);
    }

    [Fact]
    public void AsOfWithinToleranceIsAccepted()
    {
        var result = MovementRules.ValidateAsOf(Now + Tolerance, Now, Tolerance);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void AsOfBeyondToleranceIsRejectedAsValidationError()
    {
        var result = MovementRules.ValidateAsOf(Now + Tolerance + TimeSpan.FromSeconds(1), Now, Tolerance);

        Assert.False(result.IsValid);
        Assert.Equal("validation_error", result.Code);
    }

    [Theory]
    [InlineData("Credit", "10.50", "10.50")]
    [InlineData("Debit", "10.50", "-10.50")]
    [InlineData("Credit", "0.01", "0.01")]
    [InlineData("Debit", "1000000000", "-1000000000")]
    public void SignedAmountIsPositiveForCreditAndNegativeForDebit(string type, string amount, string expected)
    {
        var movementType = Enum.Parse<MovementType>(type);
        var created = MovementAmount.TryCreate(decimal.Parse(amount, CultureInfo.InvariantCulture), out var movementAmount, out _);
        Assert.True(created);
        Assert.NotNull(movementAmount);

        var signed = MovementRules.SignedAmount(movementType, movementAmount);

        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), signed);
    }

    [Fact]
    public void SignedAmountRejectsUnknownType()
    {
        var created = MovementAmount.TryCreate(1m, out var amount, out _);
        Assert.True(created);
        Assert.NotNull(amount);

        Assert.Throws<ArgumentOutOfRangeException>(() => MovementRules.SignedAmount((MovementType)0, amount));
    }
}
