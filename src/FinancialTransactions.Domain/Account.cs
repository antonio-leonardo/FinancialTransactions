namespace FinancialTransactions.Domain;

/// <summary>Conta de um cliente. Nasce com saldo zero em <see cref="OpenedAt"/> (H6).</summary>
public sealed class Account
{
    public Account(Guid id, Guid customerId, string currency, DateTimeOffset openedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        Id = id;
        CustomerId = customerId;
        Currency = currency;
        OpenedAt = openedAt;
    }

    public Guid Id { get; }
    public Guid CustomerId { get; }
    public string Currency { get; }
    public DateTimeOffset OpenedAt { get; }

    /// <summary>Conta já existia em <paramref name="asOf"/>: fronteira inclusiva (H6/H7).</summary>
    public bool IsOpenAt(DateTimeOffset asOf) => OpenedAt <= asOf;
}
