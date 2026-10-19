namespace FinancialTransactions.Consolidation;

/// <summary>Totais de um recorte de contas num dia: valores do dia e fechamento acumulado, somados em <see cref="decimal"/>.</summary>
public sealed record ConsolidationTotals(decimal Credits, decimal Debits, decimal Net, long MovementCount, decimal ClosingBalance, long? MaxAppliedSequence)
{
    /// <summary>
    /// Soma as linhas lidas da projeção. A aritmética de <c>decimal</c> é verificada: estourar a faixa (28 a 29 dígitos) lança
    /// <see cref="OverflowException"/>, observável, nunca arredonda nem troca por <c>double</c> ou saldo substituto. O fechamento de cada
    /// linha já é o acumulado até o dia (a soma de <c>net</c> dos dias anteriores e do próprio dia, lida na mesma instrução SQL).
    /// </summary>
    public static ConsolidationTotals Of(IReadOnlyCollection<ProjectionAccountRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        decimal credits = 0m, debits = 0m, net = 0m, closing = 0m;
        long count = 0;
        long? maxSequence = null;
        foreach (var row in rows)
        {
            credits += row.Credits;
            debits += row.Debits;
            net += row.Net;
            closing += row.ClosingBalance;
            count = checked(count + row.MovementCount);
            if (row.MaxAppliedSequence is { } sequence && (maxSequence is null || sequence > maxSequence))
            {
                maxSequence = sequence;
            }
        }

        return new ConsolidationTotals(credits, debits, net, count, closing, maxSequence);
    }
}
