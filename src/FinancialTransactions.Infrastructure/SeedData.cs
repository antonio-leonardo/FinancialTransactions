namespace FinancialTransactions.Infrastructure;

/// <summary>Identificadores fixos da massa sintética semeada pela migration InitialSchema. Nenhum dado pessoal.</summary>
public static class SeedData
{
    public static readonly Guid CustomerA = new("11111111-1111-1111-1111-111111111111");
    public static readonly Guid CustomerB = new("22222222-2222-2222-2222-222222222222");
    public static readonly Guid AccountA1 = new("aaaaaaa1-0000-0000-0000-000000000001");
    public static readonly Guid AccountA2 = new("aaaaaaa2-0000-0000-0000-000000000002");
    public static readonly Guid AccountB1 = new("bbbbbbb1-0000-0000-0000-000000000001");

    public static readonly DateTimeOffset AccountA1OpenedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset AccountA2OpenedAt = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset AccountB1OpenedAt = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
}
