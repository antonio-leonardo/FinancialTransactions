namespace FinancialTransactions.Domain;

/// <summary>
/// Fato financeiro registrado em uma conta. A identidade da intenção (chave de idempotência e
/// fingerprint do conteúdo) vive na própria linha: uma transação, uma restrição UNIQUE (H10).
/// </summary>
public sealed class Movement
{
    // Construtor usado pelo EF Core (binding por nome de parâmetro) e pela fábrica abaixo.
    private Movement(
        Guid id,
        Guid accountId,
        MovementType type,
        MovementAmount amount,
        string currency,
        DateTimeOffset effectiveAt,
        DateTimeOffset registeredAt,
        string idempotencyKey,
        string requestFingerprint,
        string? description)
    {
        Id = id;
        AccountId = accountId;
        Type = type;
        Amount = amount;
        Currency = currency;
        EffectiveAt = effectiveAt;
        RegisteredAt = registeredAt;
        IdempotencyKey = idempotencyKey;
        RequestFingerprint = requestFingerprint;
        Description = description;
    }

    public Guid Id { get; }
    public Guid AccountId { get; }
    public MovementType Type { get; }
    public MovementAmount Amount { get; }
    public string Currency { get; }

    /// <summary>Instante em que o fato vale para a posição; informado pelo cliente da API, guardado em UTC (H4).</summary>
    public DateTimeOffset EffectiveAt { get; }

    /// <summary>Instante atribuído pelo servidor ao registrar; não é tempo de commit nem corte de conhecimento (H9).</summary>
    public DateTimeOffset RegisteredAt { get; }

    public string IdempotencyKey { get; }
    public string RequestFingerprint { get; }
    public string? Description { get; }

    /// <summary>
    /// Única forma de criar um movimento novo. Exige um <see cref="MovementAmount"/> já validado e normaliza os
    /// instantes na fronteira do domínio: UTC e precisão de microssegundo, que é o que timestamptz guarda. Assim a
    /// resposta de criação e qualquer releitura do banco carregam exatamente o mesmo valor.
    /// </summary>
    public static Movement Create(
        Guid accountId,
        MovementType type,
        MovementAmount amount,
        DateTimeOffset effectiveAt,
        DateTimeOffset registeredAt,
        string idempotencyKey,
        string requestFingerprint,
        string? description)
    {
        ArgumentNullException.ThrowIfNull(amount);
        ArgumentException.ThrowIfNullOrEmpty(idempotencyKey);
        ArgumentException.ThrowIfNullOrEmpty(requestFingerprint);
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Tipo de movimento desconhecido.");
        }

        return new Movement(
            Guid.CreateVersion7(),
            accountId,
            type,
            amount,
            amount.Currency,
            NormalizeForPersistence(effectiveAt),
            NormalizeForPersistence(registeredAt),
            idempotencyKey,
            requestFingerprint,
            description);
    }

    /// <summary>UTC, truncado a microssegundos (1 tick = 100 ns; timestamptz resolve 1 µs).</summary>
    public static DateTimeOffset NormalizeForPersistence(DateTimeOffset instant)
    {
        const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;
        var utcTicks = instant.UtcTicks - (instant.UtcTicks % TicksPerMicrosecond);
        return new DateTimeOffset(utcTicks, TimeSpan.Zero);
    }
}
