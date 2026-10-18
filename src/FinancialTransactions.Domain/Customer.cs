namespace FinancialTransactions.Domain;

/// <summary>Cliente sintético. Cadastro semeado por migration; não há CRUD (H2).</summary>
public sealed class Customer
{
    public Customer(Guid id, string name, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id;
        Name = name;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }
    public string Name { get; }
    public DateTimeOffset CreatedAt { get; }
}
