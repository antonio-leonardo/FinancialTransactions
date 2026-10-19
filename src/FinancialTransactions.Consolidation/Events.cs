namespace FinancialTransactions.Consolidation;

/// <summary>Evento lido da Outbox da origem, ainda sem validação do conteúdo.</summary>
public sealed record ConsolidationEvent(
    long Sequence,
    Guid EventId,
    Guid AccountId,
    Guid CustomerId,
    short Type,
    decimal Amount,
    DateTimeOffset EffectiveAt,
    int Attempts);

/// <summary>Defeito atribuível aos dados do próprio evento (tipo desconhecido, valor fora do domínio). Vai para a quarentena depois de 5 tentativas.</summary>
public sealed class InvalidEventException : Exception
{
    public InvalidEventException()
        : base("Evento inválido.")
    {
    }

    public InvalidEventException(string message)
        : base(message)
    {
    }

    public InvalidEventException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public enum ApplyOutcome
{
    /// <summary>Somado na projeção, na mesma transação do marcador.</summary>
    Applied,

    /// <summary>O marcador já existia: nenhuma soma.</summary>
    AlreadyApplied,
}

/// <summary>Pontos de falha controlados, registrados só no host de teste; produção usa <see cref="NoConsolidationFailurePoints"/>.</summary>
public interface IConsolidationFailurePoints
{
    /// <summary>Dentro da transação da projeção, entre o INSERT em processed_events e o UPSERT do agregado.</summary>
    ValueTask InsideProjectionTransactionAsync(CancellationToken cancellationToken);

    /// <summary>Depois do commit da projeção e antes de marcar o evento como processado na origem.</summary>
    ValueTask AfterProjectionCommitBeforeMarkAsync(CancellationToken cancellationToken);
}

public sealed class NoConsolidationFailurePoints : IConsolidationFailurePoints
{
    public ValueTask InsideProjectionTransactionAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask AfterProjectionCommitBeforeMarkAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>Pontos de pausa do coordenador do bootstrap, registrados só no host de teste.</summary>
public interface IBootstrapFailurePoints
{
    ValueTask AfterSnapshotEstablishedAsync(CancellationToken cancellationToken);

    ValueTask AfterCaptureCommitBeforeActivationAsync(CancellationToken cancellationToken);

    ValueTask BeforeActivationAsync(CancellationToken cancellationToken);
}

public sealed class NoBootstrapFailurePoints : IBootstrapFailurePoints
{
    public ValueTask AfterSnapshotEstablishedAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask AfterCaptureCommitBeforeActivationAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask BeforeActivationAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

/// <summary>Pausa da API do consolidado entre a leitura da origem e a abertura da consulta à projeção (C14); só no host de teste.</summary>
public interface IConsolidationApiFailurePoints
{
    ValueTask BetweenOriginAndProjectionAsync(CancellationToken cancellationToken);
}

public sealed class NoConsolidationApiFailurePoints : IConsolidationApiFailurePoints
{
    public ValueTask BetweenOriginAndProjectionAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
