using System.Globalization;
using FinancialTransactions.Domain;

namespace FinancialTransactions.UnitTests;

public class IdempotencyKeyTests
{
    private static MovementAmount Amount(string value)
    {
        var created = MovementAmount.TryCreate(decimal.Parse(value, CultureInfo.InvariantCulture), out var amount, out _);
        Assert.True(created);
        Assert.NotNull(amount);
        return amount;
    }

    [Fact]
    public void FingerprintIsSameForSameInstantInDifferentOffsets()
    {
        var minus3 = DateTimeOffset.Parse("2026-09-01T09:00:00-03:00", CultureInfo.InvariantCulture);
        var utc = DateTimeOffset.Parse("2026-09-01T12:00:00Z", CultureInfo.InvariantCulture);

        var first = IdempotencyKey.Fingerprint(MovementType.Credit, Amount("100"), minus3, null);
        var second = IdempotencyKey.Fingerprint(MovementType.Credit, Amount("100.00"), utc, null);

        Assert.Equal(first, second);
    }

    [Fact]
    public void FingerprintIsStableAcrossMicrosecondTruncation()
    {
        // 7 dígitos de fração (100 ns) e o valor de 6 dígitos que o 201 devolve após o truncamento.
        var sevenDigits = DateTimeOffset.Parse("2026-09-01T12:00:00.1234567Z", CultureInfo.InvariantCulture);
        var sixDigits = DateTimeOffset.Parse("2026-09-01T12:00:00.123456Z", CultureInfo.InvariantCulture);

        var first = IdempotencyKey.Fingerprint(MovementType.Credit, Amount("100"), sevenDigits, null);
        var second = IdempotencyKey.Fingerprint(MovementType.Credit, Amount("100"), sixDigits, null);

        Assert.Equal(first, second);
    }

    [Fact]
    public void FingerprintDiffersWhenDescriptionDiffers()
    {
        var at = DateTimeOffset.Parse("2026-09-01T12:00:00Z", CultureInfo.InvariantCulture);

        var withoutDescription = IdempotencyKey.Fingerprint(MovementType.Credit, Amount("100"), at, null);
        var withDescription = IdempotencyKey.Fingerprint(MovementType.Credit, Amount("100"), at, "salário");

        Assert.NotEqual(withoutDescription, withDescription);
    }

    [Fact]
    public void FingerprintDiffersWhenTypeOrAmountDiffers()
    {
        var at = DateTimeOffset.Parse("2026-09-01T12:00:00Z", CultureInfo.InvariantCulture);

        var credit = IdempotencyKey.Fingerprint(MovementType.Credit, Amount("100"), at, null);
        var debit = IdempotencyKey.Fingerprint(MovementType.Debit, Amount("100"), at, null);
        var other = IdempotencyKey.Fingerprint(MovementType.Credit, Amount("100.01"), at, null);

        Assert.NotEqual(credit, debit);
        Assert.NotEqual(credit, other);
    }

    [Fact]
    public void FingerprintIsLowercaseHexWith64Chars()
    {
        var at = DateTimeOffset.Parse("2026-09-01T12:00:00Z", CultureInfo.InvariantCulture);

        var fingerprint = IdempotencyKey.Fingerprint(MovementType.Credit, Amount("1"), at, null);

        Assert.Equal(64, fingerprint.Length);
        Assert.Matches("^[0-9a-f]{64}$", fingerprint);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("order-2026/09#01")]
    [InlineData("3f2b7c1e-0d6a-4f0e-9c51-1b2d3e4f5a6b")]
    public void TryValidateAcceptsPrintableAsciiKeys(string raw)
    {
        var valid = IdempotencyKey.TryValidate(raw, out var key, out var error);

        Assert.True(valid);
        Assert.Equal(raw, key);
        Assert.Null(error);
    }

    [Fact]
    public void TryValidateAccepts128Chars()
    {
        var raw = new string('k', 128);

        var valid = IdempotencyKey.TryValidate(raw, out var key, out _);

        Assert.True(valid);
        Assert.Equal(raw, key);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" abc")]
    [InlineData("abc ")]
    [InlineData("ab\tc")]
    [InlineData("açaí")]
    public void TryValidateRejectsEmptyPaddedOrNonPrintableKeys(string raw)
    {
        var valid = IdempotencyKey.TryValidate(raw, out var key, out var error);

        Assert.False(valid);
        Assert.Null(key);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryValidateRejectsNull()
    {
        var valid = IdempotencyKey.TryValidate(null, out var key, out var error);

        Assert.False(valid);
        Assert.Null(key);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryValidateRejects129Chars()
    {
        var raw = new string('k', 129);

        var valid = IdempotencyKey.TryValidate(raw, out var key, out var error);

        Assert.False(valid);
        Assert.Null(key);
        Assert.NotNull(error);
    }
}
