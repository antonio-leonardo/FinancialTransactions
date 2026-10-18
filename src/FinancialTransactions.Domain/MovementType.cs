namespace FinancialTransactions.Domain;

/// <summary>Natureza do movimento. O valor numérico é o que vai para a coluna <c>type smallint</c>.</summary>
public enum MovementType
{
    Credit = 1,
    Debit = 2,
}
