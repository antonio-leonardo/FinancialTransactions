using System.Security.Cryptography;

namespace FinancialTransactions.Consolidation;

/// <summary>
/// Identidade opaca desta instância do Worker, gerada na inicialização: prefixo fixo e bytes aleatórios, sem nome de máquina nem de usuário.
/// É o "proprietário" gravado na reserva; a posse efetiva é do token de cada aquisição.
/// </summary>
public sealed class ConsolidationInstance
{
    public string Id { get; } = "w-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
}

/// <summary>Fotografia dos contadores de uma instância. Entregas e duplicatas evitadas não são movimentos financeiros distintos.</summary>
public sealed record WorkerCountersSnapshot(
    long Acquisitions,
    long AppliedNew,
    long DuplicatesAvoided,
    long UnconfirmedAcknowledgements,
    long LeaseLosses,
    long EventFailures,
    long TransientFailures,
    long EnvironmentFailures,
    long Quarantined,
    long Releases);

/// <summary>Contadores por instância (singleton), atualizados pelo consumidor e lidos pelo diagnóstico da instância.</summary>
public sealed class ConsolidationWorkerCounters
{
    private long _acquisitions;
    private long _appliedNew;
    private long _duplicatesAvoided;
    private long _unconfirmedAcks;
    private long _leaseLosses;
    private long _eventFailures;
    private long _transientFailures;
    private long _environmentFailures;
    private long _quarantined;
    private long _releases;

    public void Acquired() => Interlocked.Increment(ref _acquisitions);

    public void Applied() => Interlocked.Increment(ref _appliedNew);

    public void DuplicateAvoided() => Interlocked.Increment(ref _duplicatesAvoided);

    public void UnconfirmedAcknowledgement() => Interlocked.Increment(ref _unconfirmedAcks);

    public void LeaseLost() => Interlocked.Increment(ref _leaseLosses);

    public void EventFailed() => Interlocked.Increment(ref _eventFailures);

    public void EventQuarantined() => Interlocked.Increment(ref _quarantined);

    public void Released() => Interlocked.Increment(ref _releases);

    public void Failed(FailureClass failureClass)
    {
        switch (failureClass)
        {
            case FailureClass.Transient:
                Interlocked.Increment(ref _transientFailures);
                break;
            case FailureClass.Environment:
                Interlocked.Increment(ref _environmentFailures);
                break;
        }
    }

    public WorkerCountersSnapshot Snapshot() => new(
        Interlocked.Read(ref _acquisitions),
        Interlocked.Read(ref _appliedNew),
        Interlocked.Read(ref _duplicatesAvoided),
        Interlocked.Read(ref _unconfirmedAcks),
        Interlocked.Read(ref _leaseLosses),
        Interlocked.Read(ref _eventFailures),
        Interlocked.Read(ref _transientFailures),
        Interlocked.Read(ref _environmentFailures),
        Interlocked.Read(ref _quarantined),
        Interlocked.Read(ref _releases));
}
