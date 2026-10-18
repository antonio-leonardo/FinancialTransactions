using System.Globalization;
using FinancialTransactions.Domain;

namespace FinancialTransactions.UnitTests;

public class MovementTests
{
    private static MovementAmount Amount(string value)
    {
        var created = MovementAmount.TryCreate(decimal.Parse(value, CultureInfo.InvariantCulture), out var amount, out _);
        Assert.True(created);
        Assert.NotNull(amount);
        return amount;
    }

    [Fact]
    public void CreateNormalizesInstantsToUtcWithMicrosecondPrecision()
    {
        var effectiveAt = DateTimeOffset.Parse("2026-09-01T09:00:00.1234567-03:00", CultureInfo.InvariantCulture);
        var registeredAt = DateTimeOffset.Parse("2026-10-18T19:00:00.9999999-03:00", CultureInfo.InvariantCulture);

        var movement = Movement.Create(Guid.NewGuid(), MovementType.Credit, Amount("10"), effectiveAt, registeredAt, "k", new string('a', 64), null);

        Assert.Equal(TimeSpan.Zero, movement.EffectiveAt.Offset);
        Assert.Equal(DateTimeOffset.Parse("2026-09-01T12:00:00.123456Z", CultureInfo.InvariantCulture), movement.EffectiveAt);
        Assert.Equal(DateTimeOffset.Parse("2026-10-18T22:00:00.999999Z", CultureInfo.InvariantCulture), movement.RegisteredAt);
        Assert.Equal("BRL", movement.Currency);
        Assert.NotEqual(Guid.Empty, movement.Id);
    }

    [Fact]
    public void NormalizeForPersistenceKeepsAlreadyNormalizedInstants()
    {
        var instant = DateTimeOffset.Parse("2026-09-01T12:00:00.000001Z", CultureInfo.InvariantCulture);

        Assert.Equal(instant, Movement.NormalizeForPersistence(instant));
    }

    [Fact]
    public void CreateRejectsUnknownTypeAndEmptyIdentity()
    {
        var amount = Amount("1");
        var now = DateTimeOffset.UtcNow;

        Assert.Throws<ArgumentOutOfRangeException>(() => Movement.Create(Guid.NewGuid(), (MovementType)9, amount, now, now, "k", "f", null));
        Assert.Throws<ArgumentException>(() => Movement.Create(Guid.NewGuid(), MovementType.Credit, amount, now, now, "", "f", null));
        Assert.Throws<ArgumentException>(() => Movement.Create(Guid.NewGuid(), MovementType.Credit, amount, now, now, "k", "", null));
    }
}
