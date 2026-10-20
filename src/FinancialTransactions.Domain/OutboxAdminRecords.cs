namespace FinancialTransactions.Domain;

/// <summary>Operação administrativa de reabertura de quarentena: o identificador impede repetir o efeito e fixa a seleção.</summary>
public sealed class OutboxAdminOperation
{
    public string OperationId { get; set; } = string.Empty;

    /// <summary>SHA-256 da seleção de <c>event_id</c> (ordenados), para detectar o mesmo identificador com outra seleção.</summary>
    public string SelectionDigest { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;
    public int EventCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Trilha persistida de cada evento reaberto: o estado anterior fica registrado quando o contador corrente volta a zero.</summary>
public sealed class OutboxAdminAction
{
    public long Id { get; set; }
    public string OperationId { get; set; } = string.Empty;
    public Guid EventId { get; set; }
    public long Sequence { get; set; }
    public short PreviousStatus { get; set; }
    public int PreviousAttempts { get; set; }
    public string? PreviousError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Registro, na origem, de qual projeção é a ativa depois de uma reconstrução promovida (linha única). Enquanto não existe, nenhuma projeção
/// é considerada substituída. Depois da promoção, um destino com outra identidade (inclusive o antigo, sem identidade) deixa de ser consumido
/// e de ser servido: a volta ao banco antigo não é um rollback seguro.
/// </summary>
public sealed class ProjectionRegistry
{
    public short Id { get; set; } = 1;
    public Guid ActiveProjectionId { get; set; }
    public long Generation { get; set; }
    public DateTimeOffset PromotedAt { get; set; }
    public string OperationLabel { get; set; } = string.Empty;
    public string PreviousDatabase { get; set; } = string.Empty;
}
