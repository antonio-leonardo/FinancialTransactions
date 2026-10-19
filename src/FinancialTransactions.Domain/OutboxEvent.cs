namespace FinancialTransactions.Domain;

/// <summary>Situação do evento na Outbox. O valor numérico é o da coluna <c>status smallint</c>.</summary>
public enum OutboxStatus : short
{
    /// <summary>Enfileirado: ainda não confirmado como aplicado.</summary>
    Pending = 0,

    /// <summary>Aplicado na projeção e confirmado na origem.</summary>
    Processed = 1,

    /// <summary>Em quarentena depois de tentativas esgotadas por defeito do próprio evento.</summary>
    Failed = 2,
}

/// <summary>Procedência da linha: não indica pertença ao conjunto inicial (isso é <see cref="OutboxEvent.BootstrapId"/>).</summary>
public enum OutboxOrigin : short
{
    /// <summary>Gravada pelo registro de lançamentos, na transação do movimento.</summary>
    Online = 0,

    /// <summary>Inserida pelo bootstrap para um movimento que já existia sem evento.</summary>
    Bootstrap = 1,
}

/// <summary>
/// Trabalho durável para a consolidação diária. Nasce na mesma transação do <see cref="Movement"/> a que se refere
/// (<see cref="EventId"/> = id do movimento), de modo que não existe movimento confirmado sem evento nem evento sem movimento.
/// Os campos de andamento (status, tentativas, erro) são escritos só pelo consumidor, nunca pelo registro.
/// </summary>
public sealed class OutboxEvent
{
    // Construtor usado pelo EF Core (binding por nome de parâmetro) e pela fábrica abaixo.
    private OutboxEvent(
        Guid eventId,
        Guid accountId,
        Guid customerId,
        MovementType type,
        decimal amount,
        DateTimeOffset effectiveAt,
        DateTimeOffset createdAt,
        OutboxOrigin origin)
    {
        EventId = eventId;
        AccountId = accountId;
        CustomerId = customerId;
        Type = type;
        Amount = amount;
        EffectiveAt = effectiveAt;
        CreatedAt = createdAt;
        Origin = origin;
    }

    /// <summary>Ordem de enfileiramento, atribuída pelo banco (identity). Não é prova de visibilidade nem de completude.</summary>
    public long Sequence { get; private set; }

    public Guid EventId { get; }
    public Guid AccountId { get; }
    public Guid CustomerId { get; }
    public MovementType Type { get; }
    public decimal Amount { get; }
    public DateTimeOffset EffectiveAt { get; }
    public DateTimeOffset CreatedAt { get; }
    public OutboxOrigin Origin { get; }

    /// <summary>Pertença ao conjunto inicial capturado pelo bootstrap; nulo para tudo o que é confirmado depois do snapshot.</summary>
    public Guid? BootstrapId { get; private set; }

    public OutboxStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }

    /// <summary>Evento online do movimento recém-criado; deve ser adicionado ao mesmo contexto e salvo na mesma transação.</summary>
    public static OutboxEvent ForMovement(Movement movement, Guid customerId, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(movement);
        return new OutboxEvent(
            movement.Id,
            movement.AccountId,
            customerId,
            movement.Type,
            movement.Amount.Value,
            movement.EffectiveAt,
            Movement.NormalizeForPersistence(createdAt),
            OutboxOrigin.Online);
    }
}
