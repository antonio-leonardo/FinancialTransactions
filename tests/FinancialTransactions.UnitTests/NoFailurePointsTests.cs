using FinancialTransactions.Api.Movements;

namespace FinancialTransactions.UnitTests;

public class NoFailurePointsTests
{
    [Fact]
    public async Task ProductionImplementationNeverThrowsBeforeCommit()
    {
        var points = new NoFailurePoints();

        await points.BeforeCommitAsync(CancellationToken.None);

        Assert.True(true, "BeforeCommitAsync completou sem lançar.");
    }

    [Fact]
    public async Task ProductionImplementationNeverThrowsAfterCommit()
    {
        var points = new NoFailurePoints();

        await points.AfterCommitAsync(CancellationToken.None);

        Assert.True(true, "AfterCommitAsync completou sem lançar.");
    }
}
