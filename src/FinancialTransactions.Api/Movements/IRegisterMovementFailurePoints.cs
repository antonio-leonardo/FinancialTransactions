namespace FinancialTransactions.Api.Movements;

/// <summary>
/// Pontos de falha controlados do registro, para provar o comportamento junto ao commit. Em produção só existe
/// <see cref="NoFailurePoints"/>; nenhuma configuração, cabeçalho ou endpoint ativa outra implementação. Os testes
/// substituem o singleton no host de teste.
/// </summary>
public interface IRegisterMovementFailurePoints
{
    /// <summary>Depois de <c>SaveChanges</c> e antes de <c>Commit</c>.</summary>
    ValueTask BeforeCommitAsync(CancellationToken cancellationToken);

    /// <summary>Depois de <c>Commit</c> e antes de devolver o resultado.</summary>
    ValueTask AfterCommitAsync(CancellationToken cancellationToken);
}

/// <summary>Implementação de produção: não faz nada.</summary>
public sealed class NoFailurePoints : IRegisterMovementFailurePoints
{
    public ValueTask BeforeCommitAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask AfterCommitAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
