using FinancialTransactions.Consolidation;

namespace FinancialTransactions.UnitTests;

/// <summary>Fechamento derivado e totais por cliente: soma em decimal, máximo observado e estouro observável.</summary>
public sealed class ConsolidationTotalsTests
{
    private static readonly Guid Account1 = Guid.Parse("aaaaaaa1-0000-0000-0000-000000000001");
    private static readonly Guid Account2 = Guid.Parse("aaaaaaa2-0000-0000-0000-000000000002");

    [Fact(DisplayName = "Totals_SumTheDayValuesAndTheDerivedClosingOfEachAccount")]
    public void TotalsSumTheDayValuesAndTheDerivedClosingOfEachAccount()
    {
        // A conta 1 fechou o dia com 70 acumulados (crédito 100, débito 30 no dia); a conta 2 só tem o acumulado de dias anteriores.
        var rows = new[]
        {
            new ProjectionAccountRow(Account1, 100.00m, 30.00m, 70.00m, 2, 70.00m, 7),
            new ProjectionAccountRow(Account2, 0m, 0m, 0m, 0, 25.50m, 3),
        };

        var totals = ConsolidationTotals.Of(rows);

        Assert.Equal((100.00m, 30.00m, 70.00m, 2L, 95.50m, 7L), (totals.Credits, totals.Debits, totals.Net, totals.MovementCount, totals.ClosingBalance, totals.MaxAppliedSequence));
    }

    [Fact(DisplayName = "Totals_MaxAppliedSequence_IsTheMaximumObserved_AndNullWhenNothingWasApplied")]
    public void TotalsMaxAppliedSequenceIsTheMaximumObservedAndNullWhenNothingWasApplied()
    {
        var none = ConsolidationTotals.Of([new ProjectionAccountRow(Account1, 0m, 0m, 0m, 0, 0m, null), new ProjectionAccountRow(Account2, 0m, 0m, 0m, 0, 0m, null)]);
        var some = ConsolidationTotals.Of([new ProjectionAccountRow(Account1, 0m, 0m, 0m, 0, 0m, 4), new ProjectionAccountRow(Account2, 0m, 0m, 0m, 0, 0m, 9)]);

        Assert.Null(none.MaxAppliedSequence);
        Assert.Equal(9, some.MaxAppliedSequence);
        Assert.Equal(0m, none.ClosingBalance); // conta sem linha de projeção: zeros legítimos depois do bootstrap
    }

    [Fact(DisplayName = "Totals_KeepCentsAtTheNumeric24Limit_AndOverflowIsAnException_NeverRounded")]
    public void TotalsKeepCentsAtTheNumeric24LimitAndOverflowIsAnException()
    {
        const decimal AtTheLimit = 9_999_999_999_999_999_999_999.99m; // 10^22 - 0,01
        var thousandRows = Enumerable.Range(0, 1000)
            .Select(_ => new ProjectionAccountRow(Account1, AtTheLimit, 0m, AtTheLimit, 1, AtTheLimit, 1))
            .ToList();

        var totals = ConsolidationTotals.Of(thousandRows);

        Assert.Equal(AtTheLimit * 1000m, totals.Credits); // 10^25 menos 10, com os centavos exatos
        Assert.Equal(1000, totals.MovementCount);

        var overflowing = new[]
        {
            new ProjectionAccountRow(Account1, decimal.MaxValue, 0m, decimal.MaxValue, 1, decimal.MaxValue, 1),
            new ProjectionAccountRow(Account2, decimal.MaxValue, 0m, decimal.MaxValue, 1, decimal.MaxValue, 1),
        };
        Assert.Throws<OverflowException>(() => ConsolidationTotals.Of(overflowing));
    }
}
