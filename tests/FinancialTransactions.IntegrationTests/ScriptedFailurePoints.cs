using FinancialTransactions.Api.Movements;

namespace FinancialTransactions.IntegrationTests;

public enum FailurePoint
{
    BeforeCommit,
    AfterCommit,
}

/// <summary>Ponto de falha de teste: lança uma vez no ponto escolhido e depois volta a não falhar.</summary>
public sealed class ScriptedFailurePoints(FailurePoint failAt) : IRegisterMovementFailurePoints
{
    private int _fired;

    /// <summary>Quantas vezes a falha injetada foi disparada (0 ou 1).</summary>
    public int FiredCount => Volatile.Read(ref _fired);

    public ValueTask BeforeCommitAsync(CancellationToken cancellationToken) => Fire(FailurePoint.BeforeCommit);

    public ValueTask AfterCommitAsync(CancellationToken cancellationToken) => Fire(FailurePoint.AfterCommit);

    private ValueTask Fire(FailurePoint point)
    {
        if (point == failAt && Interlocked.CompareExchange(ref _fired, 1, 0) == 0)
        {
            throw new InvalidOperationException("falha injetada");
        }

        return ValueTask.CompletedTask;
    }
}
