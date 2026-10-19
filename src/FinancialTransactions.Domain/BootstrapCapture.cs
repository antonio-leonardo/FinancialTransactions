namespace FinancialTransactions.Domain;

/// <summary>
/// Registro de controle da captura inicial, na origem: existe se e somente se a captura foi confirmada (inclusive com conjunto
/// vazio). Linha única; é a referência durável que a ativação no destino repete. O código do projeto nunca a apaga nem a regrava.
/// </summary>
public sealed class BootstrapCapture
{
    public const short SingleRowId = 1;

    public BootstrapCapture(Guid bootstrapId, long memberCount, DateTimeOffset capturedAt, string snapshot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(memberCount);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot);
        BootstrapId = bootstrapId;
        MemberCount = memberCount;
        CapturedAt = capturedAt;
        Snapshot = snapshot;
    }

    public short Id { get; } = SingleRowId;
    public Guid BootstrapId { get; }
    public long MemberCount { get; }
    public DateTimeOffset CapturedAt { get; }

    /// <summary>Resultado de <c>pg_current_snapshot()</c> da transação de captura.</summary>
    public string Snapshot { get; }
}
