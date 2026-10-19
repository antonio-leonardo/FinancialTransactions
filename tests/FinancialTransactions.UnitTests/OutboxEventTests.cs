using FinancialTransactions.Domain;

namespace FinancialTransactions.UnitTests;

public sealed class OutboxEventTests
{
    private static readonly Guid Account = Guid.Parse("aaaaaaa1-0000-0000-0000-000000000001");
    private static readonly Guid Customer = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static Movement NewMovement(MovementType type, decimal value)
    {
        Assert.True(MovementAmount.TryCreate(value, out var amount, out _));
        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, 123, TimeSpan.Zero);
        return Movement.Create(Account, type, amount, now.AddHours(-1), now, "chave-1", new string('a', 64), null);
    }

    [Fact]
    public void ForMovementCopiesIdentityAndFinancialFields()
    {
        var movement = NewMovement(MovementType.Debit, 42.50m);
        var createdAt = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);

        var outbox = OutboxEvent.ForMovement(movement, Customer, createdAt);

        Assert.Equal(movement.Id, outbox.EventId);
        Assert.Equal(Account, outbox.AccountId);
        Assert.Equal(Customer, outbox.CustomerId);
        Assert.Equal(MovementType.Debit, outbox.Type);
        Assert.Equal(42.50m, outbox.Amount);
        Assert.Equal(movement.EffectiveAt, outbox.EffectiveAt);
        Assert.Equal(Movement.NormalizeForPersistence(createdAt), outbox.CreatedAt);
    }

    [Fact]
    public void ForMovementStartsPendingOnlineOutsideAnyBootstrapSet()
    {
        var outbox = OutboxEvent.ForMovement(NewMovement(MovementType.Credit, 1m), Customer, DateTimeOffset.UnixEpoch);

        Assert.Equal(OutboxStatus.Pending, outbox.Status);
        Assert.Equal(OutboxOrigin.Online, outbox.Origin);
        Assert.Null(outbox.BootstrapId);
        Assert.Equal(0, outbox.Attempts);
        Assert.Null(outbox.LastError);
        Assert.Null(outbox.ProcessedAt);
    }

    [Fact]
    public void StatusAndOriginNumericValuesMatchTheColumnContract()
    {
        Assert.Equal(0, (short)OutboxStatus.Pending);
        Assert.Equal(1, (short)OutboxStatus.Processed);
        Assert.Equal(2, (short)OutboxStatus.Failed);
        Assert.Equal(0, (short)OutboxOrigin.Online);
        Assert.Equal(1, (short)OutboxOrigin.Bootstrap);
    }

    [Fact]
    public void BootstrapCaptureAllowsEmptySetAndRejectsNegativeCount()
    {
        var empty = new BootstrapCapture(Guid.NewGuid(), 0, DateTimeOffset.UnixEpoch, "10:20:");
        Assert.Equal(0, empty.MemberCount);
        Assert.Equal(1, empty.Id);

        Assert.Throws<ArgumentOutOfRangeException>(() => new BootstrapCapture(Guid.NewGuid(), -1, DateTimeOffset.UnixEpoch, "10:20:"));
        Assert.Throws<ArgumentException>(() => new BootstrapCapture(Guid.NewGuid(), 0, DateTimeOffset.UnixEpoch, " "));
    }
}
